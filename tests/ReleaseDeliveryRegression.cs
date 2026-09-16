using RealtimeTranscription.Core;

internal static class ReleaseDeliveryRegression
{
    private static void Check(bool value,string message="2.1.6 regression failed")
    {if(!value)throw new Exception(message);}
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("2.1.6 松手后 Enter 单次加速，重复与跨投递 key-up 不泄漏",()=>
        {
            var guard=new ReleaseEnterGuard();
            Check(guard.Handle(true,false,true,false,out bool expedite)&&expedite);
            Check(guard.Handle(true,false,true,false,out expedite)&&!expedite);
            Check(guard.Handle(true,false,false,false,out expedite)&&!expedite);
            Check(guard.Handle(false,true,false,false,out expedite)&&!expedite);
            Check(!guard.Handle(true,false,false,false,out expedite)&&!expedite);
            Check(!guard.Handle(false,true,false,false,out expedite));
            return Task.CompletedTask;
        });
        await test("2.1.6 录音期间、组合回车与先前按住回车不被接管",()=>
        {
            foreach(bool modified in new[]{false,true})
            {
                var guard=new ReleaseEnterGuard();
                Check(!guard.Handle(true,false,modified,modified,out bool expedite)&&!expedite);
                Check(!guard.Handle(true,false,true,false,out expedite)&&!expedite);
                Check(!guard.Handle(false,true,true,false,out expedite)&&!expedite);
                Check(guard.Handle(true,false,true,false,out expedite)&&expedite);
            }
            return Task.CompletedTask;
        });
        await test("2.1.6 快速上屏默认启用且设置可持久化",async()=>
        {
            await using var f=await ControllerFixture.Create();Check(f.App.Settings.PreferFastDelivery);
            await f.App.SaveSettingsAsync(f.App.Settings with{PreferFastDelivery=false},f.App.Keys);
            await f.Reopen();Check(!f.App.Settings.PreferFastDelivery);
        });
        await test("2.1.6 回车先到时不请求润色且保留完整尾句",async()=>
        {
            await using var f=await ControllerFixture.Create();
            string raw="测试一下这次的输入是否准确。看起来没有什么问题。";
            var seed=await f.Seed(raw);await f.App.LoadSessionAsync(seed.Session);
            using var expedite=new CancellationTokenSource();expedite.Cancel();
            await f.App.FinishCurrentAsync(seed.Session.Id,forDelivery:true,expedite:expedite.Token);
            Check(f.Calls==0&&TranscriptText.Render(await f.App.SnapshotAsync())==raw);
        });
        await test("2.1.6 快速上屏限制润色等待且不截掉正文",async()=>
        {
            await using var f=await ControllerFixture.Create(async(_,token)=>
            {await Task.Delay(10000,token);return ControllerFixture.Reply("不应采用的迟到结果。");});
            string raw="测试一下这次的输入是否准确。看起来没有什么问题。";
            var seed=await f.Seed(raw);await f.App.LoadSessionAsync(seed.Session);
            var watch=System.Diagnostics.Stopwatch.StartNew();
            await f.App.FinishCurrentAsync(seed.Session.Id,forDelivery:true).WaitAsync(TimeSpan.FromSeconds(8));
            Check(watch.Elapsed>=TimeSpan.FromMilliseconds(650)&&watch.Elapsed<TimeSpan.FromSeconds(2)&&f.Calls==1&&TranscriptText.Render(await f.App.SnapshotAsync())==raw);
            Check((await f.App.SnapshotAsync()).Session!.WholePolishState=="Fallback");
        });
        await test("2.1.6 关闭快速模式后仍可用回车取消润色而不取消投递",async()=>
        {
            var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var f=await ControllerFixture.Create(async(_,token)=>
            {started.TrySetResult();await Task.Delay(10000,token);return ControllerFixture.Reply("迟到回复。");});
            await f.App.SaveSettingsAsync(f.App.Settings with{PreferFastDelivery=false},f.App.Keys);
            var seed=await f.Seed("感觉好像就不太准确。");await f.App.LoadSessionAsync(seed.Session);
            using var expedite=new CancellationTokenSource();
            var running=f.App.FinishCurrentAsync(seed.Session.Id,forDelivery:true,expedite:expedite.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));expedite.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(3));
            Check(TranscriptText.Render(await f.App.SnapshotAsync())==seed.Segment.RawText);
        });
        await test("2.1.12 不响应取消的润色有界返回，迟到结果不覆盖新会话",async()=>
        {
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var f=await ControllerFixture.Create(async(_,_)=>
            {entered.TrySetResult();await release.Task;return ControllerFixture.Reply("迟到文本不应生效。");});
            string raw="完整的第一句。完整的第二句。";
            var seed=await f.Seed(raw);await f.App.LoadSessionAsync(seed.Session);
            var running=f.App.FinishCurrentAsync(seed.Session.Id,forDelivery:true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(2));
                Check(TranscriptText.Render(await f.App.SnapshotAsync())==raw,"超时丢失完整正文");
                var next=await f.Seed("下一轮正文。");await f.App.LoadSessionAsync(next.Session);
                release.TrySetResult();
                string export=Path.Combine(Path.GetTempPath(),"VoiceInput-late-"+Guid.NewGuid().ToString("N")+".log");
                try
                {
                    using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    string logs;
                    do{await f.App.Log.ExportAsync(export,budget.Token);logs=File.ReadAllText(export);if(!logs.Contains("LatePolishDiscarded"))await Task.Delay(20,budget.Token);}
                    while(!logs.Contains("LatePolishDiscarded"));
                    Check(logs.Contains("TextProcessingTimedOut")&&!logs.Contains("TextProcessingFailed"),"等待预算误记为真实故障");
                    Check(TranscriptText.Render(await f.App.SnapshotAsync())=="下一轮正文。","迟到结果污染新会话");
                }
                finally{File.Delete(export);}
            }
            finally{release.TrySetResult();}
        });
        await test("2.1.12 完整润色优先允许超过800毫秒，并保留完整回复",async()=>
        {
            await using var f=await ControllerFixture.Create(async(_,token)=>
            {await Task.Delay(1050,token);return ControllerFixture.Reply("这个方案我们先试一下。");});
            await f.App.SaveSettingsAsync(f.App.Settings with{PreferFastDelivery=false},f.App.Keys);
            var seed=await f.Seed("嗯，这个方案我们先试一下。");await f.App.LoadSessionAsync(seed.Session);
            await f.App.FinishCurrentAsync(seed.Session.Id,forDelivery:true).WaitAsync(TimeSpan.FromSeconds(5));
            Check((await f.App.SnapshotAsync()).Session!.WholePolishState=="Completed","关闭快速模式仍受800毫秒限制");
            Check(TranscriptText.Render(await f.App.SnapshotAsync())=="这个方案我们先试一下。","完整润色回复未采用");
        });
    }
}
