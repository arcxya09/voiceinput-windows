// Deterministic OS boundary for the linked production PushToTalkService.
// Real clipboard/native target delivery is exercised by Windows desktop smoke.
using RealtimeTranscription.Core;
namespace RealtimeTranscription.Desktop.Input;
public record NativeTarget(IntPtr Window,IntPtr Focus,uint Thread,uint Process);
public record InputTarget(NativeTarget Native,string WorkerId="test",string CaptureId="test",bool CheckSelection=true);
public record PhysicalSignal(string Kind,int Key=0,long At=0,NativeTarget? Target=null,long ActivityVersion=0,long CancellationVersion=0,string InputCategory="Other");
public record TargetCapture(InputTarget? Target,string Code,string Message);
public record DeliveryResult(string State,string Message,int Accepted=0)
{
    public uint? ClipboardSequence {get;init;}
    public string Diagnostic {get;init;}="";
}
internal static class Win32
{
    public static NativeTarget? Target=new(new IntPtr(11),new IntPtr(12),13,14);
    public static FocusObservation Window=FocusObservation.Stable,Focus=FocusObservation.Stable;
    // Models another hook consuming Ctrl: no asynchronous down bit is visible.
    public static bool Down(int key)=>false;
    public static NativeTarget? Current(bool allowMissingFocus=false)=>Target;
    public static FocusObservation Observe(NativeTarget target)=>Focus;
    public static FocusObservation ObserveWindow(NativeTarget target)=>Window;
    public static uint GetClipboardSequenceNumber()=>TextDelivery.Sequence;
}
public sealed class PhysicalHook(Action<PhysicalSignal> signal,Func<bool>? awaitingDelivery=null):IDisposable
{
    public static PhysicalHook? Latest;
    public Task Ready {get;}=Task.CompletedTask;
    public int Trigger=>0xA3;
    public void Configure(string key)=>Latest=this;
    public void Enable(bool enabled){}
    public void Emit(string kind,long? at=null,string inputCategory="Keyboard")=>signal(new(kind,kind is "down" or "up"?Trigger:27,at??Environment.TickCount64,Win32.Current(true),InputCategory:inputCategory));
    public void Dispose(){}
}
public static class TextDelivery
{
    public static string CaptureCode="Ready",SentText="",CopiedText="",DirectMode="Unsupported",TypedText="";
    public static int Sends,Copies,CopyAttempts,Types,TypeAttempts;
    public static bool ClipboardBusy,BlockPaste,ClipboardChangesBeforePaste,PauseBeforeCopy,PauseBeforeSend;
    public static TaskCompletionSource CopyEntered=NewGate(),CopyRelease=NewGate(),SendEntered=NewGate(),SendRelease=NewGate(),CancellationObserved=NewGate();
    private static TaskCompletionSource NewGate()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static uint Sequence {get;private set;}
    public static Task InitializeAsync()=>Task.CompletedTask;
    public static ValueTask ShutdownAsync()=>ValueTask.CompletedTask;
    public static Task ReleaseAsync(InputTarget target)=>Task.CompletedTask;
    public static Task<TargetCapture> CaptureAsync(NativeTarget target,Func<bool> valid,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Unknown accessibility providers use the stable native focus as a fallback.
        bool native=CaptureCode is "Unavailable" or "NotEditable";
        return Task.FromResult(new TargetCapture((CaptureCode=="Ready"||native)&&valid()?new(target,native?"":"test",native?"":"test",!native):null,CaptureCode,"提供程序不可用"));
    }
    public static async Task<DeliveryResult> CopyAsync(string text,Func<bool> valid,CancellationToken token)
    {
        CopyAttempts++;
        if(PauseBeforeCopy)
        {
            using var registration=token.Register(()=>CancellationObserved.TrySetResult());
            CopyEntered.TrySetResult();await CopyRelease.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        // The real boundary reports unconfirmed/cancelled copies as CopyFailed;
        // production PTT must retain the explicit cancellation as the turn outcome.
        if(!valid()||token.IsCancellationRequested)return new("CopyFailed","未能确认正文已复制") {Diagnostic="CopyCancelledBeforeWrite"};
        if(ClipboardBusy)return new("CopyFailed","剪贴板正被其他软件占用，可手动复制");
        Copies++;CopiedText=text;
        return new("Copied","已复制，可粘贴") {ClipboardSequence=++Sequence};
    }
    // Direct input is opt-in per test so existing scenarios keep exercising the
    // clipboard path that production uses whenever direct input is unsupported.
    public static Task<DeliveryResult> TypeAsync(InputTarget target,string text,Func<bool> valid,CancellationToken token,Action? dispatched=null)
    {
        TypeAttempts++;
        if(Copies!=0)throw new Exception("Direct input must run before any clipboard write.");
        if(DirectMode=="Unsupported"||!valid()||token.IsCancellationRequested||Win32.Window!=FocusObservation.Stable)
            return Task.FromResult(new DeliveryResult("Unsupported","未直接输入。") {Diagnostic="StubUnsupported"});
        Types++;TypedText=text;dispatched?.Invoke();
        return Task.FromResult(DirectMode=="Partial"
            ?new DeliveryResult("Partial","只输入了部分文字，请核对目标内容。不会自动重发。",2) {Diagnostic="UnicodeInputPartial"}
            :new DeliveryResult("Sent","已直接输入到光标处，剪贴板未改动。",text.Length*2) {Diagnostic="UnicodeInput"});
    }
    public static async Task<DeliveryResult> SendAsync(InputTarget target,string text,Func<bool> valid,CancellationToken token,Action? dispatched=null,uint? preparedSequence=null)
    {
        if(Copies!=1||CopiedText!=text||preparedSequence!=Sequence)
            throw new Exception("Automatic paste must reuse the single completed clipboard write.");
        if(PauseBeforeSend)
        {
            using var registration=token.Register(()=>CancellationObserved.TrySetResult());
            SendEntered.TrySetResult();await SendRelease.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        if(ClipboardChangesBeforePaste)
        {Sequence++;return new("Blocked","剪贴板已变化") {Diagnostic="ClipboardChangedAfterCopy"};}
        // Paste follows the current child focus; only another window blocks it.
        if(BlockPaste||!valid()||token.IsCancellationRequested||Win32.Window!=FocusObservation.Stable)
            return new("Blocked","未投递");
        Sends++;SentText=text;dispatched?.Invoke();return new("PasteSent","正文保留在剪贴板",4);
    }
    public static void Reset()
    {
        CaptureCode="Ready";Sends=Copies=CopyAttempts=Types=TypeAttempts=0;SentText=CopiedText=TypedText="";DirectMode="Unsupported";ClipboardBusy=BlockPaste=ClipboardChangesBeforePaste=PauseBeforeCopy=PauseBeforeSend=false;Sequence=10;
        CopyEntered=NewGate();CopyRelease=NewGate();SendEntered=NewGate();SendRelease=NewGate();CancellationObserved=NewGate();
        Win32.Target=new(new IntPtr(11),new IntPtr(12),13,14);Win32.Window=Win32.Focus=FocusObservation.Stable;
    }
}
