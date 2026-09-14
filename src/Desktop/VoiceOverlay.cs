using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RealtimeTranscription.Core;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace RealtimeTranscription.Desktop;

/// <summary>A small, non-activating WinUI window. All public methods run on the UI thread.</summary>
public sealed class VoiceOverlay : Window
{
    private const double WidthDip = 360, HeightDip = 76, BottomMarginDip = 24;
    private readonly TextBlock title, preview, elapsed, warning, measure;
    private readonly Border root;
    private readonly Grid surface;
    private readonly PopupWindowChrome chrome;
    private readonly Grid heading, rows;
    private readonly UISettings uiSettings = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly Border[] levels = new Border[5];
    private readonly DispatcherTimer hide = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly SubclassProc windowProc;
    private readonly IntPtr hwnd;
    private IntPtr targetMonitor;
    private long startedAt;
    private string previewSource = "", persistentWarning = "";
    private bool positioning, dismissPending, closed, repositionQueued;
    private double accessibleWidthDip = WidthDip, accessibleHeightDip = HeightDip;

    public VoiceOverlay()
    {
        Title = "VoiceInput";
        var white = Brush(241, 245, 249);
        var muted = Brush(157, 175, 193);
        var accent = Brush(104, 219, 186);
        title = Label("OverlayStatus", 12, white);
        title.Text = "准备麦克风…";
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        title.TextAlignment = TextAlignment.Center;
        title.HorizontalAlignment = HorizontalAlignment.Stretch;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        elapsed = Label("OverlayElapsed", 11, muted);
        elapsed.HorizontalAlignment = HorizontalAlignment.Right;
        warning = Label("OverlayWarning", 10, Brush(255, 202, 119));
        warning.Visibility = Visibility.Collapsed;
        preview = Label("OverlayPreview", 14, white);
        preview.TextAlignment = TextAlignment.Center;
        preview.HorizontalAlignment = HorizontalAlignment.Stretch;
        preview.TextTrimming = TextTrimming.None;
        measure = Label("", preview.FontSize, white);

        // Equal side columns keep the status on the true centreline even when
        // the timer grows or the save-warning badge replaces the audio meter.
        heading = new Grid { Height = 22, ColumnSpacing = 8 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        var left = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        var wave = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < levels.Length; i++)
        {
            levels[i] = new Border { Width = 3, Height = 4, CornerRadius = new CornerRadius(2), Background = accent, VerticalAlignment = VerticalAlignment.Center, Opacity = .35 };
            wave.Children.Add(levels[i]);
        }
        left.Children.Add(wave);
        left.Children.Add(warning);
        Grid.SetColumn(title, 1); Grid.SetColumn(elapsed, 2);
        heading.Children.Add(left); heading.Children.Add(title); heading.Children.Add(elapsed);
        rows = new Grid { RowSpacing = 4 };
        rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        Grid.SetRow(preview, 1);
        rows.Children.Add(heading); rows.Children.Add(preview);
        root = new Border
        {
            Name = "OverlayRoot", RequestedTheme = ElementTheme.Default,
            Background = Brush(24, 31, 42), BorderBrush = Brush(67, 80, 96),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 12, 16, 12), Child = rows,
            IsHitTestVisible = false
        };
        // Fill the composition backing as well as the rounded border. Otherwise
        // its antialiased corners can blend with WinUI's black default surface.
        surface = new Grid { Background = root.Background };
        surface.Children.Add(root);
        Content = surface;
        ApplyAccessibility();
        root.ActualThemeChanged += (_, _) => { if (!closed) ApplyAccessibility(); };
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false; presenter.IsMinimizable = false;
        presenter.IsMaximizable = false; presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        chrome = new PopupWindowChrome(hwnd, 14, clickThrough: true);
        windowProc = WindowProc;
        SetWindowSubclass(hwnd, windowProc, 1, 0);
        root.Loaded += (_, _) => { Position(); FitPreview(); };
        uiSettings.TextScaleFactorChanged += SystemAppearanceChanged;
        uiSettings.ColorValuesChanged += SystemAppearanceChanged;
        // AccessibilitySettings.HighContrastChanged cannot be registered on
        // some unpackaged desktops (ERROR_NOT_FOUND). Read its supported state
        // property when native setting/color/theme broadcasts arrive instead.
        preview.SizeChanged += (_, _) => FitPreview();
        hide.Tick += (_, _) =>
        {
            hide.Stop();
            if (!closed && persistentWarning.Length == 0) AppWindow.Hide();
        };
        Closed += (_, _) =>
        {
            closed = true; hide.Stop();
            RemoveWindowSubclass(hwnd, windowProc, 1);
            chrome.Dispose();
            uiSettings.TextScaleFactorChanged -= SystemAppearanceChanged;
            uiSettings.ColorValuesChanged -= SystemAppearanceChanged;
        };
    }

    public void BeginTurn()
    {
        if (closed) return;
        targetMonitor = MonitorFromWindow(GetForegroundWindow(), 2);
        startedAt = Environment.TickCount64;
        dismissPending = false;
        SetMeter(0, true);
        Update("准备麦克风…");
    }

    public void SetMeter(float value, bool recording)
    {
        if (closed) return;
        double level = recording && float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        for (int i = 0; i < levels.Length; i++)
        {
            double shape = 1 - Math.Abs(i - 2) * .22;
            levels[i].Height = 4 + 14 * level * shape;
            levels[i].Opacity = recording ? .65 + .35 * level : .35;
            levels[i].Visibility = persistentWarning.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (startedAt > 0 && !dismissPending)
        {
            var duration = TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - startedAt));
            elapsed.Text = duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
        }
        else if (startedAt == 0) elapsed.Text = "";
    }

    public void Update(string status, string text = "", bool dismiss = false)
    {
        if (closed) return;
        dismissPending = dismiss;
        title.Text = UiPresentation.LatestText(status ?? "", 36);
        previewSource = UiPresentation.LatestText(text ?? "", 80);
        FitPreview();
        hide.Stop();
        if (dismiss) SetMeter(0, false);
        if (targetMonitor == IntPtr.Zero) targetMonitor = MonitorFromWindow(GetForegroundWindow(), 2);
        Position();
        if (!AppWindow.IsVisible) AppWindow.Show(false);
        // No Window.Activate call: showing feedback must preserve the target caret.
        Position();
        if (dismiss && persistentWarning.Length == 0) hide.Start();
    }

    public void SetPersistentWarning(string text)
    {
        if (closed || persistentWarning == text) return;
        persistentWarning = text ?? "";
        warning.Text = persistentWarning.Length == 0 ? "" : "未保存";
        AutomationProperties.SetName(warning, persistentWarning);
        warning.Visibility = persistentWarning.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var bar in levels) bar.Visibility = persistentWarning.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (persistentWarning.Length > 0)
        {
            hide.Stop();
            if (!AppWindow.IsVisible) Update("有内容尚未保存", "请在主窗口重试保存", dismiss: true);
        }
        else if (dismissPending) hide.Start();
    }

    public void Clear()
    {
        if (closed) return;
        hide.Stop(); startedAt = 0; dismissPending = true; previewSource = "";
        SetMeter(0, false);
        if (persistentWarning.Length > 0) Update("有内容尚未保存", "请在主窗口重试保存", dismiss: true);
        else AppWindow.Hide();
    }

    private void FitPreview()
    {
        if (closed) return;
        string source = previewSource.Length == 0 ? "说话时会在这里显示文字" : previewSource;
        // Measure a separate native TextBlock so the displayed line is never
        // constrained while fitting. Trim whole graphemes from the beginning,
        // preserving the newest spoken words, combining marks and emoji.
        double available = preview.ActualWidth > 1 ? preview.ActualWidth : accessibleWidthDip - 34;
        var starts = StringInfo.ParseCombiningCharacters(source);
        string fitted = source;
        for (int skip = 0; skip <= starts.Length; skip++)
        {
            fitted = skip == 0 ? source : skip < starts.Length ? "…" + source[starts[skip]..] : "…";
            measure.Text = fitted;
            measure.Measure(new Size(double.PositiveInfinity, accessibleHeightDip));
            if (measure.DesiredSize.Width <= available || skip == starts.Length) break;
        }
        if (preview.Text != fitted) preview.Text = fitted;
        preview.Opacity = previewSource.Length == 0 && !accessibility.HighContrast
            ? root.ActualTheme == ElementTheme.Dark ? .55 : .72 : 1;
    }

    private void SystemAppearanceChanged(UISettings sender, object args) => QueueAppearanceUpdate();

    private void QueueAppearanceUpdate()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closed) return;
            ApplyAccessibility();
            Position();
            FitPreview();
        });
    }

    private void ApplyAccessibility()
    {
        // Honor Windows text size without putting enlarged glyphs into the
        // original fixed-height rows. The normal-size footprint stays 360x76.
        double textScale = Math.Clamp(uiSettings.TextScaleFactor, 1, 2.25);
        double rowHeight = Math.Ceiling(22 * textScale);
        heading.Height = rowHeight;
        foreach (var row in rows.RowDefinitions) row.Height = new GridLength(rowHeight);
        heading.ColumnDefinitions[0].Width = heading.ColumnDefinitions[2].Width = new GridLength(58 * textScale);
        accessibleWidthDip = WidthDip * Math.Min(textScale, 1.5);
        accessibleHeightDip = HeightDip + 2 * (rowHeight - 22);

        bool highContrast = accessibility.HighContrast;
        bool dark = root.ActualTheme == ElementTheme.Dark;
        var foreground = highContrast ? new SolidColorBrush(uiSettings.GetColorValue(UIColorType.Foreground)) : dark ? Brush(241, 245, 249) : Brush(24, 39, 57);
        root.Background = highContrast ? new SolidColorBrush(uiSettings.GetColorValue(UIColorType.Background)) : dark ? Brush(24, 31, 42) : Brush(249, 251, 253);
        surface.Background = root.Background;
        root.BorderBrush = highContrast ? foreground : dark ? Brush(67, 80, 96) : Brush(187, 199, 211);
        title.Foreground = preview.Foreground = measure.Foreground = foreground;
        elapsed.Foreground = highContrast ? foreground : dark ? Brush(157, 175, 193) : Brush(85, 105, 124);
        warning.Foreground = highContrast ? foreground : dark ? Brush(255, 202, 119) : Brush(137, 74, 0);
        foreach (var bar in levels) bar.Background = highContrast ? foreground : dark ? Brush(104, 219, 186) : Brush(0, 119, 95);
        preview.Opacity = highContrast || previewSource.Length > 0 ? 1 : dark ? .55 : .72;
    }

    private void Position()
    {
        if (closed || positioning || targetMonitor == IntPtr.Zero) return;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(targetMonitor, ref info))
        {
            targetMonitor = MonitorFromWindow(GetForegroundWindow(), 2);
            if (!GetMonitorInfo(targetMonitor, ref info)) return;
        }
        positioning = true;
        try
        {
            // Move first so GetDpiForWindow reflects the target display. Native
            // work-area coordinates correctly preserve negative monitor origins.
            if (MonitorFromWindow(hwnd, 2) != targetMonitor)
                SetWindowPos(hwnd, IntPtr.Zero, info.Work.Left + 8, info.Work.Top + 8, 0, 0, 0x0015);
            uint dpi = GetDpiForWindow(hwnd);
            double scale = (dpi == 0 ? 96 : dpi) / 96.0;
            int workWidth = Math.Max(1, info.Work.Right - info.Work.Left);
            int workHeight = Math.Max(1, info.Work.Bottom - info.Work.Top);
            int width = Math.Min((int)Math.Round(accessibleWidthDip * scale), Math.Max(1, workWidth - (int)Math.Round(32 * scale)));
            int height = Math.Min((int)Math.Round(accessibleHeightDip * scale), workHeight);
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, width, height, 0x0012);
            // Centre the actual HWND, not XAML's desired size; neither long text
            // nor an unsaved badge can change its footprint.
            if (GetWindowRect(hwnd, out var actual))
            {
                width = actual.Right - actual.Left;
                height = actual.Bottom - actual.Top;
            }
            int left = info.Work.Left + (workWidth - width) / 2;
            int top = Math.Max(info.Work.Top, info.Work.Bottom - height - (int)Math.Round(BottomMarginDip * scale));
            SetWindowPos(hwnd, new IntPtr(-1), left, top, 0, 0, 0x0011);
            chrome.UpdateRegion();
        }
        finally { positioning = false; }
    }

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (message == 0x0021) return new IntPtr(3); // WM_MOUSEACTIVATE: MA_NOACTIVATE
        if (message == 0x0084) return new IntPtr(-1); // WM_NCHITTEST: HTTRANSPARENT
        var result = DefSubclassProc(window, message, wParam, lParam);
        if (!closed && message is 0x02E0 or 0x007E or 0x001A or 0x0015 or 0x031A && !repositionQueued)
        {
            // Let WinUI process DPI/theme messages before restoring the bottom
            // centre. WM_SYSCOLORCHANGE also signals native high-contrast changes.
            repositionQueued = true;
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                repositionQueued = false;
                if (closed) return;
                ApplyAccessibility();
                Position();
                FitPreview();
            })) repositionQueued = false;
        }
        return result;
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));
    private static TextBlock Label(string name, double size, SolidColorBrush foreground)
    {
        var label = new TextBlock
        {
            Name = name, FontFamily = new FontFamily("Segoe UI Variable, Microsoft YaHei UI, Segoe UI"),
            FontSize = size, Foreground = foreground, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap, MaxLines = 1, IsTextSelectionEnabled = false
        };
        if (name.Length > 0) AutomationProperties.SetAutomationId(label, name);
        return label;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
