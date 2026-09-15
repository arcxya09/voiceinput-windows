using Microsoft.UI.Xaml;
using RealtimeTranscription.Core;
using Forms = System.Windows.Forms;

namespace RealtimeTranscription.Desktop;

public partial class MainWindow
{
    private StartupService? startupService;
    private bool refreshingStartup;

    private void InitializeStartupSettings()
    {
        // Settings are re-read when the window receives focus, so changes in
        // Task Manager/Windows Settings are reflected without restarting VoiceInput.
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated && !closed) RefreshStartupSettings();
        };
        try
        {
            startupService = StartupService.CreateCurrent();
            if (!Program.IsStartupLaunch && !DesktopSmoke.IsSmoke(Environment.GetCommandLineArgs().Skip(1).ToArray()))
                startupService.RepairMovedPortable();
            RefreshStartupSettings();
        }
        catch (Exception e) { ShowStartupFailure(e); }
    }

    private void RefreshStartupSettings()
    {
        if (StartupToggle == null || StartupStatusText == null) return;
        try
        {
            startupService ??= StartupService.CreateCurrent();
            ApplyStartupStatus(startupService.Read());
        }
        catch (Exception e) { ShowStartupFailure(e); }
    }

    private void ApplyStartupStatus(StartupStatus status)
    {
        refreshingStartup = true;
        try
        {
            StartupToggle.IsOn = status.Enabled;
            StartupToggle.IsEnabled = status.State != StartupState.ForeignEntry;
            StartupStatusText.Text = status.Message;
        }
        finally { refreshingStartup = false; }
    }

    private void StartupToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (refreshingStartup || startupService == null || closed) return;
        bool requested = StartupToggle.IsOn;
        try { ApplyStartupStatus(startupService.SetEnabled(requested)); }
        catch (Exception e)
        {
            // A failed write rolls the switch back to the registry's actual state.
            RefreshStartupSettings();
            StartupStatusText.Text = "未能修改开机自启动：" + AppController.SafeError(e);
        }
    }

    private void ShowStartupFailure(Exception exception)
    {
        if (StartupToggle == null || StartupStatusText == null) return;
        refreshingStartup = true;
        try
        {
            StartupToggle.IsOn = false;
            StartupToggle.IsEnabled = false;
            StartupStatusText.Text = "开机自启动状态暂不可用：" + AppController.SafeError(exception);
        }
        finally { refreshingStartup = false; }
    }

    private async void OpenWindowsStartupSettings_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:startupapps")))
                StartupStatusText.Text = "无法打开系统设置。请手动打开“设置 → 应用 → 启动”管理 VoiceInput。";
        }
        catch (Exception e) { StartupStatusText.Text = "无法打开系统设置：" + AppController.SafeError(e); }
    }

    private void CompleteStartupPresentation()
    {
        if (Program.IsStartupLaunch)
        {
            // The main window has never been activated on this path. Registering
            // the hotkey does not start recording; only an explicit hold does.
            Hide();
            if (controller.StartupWarning is { } warning)
                NotifyStartupError(warning);
            else if (controller.Keys.BailianKey.Length == 0)
                NotifyStartupError("请双击托盘图标，在设置中填写百炼 API Key。");
            else if (!controller.MemoryAvailable)
                NotifyStartupError("本地记忆暂不可用，请双击托盘图标检查启动提示。");
            return;
        }
        if (controller.StartupWarning != null || controller.Keys.BailianKey.Length == 0) ShowPage(3);
        else if (controller.MemoryAvailable)
        {
            Hide();
            tray?.ShowBalloonTip(2500, "语音输入法已就绪", controller.Settings.DictationOnly
                ? $"按住 {UiPresentation.HotkeyName(controller.Settings.Hotkey)} 听写，完成后自动复制正文。"
                : $"在文本框中按住 {UiPresentation.HotkeyName(controller.Settings.Hotkey)} 说话，松开输入。", Forms.ToolTipIcon.Info);
        }
    }

    internal void NotifyStartupError(string message)
    {
        StatusText.Text = message;
        tray?.ShowBalloonTip(5000, "VoiceInput 启动提示", message, Forms.ToolTipIcon.Warning);
    }

    internal void PrepareStartupSmokeTray()
    {
        CreateTray();
        CompleteStartupPresentation();
    }
    internal bool StartupSmokeTrayVisible => tray?.Visible == true;
    internal void CleanupStartupSmokeUi()
    {
        closed = true;
        render.Stop();
        Microsoft.Win32.SystemEvents.PowerModeChanged -= PowerChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch -= SessionChanged;
        tray?.Dispose(); trayMenu?.Dispose(); trayIcon?.Dispose(); overlay.Close();
    }
}
