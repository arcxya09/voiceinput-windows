using RealtimeTranscription.Core;

internal static class FocusRegression
{
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        static void Check(bool value) { if(!value)throw new Exception("Focus regression assertion failed"); }
        await test("焦点采样短暂不可用后恢复，不取消原输入",()=>
        {
            var focus=new FocusContinuity();
            Check(focus.Observe(FocusObservation.Stable,0));
            Check(focus.Observe(FocusObservation.Unavailable,100));
            Check(focus.Observe(FocusObservation.Unavailable,599));
            Check(focus.Observe(FocusObservation.Stable,600));
            Check(focus.Observe(FocusObservation.Unavailable,900));
            return Task.CompletedTask;
        });
        await test("持续无法读取焦点达到边界后停止，恢复也不自动重发",()=>
        {
            var focus=new FocusContinuity();
            Check(focus.Observe(FocusObservation.Unavailable,0));
            Check(!focus.Observe(FocusObservation.Unavailable,500));
            Check(!focus.Observe(FocusObservation.Stable,501));
            return Task.CompletedTask;
        });
        await test("明确切换窗口或控件立即取消，切回也不恢复投递",()=>
        {
            var focus=new FocusContinuity();
            Check(focus.Observe(FocusObservation.Unavailable,0));
            Check(!focus.Observe(FocusObservation.Changed,1));
            Check(!focus.Observe(FocusObservation.Stable,2));
            return Task.CompletedTask;
        });
        await test("物理活动入队前使旧轮次失效，下一轮可重新捕获",()=>
        {
            var activity=new InputActivityVersion();long first=activity.Current;
            Check(activity.Matches(first));activity.Advance();
            Check(!activity.Matches(first));long next=activity.Current;Check(activity.Matches(next));
            activity.Advance();Check(!activity.Matches(next));
            return Task.CompletedTask;
        });
        await test("并发物理活动不丢失失效标记",async()=>
        {
            var activity=new InputActivityVersion();
            await Task.WhenAll(Enumerable.Range(0,100).Select(_=>Task.Run(activity.Advance)));
            Check(activity.Current==100&&!activity.Matches(0));
        });
        await test("辅助功能慢响应在容许期限内完成，不再使用三百毫秒门限",async()=>
        {
            var query=new BoundedInputQuery();
            var result=await query.RunAsync(()=>{Thread.Sleep(400);return "ready";},TimeSpan.FromSeconds(2));
            Check(result=="ready");
        });
        await test("辅助功能超时不释放仍运行的调用，也不创建并行查询",async()=>
        {
            var query=new BoundedInputQuery();using var release=new ManualResetEventSlim();
            var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls=0;
            try
            {
                var first=await query.RunAsync(()=>{Interlocked.Increment(ref calls);try{release.Wait();return "late";}finally{exited.TrySetResult();}},TimeSpan.FromMilliseconds(100));
                Check(first==null);
                var second=await query.RunAsync(()=>{Interlocked.Increment(ref calls);return "unsafe";},TimeSpan.FromMilliseconds(100));
                Check(second==null&&calls==1);
            }
            finally{release.Set();await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));}
            Check(await query.RunAsync(()=>"recovered",TimeSpan.FromSeconds(2))=="recovered");
        });
        await test("取消辅助功能等待不会丢失门锁，后续查询仍受限",async()=>
        {
            var query=new BoundedInputQuery();using var release=new ManualResetEventSlim();using var cancel=new CancellationTokenSource();
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var pending=query.RunAsync(()=>{entered.TrySetResult();try{release.Wait();return "late";}finally{exited.TrySetResult();}},TimeSpan.FromSeconds(5),cancel.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));cancel.Cancel();Check(await pending==null);
                bool ran=false;Check(await query.RunAsync(()=>{ran=true;return "unexpected";},TimeSpan.FromMilliseconds(100))==null&&!ran);
            }
            finally{release.Set();await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));}
            Check(await query.RunAsync(()=>"ready",TimeSpan.FromSeconds(2))=="ready");
        });
        await test("辅助功能异常不泄漏门锁，已取消请求不调用接口",async()=>
        {
            var query=new BoundedInputQuery();
            Check(await query.RunAsync<string>(()=>throw new InvalidOperationException(),TimeSpan.FromSeconds(1))==null);
            using var cancel=new CancellationTokenSource();cancel.Cancel();bool ran=false;
            Check(await query.RunAsync(()=>{ran=true;return "unexpected";},TimeSpan.FromSeconds(1),cancel.Token)==null&&!ran);
            Check(await query.RunAsync(()=>"ready",TimeSpan.FromSeconds(1))=="ready");
        });
    }
}
