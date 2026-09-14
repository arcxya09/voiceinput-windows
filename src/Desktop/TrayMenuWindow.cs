using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI.ViewManagement;

namespace RealtimeTranscription.Desktop;

public enum TrayMenuCommand { OpenManager, ToggleEnabled, ToggleDictation, Copy, Settings, Exit }

/// <summary>WinUI menu surface; the notification icon itself remains a Windows Shell adapter.</summary>
public sealed class TrayMenuWindow : Window, IDisposable
{
    private const double WidthDip = 292, EdgeGapDip = 8;
    private readonly Func<TrayMenuCommand, Task> execute;
    private readonly Border root;
    private readonly Grid surface;
    private readonly PopupWindowChrome chrome;
    private readonly UISettings uiSettings = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly List<ButtonBase> buttons = [];
    private readonly Button enabledButton;
    private readonly ToggleButton dictationButton;
    private readonly TextBlock enabledLabel;
    private readonly IntPtr hwnd;
    private readonly SubclassProc windowProc;
    private PointNative anchor;
    private int isOpen;
    private bool closed, disposing, positioning, repositionQueued, commandRunning;

    // Push-to-talk reads this from its worker; never access AppWindow on that thread.
    public bool IsOpen => Volatile.Read(ref isOpen) != 0;

    public TrayMenuWindow(Func<TrayMenuCommand, Task> execute)
    {
        this.execute = execute;
        Title = "VoiceInput 菜单";
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = "VoiceInput", FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(12, 5, 12, 7)
        });
        AddButton(stack, "TrayOpenManager", "打开管理", "\uE80F", TrayMenuCommand.OpenManager);
        enabledButton = AddButton(stack, "TrayToggleEnabled", "启用按住说话", "\uE768", TrayMenuCommand.ToggleEnabled);
        enabledLabel = (TextBlock)((Grid)enabledButton.Content).Children[1];
        dictationButton = new ToggleButton
        {
            Name = "TrayDictationMode", Tag = TrayMenuCommand.ToggleDictation,
            Content = ContentRow("仅听写，完成后手动复制", "\uE8D4"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 36, Padding = new Thickness(12, 6, 12, 6), BorderThickness = new Thickness(0)
        };
        AutomationProperties.SetName(dictationButton, "仅听写，完成后手动复制");
        ToolTipService.SetToolTip(dictationButton, "仅听写，完成后手动复制");
        dictationButton.Click += Command_Click;
        buttons.Add(dictationButton); stack.Children.Add(dictationButton);
        AddButton(stack, "TrayCopy", "复制最近结果", "\uE8C8", TrayMenuCommand.Copy);
        AddButton(stack, "TraySettings", "设置", "\uE713", TrayMenuCommand.Settings);
        stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(50, 128, 128, 128)), Margin = new Thickness(10, 5, 10, 5) });
        AddButton(stack, "TrayExit", "退出", "\uE8BB", TrayMenuCommand.Exit);
        root = new Border
        {
            Name = "TrayMenuRoot", Padding = new Thickness(8),
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Child = new ScrollViewer
            {
                Content = stack, HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            RequestedTheme = ElementTheme.Default
        };
        surface = new Grid();
        surface.Children.Add(root);
        Content = surface;
        ApplyTheme();
        root.ActualThemeChanged += (_, _) => ApplyTheme();
        root.KeyDown += Menu_KeyDown;
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false; presenter.IsMinimizable = false;
        presenter.IsMaximizable = false; presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        chrome = new PopupWindowChrome(hwnd, 10, clickThrough: false);
        windowProc = WindowProc;
        SetWindowSubclass(hwnd, windowProc, 1, 0);
        root.Loaded += (_, _) => { if (IsOpen) Position(); };
        uiSettings.TextScaleFactorChanged += SystemAppearanceChanged;
        uiSettings.ColorValuesChanged += SystemAppearanceChanged;
        accessibility.HighContrastChanged += HighContrastChanged;
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated && IsOpen)
                HideMenu();
        };
        AppWindow.Closing += (_, args) =>
        {
            if (!disposing) { args.Cancel = true; HideMenu(); }
        };
        Closed += (_, _) =>
        {
            closed = true; Volatile.Write(ref isOpen, 0);
            RemoveWindowSubclass(hwnd, windowProc, 1);
            chrome.Dispose();
            uiSettings.TextScaleFactorChanged -= SystemAppearanceChanged;
            uiSettings.ColorValuesChanged -= SystemAppearanceChanged;
            accessibility.HighContrastChanged -= HighContrastChanged;
        };
    }

    public object? FindName(string name)
        => name == root.Name ? root : buttons.FirstOrDefault(button => button.Name == name);

    public void SetState(bool enabled, bool dictation, bool canChangeMode)
    {
        if (closed) return;
        enabledLabel.Text = enabled ? "暂停按住说话" : "启用按住说话";
        AutomationProperties.SetName(enabledButton, enabledLabel.Text);
        dictationButton.IsChecked = dictation;
        dictationButton.IsEnabled = canChangeMode;
    }

    public void ShowAtCursor()
    {
        if (closed || commandRunning) return;
        if (!GetCursorPos(out anchor)) return;
        Volatile.Write(ref isOpen, 1);
        Position();
        AppWindow.Show();
        Activate();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!closed && IsOpen)
            {
                Position();
                buttons.FirstOrDefault(button => button.IsEnabled)?.Focus(FocusState.Programmatic);
            }
        });
    }

    public void HideMenu()
    {
        if (closed) return;
        Volatile.Write(ref isOpen, 0);
        AppWindow.Hide();
    }

    public void Dispose()
    {
        if (closed) return;
        disposing = true; Volatile.Write(ref isOpen, 0); Close();
    }

    private Button AddButton(StackPanel parent, string name, string text, string glyph, TrayMenuCommand command)
    {
        var button = new Button
        {
            Name = name, Tag = command, Content = ContentRow(text, glyph),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 36, Padding = new Thickness(12, 6, 12, 6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0)
        };
        AutomationProperties.SetName(button, text);
        ToolTipService.SetToolTip(button, text);
        button.Click += Command_Click;
        buttons.Add(button); parent.Children.Add(button);
        return button;
    }

    private static Grid ContentRow(string text, string glyph)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(label, 1);
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(label);
        return row;
    }

    private async void Command_Click(object sender, RoutedEventArgs args)
    {
        if (closed || commandRunning || sender is not ButtonBase { Tag: TrayMenuCommand command }) return;
        commandRunning = true;
        HideMenu();
        try { await execute(command); }
        finally { commandRunning = false; }
    }

    private void Menu_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape) { args.Handled = true; HideMenu(); return; }
        if (args.Key is not (VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End)) return;
        var active = buttons.Where(button => button.IsEnabled).ToArray();
        if (active.Length == 0) return;
        int index = Array.FindIndex(active, button => button.FocusState != FocusState.Unfocused);
        index = args.Key switch
        {
            VirtualKey.Home => 0,
            VirtualKey.End => active.Length - 1,
            VirtualKey.Up => (index < 0 ? active.Length - 1 : index + active.Length - 1) % active.Length,
            _ => (index + 1) % active.Length
        };
        args.Handled = true; active[index].Focus(FocusState.Keyboard);
    }

    private void ApplyTheme()
    {
        bool dark = root.ActualTheme == ElementTheme.Dark;
        bool highContrast = accessibility.HighContrast;
        root.Background = new SolidColorBrush(highContrast ? uiSettings.GetColorValue(UIColorType.Background) : dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 249, 249, 249));
        surface.Background = root.Background;
        root.BorderBrush = new SolidColorBrush(highContrast ? uiSettings.GetColorValue(UIColorType.Foreground) : dark ? Windows.UI.Color.FromArgb(255, 64, 64, 64) : Windows.UI.Color.FromArgb(255, 218, 218, 218));
    }

    private void HighContrastChanged(AccessibilitySettings sender, object args) => QueueAppearanceUpdate();
    private void SystemAppearanceChanged(UISettings sender, object args) => QueueAppearanceUpdate();

    private void QueueAppearanceUpdate()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closed) return;
            ApplyTheme();
            root.InvalidateMeasure();
            if (IsOpen) Position();
        });
    }

    private void Position()
    {
        if (closed || positioning) return;
        var monitor = MonitorFromPoint(anchor, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        positioning = true;
        try
        {
            if (MonitorFromWindow(hwnd, 2) != monitor)
                SetWindowPos(hwnd, IntPtr.Zero, info.Work.Left + 8, info.Work.Top + 8, 0, 0, 0x0015);
            uint dpi = GetDpiForWindow(hwnd);
            double scale = dpi == 0 ? 1 : dpi / 96.0;
            int gap = Math.Min((int)Math.Round(EdgeGapDip * scale), Math.Max(0, (Math.Min(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top) - 1) / 2));
            int availableWidth = Math.Max(1, info.Work.Right - info.Work.Left - 2 * gap);
            int availableHeight = Math.Max(1, info.Work.Bottom - info.Work.Top - 2 * gap);
            int width = Math.Min(availableWidth, (int)Math.Ceiling(WidthDip * Math.Clamp(uiSettings.TextScaleFactor, 1, 1.5) * scale));
            root.Measure(new Size(width / scale, double.PositiveInfinity));
            int height = Math.Min(availableHeight, Math.Max(1, (int)Math.Ceiling(root.DesiredSize.Height * scale)));
            int x = Math.Clamp(anchor.X - width + gap, info.Work.Left + gap, info.Work.Right - width - gap);
            int preferredY = anchor.Y - height - gap;
            if (preferredY < info.Work.Top + gap) preferredY = anchor.Y + gap;
            int y = Math.Clamp(preferredY, info.Work.Top + gap, info.Work.Bottom - height - gap);
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            chrome.UpdateRegion();
        }
        finally { positioning = false; }
    }

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (message == 0x0100 && wParam.ToInt64() == 27) { HideMenu(); return IntPtr.Zero; }
        var result = DefSubclassProc(window, message, wParam, lParam);
        if (message is 0x02E0 or 0x007E or 0x001A && !closed && !repositionQueued)
        {
            repositionQueued = true;
            if (!DispatcherQueue.TryEnqueue(() => { repositionQueued = false; if (!closed && IsOpen) Position(); })) repositionQueued = false;
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RectNative { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public RectNative Monitor, Work; public uint Flags; }
    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointNative point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(PointNative point, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
