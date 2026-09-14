using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RealtimeTranscription.Desktop;

/// <summary>
/// Gives an auxiliary WinUI HWND one frame, owned entirely by its XAML content.
/// A rounded XAML Border alone does not clip the rectangular native window.
/// </summary>
internal sealed class PopupWindowChrome : IDisposable
{
    private const int GwlStyle = -16, GwlExStyle = -20;
    private const long WsPopup = 0x80000000L;
    private const long FrameStyles = 0x00C00000L | 0x00040000L | 0x00080000L | 0x00030000L;
    private const long ExtendedFrameStyles = 0x00000001L | 0x00000100L | 0x00000200L | 0x00020000L;
    private const long WsExToolWindow = 0x80, WsExAppWindow = 0x40000;
    private const long WsExNoActivate = 0x08000000, WsExTransparent = 0x20, WsExLayered = 0x80000;
    private const nuint SubclassId = 0x56494348;
    private readonly IntPtr hwnd;
    private readonly double radiusDip;
    private readonly SubclassProc windowProc;
    private int lastWidth, lastHeight, lastDiameter;
    private bool disposed;

    public PopupWindowChrome(IntPtr hwnd, double radiusDip, bool clickThrough)
    {
        this.hwnd = hwnd;
        this.radiusDip = radiusDip;
        windowProc = WindowProc;
        if (!SetWindowSubclass(hwnd, windowProc, SubclassId, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置辅助窗口边框。");

        // Presenter.SetBorderAndTitleBar(false, false) can leave an SDK-owned
        // nonclient inset. Remove native frame styles, then force recalculation
        // through our WM_NCCALCSIZE handler before the first visible frame.
        long style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr((style & ~FrameStyles) | WsPopup));
        long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        exStyle = (exStyle | WsExToolWindow) & ~(ExtendedFrameStyles | WsExAppWindow);
        if (clickThrough)
        {
            // WS_EX_TRANSPARENT alone only affects painting order. Windows
            // documents cross-process mouse pass-through for layered windows.
            // Alpha remains fully opaque; no colour key or translucent backing.
            exStyle |= WsExNoActivate | WsExTransparent | WsExLayered;
        }
        else exStyle &= ~(WsExNoActivate | WsExTransparent | WsExLayered);
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle));
        if (clickThrough && !SetLayeredWindowAttributes(hwnd, 0, 255, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置识别浮窗的鼠标穿透。");

        ApplyFramePolicy();
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0037); // FRAMECHANGED, NOACTIVATE, NOMOVE, NOSIZE, NOZORDER
        UpdateRegion();
    }

    public void UpdateRegion()
    {
        if (disposed || !GetWindowRect(hwnd, out var rect)) return;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return;
        uint dpi = GetDpiForWindow(hwnd);
        int diameter = Math.Clamp((int)Math.Round(radiusDip * 2 * (dpi == 0 ? 1 : dpi / 96.0)), 1, Math.Min(width, height));
        if (width == lastWidth && height == lastHeight && diameter == lastDiameter) return;
        IntPtr region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        if (region == IntPtr.Zero) return;
        // Windows owns a region only after SetWindowRgn succeeds. Cache before
        // applying it because the API synchronously sends window-position events.
        (int oldWidth, int oldHeight, int oldDiameter) = (lastWidth, lastHeight, lastDiameter);
        (lastWidth, lastHeight, lastDiameter) = (width, height, diameter);
        if (SetWindowRgn(hwnd, region, true) == 0)
        {
            (lastWidth, lastHeight, lastDiameter) = (oldWidth, oldHeight, oldDiameter);
            DeleteObject(region);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        RemoveWindowSubclass(hwnd, windowProc, SubclassId);
    }

    private void ApplyFramePolicy()
    {
        // Custom regions and DWM rounding cannot be combined. Explicitly use
        // our same region on Windows 10, Windows 11 and remote/VM desktops.
        // Unsupported Windows 11 attributes simply return E_INVALIDARG on 10.
        SetDwmAttribute(2, 1);                  // DWMWA_NCRENDERING_POLICY: DISABLED
        SetDwmAttribute(33, 1);                 // DWMWA_WINDOW_CORNER_PREFERENCE: DONOTROUND
        SetDwmAttribute(34, unchecked((int)0xFFFFFFFE)); // DWMWA_BORDER_COLOR: NONE
        SetDwmAttribute(3, 1);                  // No stale rectangular show/hide transition
    }

    private void SetDwmAttribute(uint attribute, int value)
        => _ = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (message == 0x0083) return IntPtr.Zero; // WM_NCCALCSIZE: entire window is the client area
        if (message == 0x0085) return IntPtr.Zero; // WM_NCPAINT: XAML paints the sole visible border
        var result = DefSubclassProc(window, message, wParam, lParam);
        if (message == 0x0082) Dispose();         // WM_NCDESTROY
        else if (message is 0x0005 or 0x02E0) UpdateRegion(); // WM_SIZE / WM_DPICHANGED
        else if (message is 0x031A or 0x031E)    // Theme or desktop-composition change
        {
            ApplyFramePolicy();
            UpdateRegion();
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
