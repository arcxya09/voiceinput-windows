using System.Threading.Channels;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Desktop.Input;

public sealed class PushToTalkService : IAsyncDisposable
{
    private sealed class Turn(long at,NativeTarget native)
    {
        public readonly string Id=JsonCodec.Id();
        public long PressedAt=at,ReleasedAt;
        public NativeTarget Native=native;
        public readonly CancellationTokenSource Cancel=new();
        public readonly TaskCompletionSource Released=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Stop;
        public volatile bool Invalid;
        public string Reason="";
    }
    private readonly AppController controller;
    private readonly PhysicalHook hook;
    private readonly Channel<PhysicalSignal> signals=Channel.CreateBounded<PhysicalSignal>(128);
    private readonly CancellationTokenSource lifetime=new();
    private readonly Task loop,watchdog;
    private Turn? active;
    private Task? running;
    private bool disposed;
    public bool Enabled {get;private set;}=true;
    public bool Busy=>Volatile.Read(ref active)!=null;
    public event Action<string>? Notice;
    public event Action<bool>? Listening;
    public PushToTalkService(AppController controller)
    {
        this.controller=controller;controller.InputInterrupted+=Cancel;hook=new PhysicalHook(s=>{if(!signals.Writer.TryWrite(s)){Volatile.Read(ref active)?.Cancel.Cancel();controller.RequestStopCapture();}});
        loop=Task.Run(Loop);watchdog=Task.Run(Watchdog);
    }
    public async Task InitializeAsync(){await hook.Ready;Configure();}
    public void Configure()=>hook.Configure(controller.Settings.Hotkey);
    public void SetEnabled(bool value){Enabled=value;hook.Enable(value);Notice?.Invoke(value?"按住说话已启用。":"按住说话已暂停。");}
    public void Cancel(string reason="本轮输入已取消，确认文字已保留。")=>signals.Writer.TryWrite(new("cancel:"+reason,At:Environment.TickCount64));
    private async Task Loop()
    {
        try
        {
            await foreach(var s in signals.Reader.ReadAllAsync(lifetime.Token))
            {
                if(s.Kind=="down")
                {
                    if(!Enabled)continue;
                    if(active!=null){Notice?.Invoke("上一段仍在整理，请稍后再按。");continue;}
                    var native=Win32.Current();
                    if(native==null){Notice?.Invoke("请先把光标放到其他应用的文本框中。");continue;}
                    if(new[]{0xA0,0xA1,0xA2,0xA4,0xA5,0x5b,0x5c}.Any(Win32.Down)){Notice?.Invoke("检测到组合键，本次不启动语音。");continue;}
                    var t=new Turn(s.At,native);active=t;Listening?.Invoke(true);running=Run(t);
                }
                else if(active is {} t)
                {
                    if(s.Kind=="up")
                    {
                        if(t.ReleasedAt!=0)continue;
                        t.ReleasedAt=s.At;controller.RequestStopCapture();Listening?.Invoke(false);
                        if(s.At-t.PressedAt<controller.Settings.HoldMs){t.Invalid=true;t.Reason="短按已取消。";t.Cancel.Cancel();}
                        t.Stop=controller.StopAsync(false);t.Released.TrySetResult();
                    }
                    else if(s.Kind is "activity" or "escape" or "cancel"||s.Kind.StartsWith("cancel:"))
                    {
                        if(t.Invalid)continue; // Keep the first reason; cleanup/late events cannot hide the failure.
                        t.Invalid=true;t.Reason=s.Kind.StartsWith("cancel:")?s.Kind[7..]:s.Kind=="activity"?"检测到其他按键或鼠标操作，本轮改为手动复制。":"本轮输入已取消。";
                        t.Cancel.Cancel();controller.RequestStopCapture();t.Stop??=controller.StopAsync(false);t.Released.TrySetResult();Listening?.Invoke(false);
                    }
                }
            }
        }
        catch(OperationCanceledException){}
    }
    private async Task Run(Turn t)
    {
        try
        {
            var targetTask=TextDelivery.CaptureAsync(t.Native);
            var readyTask=targetTask.ContinueWith(x=>x.Status==TaskStatus.RanToCompletion&&x.Result!=null,TaskScheduler.Default);
            bool started=await controller.StartAsync(t.PressedAt,t.Cancel.Token,readyTask,t.Id);
            var target=await targetTask;
            if(target==null){t.Invalid=true;t.Reason="无法确认可编辑的输入位置，未上传语音。请使用支持的文本框。";t.Cancel.Cancel();t.Released.TrySetResult();}
            if(!started){t.Invalid=true;if(t.Reason.Length==0)t.Reason=(await controller.SnapshotAsync()).Status;t.Released.TrySetResult();}
            await t.Released.Task.WaitAsync(TimeSpan.FromSeconds(controller.Settings.MaxHoldSeconds+2));
            t.Stop??=controller.StopAsync(false);await t.Stop;
            await controller.FinishCurrentAsync(t.Id,!t.Invalid,t.Cancel.Token);
            var snapshot=await controller.SnapshotAsync();if(snapshot.Session?.Id!=t.Id){Notice?.Invoke(t.Reason.Length>0?t.Reason:snapshot.Status);return;}string text=TranscriptText.Render(snapshot);
            DeliveryResult result;
            if(t.Invalid||t.Cancel.IsCancellationRequested)result=new("Cancelled",t.Reason.Length>0?t.Reason:"本轮输入已取消，结果已保留。");
            else if(target==null||snapshot.State==CaptureState.Faulted||snapshot.Session?.Gaps.Count>0||snapshot.Segments.Any(s=>s.AsrState==AsrState.Unresolved))result=new("Blocked","识别未完整结束，确认文字可手动复制。");
            else if(text.Length==0)result=new("Empty","没有需要输入的文字。");
            else
            {
                await controller.SetDeliveryAsync("Sending","正在输入…",turnId:t.Id);
                result=await TextDelivery.SendAsync(target,text,()=>!t.Invalid&&ReferenceEquals(active,t),t.Cancel.Token);
            }
            await controller.SetDeliveryAsync(result.State,result.Message,result.Accepted,t.Id);Notice?.Invoke(result.Message);
        }
        catch(Exception e){await controller.StopAsync(false);await controller.SetDeliveryAsync("Blocked",AppController.SafeError(e),turnId:t.Id);Notice?.Invoke(AppController.SafeError(e));}
        finally{Listening?.Invoke(false);Interlocked.CompareExchange(ref active,null,t);t.Cancel.Dispose();}
    }
    private async Task Watchdog()
    {
        try
        {
            using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while(await timer.WaitForNextTickAsync(lifetime.Token))
            {
                var t=Volatile.Read(ref active);if(t==null||t.Invalid)continue;
                if(Win32.Current()!=t.Native){Cancel("输入焦点已改变，确认文字可手动复制。");continue;}
                if(t.ReleasedAt==0&&Environment.TickCount64-t.PressedAt>controller.Settings.MaxHoldSeconds*1000L)Cancel("已达到最长录音时间，确认文字可手动复制。");
                else if(t.ReleasedAt==0&&Environment.TickCount64-t.PressedAt>300&&!Win32.Down(hook.Trigger))Cancel("按键释放事件未完整到达，已停止录音，结果可手动复制。");
            }
        }
        catch(OperationCanceledException){}
    }
    public async ValueTask DisposeAsync()
    {
        if(disposed)return;disposed=true;hook.Enable(false);Cancel("程序退出，自动输入已取消。");
        try{if(running!=null)await running.WaitAsync(TimeSpan.FromSeconds(10));}catch{}
        controller.InputInterrupted-=Cancel;hook.Dispose();lifetime.Cancel();signals.Writer.TryComplete();try{await Task.WhenAll(loop,watchdog);}catch{}lifetime.Dispose();
    }
}
