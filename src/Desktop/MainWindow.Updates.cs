using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

namespace RealtimeTranscription.Desktop;

public partial class MainWindow
{
    private readonly GitHubUpdates updateClient = new();
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private CancellationTokenSource? updateCancellation;
    private GitHubRelease? availableUpdate;
    private DownloadedUpdate? downloadedUpdate;
    private bool updateBusy, updateInstalling, autoInstallPaused;
    private DateTimeOffset nextUpdateCheck = DateTimeOffset.MinValue;
    private DateTimeOffset idleUpdateSince = DateTimeOffset.UtcNow;
    private static Version CurrentVersion => typeof(Program).Assembly.GetName().Version ?? new Version(2, 2, 0);

    private static string? InstalledUpdateDirectory()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{D2D24A36-3D20-4EAE-9A0B-82C57CF0C671}_is1");
        if (key?.GetValue("InstallLocation") is not string directory || string.IsNullOrWhiteSpace(directory)) return null;
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        return string.Equals(root.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(root, "VoiceInput.exe")) ? root : null;
    }
    private void InitializeUpdates()
    {
        UpdateVersionText.Text = $"当前版本 {CurrentVersion.ToString(3)} · GitHub 正式版";
        UpdateModeText.Text = InstalledUpdateDirectory() != null
            ? "安装包校验通过后，退出 VoiceInput 并打开安装向导；设置、词库和历史记录保留。"
            : "当前为便携版。更新将打开安装版向导，原便携目录不会被替换；安装后请使用新版快捷方式。";
        AutoInstallUpdateBox.IsEnabled = InstalledUpdateDirectory() != null;
        updateTimer.Tick += UpdateTimer_Tick; updateTimer.Start(); RefreshUpdateButtons();
    }
    private async void UpdateTimer_Tick(object? sender, object e)
    {
        if (closed || shuttingDown || updateInstalling) return;
        if (ManagementBusy || AppWindow.IsVisible || trayMenu?.IsOpen == true) idleUpdateSince = DateTimeOffset.UtcNow;
        if (controller.Settings.AutoCheckUpdates && DateTimeOffset.UtcNow >= nextUpdateCheck && !updateBusy)
            await CheckUpdatesAsync(true);
        if (downloadedUpdate != null && controller.Settings.AutoOpenUpdateInstaller && !autoInstallPaused && !updateBusy &&
            !ManagementBusy && !AppWindow.IsVisible && trayMenu?.IsOpen != true && DateTimeOffset.UtcNow - idleUpdateSince >= TimeSpan.FromSeconds(60))
        {
            // Auto-open only for this registered installation. Unsaved text must never disappear silently.
            try
            {
                var snapshot = await controller.SnapshotAsync();
                if (InstalledUpdateDirectory() != null && (controller.Settings.SaveMemory || snapshot.Segments.Count == 0))
                    await InstallUpdateAsync(true);
            }
            catch (Exception error) { UpdateFailure(error); autoInstallPaused = true; }
        }
    }
    private void RefreshUpdateButtons()
    {
        UpdateCheckButton.IsEnabled = !updateBusy && !updateInstalling;
        UpdateDownloadButton.IsEnabled = availableUpdate != null && !updateBusy && !updateInstalling;
        UpdateInstallButton.IsEnabled = downloadedUpdate != null && !updateBusy && !updateInstalling;
        UpdateCancelButton.IsEnabled = updateBusy;
    }
    private void UpdateFailure(Exception error)
    {
        if (shuttingDown || closed) return;
        UpdateStatusText.Text = error is OperationCanceledException ? "更新已取消或请求超时，可重试。" :
            error is InvalidDataException or InvalidOperationException ? error.Message : "更新失败，请检查网络或下载目录后重试。";
        controller.Log.Write("Update", "OperationFailed", exception: error);
    }
    private async Task CheckUpdatesAsync(bool automatic)
    {
        if (updateBusy || updateInstalling || shuttingDown) return;
        updateBusy = true; nextUpdateCheck = DateTimeOffset.UtcNow.AddHours(6);
        updateCancellation = new(); RefreshUpdateButtons(); UpdateStatusText.Text = "正在检查 GitHub 最新正式版…";
        bool download = false;
        try
        {
            var release = await updateClient.CheckAsync(CurrentVersion, updateCancellation.Token);
            if (shuttingDown) return;
            if (availableUpdate?.Tag != release?.Tag) downloadedUpdate = null;
            availableUpdate = release;
            UpdateStatusText.Text = release == null ? "当前已是最新正式版本。" : downloadedUpdate != null ? $"{release.Tag} 已下载并校验，可退出并安装。" : $"发现新版 {release.Tag}（{release.Size / 1048576d:F1} MB）。";
            download = release != null && downloadedUpdate == null && controller.Settings.AutoDownloadUpdates;
            if (!automatic) autoInstallPaused = false;
        }
        catch (Exception error) { UpdateFailure(error); }
        finally { updateCancellation.Dispose(); updateCancellation = null; updateBusy = false; RefreshUpdateButtons(); }
        if (download && !shuttingDown) await DownloadUpdateAsync();
    }
    private async Task DownloadUpdateAsync()
    {
        if (availableUpdate == null || updateBusy || updateInstalling || shuttingDown) return;
        var release = availableUpdate; updateBusy = true; updateCancellation = new(); RefreshUpdateButtons();
        UpdateProgress.Value = 0; UpdateProgress.Visibility = Visibility.Visible;
        UpdateStatusText.Text = $"正在下载 {release.Tag}，可继续使用语音输入…";
        try
        {
            var progress = new Progress<double>(value => { if (!closed && updateBusy) UpdateProgress.Value = value; });
            downloadedUpdate = await updateClient.DownloadAsync(release, Path.Combine(Program.DataDirectory, "updates"), progress, updateCancellation.Token);
            if (shuttingDown) return;
            autoInstallPaused = false; idleUpdateSince = DateTimeOffset.UtcNow;
            UpdateStatusText.Text = $"{release.Tag} 已下载并通过 SHA-256 校验，点击“退出并安装”开始更新。";
            tray?.ShowBalloonTip(4000, "VoiceInput 新版已准备好", $"{release.Tag} 已下载，可在设置 → 软件更新中安装。", System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception error) { UpdateFailure(error); }
        finally { updateCancellation.Dispose(); updateCancellation = null; updateBusy = false; UpdateProgress.Visibility = Visibility.Collapsed; RefreshUpdateButtons(); }
    }
    private async Task InstallUpdateAsync(bool automatic)
    {
        if (downloadedUpdate == null || updateBusy || updateInstalling || shuttingDown) return;
        if (SettingsHaveUnsavedChanges()) { UpdateStatusText.Text = "设置页仍有未保存修改，请先保存设置再安装。"; return; }
        if (ManagementBusy || trayMenu?.IsOpen == true) { UpdateStatusText.Text = "请等待本轮输入或管理操作完成后安装。"; return; }
        updateInstalling = true; RefreshUpdateButtons();
        try
        {
            await RunManagementOperationAsync(async () =>
            {
                string? directory = InstalledUpdateDirectory();
                if (automatic && directory == null) return;
                if (!automatic && (directory == null || !controller.Settings.SaveMemory))
                    if (!await Dialogs.ConfirmAsync(this, "退出并安装新版", (directory == null ? "当前为便携版，将打开安装版向导，原便携文件保留。\n" : "") +
                        (!controller.Settings.SaveMemory ? "文本记忆保存已关闭，退出前请确认需要的正文已复制或导出。\n" : "") + "继续将退出 VoiceInput 并打开安装程序。", "退出并安装")) return;
                await controller.PrepareUpdateAsync();
                await using var verified = await GitHubUpdates.OpenVerifiedAsync(downloadedUpdate, CancellationToken.None);
                if (shuttingDown || closed) return;
                var start = new ProcessStartInfo(downloadedUpdate.Path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(downloadedUpdate.Path)! };
                start.ArgumentList.Add($"/UPDATEWAIT={Environment.ProcessId}"); start.ArgumentList.Add("/NORESTART");
                if (directory != null) start.ArgumentList.Add("/DIR=" + directory);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("安装程序未能启动，请重试。");
                UpdateStatusText.Text = "安装程序已启动，正在保存并退出…";
                await RequestExitAsync(true);
            });
        }
        catch (Exception error) { UpdateFailure(error); autoInstallPaused = true; }
        finally { updateInstalling = false; if (!closed) RefreshUpdateButtons(); }
    }
    private async void UpdateCheck_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(false);
    private async void UpdateDownload_Click(object sender, RoutedEventArgs e) => await DownloadUpdateAsync();
    private async void UpdateInstall_Click(object sender, RoutedEventArgs e) => await InstallUpdateAsync(false);
    private void UpdateCancel_Click(object sender, RoutedEventArgs e) { autoInstallPaused = true; updateCancellation?.Cancel(); }
    private async void UpdateRelease_Click(object sender, RoutedEventArgs e) => await Safe(() =>
    {
        Process.Start(new ProcessStartInfo(availableUpdate?.PageUrl ?? $"https://github.com/{GitHubUpdates.Repository}/releases") { UseShellExecute = true });
        return Task.CompletedTask;
    });
}
