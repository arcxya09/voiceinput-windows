using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace RealtimeTranscription.Desktop.Input;

public record NativeTarget(IntPtr Window,IntPtr Focus,uint Thread,uint Process);
public record InputTarget(NativeTarget Native,int[] RuntimeId,AutomationElement Element,TextPatternRange? Selection);
public record DeliveryResult(string State,string Message,int Accepted=0);
public record PhysicalSignal(string Kind,int Key=0,long At=0);

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
    public static NativeTarget? Current()
    {
        var hwnd=GetForegroundWindow();if(hwnd==IntPtr.Zero||!IsWindowEnabled(hwnd))return null;
        uint thread=GetWindowThreadProcessId(hwnd,out uint process);if(process==(uint)Environment.ProcessId)return null;
        var gui=new Gui{Size=(uint)Marshal.SizeOf<Gui>()};
        if(!GetGUIThreadInfo(thread,ref gui)||gui.Focus==IntPtr.Zero)return null;
        return new(hwnd,gui.Focus,thread,process);
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
    private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint threadId;
    private IntPtr keyHook,mouseHook;
    private int trigger=0xA3;
    private volatile bool held;
    private volatile bool enabled=true;
    public Task Ready=>ready.Task;
    public int Trigger=>Volatile.Read(ref trigger);
    public PhysicalHook(Action<PhysicalSignal> signal)
    {
        this.signal=signal;keyboard=Key;mouse=Mouse;
        thread=new Thread(Run){IsBackground=true,Name="Voice input physical key hook"};thread.Start();
    }
    public void Configure(string key){int next=key=="F8"?0x77:key=="F9"?0x78:0xA3;if(Win32.Down(Trigger)||Win32.Down(next))throw new InvalidOperationException("请松开说话键后再修改设置。");held=false;Volatile.Write(ref trigger,next);}
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
                if(vk==Trigger)
                {
                    if(down&&!held){held=true;if(enabled)signal(new("down",vk,Environment.TickCount64));}
                    if(up&&held){held=false;signal(new("up",vk,Environment.TickCount64));}
                }
                else if(down)signal(new(vk==27?"escape":"activity",vk,Environment.TickCount64));
            }
        }
        return Win32.CallNextHookEx(keyHook,code,w,l);
    }
    private IntPtr Mouse(int code,IntPtr w,IntPtr l)
    {
        if(code>=0&&w.ToInt64() is 0x201 or 0x204 or 0x207 or 0x20b or 0x20a or 0x20e)
        {
            var m=Marshal.PtrToStructure<Win32.MouseData>(l);if((m.Flags&1)==0)signal(new("activity",At:Environment.TickCount64));
        }
        return Win32.CallNextHookEx(mouseHook,code,w,l);
    }
    public void Dispose(){enabled=false;if(threadId!=0)Win32.PostThreadMessage(threadId,0x12,UIntPtr.Zero,IntPtr.Zero);thread.Join(1000);}
}

public static class TextDelivery
{
    // A hung accessibility provider cannot consume an unbounded number of worker threads.
    private static readonly SemaphoreSlim queryGate=new(1,1);
    private static async Task<T?> Query<T>(Func<T?> fn) where T:class
    {
        if(!await queryGate.WaitAsync(0))return null;
        var work=Task.Run(()=>{try{return fn();}catch{return null;}finally{queryGate.Release();}});
        try{return await work.WaitAsync(TimeSpan.FromMilliseconds(300));}catch(TimeoutException){return null;}
    }
    public static Task<InputTarget?> CaptureAsync(NativeTarget native)=>Query(()=>
    {
        if(Win32.Current()!=native||Win32.Composing(native.Focus))return null;
        var element=AutomationElement.FocusedElement;
        if(element==null||element.Current.IsPassword||!element.Current.IsEnabled||!element.Current.IsKeyboardFocusable||element.Current.ProcessId!=(int)native.Process)return null;
        bool editable=false;TextPatternRange? selection=null;
        if(element.TryGetCurrentPattern(ValuePattern.Pattern,out var vp))editable=!((ValuePattern)vp).Current.IsReadOnly;
        if(element.TryGetCurrentPattern(TextPattern.Pattern,out var tp))
        {
            var pattern=(TextPattern)tp;object attribute=pattern.DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
            editable|=attribute is bool readOnly&&!readOnly;
            var ranges=pattern.GetSelection();if(ranges.Length==1)selection=ranges[0].Clone();
        }
        if(!editable)return null;
        return new InputTarget(native,element.GetRuntimeId(),element,selection);
    });
    private static Task<InputTarget?> ValidateAsync(InputTarget target)=>Query(()=>
    {
        if(Win32.Current()!=target.Native||Win32.Composing(target.Native.Focus))return null;
        var current=AutomationElement.FocusedElement;
        if(current==null||current.Current.IsPassword||!current.Current.IsEnabled||!current.GetRuntimeId().SequenceEqual(target.RuntimeId))return null;
        if(current.TryGetCurrentPattern(ValuePattern.Pattern,out var value)&&((ValuePattern)value).Current.IsReadOnly)return null;
        if(target.Selection!=null&&current.TryGetCurrentPattern(TextPattern.Pattern,out var p))
        {var selected=((TextPattern)p).GetSelection();if(selected.Length!=1||!selected[0].Compare(target.Selection))return null;}
        return target;
    });
    public static async Task<DeliveryResult> SendAsync(InputTarget target,string text,Func<bool> valid,CancellationToken token)
    {
        if(text.Length==0)return new("Empty","没有需要输入的文字。");
        long end=Environment.TickCount64+200;
        bool Modified()=>new[]{0x10,0x11,0x12,0x5b,0x5c}.Any(Win32.Down);
        while(Modified()&&Environment.TickCount64<end&&!token.IsCancellationRequested)await Task.Delay(10);
        if(Modified()||!valid()||token.IsCancellationRequested||await ValidateAsync(target)==null)return new("Blocked","输入位置已变化、存在组合输入或修饰键未释放。结果已保留，可手动复制。");
        int accepted=0;long deadline=Environment.TickCount64+Math.Clamp(2000L+text.Length*2L,2000L,60000L);
        // Recheck window and activity per batch. Selection is expected to move after our first batch.
        for(int offset=0;offset<text.Length;)
        {
            if(!valid()||token.IsCancellationRequested||Win32.Current()!=target.Native||Modified()||Environment.TickCount64>=deadline)
                return new(accepted>0?"Partial":"Blocked",accepted>0?"文字可能只输入了一部分。请检查目标内容，不会自动重发。":"输入目标已变化，文字已保留。",accepted);
            int length=Math.Min(128,text.Length-offset);if(offset+length<text.Length&&char.IsHighSurrogate(text[offset+length-1]))length--;
            var input=new Win32.Input[length*2];
            for(int i=0;i<length;i++)
            {
                input[i*2]=new(){Type=1,Data=new(){Key=new(){Scan=text[offset+i],Flags=4,Extra=new UIntPtr(Win32.Marker)}}};
                input[i*2+1]=new(){Type=1,Data=new(){Key=new(){Scan=text[offset+i],Flags=6,Extra=new UIntPtr(Win32.Marker)}}};
            }
            uint count=Win32.SendInput((uint)input.Length,input,Marshal.SizeOf<Win32.Input>());accepted+=(int)count;
            if(count!=input.Length)
            {
                if(count%2==1){var release=input[(int)count];Win32.SendInput(1,[release],Marshal.SizeOf<Win32.Input>());}
                return new(accepted==0?"Blocked":"Partial",accepted==0?"目标未接受文字，可能受权限或控件限制。请手动复制。":"文字可能只输入了一部分。请检查目标内容，不会自动重发。",accepted);
            }
            offset+=length;
            // Revalidate the focused automation element without comparing the original selection.
            if(offset<text.Length&&await ValidateAsync(target with{Selection=null})==null)return new("Partial","输入期间焦点已变化，已停止后续文字。请检查目标内容。",accepted);
        }
        return new("Sent","文字已交给目标应用。",accepted);
    }
}
