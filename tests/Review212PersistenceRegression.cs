using RealtimeTranscription.Core;

static class Review212PersistenceRegression
{
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}

    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("2.1.2 切换项目后的迟到保存失败立即更新全局告警，重试恢复原会话",async()=>
        {
            await using var f=await ControllerFixture.Create();
            await f.App.Repository.SaveProjectAsync(new("other","其他项目"));
            var source=await f.Seed("原来保存的正文。");
            await f.App.LoadSessionAsync(source.Session);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var warning=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release=new ManualResetEventSlim(false);
            int updates=0;
            f.App.Updated+=snapshot=>
            {
                Interlocked.Increment(ref updates);
                if(snapshot.Session==null&&f.App.FailedSaveCount==1)warning.TrySetResult();
            };
            f.Protector.Fail=json=>
            {
                if(!json.TryGetProperty("finalText",out var text)||text.GetString()!="切换前尚未保存的修订。")return false;
                entered.TrySetResult();
                if(!release.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException("测试未释放延迟写入");
                return true;
            };
            try
            {
                await f.App.EditAsync(source.Segment.Id,"切换前尚未保存的修订。");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await f.App.ChangeProjectAsync("other");
                Check(f.App.FailedSaveCount==0,"失败前不应虚报保存失败");
                int before=Volatile.Read(ref updates);
                release.Set();
                // No SnapshotAsync here: the save receipt itself must update the
                // same counter and notification consumed by the UI/exit guard.
                await warning.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.App.FailedSaveCount==1&&Volatile.Read(ref updates)>before,"旧会话失败必须立即通知当前界面");
                var saved=await f.App.Repository.LoadSessionAsync(source.Session.Id);
                Check(saved!.Segments.Single().FinalText=="原来保存的正文。","故障写入应保持旧磁盘正文");
                f.Protector.Fail=null;
                await f.App.RetrySaveAsync();
                Check(f.App.FailedSaveCount==0,"重试成功应清除全局告警");
                saved=await f.App.Repository.LoadSessionAsync(source.Session.Id);
                Check(saved!.Segments.Single().FinalText=="切换前尚未保存的修订。"&&f.App.Settings.ProjectId=="other","重试必须保存到原会话且不切换当前项目");
            }
            finally{release.Set();f.Protector.Fail=null;}
        });

        await test("2.1.2 无界面订阅时保存失败计数仍保持最新",async()=>
        {
            await using var f=await ControllerFixture.Create();
            var source=await f.Seed("无订阅者原文。");await f.App.LoadSessionAsync(source.Session);
            f.Protector.Fail=json=>json.TryGetProperty("finalText",out var text)&&text.GetString()=="写入失败。";
            await f.App.EditAsync(source.Segment.Id,"写入失败。");
            long deadline=Environment.TickCount64+5000;
            while(f.App.FailedSaveCount==0&&Environment.TickCount64<deadline)await Task.Delay(10);
            Check(f.App.FailedSaveCount==1,"应用级保存失败计数不应依赖Updated订阅或主动拉取快照");
            f.Protector.Fail=null;await f.App.RetrySaveAsync();
            Check(f.App.FailedSaveCount==0,"恢复成功后计数应清零");
        });
    }
}
