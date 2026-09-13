using RealtimeTranscription.Core;

internal static class InputReviewRegression
{
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        static void Check(bool value) { if(!value)throw new Exception("Input review regression assertion failed"); }
        await test("启动焦点短暂不可用后在原窗口恢复",async()=>
        {
            int samples=0;
            string? result=await InputSafety.RecoverInitialFocusAsync(()=>++samples<3?(FocusObservation.Unavailable,(string?)null):(FocusObservation.Stable,"original"),()=>true,CancellationToken.None,250);
            Check(result=="original"&&samples==3);
        });
        await test("启动焦点明确改变立即拒绝，不接受后来恢复的窗口",async()=>
        {
            int samples=0;
            string? result=await InputSafety.RecoverInitialFocusAsync(()=>++samples==1?(FocusObservation.Changed,(string?)null):(FocusObservation.Stable,"other"),()=>true,CancellationToken.None,250);
            Check(result==null&&samples==1);
        });
        await test("启动焦点恢复遵守活动取消和有界期限",async()=>
        {
            int calls=0;
            Check(await InputSafety.RecoverInitialFocusAsync(()=>{calls++;return (FocusObservation.Stable,"unsafe");},()=>false,CancellationToken.None)==null&&calls==0);
            Check(await InputSafety.RecoverInitialFocusAsync<string>(()=>(FocusObservation.Unavailable,null),()=>true,CancellationToken.None,30)==null);
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            Check(await InputSafety.RecoverInitialFocusAsync(()=>{calls++;return (FocusObservation.Stable,"unsafe");},()=>true,cancel.Token)==null&&calls==0);
        });
        await test("F8和F9拒绝右Ctrl组合，右Ctrl触发键只豁免自身",()=>
        {
            Check(InputSafety.HasOtherModifier(0x77,key=>key==0xA3));
            Check(InputSafety.HasOtherModifier(0x78,key=>key==0xA3));
            Check(!InputSafety.HasOtherModifier(0xA3,key=>key==0xA3));
            Check(InputSafety.HasOtherModifier(0xA3,key=>key is 0xA2 or 0xA3));
            Check(InputSafety.HasOtherModifier(0x77,key=>key==0xA5));
            return Task.CompletedTask;
        });
        await test("长文本预算包含慢辅助功能校验并保持总量有界",()=>
        {
            long batches=(5000L+InputSafety.BatchCharacters-1)/InputSafety.BatchCharacters;
            Check(InputSafety.DeliveryBudgetMilliseconds(5000)>batches*400);
            Check(InputSafety.DeliveryBudgetMilliseconds(5000)>batches*3760);
            Check(InputSafety.DeliveryBudgetMilliseconds(20000)>InputSafety.DeliveryBudgetMilliseconds(5000));
            Check(InputSafety.DeliveryBudgetMilliseconds(int.MaxValue)==600000);
            Check(InputSafety.DeliveryBudgetMilliseconds(0)==2000);
            return Task.CompletedTask;
        });
        await test("挂起辅助功能工作进程先终止再重启，旧选区令牌不能复用",async()=>
        {
            var hung=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var first=new FakeWorker((request,_)=>request=="capture"?Task.FromResult<string?>("range"):hung.Task);
            var second=new FakeWorker((_,_)=>Task.FromResult<string?>("new range"));
            int created=0;
            await using var query=new IsolatedInputQuery(()=>++created==1?first:second);
            var original=await query.RunAsync("capture",TimeSpan.FromSeconds(1));Check(original!=null);
            Check(await query.RunAsync("validate",TimeSpan.FromMilliseconds(30),expectedGeneration:original!.Generation)==null);
            Check(first.Stops==1&&first.Disposed);
            // Validation must not create a new process or accept a freshly captured caret.
            Check(await query.RunAsync("validate",TimeSpan.FromSeconds(1),expectedGeneration:original.Generation)==null&&created==1);
            var next=await query.RunAsync("capture",TimeSpan.FromSeconds(1));
            Check(next!=null&&created==2&&next.Generation!=original.Generation);
            Check(await query.RunAsync("validate",TimeSpan.FromSeconds(1),expectedGeneration:original.Generation)==null&&second.Calls==1);
            hung.TrySetResult(null);
        });
        await test("辅助功能进程无法确认退出时不创建更多进程",async()=>
        {
            var hung=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var first=new FakeWorker((_,_)=>hung.Task){CanStop=false};
            int created=0;
            await using var query=new IsolatedInputQuery(()=>{created++;return created==1?first:new FakeWorker((_,_)=>Task.FromResult<string?>("recovered"));});
            Check(await query.RunAsync("capture",TimeSpan.FromMilliseconds(30))==null);
            Check(await query.RunAsync("capture",TimeSpan.FromMilliseconds(30))==null&&created==1&&first.Calls==1);
            first.CanStop=true;
            Check((await query.RunAsync("capture",TimeSpan.FromSeconds(1)))?.Value=="recovered"&&created==2&&first.Disposed);
            hung.TrySetResult(null);
        });
        await test("取消挂起的辅助功能查询后下一轮能够重新捕获",async()=>
        {
            var hung=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first=new FakeWorker((_,_)=>{entered.TrySetResult();return hung.Task;});int created=0;
            await using var query=new IsolatedInputQuery(()=>++created==1?first:new FakeWorker((_,_)=>Task.FromResult<string?>("ready")));
            using var cancel=new CancellationTokenSource();
            var waiting=query.RunAsync("capture",TimeSpan.FromSeconds(2),cancel.Token);
            await entered.Task;cancel.Cancel();Check(await waiting==null&&first.Disposed);
            Check((await query.RunAsync("capture",TimeSpan.FromSeconds(1)))?.Value=="ready"&&created==2);
            hung.TrySetResult(null);
        });
        await test("辅助功能并发请求串行运行且共用一个健康进程",async()=>
        {
            int created=0,active=0,max=0;
            await using var query=new IsolatedInputQuery(()=>{created++;return new FakeWorker(async(request,_)=>
            {int count=Interlocked.Increment(ref active);max=Math.Max(max,count);await Task.Delay(20);Interlocked.Decrement(ref active);return request;});});
            var replies=await Task.WhenAll(Enumerable.Range(0,4).Select(n=>query.RunAsync(n.ToString(),TimeSpan.FromSeconds(2))));
            Check(created==1&&max==1&&replies.All(x=>x!=null)&&replies.Select(x=>x!.Generation).Distinct().Count()==1);
        });
    }
    private sealed class FakeWorker(Func<string,CancellationToken,Task<string?>> reply):IIsolatedInputWorker
    {
        public bool CanStop=true,Disposed;
        public int Calls,Stops;
        public Task<string?> QueryAsync(string request,CancellationToken token){Calls++;return reply(request,token);}
        public Task<bool> StopAsync(){Stops++;return Task.FromResult(CanStop);}
        public ValueTask DisposeAsync(){Disposed=true;return ValueTask.CompletedTask;}
    }
}
