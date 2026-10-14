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
    [DllImport("imm32.dll")]public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);
    [DllImport("user32.dll")]public static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern int GetClassName(IntPtr hwnd,System.Text.StringBuilder name,int count);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]public static extern IntPtr GetWindowLongPtr(IntPtr hwnd,int index);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]public static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint message,IntPtr w,string l,uint flags,uint timeout,out IntPtr result);
    [DllImport("user32.dll",SetLastError=true)]public static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint message,IntPtr w,IntPtr l,uint flags,uint timeout,out IntPtr result);
    public const uint AbortIfHung=0x2;
    // Our own injected events seen by the low-level hook. Typing waits for the
    // system to route them before releasing the temporary IME state.
    public static long MarkerEvents;
    public static volatile bool MarkerHookActive;
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
    public static string ClassName(IntPtr hwnd)
    {
        var name=new System.Text.StringBuilder(256);
        return GetClassName(hwnd,name,name.Capacity)>0?name.ToString():"";
    }
    // Window classes that implement the standard edit-control message contract,
    // including WinForms, Delphi and Notepad's RichEdit variants.
    public static bool IsStandardEdit(string name)=>
        name.Equals("Edit",StringComparison.OrdinalIgnoreCase)||name.StartsWith("RichEdit",StringComparison.OrdinalIgnoreCase)||
        name is "TEdit" or "TMemo"||
        name.StartsWith("WindowsForms10.EDIT.",StringComparison.OrdinalIgnoreCase)||name.StartsWith("WindowsForms10.RichEdit",StringComparison.OrdinalIgnoreCase);
    // Native styles of a standard edit control are authoritative even when its
    // accessibility provider cannot be queried.
    public static string? EditRefusal(IntPtr hwnd)
    {
        if(!IsStandardEdit(ClassName(hwnd)))return null;
        long style=GetWindowLongPtr(hwnd,-16).ToInt64();
        return (style&0x20)!=0?"Password":(style&0x800)!=0?"ReadOnly":null;
    }
    // Chinese, Japanese and Korean layouts route keystrokes through an IME.
    public static bool ImeLayout(uint thread)=>(GetKeyboardLayout(thread).ToInt64()&0x3ff) is 0x04 or 0x11 or 0x12;
    public static int? ImeControl(IntPtr ime,int command,int value)
        =>SendMessageTimeout(ime,0x283,new IntPtr(command),new IntPtr(value),AbortIfHung,300,out var result)!=IntPtr.Zero?(int)result.ToInt64():null;
    // A sent message is handled the next time the target thread retrieves a
    // message, so each round trip lets it consume at most one queued input event.
    public static bool Ping(IntPtr hwnd)=>SendMessageTimeout(hwnd,0,IntPtr.Zero,IntPtr.Zero,AbortIfHung,250,out _)!=IntPtr.Zero;
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
            Win32.MarkerHookActive=true;ready.TrySetResult();while(Win32.GetMessage(out _,IntPtr.Zero,0,0)>0){}
        }
        catch(Exception e){ready.TrySetException(e);}
        finally{Win32.MarkerHookActive=false;if(keyHook!=IntPtr.Zero)Win32.UnhookWindowsHookEx(keyHook);if(mouseHook!=IntPtr.Zero)Win32.UnhookWindowsHookEx(mouseHook);}
    }
    private IntPtr Key(int code,IntPtr w,IntPtr l)
    {
        if(code>=0)
        {
            var k=Marshal.PtrToStructure<Win32.KeyData>(l);
            if((k.Flags&0x10)!=0&&k.Extra.ToUInt64()==Win32.Marker)Interlocked.Increment(ref Win32.MarkerEvents);
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

    // Delivery follows the current focus inside the original top-level window,
    // as an IME commits at the caret: editors move child focus or rebuild their
    // accessibility elements while the user speaks. Another window never receives text.
    private static async Task<NativeTarget?> CurrentTargetAsync(NativeTarget original,Func<bool> valid,CancellationToken token)
    {
        // A toast or flyout can hold the foreground briefly; wait for the original.
        long deadline=Environment.TickCount64+800;
        while(true)
        {
            if(!valid()||token.IsCancellationRequested)return null;
            var current=Win32.Current();
            if(current!=null&&current.Window==original.Window&&current.Thread==original.Thread&&current.Process==original.Process)return current;
            if(Environment.TickCount64>=deadline)return null;
            try{await Task.Delay(25,token);}catch(OperationCanceledException){return null;}
        }
    }
    // Stateless check of whatever is focused now. An unknown provider does not
    // block input; a recognized password, disabled or read-only control does.
    private static async Task<string> InspectAsync(NativeTarget current,CancellationToken token)
    {
        var result=await Query(new("Inspect",current.Window.ToInt64(),current.Focus.ToInt64(),current.Thread,current.Process),token,timeoutMs:600);
        return result?.Reply.Code??"Unavailable";
    }
    private static bool Modified()=>new[]{0x10,0x11,0x12,0x5b,0x5c}.Any(Win32.Down);
    private static async Task<bool> ModifiersReleasedAsync(CancellationToken token)
    {
        long end=Environment.TickCount64+200;
        while(Modified()&&Environment.TickCount64<end&&!token.IsCancellationRequested)await Task.Delay(10);
        return !Modified();
    }

    public const int MaxTypedCharacters=4000;
    private static DeliveryResult NotTyped(string diagnostic)=>new("Unsupported","未直接输入。") {Diagnostic=diagnostic};

    // Native insertion without the clipboard. "Unsupported" guarantees nothing was
    // sent, so the caller may fall back to paste; any other state may have reached
    // the target and must never be repeated by another route.
    public static async Task<DeliveryResult> TypeAsync(InputTarget target,string text,Func<bool> valid,CancellationToken token,Action? dispatched=null)
    {
        if(text.Length==0)return new("Empty","没有需要输入的文字。");
        if(!ClipboardPaste.IsSupportedText(text))return NotTyped("TextUnsupported");
        if(!await ModifiersReleasedAsync(token))return NotTyped("ModifiersHeld");
        var current=await CurrentTargetAsync(target.Native,valid,token);
        if(current==null)return NotTyped("TargetChanged");
        string inspection=Win32.EditRefusal(current.Focus)??await InspectAsync(current,token);
        if(inspection is "Password" or "Disabled" or "ReadOnly")return NotTyped("Target"+inspection);
        if(!valid()||token.IsCancellationRequested)return NotTyped("Interrupted");
        if(Win32.IsStandardEdit(Win32.ClassName(current.Focus)))
        {
            var replaced=await Task.Run(()=>ReplaceSelection(current,text,valid,dispatched));
            if(replaced.State!="Unsupported")return replaced;
        }
        if(text.Length>MaxTypedCharacters)return NotTyped("TextTooLong");
        // Line breaks and tabs are keys with application meanings (send a chat
        // message, move focus). Whole-text paste keeps them as text.
        if(text.Any(c=>c<0x20||c==0x7f))return NotTyped("ControlCharacters");
        return await TypeUnicodeAsync(current,text,valid,token,dispatched);
    }

    // Standard edit controls insert at the caret or replace the selection in one
    // message, with the control's own undo. The system marshals the string.
    private static DeliveryResult ReplaceSelection(NativeTarget current,string text,Func<bool> valid,Action? dispatched)
    {
        long style=Win32.GetWindowLongPtr(current.Focus,-16).ToInt64();
        string body=text.Replace("\r\n","\n",StringComparison.Ordinal).Replace('\r','\n');
        if(body.Contains('\n'))
        {
            if((style&0x4)==0)return NotTyped("EditSingleLine");
            body=body.Replace("\n","\r\n",StringComparison.Ordinal);
        }
        if(!valid()||Win32.Current()!=current||Modified())return NotTyped("Interrupted");
        IntPtr sent=Win32.SendMessageTimeout(current.Focus,0xC2,new IntPtr(1),body,Win32.AbortIfHung,2000,out _);
        if(sent!=IntPtr.Zero)
        {
            dispatched?.Invoke();
            return new("Sent","已直接输入到光标处，剪贴板未改动。",text.Length) {Diagnostic="EditReplaceSelection"};
        }
        // A timed-out message can still be processed later: never send it again.
        if(Marshal.GetLastWin32Error()==1460)
        {
            dispatched?.Invoke();
            return new("Unknown","输入结果需要核对，请检查目标内容。不会自动重发。") {Diagnostic="EditReplaceTimeout"};
        }
        return NotTyped("EditReplaceRejected");
    }

    // Other editors receive the text as Unicode key events (VK_PACKET/WM_CHAR) in
    // one SendInput call, which the system never interleaves with physical input.
    private static async Task<DeliveryResult> TypeUnicodeAsync(NativeTarget current,string text,Func<bool> valid,CancellationToken token,Action? dispatched)
    {
        // A CJK IME in native mode may convert injected characters into
        // punctuation or candidates. Close it while typing, then restore it.
        IntPtr ime=Win32.ImmGetDefaultIMEWnd(current.Focus);
        int? open=ime==IntPtr.Zero?null:Win32.ImeControl(ime,5,0);
        bool reopen=false;
        if(open is null){if(Win32.ImeLayout(current.Thread))return NotTyped("ImeStateUnknown");}
        else if(open!=0)
        {
            if(Win32.ImeControl(ime,6,0) is null||Win32.ImeControl(ime,5,0)!=0)
            {Win32.ImeControl(ime,6,1);return NotTyped("ImeNotClosed");}
            reopen=true;
        }
        try
        {
            if(!valid()||token.IsCancellationRequested||Win32.Current()!=current||Modified())return NotTyped("Interrupted");
            static Win32.Input Key(char unit,bool up)=>new(){Type=1,Data=new(){Key=new(){Vk=0,Scan=unit,Flags=4u|(up?2u:0u),Extra=new UIntPtr(Win32.Marker)}}};
            var input=new Win32.Input[text.Length*2];
            for(int index=0;index<text.Length;index++){input[index*2]=Key(text[index],false);input[index*2+1]=Key(text[index],true);}
            long before=Interlocked.Read(ref Win32.MarkerEvents);
            uint count=Win32.SendInput((uint)input.Length,input,Marshal.SizeOf<Win32.Input>());
            if(count==0)return NotTyped("InputRejected");
            dispatched?.Invoke();
            if(count<input.Length)
            {
                // Release an unmatched key-down; never replay text events.
                if(count%2==1){try{if(Win32.SendInput(1,[input[count]],Marshal.SizeOf<Win32.Input>())==1)count++;}catch{}}
                await DrainAsync(current.Focus,before,count);
                return new("Partial","只输入了部分文字，请核对目标内容。不会自动重发。",(int)count) {Diagnostic="UnicodeInputPartial"};
            }
            await DrainAsync(current.Focus,before,count);
            return new("Sent","已直接输入到光标处，剪贴板未改动。",(int)count) {Diagnostic=reopen?"UnicodeInputImeClosed":"UnicodeInput"};
        }
        finally
        {
            // Restore only our own change; the user may have switched modes meanwhile.
            if(reopen&&Win32.ImeControl(ime,5,0)==0)Win32.ImeControl(ime,6,1);
        }
    }

    // Keep the IME closed until the target has taken the injected events: first
    // until the system has routed them through the hook chain, then one sent-message
    // round trip per queued message, because the target handles sent messages first.
    private static async Task DrainAsync(IntPtr focus,long before,uint events)
    {
        long deadline=Environment.TickCount64+Math.Clamp(500L+events*2L,500L,5000L);
        if(Win32.MarkerHookActive)
            while(Interlocked.Read(ref Win32.MarkerEvents)-before<events&&Environment.TickCount64<deadline)await Task.Delay(5);
        await Task.Delay(20);
        await Task.Run(()=>
        {
            for(long round=0;round<events*2L+16&&Environment.TickCount64<deadline;round++)
                if(!Win32.Ping(focus))break;
        });
        await Task.Delay(20);
    }

    public static async Task<DeliveryResult> SendAsync(InputTarget target,string text,Func<bool> valid,CancellationToken token,Action? dispatched=null,uint? preparedSequence=null)
    {
        if(text.Length==0)return new("Empty","没有需要输入的文字。");
        if(!await ModifiersReleasedAsync(token))return new("Blocked","修饰键仍未释放，文字已保留，可手动复制。") {Diagnostic="ModifiersHeld"};
        if(!valid()||token.IsCancellationRequested)return new("Blocked","检测到其他操作，文字已保留，可手动复制。");
        var current=await CurrentTargetAsync(target.Native,valid,token);
        if(current==null)return new("Blocked","输入窗口已切换，文字已保留，可手动粘贴。") {Diagnostic="TargetChanged"};
        string inspection=Win32.EditRefusal(current.Focus)??await InspectAsync(current,token);
        if(inspection is "Password" or "Disabled" or "ReadOnly")
            return new("Blocked","当前输入位置不适合自动粘贴，文字已保留，可手动粘贴。") {Diagnostic="Target"+inspection};
        if(Win32.Composing(current.Focus))return new("Blocked","输入法仍有未确认的候选词，文字已保留，可手动复制。");
        string diagnostic="PasteNotPrepared";
        bool Uninterrupted()=>valid()&&Win32.Current()==current;
        var result=await ClipboardPaste.SendAsync(text,async(body,cancellation)=>
            {
                if(preparedSequence is uint existing)
                {
                    // The finished result was already copied. Do not rewrite the
                    // clipboard or adopt a newer copy made by someone else.
                    if(Win32.GetClipboardSequenceNumber()!=existing)
                    {diagnostic="ClipboardChangedAfterCopy";return null;}
                    diagnostic="CopiedTextReady";
                    return existing;
                }
                uint previous=Win32.GetClipboardSequenceNumber();
                var prepared=await Query(new("PrepareNativePaste",current.Window.ToInt64(),current.Focus.ToInt64(),current.Thread,current.Process,
                    Text:body,ClipboardSequence:previous),cancellation,timeoutMs:3000);
                diagnostic=prepared?.Reply.Code??"PasteWorkerUnavailable";
                return prepared is {} response&&response.Reply.Code=="Ready"?response.Reply.ClipboardSequence:null;
            },
            sequence=>
            {
                bool unchanged=Win32.GetClipboardSequenceNumber()==sequence;
                if(!unchanged)diagnostic="ClipboardChangedAfterPrepare";
                return unchanged;
            },
            ()=>Uninterrupted()&&!Modified()&&!Win32.Composing(current.Focus),
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
