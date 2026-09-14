using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RealtimeTranscription.Desktop;

/// <summary>Lets DWM compose the native border, shadow and rounded corners of an auxiliary WinUI window.</summary>
internal sealed class PopupWindowChrome : IDisposable
{
    private const int GwlStyle = -16, GwlExStyle = -20;
    private const long WsPopup = 0x80000000L;
    private const long WsCaption = 0x00C00000L, WsThickFrame = 0x00040000L;
    private const long WindowCommands = 0x00080000L | 0x00030000L;
    private const long ExtendedFrameStyles = 0x00000001L | 0x00000200L | 0x00020000L;
    private const long WsExToolWindow = 0x80, WsExWindowEdge = 0x100, WsExAppWindow = 0x40000;
    private const long WsExNoActivate = 0x08000000, WsExTransparent = 0x20, WsExLayered = 0x80000;
    private const nuint SubclassId = 0x56494348;
    private readonly IntPtr hwnd;
    private readonly bool clickThrough;
    private readonly SubclassProc windowProc;
    private bool disposed, dark;
    private int borderColor = -1; // DWMWA_COLOR_DEFAULT

    public PopupWindowChrome(IntPtr hwnd, bool clickThrough)
    {
        this.hwnd = hwnd;
        this.clickThrough = clickThrough;
        windowProc = WindowProc;
        if (!SetWindowSubclass(hwnd, windowProc, SubclassId, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置辅助窗口边框。");

        long style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(NormalizeStyle(style)));
        long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(NormalizeExtendedStyle(exStyle)));
        if (clickThrough && !SetLayeredWindowAttributes(hwnd, 0, 255, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置识别浮窗的鼠标穿透。");

        // Constant alpha keeps the rectangular WinUI surface fully opaque.
        // GDI regions and per-pixel-alpha shapes would prevent native rounding.
        ApplyFramePolicy();
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0037);
    }

    public void UpdateAppearance(bool dark, bool highContrast, Windows.UI.Color foreground)
    {
        if (disposed) return;
        this.dark = dark;
        borderColor = highContrast ? foreground.R | (foreground.G << 8) | (foreground.B << 16) : -1;
        ApplyFramePolicy();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        RemoveWindowSubclass(hwnd, windowProc, SubclassId);
    }

    private void ApplyFramePolicy()
    {
        if (disposed) return;
        SetDwmAttribute(2, 2); // DWMNCRP_ENABLED
        SetDwmAttribute(3, 0); // Native show/hide transitions.
        SetDwmAttribute(20, dark ? 1 : 0);
        SetDwmAttribute(33, clickThrough ? 2 : 3); // ROUND / ROUNDSMALL
        SetDwmAttribute(34, borderColor);
        var margins = new Margins { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        // Windows 10 and remote/VM sessions may choose square corners.
        // Honor that native policy instead of adding a hard-edged region.
    }

    private void SetDwmAttribute(uint attribute, int value)
        => _ = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    private static long NormalizeStyle(long style)
        => (style & ~WindowCommands) | WsPopup | WsCaption | WsThickFrame;

    private long NormalizeExtendedStyle(long style)
    {
        // Keep the native palette-window edge alongside DWM frame styles.
        // Remove only the additional dialog, client and static 3D edges.
        style = (style | WsExToolWindow | WsExWindowEdge) & ~(ExtendedFrameStyles | WsExAppWindow);
        return clickThrough
            ? style | WsExNoActivate | WsExTransparent | WsExLayered
            : style & ~(WsExNoActivate | WsExTransparent | WsExLayered);
    }

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (message == 0x0083) return IntPtr.Zero; // WM_NCCALCSIZE: full content, no caption or resize inset.
        if (message == 0x0084) return new IntPtr(clickThrough ? -1 : 1); // HTTRANSPARENT / HTCLIENT
        if (message == 0x0021 && clickThrough) return new IntPtr(3); // MA_NOACTIVATE
        // Leave non-client painting/activation to DWM for the native outline.
        var result = DefSubclassProc(window, message, wParam, lParam);
        if (message == 0x007C && lParam != IntPtr.Zero)
        {
            int index = unchecked((int)wParam.ToInt64());
            if (index is GwlStyle or GwlExStyle)
            {
                // WinUI can restore cached frame flags on Show/Activate.
                var styles = Marshal.PtrToStructure<StyleStruct>(lParam);
                styles.NewStyle = unchecked((uint)(index == GwlStyle
                    ? NormalizeStyle(styles.NewStyle) : NormalizeExtendedStyle(styles.NewStyle)));
                Marshal.StructureToPtr(styles, lParam, false);
                return IntPtr.Zero;
            }
        }
        if (message == 0x0082) Dispose();
        else if (message is 0x02E0 or 0x031A or 0x031E) ApplyFramePolicy();
        return result;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct StyleStruct { public uint OldStyle, NewStyle; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
