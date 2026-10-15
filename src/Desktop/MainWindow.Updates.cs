using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

namespace RealtimeTranscription.Desktop;

public partial class MainWindow
{
    private enum UpdateKind { Unsupported, Installed, Portable }
    private readonly GitHubUpdates updateClient = new();
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private CancellationTokenSource? updateCancellation;
    private GitHubRelease? availableUpdate;
    private DownloadedUpdate? downloadedUpdate;
    private UpdateKind updateKind;
    private string? portableRoot;
    private bool updateBusy, updateInstalling, autoInstallPaused, updateBalloon;
    // Set when this launch is the old copy reopened after a failed update (portable swap or
    // silent installer). A failure that repeats (a locked folder, a full disk) would otherwise
    // exit and relaunch every idle minute. Manual and exit installs remain available.
    private bool autoInstallFailedBefore;
    private DateTimeOffset nextUpdateCheck = DateTimeOffset.MinValue;
    private DateTimeOffset idleUpdateSince = DateTimeOffset.UtcNow;
    // Automatic installs wait for this much keyboard and mouse silence, so a restart never lands mid-dictation.
    private static readonly TimeSpan AutoInstallInputIdle = TimeSpan.FromMinutes(3);
    private static Version CurrentVersion => typeof(Program).Assembly.GetName().Version ?? new Version(2, 2, 0);
    private static string UpdatesDirectory => Path.Combine(Program.DataDirectory, "updates");

    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size; public uint Time; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    private static TimeSpan InputIdle()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.Zero;
    }

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
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        updateKind = InstalledUpdateDirectory() != null ? UpdateKind.Installed : PortableUpdate.IsRoot(root) ? UpdateKind.Portable : UpdateKind.Unsupported;
        portableRoot = updateKind == UpdateKind.Portable ? root : null;
        try { PortableUpdate.Cleanup(UpdatesDirectory, portableRoot, CurrentVersion); }
        catch (Exception error) { controller.Log.Write("Update", "CleanupFailed", exception: error); }
        UpdateVersionText.Text = $"当前版本 {CurrentVersion.ToString(3)} · GitHub 正式版";
        PaneVersionText.Text = $"语音输入 · {CurrentVersion.ToString(3)}";
        UpdateModeText.Text = updateKind switch
        {
            UpdateKind.Installed => "安装版：新版校验通过后在后台静默安装，完成后自动重新打开 VoiceInput；设置、词库和历史记录保留。",
            UpdateKind.Portable => "便携版：下载新版便携包并校验后，在原文件夹替换程序文件，完成后自动重新打开；数据目录不受影响。",
            _ => "未识别安装位置，只能检查新版，请从 GitHub 下载。"
        };
        if (PortableUpdate.TakeFailure(UpdatesDirectory) is string failure)
        {
            UpdateStatusText.Text = "上次便携版更新未完成，已保留原版本：" + failure;
            controller.Log.Write("Update", "PortableApplyFailed");
            autoInstallFailedBefore = true;
        }
        // VoiceInput.iss writes this marker when an in-app silent install stops before installing.
        string installerFailure = Path.Combine(UpdatesDirectory, PortableUpdate.InstallerFailureFile);
        try
        {
            if (File.Exists(installerFailure))
            {
                File.Delete(installerFailure);
                UpdateStatusText.Text = "上次后台安装新版未完成，已保留原版本。可从托盘菜单或本页手动安装，安装失败时请改用安装向导。";
                controller.Log.Write("Update", "InstallerFailed");
                autoInstallFailedBefore = true;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { autoInstallFailedBefore = true; }
        AutoInstallUpdateBox.IsEnabled = updateKind != UpdateKind.Unsupported;
        updateTimer.Tick += UpdateTimer_Tick; updateTimer.Start(); RefreshUpdateButtons();
    }
    private async void UpdateTimer_Tick(object? sender, object e)
    {
        if (closed || shuttingDown || updateInstalling) return;
        if (ManagementBusy || AppWindow.IsVisible || trayMenu?.IsOpen == true) idleUpdateSince = DateTimeOffset.UtcNow;
        if (controller.Settings.AutoCheckUpdates && DateTimeOffset.UtcNow >= nextUpdateCheck && !updateBusy)
            await CheckUpdatesAsync(true);
        if (downloadedUpdate != null && updateKind != UpdateKind.Unsupported && controller.Settings.AutoInstallUpdates && !autoInstallPaused && !autoInstallFailedBefore && !updateBusy &&
            !ManagementBusy && !AppWindow.IsVisible && trayMenu?.IsOpen != true && DateTimeOffset.UtcNow - idleUpdateSince >= TimeSpan.FromSeconds(60) &&
            InputIdle() >= AutoInstallInputIdle)
        {
            // Unsaved text must never disappear silently.
            try
            {
                var snapshot = await controller.SnapshotAsync();
                if (controller.Settings.SaveMemory || snapshot.Segments.Count == 0)
                    await InstallUpdateAsync(true);
            }
            catch (Exception error) { UpdateFailure(error); autoInstallPaused = true; }
        }
    }
    private void RefreshUpdateButtons()
    {
        UpdateCheckButton.IsEnabled = !updateBusy && !updateInstalling;
        UpdateDownloadButton.IsEnabled = availableUpdate != null && updateKind != UpdateKind.Unsupported && !updateBusy && !updateInstalling;
        UpdateDownloadButton.Content = downloadedUpdate != null ? "重新下载" : "下载";
        UpdateInstallButton.IsEnabled = downloadedUpdate != null && updateKind != UpdateKind.Unsupported && !updateBusy && !updateInstalling;
        UpdateCancelButton.IsEnabled = updateBusy;
        trayMenu?.SetUpdate(UpdateInstallButton.IsEnabled ? $"安装新版 {downloadedUpdate!.Release.Tag} 并重启" : null);
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
            var release = await updateClient.CheckAsync(CurrentVersion, updateCancellation.Token, updateKind == UpdateKind.Portable);
            if (shuttingDown) return;
            if (availableUpdate?.Tag != release?.Tag) downloadedUpdate = null;
            availableUpdate = release;
            string checkedAt = $"（检查于 {DateTime.Now:HH:mm}）";
            UpdateStatusText.Text = release == null ? $"当前已是最新正式版本 {CurrentVersion.ToString(3)}{checkedAt}。" : downloadedUpdate != null ? $"{release.Tag} 已下载并校验，可安装并重启。" :
                updateKind == UpdateKind.Unsupported ? $"发现新版 {release.Tag}，请点击“版本说明”下载。" : $"发现新版 {release.Tag}（{release.Size / 1048576d:F1} MB）。";
            // A manual check is an explicit request for the new version, so it downloads even with automatic download off.
            download = release != null && downloadedUpdate == null && updateKind != UpdateKind.Unsupported && (!automatic || controller.Settings.AutoDownloadUpdates);
            if (!automatic) autoInstallPaused = false;
        }
        catch (Exception error) { UpdateFailure(error); }
        finally { updateCancellation.Dispose(); updateCancellation = null; updateBusy = false; RefreshUpdateButtons(); }
        if (download && !shuttingDown) await DownloadUpdateAsync();
    }
    private async Task DownloadUpdateAsync()
    {
        if (availableUpdate == null || updateKind == UpdateKind.Unsupported || updateBusy || updateInstalling || shuttingDown) return;
        var release = availableUpdate; updateBusy = true; updateCancellation = new(); RefreshUpdateButtons();
        UpdateProgress.Value = 0; UpdateProgress.Visibility = Visibility.Visible;
        UpdateStatusText.Text = $"正在下载 {release.Tag}，可继续使用语音输入…";
        try
        {
            var progress = new Progress<double>(value => { if (!closed && updateBusy) UpdateProgress.Value = value; });
            downloadedUpdate = await updateClient.DownloadAsync(release, UpdatesDirectory, progress, updateCancellation.Token);
            if (shuttingDown) return;
            autoInstallPaused = false; idleUpdateSince = DateTimeOffset.UtcNow;
            UpdateStatusText.Text = $"{release.Tag} 已下载并通过 SHA-256 校验，点击“安装并重启”或从托盘菜单安装。";
            updateBalloon = true;
            tray?.ShowBalloonTip(6000, "VoiceInput 新版已准备好", $"{release.Tag} 已下载并校验。点击此提示安装并重启" +
                (controller.Settings.AutoInstallUpdates ? "，或等电脑空闲、退出程序时自动安装。" : "，也可从托盘菜单安装。"), System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception error) { UpdateFailure(error); }
        finally { updateCancellation.Dispose(); updateCancellation = null; updateBusy = false; UpdateProgress.Visibility = Visibility.Collapsed; RefreshUpdateButtons(); }
    }
    /// <summary>Tray Exit installs a downloaded update without restarting. Returns true when the application is exiting for it.</summary>
    private async Task<bool> InstallUpdateOnExitAsync()
    {
        if (downloadedUpdate == null || updateKind == UpdateKind.Unsupported || !controller.Settings.AutoInstallUpdates || controller.FailedSaveCount > 0) return false;
        await InstallUpdateAsync(true, restart: false);
        return shuttingDown || closed;
    }
    private async Task InstallUpdateAsync(bool automatic, bool restart = true)
    {
        if (downloadedUpdate == null || updateKind == UpdateKind.Unsupported || updateBusy || updateInstalling || shuttingDown) return;
        if (SettingsHaveUnsavedChanges()) { UpdateStatusText.Text = "设置页仍有未保存修改，请先保存设置再安装。"; return; }
        if (ManagementBusy || trayMenu?.IsOpen == true) { UpdateStatusText.Text = "请等待本轮输入或管理操作完成后安装。"; return; }
        updateInstalling = true; RefreshUpdateButtons();
        try
        {
            await RunManagementOperationAsync(async () =>
            {
                if (!automatic && !controller.Settings.SaveMemory &&
                    !await Dialogs.ConfirmAsync(this, "安装新版", "文本记忆保存已关闭，退出前请确认需要的正文已复制或导出。\n继续将退出 VoiceInput，更新完成后自动重新打开。", "安装并重启")) return;
                await controller.PrepareUpdateAsync();
                var update = downloadedUpdate ?? throw new InvalidOperationException("更新包已失效，请重新下载。");
                ProcessStartInfo start; FileStream? verified = null;
                try
                {
                    if (updateKind == UpdateKind.Installed)
                    {
                        string directory = InstalledUpdateDirectory() ?? throw new InvalidOperationException("未找到安装位置，请从 GitHub 下载安装包。");
                        verified = await GitHubUpdates.OpenVerifiedAsync(update, CancellationToken.None);
                        start = new(update.Path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(update.Path)! };
                        // The installer waits for this process, installs silently and reopens VoiceInput (VoiceInput.iss).
                        foreach (string argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", $"/UPDATEWAIT={Environment.ProcessId}", "/DIR=" + directory })
                            start.ArgumentList.Add(argument);
                        if (restart) start.ArgumentList.Add("/UPDATERELAUNCH=1");
                        // An automatic install must not pop a window up on an idle (possibly presenting) screen.
                        if (restart && automatic) start.ArgumentList.Add("/UPDATEQUIET=1");
                    }
                    else
                    {
                        string root = portableRoot!;
                        PortableUpdate.EnsureWritable(root);
                        UpdateStatusText.Text = "正在解压新版便携包…";
                        string staging = await PortableUpdate.StageAsync(update, UpdatesDirectory, CancellationToken.None);
                        string helper = Path.Combine(staging, "app", "RealtimeTranscription.exe");
                        start = new(helper) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(helper)! };
                        foreach (string argument in PortableUpdate.HelperArguments(Environment.ProcessId, root, restart, quiet: automatic)) start.ArgumentList.Add(argument);
                    }
                    if (shuttingDown || closed) return;
                    // Start the updater only when RequestExitAsync below will exit. Otherwise it would
                    // wait for this process, give up silently and VoiceInput would keep running.
                    if (Dialogs.IsOpen || pickerOpen || closingPrompt) throw new InvalidOperationException("请先关闭当前对话框，再安装新版。");
                    using var process = Process.Start(start) ?? throw new InvalidOperationException("更新程序未能启动，请重试。");
                }
                finally { if (verified != null) await verified.DisposeAsync(); }
                controller.Log.Write("Update", "InstallStarted", fields: new Dictionary<string, object?> { ["version"] = update.Release.Version.ToString(3), ["portable"] = update.Release.Portable, ["restart"] = restart });
                UpdateStatusText.Text = restart ? "正在退出并后台安装新版，完成后自动重新打开…" : "正在退出并后台安装新版…";
                await RequestExitAsync(true);
            });
        }
        catch (Exception error)
        {
            UpdateFailure(error); autoInstallPaused = true;
            // Tray and idle installs run with the manager window hidden; surface the reason there too.
            if (!shuttingDown && !closed && !AppWindow.IsVisible)
            {
                updateBalloon = false;
                tray?.ShowBalloonTip(6000, "VoiceInput 新版未安装", UpdateStatusText.Text, System.Windows.Forms.ToolTipIcon.Warning);
            }
        }
        finally { updateInstalling = false; if (!closed) RefreshUpdateButtons(); }
    }
    private async void UpdateBalloon_Clicked(object? sender, EventArgs e)
    {
        if (!updateBalloon) return;
        updateBalloon = false;
        await InstallUpdateAsync(false);
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
