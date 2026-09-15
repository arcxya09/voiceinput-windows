using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace RealtimeTranscription.Desktop;

/// <summary>Maintains a visible capsule's native Z order without activating or showing it.</summary>
internal sealed class OverlayTopmostGuard : IDisposable
{
    private const long WsExTopmost = 0x8;
    private readonly IntPtr hwnd;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool disposed;

    internal bool IsRunning => timer.IsEnabled;

    public OverlayTopmostGuard(IntPtr hwnd)
    {
        this.hwnd = hwnd;
        timer.Tick += CheckTopmost;
    }

    public void Start()
    {
        if (disposed) return;
        if (!timer.IsEnabled) timer.Start();
        CheckTopmost(null, null);
    }

    public void Stop() => timer.Stop();

    private void CheckTopmost(object? sender, object? args)
    {
        if (disposed) return;
        // AppWindow can also be hidden by its owner or the Shell. Never revive
        // a hidden window, and do not keep polling after it is hidden.
        if (!IsWindowVisible(hwnd)) { Stop(); return; }
        if (IsIconic(hwnd) || !GetWindowRect(hwnd, out var bounds)) return;
        if ((GetWindowLongPtr(hwnd, -20).ToInt64() & WsExTopmost) != 0 && !IsCovered(bounds)) return;

        // Another topmost window can move above an already-topmost capsule.
        // Repair only when needed; never activate, resize, move or show it.
        _ = SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0213); // NOOWNERZORDER | NOACTIVATE | NOMOVE | NOSIZE
    }

    private bool IsCovered(Rect bounds)
    {
        // Walk only windows above this one. Bound the traversal because another
        // process can change the window list while it is being inspected.
        IntPtr above = GetWindow(hwnd, 3); // GW_HWNDPREV
        for (int visited = 0; above != IntPtr.Zero && above != hwnd && visited < 256; visited++)
        {
            if (IsWindowVisible(above) && !IsIconic(above) &&
                GetWindowRect(above, out var other) &&
                other.Left < bounds.Right && other.Right > bounds.Left &&
                other.Top < bounds.Bottom && other.Bottom > bounds.Top &&
                (DwmGetWindowAttribute(above, 14, out int cloaked, sizeof(int)) != 0 || cloaked == 0))
                return true;
            above = GetWindow(above, 3);
        }
        return false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();
        timer.Tick -= CheckTopmost;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
}
