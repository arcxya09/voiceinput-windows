using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace RealtimeTranscription.Desktop;

public sealed class VoiceOverlay : Window
{
    private readonly TextBlock title, preview, elapsed, warning;
    private readonly ProgressBar meter;
    private readonly System.Windows.Threading.DispatcherTimer hide = new() { Interval = TimeSpan.FromSeconds(4) };
    private IntPtr targetMonitor;
    private long startedAt;
    private bool positioning, dismissPending;

    public VoiceOverlay()
    {
        Width = 610; SizeToContent = SizeToContent.Height; WindowStyle = WindowStyle.None;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowActivated = false;
        ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize; Focusable = false;
        var border = new Border { Background = new SolidColorBrush(Color.FromRgb(25, 46, 62)), CornerRadius = new CornerRadius(12), Padding = new Thickness(20, 14, 20, 16), Margin = new Thickness(6), BorderBrush = new SolidColorBrush(Color.FromRgb(92, 140, 145)), BorderThickness = new Thickness(1) };
        var stack = new StackPanel();
        var heading = new DockPanel();
        elapsed = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(164, 193, 194)), Margin = new Thickness(12, 2, 0, 0) };
        DockPanel.SetDock(elapsed, Dock.Right); heading.Children.Add(elapsed);
        title = new TextBlock { Text = "准备麦克风…", FontSize = 14, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap }; heading.Children.Add(title);
        preview = new TextBlock { FontSize = 19, Foreground = new SolidColorBrush(Color.FromRgb(205, 233, 225)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 0) };
        meter = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4, Foreground = new SolidColorBrush(Color.FromRgb(110, 206, 178)), Background = new SolidColorBrush(Color.FromRgb(48, 73, 87)), Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        warning = new TextBlock { FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(255, 208, 145)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        stack.Children.Add(heading); stack.Children.Add(preview); stack.Children.Add(meter); stack.Children.Add(warning); border.Child = stack; Content = border;
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(h, -20, new IntPtr(GetWindowLongPtr(h, -20).ToInt64() | 0x08000000 | 0x80 | 0x20));
            HwndSource.FromHwnd(h)?.AddHook((IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (message == 0x21) { handled = true; return new IntPtr(3); }
                return IntPtr.Zero;
            });
        };
        SizeChanged += (_, _) => Position();
        hide.Tick += (_, _) => { hide.Stop(); if (warning.Text.Length == 0) Hide(); };
    }

    public void BeginTurn()
    {
        targetMonitor = MonitorFromWindow(GetForegroundWindow(), 2);
        startedAt = Environment.TickCount64;
        Update("准备麦克风…");
    }

    public void SetMeter(float value, bool recording)
    {
        meter.Value = recording ? Math.Clamp(value, 0, 1) : 0;
        meter.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        elapsed.Text = recording && startedAt > 0 ? TimeSpan.FromMilliseconds(Environment.TickCount64 - startedAt).ToString(@"m\:ss") : "";
    }

    public void Update(string status, string text = "", bool dismiss = false)
    {
        dismissPending = dismiss;
        title.Text = status; preview.Text = text;
        preview.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        hide.Stop();
        if (dismiss) { SetMeter(0, false); startedAt = 0; }
        if (targetMonitor == IntPtr.Zero) targetMonitor = MonitorFromWindow(GetForegroundWindow(), 2);
        new WindowInteropHelper(this).EnsureHandle();
        Position();
        if (!IsVisible) Show();
        UpdateLayout(); Position();
        if (dismiss) hide.Start();
    }

    public void SetPersistentWarning(string text)
    {
        if (warning.Text == text) return;
        warning.Text = text; warning.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (text.Length > 0 && !IsVisible) Update("有内容尚未保存", dismiss: true);
        else if (text.Length == 0 && dismissPending) hide.Start();
        if (IsVisible) { UpdateLayout(); Position(); }
    }

    private void Position()
    {
        if (positioning || targetMonitor == IntPtr.Zero) return;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(targetMonitor, ref info))
        {
            targetMonitor = MonitorFromWindow(GetForegroundWindow(), 2);
            if (!GetMonitorInfo(targetMonitor, ref info)) return;
        }
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        positioning = true;
        try
        {
            // Native coordinates preserve negative monitor origins and avoid applying
            // the primary screen's DPI to a target on another monitor.
            if (MonitorFromWindow(hwnd, 2) != targetMonitor)
                SetWindowPos(hwnd, IntPtr.Zero, info.Work.Left + 16, info.Work.Top + 16, 0, 0, 0x15);
            uint dpi = GetDpiForWindow(hwnd); double scale = (dpi == 0 ? 96 : dpi) / 96.0;
            double available = Math.Max(160, (info.Work.Right - info.Work.Left) / scale - 32);
            Width = Math.Min(610, available); UpdateLayout();
            int width = (int)Math.Ceiling((ActualWidth > 0 ? ActualWidth : Width) * scale);
            int height = (int)Math.Ceiling((ActualHeight > 0 ? ActualHeight : 160) * scale);
            int left = info.Work.Left + Math.Max(0, (info.Work.Right - info.Work.Left - width) / 2);
            int top = Math.Max(info.Work.Top + 8, info.Work.Bottom - height - (int)(28 * scale));
            SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0, 0x15);
        }
        finally { positioning = false; }
    }

    public void Clear() { hide.Stop(); startedAt = 0; SetMeter(0, false); Hide(); }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
