using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.Storage.Pickers;
using RealtimeTranscription.Desktop.Input;

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
        int width = Math.Min((int)Math.Round(1100 * scale), Math.Max(480, area.Width - 32));
        int height = Math.Min((int)Math.Round(790 * scale), Math.Max(400, area.Height - 32));
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        MainRoot.Loaded += (_, _) => ShowPage(0);
    }

    private double NativeScale()
    {
        uint dpi = GetDpiForMainWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        return dpi == 0 ? 1 : dpi / 96.0;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint GetDpiForMainWindow(IntPtr window);

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
        }
        finally { selectingPage = false; }
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (MainRoot == null || LivePage == null || HelpPage == null) return;
        if (args.SelectedItem is NavigationViewItem item && int.TryParse(item.Tag?.ToString(), out int index)) ShowPage(index);
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
