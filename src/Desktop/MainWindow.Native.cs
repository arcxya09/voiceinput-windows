using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.Storage.Pickers;
using RealtimeTranscription.Desktop.Input;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Desktop;

public partial class MainWindow
{
    private bool selectingPage, closingPrompt;
    private bool pickerOpen;
    public object? FindName(string name) => MainRoot.FindName(name);
    public void Show() => AppWindow.Show();
    public void Hide() => AppWindow.Hide();

    private void ConfigureWindow()
    {
        Title = "语音输入法";
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = NativeScale();
        int width = Math.Min((int)Math.Round(1100 * scale), Math.Max(1, area.Width - 32));
        int height = Math.Min((int)Math.Round(790 * scale), Math.Max(1, area.Height - 32));
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        // A hidden startup can receive "Settings" before the first Loaded event.
        // Preserve that request when the content is first made visible.
        MainRoot.Loaded += (_, _) => { if (Navigation.SelectedItem == null) ShowPage(0); };
        InstallMinimumWindowSize();
    }

    private double NativeScale()
    {
        uint dpi = GetDpiForMainWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        return dpi == 0 ? 1 : dpi / 96.0;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint GetDpiForMainWindow(IntPtr window);

    private MainWindowSubclass? mainWindowSubclass;
    private void InstallMinimumWindowSize()
    {
        mainWindowSubclass = (window, message, wParam, lParam, subclassId, reference) =>
        {
            nint result = MainDefSubclassProc(window, message, wParam, lParam);
            if (message == 0x0024 && lParam != IntPtr.Zero) // WM_GETMINMAXINFO
            {
                var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
                var limits = System.Runtime.InteropServices.Marshal.PtrToStructure<MainMinMaxInfo>(lParam);
                double dpi = NativeScale();
                limits.MinTrack.X = Math.Min((int)Math.Round(600 * dpi), Math.Max(1, work.Width - 16));
                limits.MinTrack.Y = Math.Min((int)Math.Round(420 * dpi), Math.Max(1, work.Height - 16));
                System.Runtime.InteropServices.Marshal.StructureToPtr(limits, lParam, false);
            }
            return result;
        };
        MainSetWindowSubclass(WinRT.Interop.WindowNative.GetWindowHandle(this), mainWindowSubclass, 0x56494D, 0);
    }
    private delegate nint MainWindowSubclass(nint window, uint message, nint wParam, nint lParam, nuint subclassId, nuint reference);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MainPoint { public int X, Y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MainMinMaxInfo { public MainPoint Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    [System.Runtime.InteropServices.DllImport("comctl32.dll", EntryPoint = "SetWindowSubclass")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool MainSetWindowSubclass(nint window, MainWindowSubclass callback, nuint id, nuint reference);
    [System.Runtime.InteropServices.DllImport("comctl32.dll", EntryPoint = "DefSubclassProc")]
    private static extern nint MainDefSubclassProc(nint window, uint message, nint wParam, nint lParam);

    public void ShowPage(int index)
    {
        if (selectingPage) return;
        selectingPage = true;
        try
        {
            var pages = new FrameworkElement[] { LivePage, HistoryPage, VocabularyPage, SettingsPage, HelpPage };
            index = Math.Clamp(index, 0, pages.Length - 1);
            for (int i = 0; i < pages.Length; i++) pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            PageTitle.Text = new[] { "听写", "历史记录", "词库", "设置", "帮助" }[index];
            Navigation.SelectedItem = Navigation.MenuItems.Concat(Navigation.FooterMenuItems)
                .OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag?.ToString() == index.ToString());
            if (index == 3) RefreshStartupSettings();
        }
        finally { selectingPage = false; }
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (MainRoot == null || LivePage == null || HelpPage == null) return;
        if (args.SelectedItem is NavigationViewItem item && int.TryParse(item.Tag?.ToString(), out int index)) ShowPage(index);
    }

    // A double tap on empty list space must never act on a previously selected row.
    private static T? ResolveListAction<T>(ListView list, RoutedEventArgs args) where T : class
    {
        if (args is not Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs tap)
            return list.SelectedItem as T;
        for (DependencyObject? source = tap.OriginalSource as DependencyObject;
            source != null && source != list; source = VisualTreeHelper.GetParent(source))
        {
            if (source is ListViewItem item && list.IndexFromContainer(item) >= 0 && item.Content is T hit)
            {
                tap.Handled = true;
                list.SelectedItem = hit;
                return hit;
            }
        }
        return null;
    }

    private void RefreshSelectionActions()
    {
        if (HistoryOpenButton == null || HistoryMoreButton == null || TermEditButton == null || CorrectionActions == null) return;
        HistoryOpenButton.IsEnabled = !ManagementBusy && HistoryGrid.SelectedItem is MemoryHit;
        HistoryMoreButton.IsEnabled = !ManagementBusy && HistoryGrid.SelectedItem is MemoryHit;
        TermEditButton.IsEnabled = !ManagementBusy && TermsGrid.SelectedItems.Count == 1;
        CorrectionActions.IsEnabled = !ManagementBusy && CorrectionsGrid.SelectedItem is CorrectionCandidate;
    }

    private void UiSelection_Changed(object sender, SelectionChangedEventArgs args) => RefreshSelectionActions();

    private void HistoryQuery_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Enter) return;
        args.Handled = true;
        HistorySearch_Click(sender, args);
    }

    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(parent, i)) is T child) return child;
        return null;
    }
    private static bool IsAtTranscriptEnd(TextBox box)
    {
        var scroll = FindVisual<ScrollViewer>(box);
        return scroll == null || scroll.VerticalOffset >= scroll.ScrollableHeight - 3;
    }
    // WinUI's native text host can represent CRLF paragraphs as CR. Compare
    // logical line breaks so an unchanged snapshot does not reset selection.
    // Copy/export continue to use the original controller transcript.
    private static bool SameDisplayedText(string displayed, string source)
        => displayed == source || displayed.Replace("\r\n", "\n").Replace('\r', '\n') == source.Replace("\r\n", "\n").Replace('\r', '\n');
    private void ScrollTranscriptToEnd(TextBox box) => DispatcherQueue.TryEnqueue(() =>
    {
        if (closed) return;
        box.UpdateLayout();
        if (FindVisual<ScrollViewer>(box) is { } scroll) scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
    });

    private async Task<string?> PickSaveAsync(string filename, string extension)
    {
        if (pickerOpen) throw new InvalidOperationException("请先完成当前文件选择。");
        pickerOpen = true;
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = filename, DefaultFileExtension = extension };
            picker.FileTypeChoices.Add(extension == ".json" ? "JSON 文件" : extension == ".md" ? "Markdown 文档" : "文本文件", new List<string> { extension });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            return (await picker.PickSaveFileAsync())?.Path;
        }
        finally { pickerOpen = false; }
    }

    private async Task<string?> PickOpenAsync(params string[] extensions)
    {
        if (pickerOpen) throw new InvalidOperationException("请先完成当前文件选择。");
        pickerOpen = true;
        try
        {
            var picker = new FileOpenPicker();
            foreach (string extension in extensions) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            return (await picker.PickSingleFileAsync())?.Path;
        }
        finally { pickerOpen = false; }
    }
}
