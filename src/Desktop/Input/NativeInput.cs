using RealtimeTranscription.Core;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RealtimeTranscription.Desktop.Input;

public record NativeTarget(IntPtr Window,IntPtr Focus,uint Thread,uint Process);
public record InputTarget(NativeTarget Native,string WorkerId,string CaptureId,bool CheckSelection=true);
public record DeliveryResult(string State,string Message,int Accepted=0)
{
    // Stage codes only; never include clipboard or target document contents.
    public string Diagnostic { get; init; } = "";
    public uint? ClipboardSequence { get; init; }
}
public record PhysicalSignal(string Kind,int Key=0,long At=0,NativeTarget? Target=null,long ActivityVersion=0,long CancellationVersion=0,string InputCategory="Other");
public record TargetCapture(InputTarget? Target,string Code,string Message);

internal static class Win32
{
    public const uint Marker=0x56505454;
    [StructLayout(LayoutKind.Sequential)] public struct Point {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] public struct Rect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] public struct Gui {public uint Size,Flags;public IntPtr Active,Focus,Capture,Menu,Move,Caret;public Rect CaretRect;}
    [StructLayout(LayoutKind.Sequential)] public struct KeyData {public uint Vk,Scan,Flags,Time;public UIntPtr Extra;}
    [StructLayout(LayoutKind.Sequential)] public struct MouseData {public Point Point;public uint Mouse,Flags,Time;public UIntPtr Extra;}
    [StructLayout(LayoutKind.Sequential)] public struct Keyboard {public ushort Vk,Scan;public uint Flags,Time;public UIntPtr Extra;}
    [StructLayout(LayoutKind.Sequential)] public struct Mouse {public int X,Y;public uint Data,Flags,Time;public UIntPtr Extra;}
    [StructLayout(LayoutKind.Explicit)] public struct Union {[FieldOffset(0)]public Keyboard Key;[FieldOffset(0)]public Mouse Mouse;}
    [StructLayout(LayoutKind.Sequential)] public struct Input {public uint Type;public Union Data;}
    [StructLayout(LayoutKind.Sequential)] public struct Message {public IntPtr Window;public uint Id;public UIntPtr WParam;public IntPtr LParam;public uint Time;public Point Point;public uint Private;}
    public delegate IntPtr Hook(int code,IntPtr w,IntPtr l);
    [DllImport("user32.dll")]public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]public static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint process);
    [DllImport("user32.dll")]public static extern bool GetGUIThreadInfo(uint thread,ref Gui info);
    [DllImport("user32.dll")]public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]public static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")]public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll",SetLastError=true)]public static extern uint SendInput(uint count,Input[] input,int size);
    [DllImport("user32.dll")]public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll",SetLastError=true)]public static extern IntPtr SetWindowsHookEx(int id,Hook callback,IntPtr module,uint thread);
    [DllImport("user32.dll")]public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]public static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr w,IntPtr l);
    [DllImport("user32.dll")]public static extern int GetMessage(out Message message,IntPtr hwnd,uint min,uint max);
    [DllImport("user32.dll")]public static extern bool PeekMessage(out Message message,IntPtr hwnd,uint min,uint max,uint remove);
    [DllImport("user32.dll")]public static extern bool PostThreadMessage(uint thread,uint message,UIntPtr w,IntPtr l);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]public static extern IntPtr GetModuleHandle(string? module);
    [DllImport("kernel32.dll")]public static extern uint GetCurrentThreadId();
    [DllImport("imm32.dll")]public static extern IntPtr ImmGetContext(IntPtr hwnd);
    [DllImport("imm32.dll")]public static extern bool ImmReleaseContext(IntPtr hwnd,IntPtr context);
    [DllImport("imm32.dll",CharSet=CharSet.Unicode)]public static extern int ImmGetCompositionStringW(IntPtr context,uint index,IntPtr buffer,uint length);
    public static bool Down(int key)=>(GetAsyncKeyState(key)&0x8000)!=0;
    public static NativeTarget? Current(bool allowMissingFocus=false)
    {
        var hwnd=GetForegroundWindow();if(hwnd==IntPtr.Zero||!IsWindowEnabled(hwnd))return null;
        uint thread=GetWindowThreadProcessId(hwnd,out uint process);if(process==(uint)Environment.ProcessId)return null;
        var gui=new Gui{Size=(uint)Marshal.SizeOf<Gui>()};
        if(thread==0||GetForegroundWindow()!=hwnd)return null;
        bool hasFocus=GetGUIThreadInfo(thread,ref gui)&&gui.Focus!=IntPtr.Zero&&IsWindow(gui.Focus);
        if(GetForegroundWindow()!=hwnd)return null;
        return hasFocus?new(hwnd,gui.Focus,thread,process):allowMissingFocus?new(hwnd,IntPtr.Zero,thread,process):null;
    }
    // Foreground identity only: child-focus churn is handled separately while recording.
    public static FocusObservation ObserveWindow(NativeTarget target)
    {
        var foreground=GetForegroundWindow();
        if(foreground==IntPtr.Zero)return FocusObservation.Unavailable;
        if(foreground!=target.Window||!IsWindow(target.Window)||!IsWindowEnabled(target.Window))return FocusObservation.Changed;
        uint thread=GetWindowThreadProcessId(foreground,out uint process);
        return thread==target.Thread&&process==target.Process&&GetForegroundWindow()==foreground
            ?FocusObservation.Stable:FocusObservation.Changed;
    }
    public static FocusObservation Observe(NativeTarget target)
    {
        var foreground=GetForegroundWindow();
        if(foreground==IntPtr.Zero)return FocusObservation.Unavailable;
        if(foreground!=target.Window||!IsWindow(target.Window)||(target.Focus!=IntPtr.Zero&&!IsWindow(target.Focus))||!IsWindowEnabled(target.Window))return FocusObservation.Changed;
        uint thread=GetWindowThreadProcessId(foreground,out uint process);
        if(thread!=target.Thread||process!=target.Process)return FocusObservation.Changed;
        var gui=new Gui{Size=(uint)Marshal.SizeOf<Gui>()};
        if(!GetGUIThreadInfo(thread,ref gui)||gui.Focus==IntPtr.Zero)return FocusObservation.Unavailable;
        if(GetForegroundWindow()!=foreground)return FocusObservation.Changed;
        return target.Focus==IntPtr.Zero?FocusObservation.Unavailable:gui.Focus==target.Focus?FocusObservation.Stable:FocusObservation.Changed;
    }
    public static bool Composing(IntPtr hwnd)
    {
        IntPtr context=ImmGetContext(hwnd);if(context==IntPtr.Zero)return false;
        try{return ImmGetCompositionStringW(context,8,IntPtr.Zero,0)>0;}finally{ImmReleaseContext(hwnd,context);}
    }
}

public sealed class PhysicalHook : IDisposable
{
    private readonly Thread thread;
    private readonly Win32.Hook keyboard,mouse;
    private readonly Action<PhysicalSignal> signal;
    private readonly Func<bool> awaitingDelivery;
    private readonly ReleaseEnterGuard enter=new();
    private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint threadId;
    private IntPtr keyHook,mouseHook;
    private int trigger=0xA3;
    private volatile bool held;
    private volatile bool enabled=true;
    public Task Ready=>ready.Task;
    public int Trigger=>Volatile.Read(ref trigger);
    public bool IsHeld=>held;
    public PhysicalHook(Action<PhysicalSignal> signal,Func<bool>? awaitingDelivery=null)
    {
        this.signal=signal;this.awaitingDelivery=awaitingDelivery??(()=>false);keyboard=Key;mouse=Mouse;
        thread=new Thread(Run){IsBackground=true,Name="Voice input physical key hook"};thread.Start();
    }
    public void Configure(string key){int next=key=="F8"?0x77:key=="F9"?0x78:0xA3;if(held||Win32.Down(Trigger)||Win32.Down(next))throw new InvalidOperationException("请松开说话键后再修改设置。");held=false;Volatile.Write(ref trigger,next);}
    public void Enable(bool value){enabled=value;if(!value)signal(new("cancel",At:Environment.TickCount64));}
    private void Run()
    {
        threadId=Win32.GetCurrentThreadId();Win32.PeekMessage(out _,IntPtr.Zero,0,0,0);
        try
        {
            keyHook=Win32.SetWindowsHookEx(13,keyboard,Win32.GetModuleHandle(null),0);
            mouseHook=Win32.SetWindowsHookEx(14,mouse,Win32.GetModuleHandle(null),0);
            if(keyHook==IntPtr.Zero||mouseHook==IntPtr.Zero)throw new InvalidOperationException("无法监听全局按键，请退出后重新启动程序。");
            ready.TrySetResult();while(Win32.GetMessage(out _,IntPtr.Zero,0,0)>0){}
        }
        catch(Exception e){ready.TrySetException(e);}
        finally{if(keyHook!=IntPtr.Zero)Win32.UnhookWindowsHookEx(keyHook);if(mouseHook!=IntPtr.Zero)Win32.UnhookWindowsHookEx(mouseHook);}
    }
    private IntPtr Key(int code,IntPtr w,IntPtr l)
    {
        if(code>=0)
        {
            var k=Marshal.PtrToStructure<Win32.KeyData>(l);
            if((k.Flags&0x10)==0&&k.Extra.ToUInt64()!=Win32.Marker)
            {
                int vk=(int)k.Vk;if(vk==0x11)vk=(k.Flags&1)!=0?0xA3:0xA2;
                bool down=w.ToInt64() is 0x100 or 0x104;bool up=w.ToInt64() is 0x101 or 0x105;
                if(vk==13 && enter.Handle(down,up,awaitingDelivery(),new[]{0x10,0x11,0x12,0x5b,0x5c}.Any(Win32.Down),out bool expedite))
                {
                    if(expedite)signal(new("expedite",vk,Environment.TickCount64));
                    return new IntPtr(1);
                }
                if(vk==Trigger)
                {
                    if(down&&!held){held=true;if(enabled)signal(new("down",vk,Environment.TickCount64,Win32.Current(allowMissingFocus:true)));}
                    if(up&&held){held=false;signal(new("up",vk,Environment.TickCount64));}
                }
                else if(down)signal(new(vk==27?"escape":"activity",vk,Environment.TickCount64,InputCategory:"Keyboard"));
            }
        }
        return Win32.CallNextHookEx(keyHook,code,w,l);
    }
    private IntPtr Mouse(int code,IntPtr w,IntPtr l)
    {
        if(code>=0&&w.ToInt64() is 0x201 or 0x204 or 0x207 or 0x20b or 0x20a or 0x20e)
        {
            var m=Marshal.PtrToStructure<Win32.MouseData>(l);if((m.Flags&1)==0)signal(new("activity",At:Environment.TickCount64,InputCategory:w.ToInt64() is 0x20a or 0x20e?"MouseWheel":"MouseButton"));
        }
        return Win32.CallNextHookEx(mouseHook,code,w,l);
    }
    public void Dispose(){enabled=false;if(threadId!=0)Win32.PostThreadMessage(threadId,0x12,UIntPtr.Zero,IntPtr.Zero);thread.Join(1000);}
}

public static class TextDelivery
{
    private static readonly IsolatedInputQuery queries=new(()=>new UiaProcess());
    public static async Task InitializeAsync()
    { _ = await queries.RunAsync(JsonSerializer.Serialize(new UiaRequest("Ping")),TimeSpan.FromSeconds(5)); }
    public static ValueTask ShutdownAsync()=>queries.DisposeAsync();
    private static async Task<(UiaResponse Reply,string Worker)?> Query(UiaRequest request,CancellationToken token,string? worker=null,int timeoutMs=1200)
    {
        var reply=await queries.RunAsync(JsonSerializer.Serialize(request),TimeSpan.FromMilliseconds(timeoutMs),token,worker);
        if(reply==null)return null;
        try{var parsed=JsonSerializer.Deserialize<UiaResponse>(reply.Value);return parsed==null?null:(parsed,reply.Generation);}
        catch{return null;}
    }
    public static async Task ReleaseAsync(InputTarget target)
    {
        if(target.WorkerId.Length>0)_ = await Query(new("Release"),CancellationToken.None,target.WorkerId);
    }

    public static async Task<TargetCapture> CaptureAsync(NativeTarget native,Func<bool> valid,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var recovered=await InputSafety.RecoverInitialFocusAsync(() =>
        {
            if(Win32.ObserveWindow(native)==FocusObservation.Changed)return(FocusObservation.Changed,(NativeTarget?)null);
            return Win32.Observe(native)==FocusObservation.Stable?(FocusObservation.Stable,(NativeTarget?)native):(FocusObservation.Unavailable,(NativeTarget?)null);
        },valid,token,1000);
        if(recovered==null)return new(null,"TargetChanged","暂时无法确认原输入位置。");
        // UIA adds password/editability and selection information when available.
        // One bounded attempt keeps a slow or unsupported provider off the normal
        // recognition path; stable native focus can still accept ordinary Ctrl+V.
        var result=await Query(new("Capture",native.Window.ToInt64(),native.Focus.ToInt64(),native.Thread,native.Process),token,timeoutMs:400);
        token.ThrowIfCancellationRequested();
        // Preserve a positive refusal even if the application moves focus while
        // the response is returning. In particular, never turn a known password
        // field into the unknown-target dictation fallback.
        if(result is {} denied&&denied.Reply.Code is "Password" or "Disabled" or "ReadOnly")
            return new(null,denied.Reply.Code,denied.Reply.Message);
        if(!valid()||Win32.Observe(native)!=FocusObservation.Stable)
            return new(null,"TargetChanged","输入位置已变化，完成后将复制识别文字。");
        if(result is {} response)
        {
            if(response.Reply.Code=="Ready")return new(new(native,response.Worker,response.Reply.CaptureId),"Ready",response.Reply.Message);
            if(response.Reply.Code is not ("Unavailable" or "NotEditable"))
                return new(null,response.Reply.Code,response.Reply.Message);
        }
        if(Win32.Composing(native.Focus))return new(null,"Composing","请先确认或取消输入法候选词，再按住说话。");
        return new(new(native,"",""),"Ready","当前软件使用原生焦点粘贴。");
    }

    public static async Task<DeliveryResult> CopyAsync(string text,Func<bool> valid,CancellationToken token)
    {
        DeliveryResult Failed(string diagnostic)=>new("CopyFailed","本轮文字未确认复制，剪贴板可能仍是旧内容。请在程序中复制本轮识别文字。") {Diagnostic=diagnostic};
        if(!ClipboardPaste.IsSupportedText(text))return Failed("ClipboardTextUnsupported");
        try
        {
            if(token.IsCancellationRequested||!valid())return Failed("CopyCancelledBeforeWrite");
            uint previous=Win32.GetClipboardSequenceNumber();
            // Copy never uses an expected worker generation: the clipboard is
            // independent of UIA targets, and survives a retired capture worker.
            var result=await Query(new("Copy",Text:text,ClipboardSequence:previous),token,timeoutMs:3000);
            if(result is not {} response||response.Reply.Code!="Ready"||response.Reply.ClipboardSequence is not uint sequence)
                return Failed(result?.Reply.Code??"CopyWorkerUnavailable");
            // Cancellation can race the native clipboard commit. Do not retry or
            // claim that no write occurred when its completion cannot be confirmed.
            if(token.IsCancellationRequested||!valid())return Failed("CopyInterruptedAfterWrite");
            if(Win32.GetClipboardSequenceNumber()!=sequence)return Failed("ClipboardChangedAfterCopy");
            return new("Copied","正文已复制，可在输入框中按 Ctrl+V 粘贴。") {ClipboardSequence=sequence,Diagnostic="ClipboardCopied"};
        }
        catch {return Failed("CopyNotConfirmed");}
    }

    public static async Task<DeliveryResult> SendAsync(InputTarget target,string text,Func<bool> valid,CancellationToken token,Action? dispatched=null,uint? preparedSequence=null)
    {
        if(text.Length==0)return new("Empty","没有需要输入的文字。");
        long end=Environment.TickCount64+200;
        bool Modified()=>new[]{0x10,0x11,0x12,0x5b,0x5c}.Any(Win32.Down);
        while(Modified()&&Environment.TickCount64<end&&!token.IsCancellationRequested)await Task.Delay(10);
        if(Modified())return new("Blocked","修饰键仍未释放，文字已保留，可手动复制。");
        if(!valid()||token.IsCancellationRequested)return new("Blocked","检测到其他操作，文字已保留，可手动复制。");
        if(Win32.Composing(target.Native.Focus))return new("Blocked","输入法仍有未确认的候选词，文字已保留，可手动复制。");
        string diagnostic="PasteNotPrepared";
        bool nativeOnly=target.WorkerId.Length==0&&target.CaptureId.Length==0;
        bool Uninterrupted()=>valid()&&Win32.Current()==target.Native;
        var result=await ClipboardPaste.SendAsync(text,async(body,cancellation)=>
            {
                if(preparedSequence is uint existing)
                {
                    // The finished result was already copied. Validate the old
                    // target without rewriting the clipboard or adopting a newer copy.
                    if(Win32.GetClipboardSequenceNumber()!=existing)
                    {diagnostic="ClipboardChangedAfterCopy";return null;}
                    if(!nativeOnly)
                    {
                        var validation=await Query(new("Validate",CaptureId:target.CaptureId,CheckSelection:true),
                            cancellation,target.WorkerId,timeoutMs:1200);
                        diagnostic=validation?.Reply.Code??"PasteWorkerUnavailable";
                        if(validation?.Reply.Code!="Ready")return null;
                    }
                    diagnostic="CopiedTextReady";
                    return existing;
                }
                uint previous=Win32.GetClipboardSequenceNumber();
                UiaRequest request=nativeOnly
                    ?new("PrepareNativePaste",target.Native.Window.ToInt64(),target.Native.Focus.ToInt64(),target.Native.Thread,target.Native.Process,
                        Text:body,ClipboardSequence:previous)
                    :new("PreparePaste",CaptureId:target.CaptureId,Text:body,ClipboardSequence:previous);
                var prepared=await Query(request,cancellation,nativeOnly?null:target.WorkerId,timeoutMs:3000);
                diagnostic=prepared?.Reply.Code??"PasteWorkerUnavailable";
                return prepared is {} response&&response.Reply.Code=="Ready"?response.Reply.ClipboardSequence:null;
            },
            sequence=>
            {
                bool unchanged=Win32.GetClipboardSequenceNumber()==sequence;
                if(!unchanged)diagnostic="ClipboardChangedAfterPrepare";
                return unchanged;
            },
            ()=>Uninterrupted()&&!Modified()&&!Win32.Composing(target.Native.Focus),
            ()=>{int accepted=SendPasteShortcut();if(accepted>=2)dispatched?.Invoke();return accepted;},token,Uninterrupted);
        DeliveryResult delivery=result.State switch
        {
            "Sent"=>new("PasteSent","已发起整段粘贴，正文保留在剪贴板。",result.Accepted),
            "Unknown"=>new("Unknown","粘贴结果需要核对，请检查目标内容。不会自动重发。",result.Accepted),
            _=>new("Blocked","未发起粘贴：剪贴板暂不可用、内容不受支持，或输入位置及按键状态已变化。文字已保留，可手动复制。",result.Accepted)
        };
        return delivery with{Diagnostic=diagnostic};
    }
    private static int SendPasteShortcut()
    {
        static Win32.Input Key(ushort key,bool up)=>new(){Type=1,Data=new(){Key=new(){Vk=key,Flags=up?2u:0u,Extra=new UIntPtr(Win32.Marker)}}};
        Win32.Input[] input=[Key(0x11,false),Key(0x56,false),Key(0x56,true),Key(0x11,true)];
        uint count=Win32.SendInput((uint)input.Length,input,Marshal.SizeOf<Win32.Input>());
        if(count is >0 and <4)
        {
            // A V-down may already have requested a paste. Release only keys
            // that remain down; never retry V-down or fall back to typing.
            if(count==2){try{Win32.SendInput(1,[input[2]],Marshal.SizeOf<Win32.Input>());}catch{}}
            // Attempt Ctrl-up even if V-up cleanup failed or was blocked.
            try{Win32.SendInput(1,[input[3]],Marshal.SizeOf<Win32.Input>());}catch{}
        }
        return (int)count;
    }
}
