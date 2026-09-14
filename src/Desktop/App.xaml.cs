using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RealtimeTranscription.Desktop;

public partial class App : Microsoft.UI.Xaml.Application
{
    public MainWindow? MainWindow { get; private set; }
    private bool showingError;

    public App()
    {
        InitializeComponent();
        UnhandledException += async (_, args) =>
        {
            args.Handled = true;
            await ShowErrorAsync("操作遇到错误。已显示的正文可以继续复制或导出。", "语音输入法");
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (DesktopSmoke.IsSmoke(commandLine))
        {
            Environment.ExitCode = await DesktopSmoke.RunAsync(commandLine);
            Exit();
            return;
        }

        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RealtimeTranscription");
        var controller = new AppController(folder);
        MainWindow = new MainWindow(controller);
        MainWindow.Activate();
        try { await controller.InitializeAsync(); }
        catch { await ShowErrorAsync("配置载入未完成，请检查设置。本地数据已保留。", "启动"); }
        MainWindow.Ready();
    }

    private async Task ShowErrorAsync(string message, string title)
    {
        if (showingError || MainWindow?.Content is not FrameworkElement root || root.XamlRoot == null) return;
        showingError = true;
        try
        {
            await Dialogs.MessageAsync(MainWindow, title, message);
        }
        catch { /* A closing window cannot host another dialog. */ }
        finally { showingError = false; }
    }
}
