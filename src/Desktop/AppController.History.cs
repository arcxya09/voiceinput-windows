using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

namespace RealtimeTranscription.Desktop;

public sealed partial class AppController
{
    public Task<HistoryExtractionProgress> ExtractAllHistoryAsync(IProgress<HistoryExtractionProgress>? progress,CancellationToken token)
        =>RunExtractionAsync(null,progress,token);

    private async Task<HistoryExtractionProgress> RunExtractionAsync(string? sessionId,IProgress<HistoryExtractionProgress>? progress,CancellationToken token)
    {
        if(!await extractionGate.WaitAsync(0,token))throw new InvalidOperationException("另一个词库 AI 任务正在进行，请先完成或取消。");
        using var cancelled=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,token);extraction=cancelled;Extracting?.Invoke(true);
        try
        {
            await Repository.BarrierAsync();var snap=await SnapshotAsync();
            if(snap.State is CaptureState.Recording or CaptureState.Connecting or CaptureState.Draining||snap.Pending>0||Volatile.Read(ref activePolish)>0)throw new InvalidOperationException("请等本轮输入结束后整理全部历史。");
            if(!Settings.SaveMemory||!Settings.AllowLearning)throw new InvalidOperationException("请先启用本地文本记忆和词条整理，并保存设置。");
            if(snap.Unsaved>0)throw new InvalidOperationException("还有正文或会话许可未保存，请先重试保存后整理词条。");
            if(sessionId!=null&&(snap.Session?.Id!=sessionId||!snap.Session.AllowLearning))throw new InvalidOperationException("当前会话未允许整理词条。");
            string key=Keys.DeepSeekKey;if(key.Length==0)throw new InvalidOperationException("请先保存 DeepSeek API Key。");
            var worker=new HistoryExtractor(Repository,async(batch,requestToken)=>
            {
                int reserve=DeepSeekClient.Request(DeepSeekClient.ExtractPrompt,DeepSeekClient.ExtractionInput(batch),true,2048).Length+2048;
                using var budget=CancellationTokenSource.CreateLinkedTokenSource(requestToken);budget.CancelAfter(35000);
                long deadline=Environment.TickCount64+35000;
                for(int attempt=0;;attempt++)
                {
                    var at=DateTimeOffset.UtcNow;
                    await Repository.ReserveTermBudgetAsync(reserve,Settings.DailyExtractionTokens,at,budget.Token);UsageData? actual=null;
                    try{return await deepseek.ExtractAsync(batch,key,budget.Token,u=>actual=u);}
                    catch(ProviderException e)when(e.Retry&&attempt==0&&deadline-Environment.TickCount64>e.RetryMs+500){await Task.Delay(e.RetryMs,budget.Token);}
                    finally{if(actual is{Unknown:false})await Repository.SaveUsageAsync(new("term_budget",actual.InputTokens+actual.OutputTokens-reserve,0,false,at));}
                }
            },CommitHistoryBatchAsync);
            var result=await worker.RunAsync(progress,cancelled.Token,sessionId:sessionId,maxBatches:sessionId==null?null:5);
            await ReloadTerms();await OnActor(()=>Status(result.Note));return result;
        }
        finally{extraction=null;extractionGate.Release();Extracting?.Invoke(false);}
    }

    private async Task<ExtractionCommit> CommitHistoryBatchAsync(SessionData source,IReadOnlyList<ExtractionSlice> slices,IReadOnlyList<TermData> candidates,CancellationToken token)
    {
        await knowledgeGate.WaitAsync(token);
        try
        {
            var saved=await Repository.CommitExtractionAsync(source,slices,candidates,token);
            await OnActor(()=>
            {
                if(engine?.Session.Id!=saved.Session.Id)return;
                var current=engine.Session;
                engine.UpdateSession(current with{LearnedVersions=current.LearnedVersions.Concat(saved.Session.LearnedVersions).Distinct().ToList(),Revision=Math.Max(current.Revision,saved.Session.Revision)});
                Notify();
            });
            await ReloadTerms();return saved;
        }
        finally{knowledgeGate.Release();}
    }
}
