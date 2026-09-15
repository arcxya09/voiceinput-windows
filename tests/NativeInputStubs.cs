// Deterministic OS boundary for the linked production PushToTalkService.
// Real clipboard/native target delivery is exercised by Windows desktop smoke.
using RealtimeTranscription.Core;
namespace RealtimeTranscription.Desktop.Input;
public record NativeTarget(IntPtr Window,IntPtr Focus,uint Thread,uint Process);
public record InputTarget(NativeTarget Native,string WorkerId="test",string CaptureId="test",bool CheckSelection=true);
public record PhysicalSignal(string Kind,int Key=0,long At=0,NativeTarget? Target=null,long ActivityVersion=0);
public record TargetCapture(InputTarget? Target,string Code,string Message);
public record DeliveryResult(string State,string Message,int Accepted=0);
internal static class Win32
{
    public static NativeTarget? Target=new(new IntPtr(11),new IntPtr(12),13,14);
    public static FocusObservation Window=FocusObservation.Stable,Focus=FocusObservation.Stable;
    // Models another hook consuming Ctrl: no asynchronous down bit is visible.
    public static bool Down(int key)=>false;
    public static NativeTarget? Current(bool allowMissingFocus=false)=>Target;
    public static FocusObservation Observe(NativeTarget target)=>Focus;
    public static FocusObservation ObserveWindow(NativeTarget target)=>Window;
}
public sealed class PhysicalHook(Action<PhysicalSignal> signal,Func<bool>? awaitingDelivery=null):IDisposable
{
    public static PhysicalHook? Latest;
    public Task Ready {get;}=Task.CompletedTask;
    public int Trigger=>0xA3;
    public void Configure(string key)=>Latest=this;
    public void Enable(bool enabled){}
    public void Emit(string kind)=>signal(new(kind,kind is "down" or "up"?Trigger:27,Environment.TickCount64,Win32.Current(true)));
    public void Dispose(){}
}
public static class TextDelivery
{
    public static string CaptureCode="Ready",SentText="";
    public static int Sends;
    public static Task InitializeAsync()=>Task.CompletedTask;
    public static ValueTask ShutdownAsync()=>ValueTask.CompletedTask;
    public static Task ReleaseAsync(InputTarget target)=>Task.CompletedTask;
    public static Task<TargetCapture> CaptureAsync(NativeTarget target,Func<bool> valid,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new TargetCapture(CaptureCode=="Ready"&&valid()?new(target):null,CaptureCode,
            CaptureCode=="Password"?"密码输入框不支持自动语音输入，未上传语音。":"提供程序不可用"));
    }
    public static Task<DeliveryResult> SendAsync(InputTarget target,string text,Func<bool> valid,CancellationToken token,Action? dispatched=null)
    {
        if(!valid()||token.IsCancellationRequested||Win32.Focus!=FocusObservation.Stable)return Task.FromResult(new DeliveryResult("Blocked","未投递"));
        Sends++;SentText=text;dispatched?.Invoke();return Task.FromResult(new DeliveryResult("PasteSent","正文保留在剪贴板",4));
    }
    public static void Reset()
    {
        CaptureCode="Ready";Sends=0;SentText="";Win32.Target=new(new IntPtr(11),new IntPtr(12),13,14);
        Win32.Window=Win32.Focus=FocusObservation.Stable;
    }
}
