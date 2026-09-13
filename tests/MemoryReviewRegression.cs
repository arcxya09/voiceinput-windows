using System.Reflection;
using Microsoft.Data.Sqlite;
using RealtimeTranscription.Core;
using RealtimeTranscription.Desktop;
using RealtimeTranscription.Infrastructure;

static class MemoryReviewRegression
{
    private static void Check(bool value,string message="审查修复回归失败") { if(!value)throw new Exception(message); }
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("R02 损坏数据库降级后仍可设置和收尾内存正文",async()=>
        {
            string folder=Path.Combine(Path.GetTempPath(),"voice-review-"+JsonCodec.Id());Directory.CreateDirectory(folder);
            try
            {
                var protector=new TestProtector();new SettingsStore(folder,protector).Save(new(){LegacyEndpoint=true},new("TEST_ONLY",""));
                await File.WriteAllTextAsync(Path.Combine(folder,"sessions.db"),"corrupt database fixture");
                await using var app=new AppController(folder,protector);
                await app.InitializeAsync();Check(!app.MemoryAvailable&&app.Terms.Count==0&&app.Projects.Count==1);
                await app.SaveSettingsAsync(app.Settings with{SaveMemory=false,DictationOnly=true},app.Keys);
                Check(app.Settings.DictationOnly&&!app.Settings.SaveMemory);
                var session=new SessionData();var segment=new SegmentData{SessionId=session.Id,TaskId="offline",TaskOrder=1,SentenceId=1,RawText="数据库故障时的内存正文。",FinalText="数据库故障时的内存正文。",AsrState=AsrState.Confirmed,OutputState=OutputState.Published};
                // Seed the same in-memory engine used after an ASR final, without a microphone
                // or paid network call. All finish/setting/persistence paths remain production.
                typeof(AppController).GetMethod("NewEngine",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(app,[session,new[]{segment}]);
                await app.FinishCurrentAsync(allowPolish:false);
                Check(TranscriptText.Render(await app.SnapshotAsync())==segment.FinalText);
                Check((await app.SnapshotAsync()).Unsaved==0&&app.FailedSaveCount==0);
                bool rejected=false;try{await app.Repository.SaveProjectAsync(new("blocked","不应写入"));}catch(InvalidOperationException){rejected=true;}Check(rejected);
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(folder,true);}
        });
        await test("R02 拒绝未来schema后所有后续数据库读写仍被阻止",async()=>
        {
            string folder=Path.Combine(Path.GetTempPath(),"voice-future-"+JsonCodec.Id());Directory.CreateDirectory(folder);string path=Path.Combine(folder,"sessions.db");
            try
            {
                var protector=new TestProtector();
                await using(var seed=new MemoryRepository(path,protector)){await seed.InitializeAsync();await seed.SaveProjectAsync(new("kept","保留项目"));}
                using(var c=new SqliteConnection($"Data Source={path}")){c.Open();using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA user_version=4";cmd.ExecuteNonQuery();}
                await using(var repo=new MemoryRepository(path,protector))
                {
                    bool rejected=false;try{await repo.InitializeAsync();}catch(InvalidOperationException){rejected=true;}Check(rejected&&!repo.IsAvailable);
                    rejected=false;try{await repo.SaveProjectAsync(new("bad","拒绝"));}catch(InvalidOperationException){rejected=true;}Check(rejected);
                    rejected=false;try{await repo.ProjectsAsync();}catch(InvalidOperationException){rejected=true;}Check(rejected);
                }
                using(var c=new SqliteConnection($"Data Source={path}")){c.Open();using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA user_version";Check(Convert.ToInt32(cmd.ExecuteScalar())==4);cmd.CommandText="SELECT COUNT(*) FROM projects";Check(Convert.ToInt32(cmd.ExecuteScalar())==1);}
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(folder,true);}
        });
        await test("R17 保存保留期限立即清理历史及来源",async()=>
        {
            await using var f=await ControllerFixture.Create();var old=new SessionData{CreatedAt=DateTimeOffset.UtcNow.AddDays(-20)};
            await f.App.Repository.SaveSessionAsync(old);var recent=await f.Seed("最近记录。");
            await f.App.SaveSettingsAsync(f.App.Settings with{RetentionDays=7},f.App.Keys);
            Check(await f.App.Repository.LoadSessionAsync(old.Id)==null);
            Check(await f.App.Repository.LoadSessionAsync(recent.Session.Id)!=null);
            bool rejected=false;try{await f.App.Repository.SaveSessionAsync(old);}catch(InvalidOperationException){rejected=true;}Check(rejected,"旧写入不能复活过期记录");
        });
        await test("R17 运行期维护清理新过期记录，无需重启",async()=>
        {
            await using var f=await ControllerFixture.Create();await f.App.SaveSettingsAsync(f.App.Settings with{RetentionDays=7},f.App.Keys);
            var expired=new SessionData{CreatedAt=DateTimeOffset.UtcNow.AddDays(-8)};await f.App.Repository.SaveSessionAsync(expired);
            await f.App.MaintainMemoryAsync();Check(await f.App.Repository.LoadSessionAsync(expired.Id)==null);
        });
        await test("R17 清理非当前过期会话同步移除失败保存队列，重试不复活历史",async()=>
        {
            await using var f=await ControllerFixture.Create();
            var expired=new SessionData{CreatedAt=DateTimeOffset.UtcNow.AddDays(-20)};
            var source=new SegmentData{SessionId=expired.Id,TaskId=JsonCodec.Id(),TaskOrder=1,SentenceId=1,RawText="过期原文。",FinalText="过期原文。",AsrState=AsrState.Confirmed,OutputState=OutputState.Published,SaveState=SaveState.Saved,SourceRevision=1};
            await f.App.Repository.SaveSegmentAsync(expired,source);await f.App.LoadSessionAsync(expired);
            var failed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.App.Updated+=snapshot=>{if(snapshot.Segments.Any(s=>s.Id==source.Id&&s.SaveState==SaveState.Failed))failed.TrySetResult();};
            f.Protector.Fail=j=>j.TryGetProperty("finalText",out var value)&&value.GetString()=="过期修订保存失败。";
            await f.App.EditAsync(source.Id,"过期修订保存失败。");await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.App.FailedSaveCount>0);
            var recent=await f.Seed("近期正文。");await f.App.LoadSessionAsync(recent.Session);
            Check(f.App.FailedSaveCount>0,"切换会话不能丢弃尚未清理的保存失败");
            await f.App.SaveSettingsAsync(f.App.Settings with{RetentionDays=7},f.App.Keys);
            Check(await f.App.Repository.LoadSessionAsync(expired.Id)==null);
            Check(f.App.FailedSaveCount==0,"清理非当前来源后应立即更新持续告警");
            f.Protector.Fail=null;await f.App.RetrySaveAsync();
            var snapshot=await f.App.SnapshotAsync();
            Check(snapshot.Unsaved==0&&f.App.FailedSaveCount==0&&snapshot.Session?.Id==recent.Session.Id&&TranscriptText.Render(snapshot)=="近期正文。");
            bool rejected=false;try{await f.App.Repository.SaveSegmentAsync(expired,source);}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&await f.App.Repository.LoadSessionAsync(expired.Id)==null,"墓碑仍须拒绝迟到写入");
        });
        await test("R18 投递成功不清除真实保存失败状态，重试成功后复位",async()=>
        {
            await using var f=await ControllerFixture.Create();var source=await f.Seed("保存状态。");await f.App.LoadSessionAsync(source.Session);
            var failed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.App.Updated+=snapshot=>{if(snapshot.Segments.Any(s=>s.SaveState==SaveState.Failed))failed.TrySetResult();};
            f.Protector.Fail=j=>j.TryGetProperty("finalText",out var value)&&value.GetString()=="修订失败。";
            await f.App.EditAsync(source.Segment.Id,"修订失败。");await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.App.FailedSaveCount>0);
            await f.App.SetDeliveryAsync("Sent","已输入");Check(f.App.FailedSaveCount>0);
            f.Protector.Fail=null;await f.App.RetrySaveAsync();Check(f.App.FailedSaveCount==0);
        });
        await test("R06 轻声持续识别有进展不触发误超时",()=>
        {
            Check(!RecognitionTimeout.Stalled(20500,20500,20000,0,true,true,2500));
            Check(!RecognitionTimeout.Stalled(90000,90000,89900,0,true,true,2500));
            Check(RecognitionTimeout.Stalled(30000,29900,0,0,true,true,2500));
            Check(RecognitionTimeout.Stalled(30000,0,0,0,false,false,2500));
            return Task.CompletedTask;
        });
        await test("R05 起录前失败形成空会话时可正常结束，不调用润色",async()=>
        {
            await using var f=await ControllerFixture.Create();
            typeof(AppController).GetMethod("NewEngine",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(f.App,[new SessionData(),null]);
            await f.App.FinishCurrentAsync();Check(TranscriptText.Render(await f.App.SnapshotAsync())==""&&f.Calls==0);
        });
    }
}
