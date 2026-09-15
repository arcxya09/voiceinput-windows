using System.Threading.Channels;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Desktop.Input;

public sealed class PushToTalkService : IAsyncDisposable, IVoicePreviewEvents
{
    private sealed class Turn(long at,NativeTarget? native,long activityVersion,bool dictationOnly)
    {
        public readonly string Id=JsonCodec.Id();
        public long PressedAt=at,ReleasedAt;
        public volatile NativeTarget? Native=native;
        public readonly bool DictationOnly=dictationOnly;
        public readonly long ActivityVersion=activityVersion;
        public readonly RecordingFocusContinuity Focus=new();
        public volatile bool TargetCaptured,ManualDelivery;
        public string ManualReason="",EndState="Cancelled";
        public void UseManualDelivery(string reason){ManualReason=reason;ManualDelivery=true;}
        public readonly CancellationTokenSource Cancel=new();
        public readonly CancellationTokenSource Expedite=new();
        public volatile bool PhysicalReleased,DeliveryDispatched;
        public void ExpediteInput(){try{Expedite.Cancel();}catch(ObjectDisposedException){}}
        public readonly TaskCompletionSource Released=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Stop;
        public volatile bool Invalid;
        public string Reason="";
        public void CancelInput(){try{Cancel.Cancel();}catch(ObjectDisposedException){}}
    }
    private readonly AppController controller;
    private readonly InputActivityVersion activity=new();
    private readonly PhysicalHook hook;
    private readonly Channel<PhysicalSignal> signals=Channel.CreateBounded<PhysicalSignal>(128);
    private readonly CancellationTokenSource lifetime=new();
    private readonly Task loop,watchdog;
    private Turn? active;
    private Task? running;
    private bool disposed;
    public bool Enabled {get;private set;}=true;
    public Func<bool>? CanStart { get; set; }
    public bool Busy=>Volatile.Read(ref active)!=null;
    public bool DictationOnly=>controller.Settings.DictationOnly;
    public event Action<VoiceInputNotice>? Notice;
    public event Action<string>? TurnStarted;
    public event Action<VoiceTurnCompletion>? TurnCompleted;
    public event Action<bool>? Listening;
    public PushToTalkService(AppController controller)
    {
        this.controller=controller;controller.InputInterrupted+=Cancel;hook=new PhysicalHook(s=>
        {
            var current=Volatile.Read(ref active);
            if(s.Kind=="up"&&current!=null&&s.At-current.PressedAt>=controller.Settings.HoldMs)current.PhysicalReleased=true;
            if(s.Kind=="expedite"){current?.ExpediteInput();return;}
            // Invalidate before queuing, so SendInput cannot race the channel consumer.
            if(s.Kind is "activity" or "escape" or "cancel")activity.Advance();
            s=s with{ActivityVersion=activity.Current};
            if(!signals.Writer.TryWrite(s)){activity.Advance();controller.RequestStopCapture();}
        },()=>Volatile.Read(ref active) is {PhysicalReleased:true,DeliveryDispatched:false,Invalid:false,DictationOnly:false});
        loop=Task.Run(Loop);watchdog=Task.Run(Watchdog);
    }
    public async Task InitializeAsync(){await hook.Ready;Configure();await TextDelivery.InitializeAsync();}
    public void Configure()=>hook.Configure(controller.Settings.Hotkey);
    public void SetEnabled(bool value){Enabled=value;hook.Enable(value);PublishNotice(value?"按住说话已启用。":"按住说话已暂停。");}
    public void Cancel(string reason="本轮输入已取消，确认文字已保留。")
    {
        activity.Advance();
        signals.Writer.TryWrite(new("cancel:"+reason,At:Environment.TickCount64));
    }
    private async Task Loop()
    {
        try
        {
            await foreach(var s in signals.Reader.ReadAllAsync(lifetime.Token))
            {
                if(s.Kind=="down")
                {
                    if(!Enabled||CanStart?.Invoke()==false)continue;
                    if(active!=null){PublishNotice("上一段仍在整理，请稍后再按。");continue;}
                    var native=s.Target;bool dictationOnly=DictationOnly;
                    if(InputSafety.HasOtherModifier(hook.Trigger,Win32.Down)){PublishNotice("检测到组合键，本次不启动语音。");continue;}
                    if(!activity.Matches(s.ActivityVersion)){PublishNotice("按键后输入位置或操作已改变，请重新按住说话。");continue;}
                    var t=new Turn(s.At,native,s.ActivityVersion,dictationOnly);active=t;TurnStarted?.Invoke(t.Id);Listening?.Invoke(true);running=Run(t);
                }
                else if(active is {} t)
                {
                    if(s.Kind=="up")
                    {
                        if(t.ReleasedAt!=0)continue;
                        t.ReleasedAt=s.At;controller.RequestStopCapture();Listening?.Invoke(false);
                        if(s.At-t.PressedAt<controller.Settings.HoldMs){t.Invalid=true;t.Reason="短按已取消。";t.CancelInput();}
                        t.Stop=controller.StopAsync(false);t.Released.TrySetResult();
                    }
                    else if(s.Kind is "activity" or "escape" or "cancel"||s.Kind.StartsWith("cancel:"))
                    {
                        if(t.Invalid)continue; // Keep the first reason; cleanup/late events cannot hide the failure.
                        t.Invalid=true;t.Reason=s.Kind.StartsWith("cancel:")?s.Kind[7..]:s.Kind=="activity"?"检测到其他按键或鼠标操作，本轮改为手动复制。":"本轮输入已取消。";
                        t.CancelInput();controller.RequestStopCapture();t.Stop??=controller.StopAsync(false);t.Released.TrySetResult();Listening?.Invoke(false);
                    }
                }
            }
        }
        catch(OperationCanceledException){}
    }
    private async Task<TargetCapture> CaptureTarget(Turn t)
    {
        if(t.DictationOnly)return new(null,"Dictation","");
        TargetCapture Manual(string reason)
        {
            t.UseManualDelivery(reason);
            return new(null,"DictationFallback",reason);
        }
        if(t.Native is not {} anchor)return Manual("未检测到可靠的输入焦点，已继续听写；完成后请手动复制。");
        var native=await InputSafety.RecoverInitialFocusAsync(() =>
        {
            if(Win32.ObserveWindow(anchor)==FocusObservation.Changed)return (FocusObservation.Changed,(NativeTarget?)null);
            var current=Win32.Current();
            if(current==null)return (FocusObservation.Unavailable,(NativeTarget?)null);
            if(current.Window!=anchor.Window||current.Thread!=anchor.Thread||current.Process!=anchor.Process)
                return (FocusObservation.Changed,(NativeTarget?)null);
            if(anchor.Focus!=IntPtr.Zero&&current.Focus!=anchor.Focus)return (FocusObservation.Unavailable,(NativeTarget?)null);
            return (FocusObservation.Stable,(NativeTarget?)current);
        },()=>activity.Matches(t.ActivityVersion)&&!t.Invalid,t.Cancel.Token,1000);
        if(native==null)return Manual("输入位置暂不可用，已继续听写；完成后请手动复制。");
        t.Native=native;
        var result=await TextDelivery.CaptureAsync(native,()=>activity.Matches(t.ActivityVersion)&&!t.Invalid,t.Cancel.Token);
        if(result.Target!=null){t.TargetCaptured=true;return result;}
        if(result.Code=="Password")return result;
        t.Cancel.Token.ThrowIfCancellationRequested();
        return Manual("当前软件未提供可靠的编辑控件信息，已继续听写；完成后请手动复制。");
    }
    private async Task Run(Turn t)
    {
        InputTarget? capturedTarget=null;
        try
        {
            var targetTask=CaptureTarget(t);
            var readyTask=targetTask.ContinueWith(x=>x.Status==TaskStatus.RanToCompletion&&(t.DictationOnly||x.Result.Target!=null||x.Result.Code=="DictationFallback")&&activity.Matches(t.ActivityVersion)&&!t.Invalid,TaskScheduler.Default);
            bool started=await controller.StartAsync(t.PressedAt,t.Cancel.Token,readyTask,t.Id);
            var capture=await targetTask;var target=capture.Target;capturedTarget=target;
            if(target==null&&!t.DictationOnly&&!t.ManualDelivery){if(!t.Invalid)t.EndState="StartFailed";t.Invalid=true;if(t.Reason.Length==0)t.Reason=capture.Message;t.CancelInput();t.Released.TrySetResult();}
            if(!started){if(!t.Invalid)t.EndState="StartFailed";t.Invalid=true;if(t.Reason.Length==0)t.Reason=(await controller.SnapshotAsync()).Status;t.Released.TrySetResult();}
            if(started&&t.ManualDelivery)await controller.SetDeliveryAsync("Pending",t.ManualReason,turnId:t.Id);
            await t.Released.Task.WaitAsync(TimeSpan.FromSeconds(controller.Settings.MaxHoldSeconds+2));
            t.Stop??=controller.StopAsync(false);await t.Stop;
            await controller.FinishCurrentAsync(t.Id,!t.Invalid,t.Cancel.Token,forDelivery:!t.DictationOnly,expedite:t.Expedite.Token);
            var snapshot=await controller.SnapshotAsync();if(snapshot.Session?.Id!=t.Id){await CompletePreviewAsync(t,t.Reason.Length>0?t.Reason:snapshot.Status);return;}string text=TranscriptText.Render(snapshot);
            DeliveryResult result;
            if(t.Invalid||t.Cancel.IsCancellationRequested||!activity.Matches(t.ActivityVersion))result=new(t.EndState,t.Reason.Length>0?t.Reason:"本轮输入已取消，结果已保留。");
            else if(snapshot.State==CaptureState.Faulted||snapshot.Session?.Gaps.Count>0||snapshot.Segments.Any(s=>s.AsrState==AsrState.Unresolved))result=new("Blocked","识别未完整结束，确认文字可手动复制。");
            else if(text.Length==0)result=new("Empty","没有需要输入的文字。");
            else if(t.ManualDelivery)result=new("Dictated",t.ManualReason);
            else if(t.DictationOnly)result=new("Dictated","听写完成，文字已保留，可复制。");
            else
            {
                await controller.SetDeliveryAsync("Sending","正在粘贴…",turnId:t.Id);
                result=await TextDelivery.SendAsync(target!,text,()=>!t.Invalid&&!t.ManualDelivery&&activity.Matches(t.ActivityVersion)&&ReferenceEquals(active,t),t.Cancel.Token,()=>t.DeliveryDispatched=true);
            }
            t.DeliveryDispatched=true;
            await controller.SetDeliveryAsync(result.State,result.Message,result.Accepted,t.Id);
            await CompletePreviewAsync(t,result.Message);
            if(!t.DictationOnly)await controller.RefreshMemoryAfterDeliveryAsync();
        }
        catch(Exception e)
        {
            string reason=t.Reason.Length>0?t.Reason:AppController.SafeError(e);
            try { await controller.StopAsync(false);await controller.SetDeliveryAsync("Blocked",reason,turnId:t.Id); }
            finally { await CompletePreviewAsync(t,reason); }
        }
        finally{if(capturedTarget!=null)await TextDelivery.ReleaseAsync(capturedTarget);Listening?.Invoke(false);Interlocked.CompareExchange(ref active,null,t);t.Cancel.Dispose();t.Expedite.Dispose();}
    }
    private void PublishNotice(string message)
        => Notice?.Invoke(new(message, Volatile.Read(ref active)?.Id));

    private async Task CompletePreviewAsync(Turn turn, string status)
    {
        string text = "";
        string? deliveryState = turn.Invalid?turn.EndState:null;
        try
        {
            var snapshot = await controller.SnapshotAsync();
            if (snapshot.Session?.Id == turn.Id)
            {
                text = TranscriptText.Render(snapshot);
                deliveryState = turn.Invalid&&snapshot.Session.DeliveryState=="Pending"?turn.EndState:snapshot.Session.DeliveryState;
            }
        }
        catch { /* A controller being disposed cannot supply another snapshot. */ }
        TurnCompleted?.Invoke(new(turn.Id, status, text) { DeliveryState = deliveryState });
    }

    private async Task Watchdog()
    {
        try
        {
            using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while(await timer.WaitForNextTickAsync(lifetime.Token))
            {
                var t=Volatile.Read(ref active);if(t==null||t.Invalid||t.DeliveryDispatched)continue;
                if(!activity.Matches(t.ActivityVersion)){Cancel("检测到其他操作，本轮改为手动复制。");continue;}
                if(t.TargetCaptured&&!t.ManualDelivery&&!t.DictationOnly&&t.Native is {} native&&
                    !t.Focus.Observe(Win32.ObserveWindow(native),Win32.Observe(native),Environment.TickCount64))
                {
                    t.UseManualDelivery("输入焦点已变化，已继续听写；完成后请手动复制。");
                    await controller.SetDeliveryAsync("Pending",t.ManualReason,turnId:t.Id);
                }
                if(t.ReleasedAt==0&&Environment.TickCount64-t.PressedAt>controller.Settings.MaxHoldSeconds*1000L)Cancel("已达到最长录音时间，确认文字可手动复制。");
                // GetAsyncKeyState can be false while another application's hook
                // consumes Ctrl. Only the observed physical key-up ends a normal hold.
                // The maximum-duration and explicit-cancel guards remain in force.
            }
        }
        catch(OperationCanceledException){}
    }
    public async ValueTask DisposeAsync()
    {
        if(disposed)return;disposed=true;hook.Enable(false);Cancel("程序退出，自动输入已取消。");
        try{if(running!=null)await running.WaitAsync(TimeSpan.FromSeconds(10));}catch{}
        controller.InputInterrupted-=Cancel;hook.Dispose();lifetime.Cancel();signals.Writer.TryComplete();try{await Task.WhenAll(loop,watchdog);}catch{}await TextDelivery.ShutdownAsync();lifetime.Dispose();
    }
}
