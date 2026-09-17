using System.Threading.Channels;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Desktop.Input;

public sealed class PushToTalkService : IAsyncDisposable, IVoicePreviewEvents
{
    private sealed class Turn(long at,NativeTarget? native,long activityVersion,long cancellationVersion,bool dictationOnly)
    {
        public readonly string Id=JsonCodec.Id();
        public long PressedAt=at,ReleasedAt;
        public volatile NativeTarget? Native=native;
        public readonly bool DictationOnly=dictationOnly;
        public readonly long ActivityVersion=activityVersion,CancellationVersion=cancellationVersion;
        public int OrdinaryInputs,OrdinaryInputLogged,CancelHandled;
        public long CopyMs=-1,PasteMs=-1;
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
        public string Reason="",CancellationCategory="ExternalCancellation";
        public void CancelInput(){try{Cancel.Cancel();}catch(ObjectDisposedException){}}
    }
    private readonly AppController controller;
    private readonly InputActivityVersion activity=new(),cancellations=new();
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
    private void LogTurn(string eventName,Turn? turn=null,Exception? error=null,params (string Key,object? Value)[] fields)
        =>controller.Log.Write("PushToTalk",eventName,AppController.LogCorrelation(turn?.Id),fields.ToDictionary(x=>x.Key,x=>x.Value),error);
    private void UseManualDelivery(Turn turn,string reason,string category)
    {
        turn.UseManualDelivery(reason);
        LogTurn("ManualDelivery",turn,fields:[("Reason",category)]);
    }
    private bool CanContinue(Turn turn)=>!turn.Invalid&&!turn.Cancel.IsCancellationRequested&&
        cancellations.Matches(turn.CancellationVersion)&&ReferenceEquals(Volatile.Read(ref active),turn);
    private async Task ManualHint(Turn turn)
    {
        if(CanContinue(turn)&&!turn.DeliveryDispatched)
            await controller.SetDeliveryAsync("Pending",turn.ManualReason,turnId:turn.Id,onlyIfPending:true);
    }
    private static string TurnPhase(Turn turn)=>turn.DeliveryDispatched?"Delivered":turn.PhysicalReleased?"Finishing":"Recording";
    public PushToTalkService(AppController controller)
    {
        this.controller=controller;controller.InputInterrupted+=CaptureInterrupted;hook=new PhysicalHook(s=>
        {
            var current=Volatile.Read(ref active);
            if(s.Kind=="up"&&current!=null&&s.At-current.PressedAt>=controller.Settings.HoldMs)current.PhysicalReleased=true;
            if(s.Kind=="expedite"){current?.ExpediteInput();return;}
            // Paste eligibility changes synchronously, before the channel consumer.
            // Ordinary input never cancels recognition or the clipboard write.
            if(s.Kind=="activity")
            {
                activity.Advance();
                if(current is {Invalid:false,DeliveryDispatched:false})
                {
                    current.UseManualDelivery("检测到其他操作，继续听写；完成后自动复制。");
                    Interlocked.Increment(ref current.OrdinaryInputs);
                }
            }
            if(s.Kind is "escape" or "cancel")
            {
                activity.Advance();cancellations.Advance();
                if(current!=null)MarkCancellation(current,"本轮输入已取消。",s.Kind=="escape"?"Escape":"PhysicalCancellation");
            }
            s=s with{ActivityVersion=activity.Current,CancellationVersion=cancellations.Current};
            if(!signals.Writer.TryWrite(s))
            {
                // The sticky manual flag already protects a paste if ordinary
                // input fills the queue. A lost control signal cancels safely.
                if(s.Kind!="activity"){LogTurn("SignalQueueFull",current);activity.Advance();cancellations.Advance();controller.RequestStopCapture();}
            }
        },()=>Volatile.Read(ref active) is {PhysicalReleased:true,DeliveryDispatched:false,Invalid:false,DictationOnly:false});
        loop=Task.Run(Loop);watchdog=Task.Run(Watchdog);
    }
    public async Task InitializeAsync()
    {
        LogTurn("InitializeStarted");
        try{await hook.Ready;Configure();await TextDelivery.InitializeAsync();LogTurn("InitializeCompleted");}
        catch(Exception e){LogTurn("InitializeFailed",error:e);throw;}
    }
    public void Configure()=>hook.Configure(controller.Settings.Hotkey);
    public void SetEnabled(bool value){Enabled=value;hook.Enable(value);PublishNotice(value?"按住说话已启用。":"按住说话已暂停。");}
    public void Cancel(string reason="本轮输入已取消，确认文字已保留。")
        =>QueueCancel(reason,"ExternalCancellation");
    private void CaptureInterrupted(string reason)
    {
        var turn=Volatile.Read(ref active);
        if(turn==null||turn.Invalid||turn.DeliveryDispatched)return;
        // Publish the root cause before canceling startup/target discovery.
        // Queuing the reason used to race their cancellation continuations.
        turn.Reason=reason;turn.EndState="Failed";turn.Invalid=true;Interlocked.Exchange(ref turn.CancelHandled,1);
        LogTurn("CaptureInterrupted",turn,fields:[("Reason","CaptureOrRecognitionFailure")]);
        activity.Advance();cancellations.Advance();turn.CancelInput();turn.Released.TrySetResult();
        controller.RequestStopCapture();Listening?.Invoke(false);
    }
    private static void MarkCancellation(Turn turn,string reason,string category)
    {
        if(turn.Invalid||turn.DeliveryDispatched)return;
        turn.Reason=reason;turn.CancellationCategory=category;turn.Invalid=true;
    }
    private void QueueCancel(string reason,string category)
    {
        if(Volatile.Read(ref active) is {} turn)MarkCancellation(turn,reason,category);
        activity.Advance();cancellations.Advance();
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
                    if(!cancellations.Matches(s.CancellationVersion))continue;
                    var t=new Turn(s.At,native,s.ActivityVersion,s.CancellationVersion,dictationOnly);active=t;
                    if(!activity.Matches(s.ActivityVersion))UseManualDelivery(t,"检测到其他操作，继续听写；完成后自动复制。","InputBeforeStartup");LogTurn("Started",t,fields:[("DictationOnly",dictationOnly)]);TurnStarted?.Invoke(t.Id);Listening?.Invoke(true);running=Run(t);
                }
                else if(active is {} t)
                {
                    if(s.Kind=="up")
                    {
                        if(t.ReleasedAt!=0)continue;
                        t.ReleasedAt=s.At;controller.RequestStopCapture();Listening?.Invoke(false);
                        LogTurn("Released",t,fields:[("HeldMs",Math.Max(0,s.At-t.PressedAt))]);
                        if(!t.Invalid&&s.At-t.PressedAt<controller.Settings.HoldMs){LogTurn("Cancelled",t,fields:[("Reason","ShortPress")]);t.Invalid=true;t.Reason="短按已取消。";Interlocked.Exchange(ref t.CancelHandled,1);t.CancelInput();}
                        t.Stop=controller.StopAsync(false);t.Released.TrySetResult();
                    }
                    else if(s.Kind=="activity")
                    {
                        if(!CanContinue(t)||t.DeliveryDispatched)continue;
                        if(Interlocked.Exchange(ref t.OrdinaryInputLogged,1)==0)
                            LogTurn("PasteDisabledByInput",t,fields:[("InputCategory",s.InputCategory is "Keyboard" or "MouseButton" or "MouseWheel"?s.InputCategory:"Other"),("Phase",TurnPhase(t))]);
                        await ManualHint(t);
                    }
                    else if(s.Kind is "escape" or "cancel"||s.Kind.StartsWith("cancel:"))
                    {
                        if(t.DeliveryDispatched||Interlocked.Exchange(ref t.CancelHandled,1)!=0)continue;
                        MarkCancellation(t,s.Kind.StartsWith("cancel:")?s.Kind[7..]:"本轮输入已取消。",t.CancellationCategory);
                        LogTurn("Cancelled",t,fields:[("Reason",t.CancellationCategory)]);
                        t.CancelInput();controller.RequestStopCapture();t.Stop??=controller.StopAsync(false);t.Released.TrySetResult();Listening?.Invoke(false);
                    }
                }
            }
        }
        catch(OperationCanceledException){}
        catch(Exception e){LogTurn("LoopFailed",Volatile.Read(ref active),e);throw;}
    }
    private async Task<TargetCapture> CaptureTarget(Turn t)
    {
        if(t.DictationOnly)return new(null,"Dictation","");
        TargetCapture Manual(string reason,string category)
        {
            UseManualDelivery(t,reason,category);
            return new(null,"DictationFallback",reason);
        }
        if(t.Native is not {} anchor||anchor.Focus==IntPtr.Zero)return Manual("未检测到可靠的输入焦点，已继续听写；完成后自动复制。","MissingInitialFocus");
        var native=await InputSafety.RecoverInitialFocusAsync(() =>
        {
            if(Win32.ObserveWindow(anchor)==FocusObservation.Changed)return (FocusObservation.Changed,(NativeTarget?)null);
            var current=Win32.Current();
            if(current==null)return (FocusObservation.Unavailable,(NativeTarget?)null);
            if(current.Window!=anchor.Window||current.Thread!=anchor.Thread||current.Process!=anchor.Process)
                return (FocusObservation.Changed,(NativeTarget?)null);
            if(anchor.Focus!=IntPtr.Zero&&current.Focus!=anchor.Focus)return (FocusObservation.Unavailable,(NativeTarget?)null);
            return (FocusObservation.Stable,(NativeTarget?)current);
        },()=>CanContinue(t),t.Cancel.Token,1000);
        if(native==null)return Manual("输入位置暂不可用，已继续听写；完成后自动复制。","InitialFocusUnavailable");
        t.Native=native;
        var result=await TextDelivery.CaptureAsync(native,()=>CanContinue(t),t.Cancel.Token);
        if(result.Target!=null){t.TargetCaptured=true;return result;}
        if(result.Code=="Password")return result;
        t.Cancel.Token.ThrowIfCancellationRequested();
        return Manual("当前输入位置不适合自动粘贴，已继续听写；完成后自动复制。","TargetNotEditable");
    }
    private async Task Run(Turn t)
    {
        InputTarget? capturedTarget=null;
        try
        {
            var targetTask=CaptureTarget(t);
            var readyTask=targetTask.ContinueWith(x=>x.Status==TaskStatus.RanToCompletion&&(t.DictationOnly||x.Result.Target!=null||x.Result.Code=="DictationFallback")&&CanContinue(t),TaskScheduler.Default);
            bool started=await controller.StartAsync(t.PressedAt,t.Cancel.Token,readyTask,t.Id);
            var capture=await targetTask;var target=capture.Target;capturedTarget=target;
            if(capture.Code=="Password"||(target==null&&!t.DictationOnly&&!t.ManualDelivery)){LogTurn("StartRejected",t,fields:[("Reason","TargetUnavailable")]);if(!t.Invalid)t.EndState="StartFailed";t.Invalid=true;if(t.Reason.Length==0)t.Reason=capture.Message;t.CancelInput();t.Released.TrySetResult();}
            if(!started){LogTurn("StartRejected",t,fields:[("Reason","ControllerStartFailed")]);if(!t.Invalid)t.EndState="StartFailed";t.Invalid=true;if(t.Reason.Length==0)t.Reason=(await controller.SnapshotAsync()).Status;t.Released.TrySetResult();}
            if(started&&t.ManualDelivery)await ManualHint(t);
            await t.Released.Task.WaitAsync(TimeSpan.FromSeconds(controller.Settings.MaxHoldSeconds+2));
            t.Stop??=controller.StopAsync(false);await t.Stop;
            await controller.FinishCurrentAsync(t.Id,!t.Invalid,t.Cancel.Token,forDelivery:true,expedite:t.Expedite.Token);
            var snapshot=await controller.SnapshotAsync();if(snapshot.Session?.Id!=t.Id){await CompletePreviewAsync(t,t.Reason.Length>0?t.Reason:snapshot.Status);return;}string text=TranscriptText.Render(snapshot);
            string? captureWarning=await controller.CaptureWarningAsync(t.Id);
            if(captureWarning!=null)UseManualDelivery(t,captureWarning,"AudioPositionGap");
            DeliveryResult result;
            if(!CanContinue(t))result=new(t.EndState,t.Reason.Length>0?t.Reason:"本轮输入已取消，结果已保留。");
            else if(snapshot.State==CaptureState.Faulted||snapshot.Session?.Gaps.Count>0||snapshot.Segments.Any(s=>s.AsrState==AsrState.Unresolved))result=new("Blocked","识别未完整结束，确认文字可手动复制。");
            else if(text.Length==0)result=new("Empty","没有需要输入的文字。");
            else
            {
                // A complete result is copied independently of target discovery.
                // Pasting is optional; missing focus cannot discard the copy step.
                bool Valid()=>CanContinue(t);
                await controller.SetDeliveryAsync("Copying","正在复制…",turnId:t.Id);
                long copyingAt=Environment.TickCount64;
                result=await TextDelivery.CopyAsync(text,Valid,t.Cancel.Token);
                t.CopyMs=Math.Max(0,Environment.TickCount64-copyingAt);
                LogTurn("CopyCompleted",t,fields:[("State",AppController.LogDeliveryState(result.State)),
                    ("Diagnostic",result.Diagnostic),("ElapsedMs",t.CopyMs)]);
                if(result.State=="Copied"&&captureWarning!=null)result=result with{Message=captureWarning};
                if(result.State=="Copied"&&Valid()&&activity.Matches(t.ActivityVersion)&&!t.ManualDelivery&&!t.DictationOnly&&target!=null)
                {
                    await controller.SetDeliveryAsync("Sending","正在粘贴…",turnId:t.Id);
                    long pastingAt=Environment.TickCount64;
                    var pasted=await TextDelivery.SendAsync(target,text,()=>Valid()&&activity.Matches(t.ActivityVersion)&&!t.ManualDelivery,t.Cancel.Token,
                        ()=>t.DeliveryDispatched=true,preparedSequence:result.ClipboardSequence);
                    t.PasteMs=Math.Max(0,Environment.TickCount64-pastingAt);
                    LogTurn("PasteCompleted",t,fields:[("State",AppController.LogDeliveryState(pasted.State)),
                        ("Diagnostic",pasted.Diagnostic),("AcceptedInputEvents",pasted.Accepted),("ElapsedMs",t.PasteMs)]);
                    // A blocked paste does not undo the successful copy. A changed
                    // clipboard must not be described as still containing our text.
                    result=pasted.State=="Blocked"&&result.ClipboardSequence==Win32.GetClipboardSequenceNumber()
                        ?result with{Message="文字已复制，未自动粘贴；可在目标输入框按 Ctrl+V。",Diagnostic=pasted.Diagnostic}
                        :pasted;
                }
            }
            // Copy may finish concurrently with a cancellation. Preserve the
            // first cancellation reason unless a paste might already be dispatched.
            if(!t.DeliveryDispatched&&result.State!="Unknown"&&
                (!CanContinue(t)))
                result=new(t.EndState,t.Reason.Length>0?t.Reason:"本轮输入已取消，结果已保留。");
            t.DeliveryDispatched=true;
            await controller.SetDeliveryAsync(result.State,result.Message,result.Accepted,t.Id);
            await CompletePreviewAsync(t,result.Message);
            await controller.RefreshMemoryAfterDeliveryAsync();
        }
        catch(Exception e)
        {
            LogTurn(e is OperationCanceledException?"RunCancelled":"RunFailed",t,e is OperationCanceledException?null:e,fields:[("Cancelled",e is OperationCanceledException)]);
            string reason=t.Reason.Length>0?t.Reason:AppController.SafeError(e);
            try { await controller.StopAsync(false);await controller.SetDeliveryAsync(t.Invalid?t.EndState:"Blocked",reason,turnId:t.Id); }
            finally { await CompletePreviewAsync(t,reason); }
        }
        finally
        {
            try{if(capturedTarget!=null)await TextDelivery.ReleaseAsync(capturedTarget);}
            catch(Exception e){LogTurn("TargetReleaseFailed",t,e);throw;}
            finally{Listening?.Invoke(false);Interlocked.CompareExchange(ref active,null,t);t.Cancel.Dispose();t.Expedite.Dispose();}
        }
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
        LogTurn("Completed",turn,fields:[("State",AppController.LogDeliveryState(deliveryState)),("Cancelled",turn.Invalid),
            ("ManualDelivery",turn.ManualDelivery),("CharacterCount",text.Length),("ElapsedMs",Math.Max(0,Environment.TickCount64-turn.PressedAt))]);
        await controller.LogTurnSummaryAsync(turn.Id,deliveryState,text.Length,turn.ManualDelivery,
            turn.Invalid,turn.CopyMs,turn.PasteMs,turn.ReleasedAt,Volatile.Read(ref turn.OrdinaryInputs));
        TurnCompleted?.Invoke(new(turn.Id, status, text) { DeliveryState = deliveryState });
    }

    private async Task Watchdog()
    {
        try
        {
            using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while(await timer.WaitForNextTickAsync(lifetime.Token))
            {
                var t=Volatile.Read(ref active);if(t==null||t.DeliveryDispatched)continue;
                if(t.Invalid)
                {
                    if(Volatile.Read(ref t.CancelHandled)==0)QueueCancel(t.Reason,t.CancellationCategory);
                    continue;
                }
                if(!cancellations.Matches(t.CancellationVersion)){QueueCancel("本轮输入已取消。","ExplicitCancellation");continue;}
                if(!activity.Matches(t.ActivityVersion)&&!t.ManualDelivery)
                {
                    UseManualDelivery(t,"检测到其他操作，继续听写；完成后自动复制。","InputActivity");
                    await ManualHint(t);
                }
                if(t.TargetCaptured&&!t.ManualDelivery&&!t.DictationOnly&&t.Native is {} native&&
                    !t.Focus.Observe(Win32.ObserveWindow(native),Win32.Observe(native),Environment.TickCount64))
                {
                    UseManualDelivery(t,"输入焦点已变化，已继续听写；完成后自动复制。","FocusChanged");
                    await ManualHint(t);
                }
                if(t.ReleasedAt==0&&Environment.TickCount64-t.PressedAt>controller.Settings.MaxHoldSeconds*1000L)QueueCancel("已达到最长录音时间，确认文字可手动复制。","MaximumHoldDuration");
                // GetAsyncKeyState can be false while another application's hook
                // consumes Ctrl. Only the observed physical key-up ends a normal hold.
                // The maximum-duration and explicit-cancel guards remain in force.
            }
        }
        catch(OperationCanceledException){}
        catch(Exception e){LogTurn("WatchdogFailed",Volatile.Read(ref active),e);throw;}
    }
    public async ValueTask DisposeAsync()
    {
        if(disposed)return;disposed=true;hook.Enable(false);QueueCancel("程序退出，自动输入已取消。","Shutdown");
        try{if(running!=null)await running.WaitAsync(TimeSpan.FromSeconds(10));}catch{}
        controller.InputInterrupted-=CaptureInterrupted;hook.Dispose();lifetime.Cancel();signals.Writer.TryComplete();try{await Task.WhenAll(loop,watchdog);}catch{}await TextDelivery.ShutdownAsync();lifetime.Dispose();
    }
}
