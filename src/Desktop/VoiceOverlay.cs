using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RealtimeTranscription.Core;
using Windows.UI.ViewManagement;

namespace RealtimeTranscription.Desktop;

/// <summary>A native acrylic capsule that never activates or intercepts the target application's input.</summary>
public sealed class VoiceOverlay : Window
{
    private const double WidthDip = 360, HeightDip = 56, ShadowPaddingDip = 12, BottomMarginDip = 24;
    private readonly TextBlock title, preview, elapsed, warning, completion;
    private readonly TailPreviewPanel previewLine;
    private readonly Border root;
    private readonly Grid surface, row;
    private readonly CapsuleSurface capsule;
    private readonly UISettings uiSettings = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly Border[] levels = new Border[5];
    private readonly DispatcherTimer hide = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly SubclassProc windowProc;
    private readonly IntPtr hwnd;
    private Storyboard? fade;
    private IntPtr targetMonitor;
    private long startedAt;
    private string previewSource = "", persistentWarning = "", currentStatus = "准备麦克风…";
    private string? completionState;
    private bool positioning, dismissPending, closed, repositionQueued;
    private double accessibleWidthDip = WidthDip, accessibleHeightDip = HeightDip;

    internal bool HasAntialiasedAcrylic => capsule.HasAntialiasedAcrylic;
    internal bool IsUsingAcrylic => capsule.IsUsingAcrylic;
    internal string MaterialDiagnostics => capsule.MaterialDiagnostics;

    public VoiceOverlay()
    {
        Title = "VoiceInput";
        var foreground = Brush(241, 245, 249);
        var muted = Brush(157, 175, 193);
        var accent = Brush(104, 219, 186);
        // Keep full status available to automation and the production event bridge.
        // The visible capsule has only one line; its centre is reserved for speech.
        title = Label("OverlayStatus", 12, foreground);
        title.Text = currentStatus;
        title.Visibility = Visibility.Collapsed;
        preview = Label("OverlayPreview", 14, foreground);
        preview.TextAlignment = TextAlignment.Center;
        previewLine = new TailPreviewPanel(preview) { HorizontalAlignment = HorizontalAlignment.Stretch };
        elapsed = Label("OverlayElapsed", 11, muted);
        elapsed.HorizontalAlignment = HorizontalAlignment.Right;
        warning = Label("OverlayWarning", 10, Brush(255, 202, 119));
        warning.Visibility = Visibility.Collapsed;
        completion = Label("OverlayCompletion", 16, accent);
        completion.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        completion.Visibility = Visibility.Collapsed;

        // Equal columns keep recognized words on the true centreline.
        row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        var left = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var wave = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < levels.Length; i++)
        {
            levels[i] = new Border { Width = 3, Height = 4, CornerRadius = new CornerRadius(2),
                Background = accent, VerticalAlignment = VerticalAlignment.Center, Opacity = .35 };
            wave.Children.Add(levels[i]);
        }
        left.Children.Add(wave);
        left.Children.Add(completion);
        left.Children.Add(warning);
        Grid.SetColumn(previewLine, 1);
        Grid.SetColumn(elapsed, 2);
        row.Children.Add(left);
        row.Children.Add(previewLine);
        row.Children.Add(elapsed);
        row.Children.Add(title);
        root = new Border
        {
            Name = "OverlayRoot", RequestedTheme = ElementTheme.Default,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(HeightDip / 2),
            Margin = new Thickness(ShadowPaddingDip), Padding = new Thickness(20, 10, 20, 10),
            Child = row, IsHitTestVisible = false
        };
        surface = new Grid { Name = "OverlayHost", Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        surface.Children.Add(root);
        Content = surface;
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        capsule = new CapsuleSurface(this, surface, root);
        ApplyAccessibility();
        root.ActualThemeChanged += (_, _) => { if (!closed) ApplyAccessibility(); };
        windowProc = WindowProc;
        SetWindowSubclass(hwnd, windowProc, 1, 0);
        root.Loaded += (_, _) => { Position(); UpdatePreview(); };
        uiSettings.TextScaleFactorChanged += SystemAppearanceChanged;
        uiSettings.ColorValuesChanged += SystemAppearanceChanged;
        hide.Tick += (_, _) => { hide.Stop(); FadeAndHide(); };
        Closed += (_, _) =>
        {
            closed = true;
            hide.Stop();
            CancelFade();
            RemoveWindowSubclass(hwnd, windowProc, 1);
            capsule.Dispose();
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
        completionState = null;
        SetMeter(0, true);
        previewSource = "";
        Update("准备麦克风…", text: "");
    }

    public void SetMeter(float value, bool recording)
    {
        if (closed) return;
        double level = recording && !dismissPending && float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        for (int i = 0; i < levels.Length; i++)
        {
            double shape = 1 - Math.Abs(i - 2) * .22;
            levels[i].Height = 4 + 14 * level * shape;
            levels[i].Opacity = recording ? .65 + .35 * level : .35;
            levels[i].Visibility = persistentWarning.Length == 0 && !dismissPending ? Visibility.Visible : Visibility.Collapsed;
        }
        if (dismissPending) elapsed.Text = CapsulePresentation.CompletionLabel(completionState);
        else if (startedAt > 0)
        {
            var duration = TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - startedAt));
            elapsed.Text = duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
        }
        else elapsed.Text = "";
    }

    public void Update(string status, string? text = null, bool dismiss = false, string? deliveryState = null)
    {
        if (closed) return;
        CancelFade();
        dismissPending = dismiss;
        completionState = dismiss ? deliveryState : null;
        currentStatus = status ?? "";
        title.Text = UiPresentation.LatestText(currentStatus, 80);
        // A status-only notification preserves the recognized text.
        if (text != null) previewSource = UiPresentation.LatestText(text, 80);
        UpdatePreview();
        UpdateIndicators();
        hide.Stop();
        if (dismiss) SetMeter(0, false);
        if (targetMonitor == IntPtr.Zero) targetMonitor = MonitorFromWindow(GetForegroundWindow(), 2);
        Position();
        if (!AppWindow.IsVisible) AppWindow.Show(false);
        Position();
        if (dismiss && persistentWarning.Length == 0) hide.Start();
    }

    public void SetPersistentWarning(string text)
    {
        if (closed || persistentWarning == text) return;
        persistentWarning = text ?? "";
        warning.Text = persistentWarning.Length == 0 ? "" : "未保存";
        AutomationProperties.SetName(warning, persistentWarning);
        UpdateIndicators();
        UpdateAccessibleName();
        if (persistentWarning.Length > 0)
        {
            hide.Stop();
            CancelFade();
            if (!AppWindow.IsVisible) Update("有内容尚未保存", "请在主窗口重试保存", dismiss: true);
        }
        else if (dismissPending) hide.Start();
    }

    public void Clear()
    {
        if (closed) return;
        hide.Stop();
        CancelFade();
        startedAt = 0;
        dismissPending = true;
        completionState = null;
        previewSource = "";
        SetMeter(0, false);
        if (persistentWarning.Length > 0) Update("有内容尚未保存", "请在主窗口重试保存", dismiss: true);
        else AppWindow.Hide();
    }

    private void UpdatePreview()
    {
        if (closed) return;
        previewLine.Text = previewSource.Length > 0 ? previewSource
            : CapsulePresentation.EmptyPreview(currentStatus, dismissPending, completionState);
        preview.Opacity = previewSource.Length == 0 && !accessibility.HighContrast
            ? root.ActualTheme == ElementTheme.Dark ? .72 : .8 : 1;
        UpdateAccessibleName();
    }

    private void UpdateAccessibleName() => AutomationProperties.SetName(root,
        string.Join("，", new[] { "语音识别", currentStatus, previewSource, persistentWarning }.Where(s => s.Length > 0)));

    private void UpdateIndicators()
    {
        warning.Visibility = persistentWarning.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        completion.Visibility = dismissPending && persistentWarning.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        completion.Text = completionState switch
        {
            "Sent" or "Dictated" => "\uE73E",
            "Cancelled" => "\uE711",
            "Blocked" or "Partial" or "Unknown" => "\uE7BA",
            _ => "\uE946"
        };
        AutomationProperties.SetName(completion, currentStatus);
        foreach (var bar in levels)
            bar.Visibility = persistentWarning.Length == 0 && !dismissPending ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FadeAndHide()
    {
        if (closed || persistentWarning.Length > 0 || !dismissPending) return;
        if (!uiSettings.AnimationsEnabled || accessibility.HighContrast)
        {
            AppWindow.Hide();
            return;
        }
        CancelFade();
        var storyboard = new Storyboard();
        var animation = new DoubleAnimation { To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(180)) };
        Storyboard.SetTarget(animation, surface);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        fade = storyboard;
        storyboard.Completed += (_, _) =>
        {
            // A new turn or save warning can cancel a fade before this callback runs.
            if (closed || !ReferenceEquals(fade, storyboard)) return;
            fade = null;
            AppWindow.Hide();
            storyboard.Stop();
            surface.Opacity = 1;
        };
        storyboard.Begin();
    }

    private void CancelFade()
    {
        var old = fade;
        fade = null;
        old?.Stop();
        surface.Opacity = 1;
    }

    private void SystemAppearanceChanged(UISettings sender, object args) => QueueAppearanceUpdate();

    private void QueueAppearanceUpdate()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closed) return;
            ApplyAccessibility();
            Position();
            UpdatePreview();
        });
    }

    private void ApplyAccessibility()
    {
        // Respect the Windows text-size setting while retaining a single row.
        double textScale = Math.Clamp(uiSettings.TextScaleFactor, 1, 2.25);
        row.ColumnDefinitions[0].Width = row.ColumnDefinitions[2].Width = new GridLength(46 * textScale);
        accessibleWidthDip = WidthDip * Math.Min(textScale, 1.5);
        accessibleHeightDip = HeightDip + Math.Ceiling(22 * textScale) - 22;
        bool highContrast = accessibility.HighContrast;
        bool dark = root.ActualTheme == ElementTheme.Dark;
        var foreground = highContrast ? new SolidColorBrush(uiSettings.GetColorValue(UIColorType.Foreground))
            : dark ? Brush(241, 245, 249) : Brush(24, 39, 57);
        capsule.UpdateAppearance(dark, highContrast, foreground.Color);
        title.Foreground = preview.Foreground = foreground;
        previewLine.InvalidateTextMetrics();
        elapsed.Foreground = highContrast ? foreground : dark ? Brush(177, 193, 210) : Brush(67, 86, 108);
        warning.Foreground = highContrast ? foreground : dark ? Brush(255, 202, 119) : Brush(137, 74, 0);
        completion.Foreground = foreground;
        foreach (var bar in levels) bar.Background = highContrast ? foreground : dark ? Brush(104, 219, 186) : Brush(0, 119, 95);
        preview.Opacity = highContrast || previewSource.Length > 0 ? 1 : dark ? .72 : .8;
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
            int width = Math.Min((int)Math.Round((accessibleWidthDip + 2 * ShadowPaddingDip) * scale), Math.Max(1, workWidth - (int)Math.Round(32 * scale)));
            int height = Math.Min((int)Math.Round((accessibleHeightDip + 2 * ShadowPaddingDip) * scale), workHeight);
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, width, height, 0x0012);
            // Centre the actual HWND, not XAML's desired size; neither long text
            // nor an unsaved badge can change its footprint.
            if (GetWindowRect(hwnd, out var actual))
            {
                width = actual.Right - actual.Left;
                height = actual.Bottom - actual.Top;
            }
            int left = info.Work.Left + (workWidth - width) / 2;
            int top = Math.Max(info.Work.Top, info.Work.Bottom - height - (int)Math.Round((BottomMarginDip - ShadowPaddingDip) * scale));
            SetWindowPos(hwnd, new IntPtr(-1), left, top, 0, 0, 0x0011);
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
                UpdatePreview();
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
