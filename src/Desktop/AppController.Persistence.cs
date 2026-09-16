using RealtimeTranscription.Core;

namespace RealtimeTranscription.Desktop;

public sealed partial class AppController
{
    private readonly HashSet<string> forgottenSessions = [];
    private readonly HashSet<string> forgottenProjects = [];

    private bool IsForgotten(SessionData session)=>forgottenSessions.Contains(session.Id)||forgottenProjects.Contains(session.ProjectId);
    private bool HasPending(string sessionId)=>pendingSessions.Keys.Any(k=>k.Id==sessionId)||failedWrites.Values.Any(s=>s.SessionId==sessionId)
        ||(engine?.Session.Id==sessionId&&engine.Segments.Any(s=>s.SaveState is SaveState.Pending or SaveState.Failed));
    private void ForgetProjectPending(string projectId)
    {
        forgottenProjects.Add(projectId);
        var sessions=pendingSessions.Values.Where(s=>s.ProjectId==projectId).Select(s=>s.Id)
            .Concat(failedSources.Values.Where(s=>s.Session.ProjectId==projectId).Select(s=>s.Session.Id)).Distinct().ToArray();
        foreach(var id in sessions)ForgetPending(id);
    }

    private void ForgetPending(string sessionId)
    {
        forgottenSessions.Add(sessionId);
        foreach(var key in pendingSessions.Keys.Where(k=>k.Id==sessionId).ToArray())pendingSessions.Remove(key);
        failedSessionWrites.RemoveWhere(k=>k.Id==sessionId);
        foreach(var id in failedWrites.Where(p=>p.Value.SessionId==sessionId).Select(p=>p.Key).ToArray())
        {failedWrites.Remove(id);failedSources.Remove(id);}
    }

    private async Task<bool> SaveSessionSafe(SessionData session,bool permissionOnly=false)
    {
        if(!MemoryAvailable)return false;
        var key=(session.Id,permissionOnly);
        bool accepted=await OnActor(()=>
        {
            if(IsForgotten(session))return false;
            if(!pendingSessions.TryGetValue(key,out var newer)||newer.Revision<=session.Revision)pendingSessions[key]=session;
            Notify();return true;
        });
        if(!accepted)return false;
        bool success=true;
        try
        {
            if(permissionOnly)await Repository.SaveLearningPermissionAsync(session);
            else await Repository.SaveSessionAsync(session);
        }
        catch(Exception e){success=false;LogEvent("PersistenceFailed",e,session.Id,("Operation",permissionOnly?"SaveLearningPermission":"SaveSession"),("Revision",session.Revision));}
        await OnActor(()=>
        {
            if(IsForgotten(session))return;
            if(success)
            {
                CorrectionsUpdated?.Invoke();
                if(pendingSessions.TryGetValue(key,out var pending)&&pending.Revision<=session.Revision)pendingSessions.Remove(key);
                if(!permissionOnly&&pendingSessions.TryGetValue((session.Id,true),out var permission)&&permission.LearningRevision<=session.LearningRevision)pendingSessions.Remove((session.Id,true));
                foreach(var failed in failedSessionWrites.Where(k=>k.Id==session.Id&&!pendingSessions.ContainsKey(k)).ToArray())failedSessionWrites.Remove(failed);
                Notify();
            }
            else if(pendingSessions.ContainsKey(key)){failedSessionWrites.Add(key);Status("会话信息未保存，正文仍可复制。请点击重试保存。");}
        });
        // Revoking consent deletes observations in the repository transaction. Refresh the
        // vocabulary before acknowledging success, including the retry-save path.
        if(success&&!session.AllowLearning)await ReloadTerms();
        return success;
    }

    public async Task SetSessionLearningAsync(bool enabled)
    {
        if(!MemoryAvailable)throw new InvalidOperationException(MemoryStatus);
        await settingsGate.WaitAsync();
        try
        {
            var session=await OnActor(()=>
            {
                if(engine==null)throw new InvalidOperationException("请先完成或载入一个会话。");
                if(state is CaptureState.Recording or CaptureState.Connecting or CaptureState.Draining||activePolish>0)throw new InvalidOperationException("请等待本轮输入完成后修改许可。");
                knowledgeEpoch++;CancelToken(extraction);engine.Learning(enabled);
                var current=engine.Session;
                if(pendingSessions.TryGetValue((current.Id,false),out var pending))
                    pendingSessions[(current.Id,false)]=pending with{AllowLearning=current.AllowLearning,LearningRevision=current.LearningRevision,Revision=Math.Max(pending.Revision,current.Revision)};
                Notify();return current;
            });
            if(!await SaveSessionSafe(session,!Settings.SaveMemory))throw new InvalidOperationException("当前会话的词条整理许可未保存，请重试保存。");
            await OnActor(()=>Status(enabled?"当前会话已允许整理词条。":"当前会话已禁止整理词条。"));
        }
        finally{settingsGate.Release();}
    }

    public async Task RetrySaveAsync()
    {
        var current=await SnapshotAsync();
        if(current.State is CaptureState.Connecting or CaptureState.Recording or CaptureState.Draining||Volatile.Read(ref activePolish)>0)
            throw new InvalidOperationException("请等本轮输入完成后重试保存。");
        if(!MemoryAvailable)
        {
            await InitializeMemoryAsync();
            if(!MemoryAvailable)throw new InvalidOperationException(MemoryStatus);
            if(!Settings.SaveMemory){await OnActor(()=>Status("本地记忆已恢复；文本保存保持关闭。"));return;}
        }
        var work=await OnActor(()=>
        {
            if(state is CaptureState.Connecting or CaptureState.Recording or CaptureState.Draining||activePolish>0)throw new InvalidOperationException("请等本轮输入完成后重试保存。");
            if(!Settings.SaveMemory&&(pendingSessions.Keys.Any(k=>!k.PermissionOnly)||failedWrites.Count>0))throw new InvalidOperationException("请先开启文本记忆保存，再重试保存正文。");
            if(!Settings.SaveMemory&&pendingSessions.Count==0)throw new InvalidOperationException("文本记忆保存已关闭；如需保存当前正文，请先开启该设置。");
            var segments=failedWrites.Select(p=>(Source:failedSources[p.Key],Segment:p.Value)).ToList();
            if(Settings.SaveMemory&&engine!=null)segments.AddRange(engine.Segments.Where(s=>s.SaveState!=SaveState.Saved).Select(s=>(engine,s)));
            var sessions=pendingSessions.ToDictionary(p=>p.Key,p=>p.Value);
            if(Settings.SaveMemory&&engine!=null)sessions[(engine.Session.Id,false)]=engine.Session;
            return(Segments:segments.GroupBy(p=>p.Segment.Id).Select(g=>g.MaxBy(p=>p.Segment.Revision)).ToArray(),Sessions:sessions);
        });
        bool success=true;
        foreach(var item in work.Segments)
        {
            var session=await OnActor(()=>{item.Source.MarkPending(item.Segment.Id,true);return item.Source.Session;});
            success&=await SaveSegment(item.Source,session,item.Segment with{SaveState=SaveState.Saved});
        }
        foreach(var item in work.Sessions)success&=await SaveSessionSafe(item.Value,item.Key.PermissionOnly);
        await Repository.BarrierAsync();
        await OnActor(()=>Status(success&&UnsavedCount()==0?(Settings.SaveMemory?"正文和会话信息已保存。":"会话许可已保存；文本记忆保存仍处于关闭状态。") :"仍有内容未保存，请检查保存位置后重试；正文可复制。"));
    }
}
