using Microsoft.Win32;

namespace RealtimeTranscription.Desktop;

internal enum StartupState { Disabled, Enabled, DisabledByWindows, OtherLocation, ForeignEntry }

internal sealed record StartupStatus(StartupState State, string Message)
{
    public bool Enabled => State == StartupState.Enabled;
}

/// <summary>Per-user startup registration. The registry, including Windows' veto, is the source of truth.</summary>
internal sealed class StartupService
{
    internal const string ValueName = "VoiceInput";
    internal const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string MetadataPath = @"Software\VoiceInput\Startup";
    private const string PolicyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private readonly RegistryKey root;
    private readonly string runPath, approvedPath, metadataPath;
    private readonly Func<string, bool> fileExists;
    private readonly Func<bool> disabledByPolicy;
    internal string ExecutablePath { get; }
    internal string Command { get; }

    internal static StartupService CreateCurrent() => new(Registry.CurrentUser, RunPath, ApprovedPath, MetadataPath,
        ResolveExecutable(AppContext.BaseDirectory, Environment.ProcessPath, File.Exists), File.Exists, IsRunDisabledByPolicy);

    // Injection keeps Windows smoke checks confined to a disposable registry branch.
    internal StartupService(RegistryKey root, string runPath, string approvedPath, string metadataPath,
        string executablePath, Func<string, bool> fileExists, Func<bool> disabledByPolicy)
    {
        this.root = root;
        this.runPath = runPath;
        this.approvedPath = approvedPath;
        this.metadataPath = metadataPath;
        this.fileExists = fileExists;
        this.disabledByPolicy = disabledByPolicy;
        ExecutablePath = Path.GetFullPath(executablePath);
        Command = BuildCommand(ExecutablePath);
    }

    internal static bool IsStartup(IEnumerable<string> args) => args.Any(arg => arg.Equals("--startup", StringComparison.OrdinalIgnoreCase));

    internal static string ResolveExecutable(string baseDirectory, string? processPath, Func<string, bool> exists)
    {
        string directory = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Only the known app/ layout opts into a parent launcher; do not pick an
        // unrelated executable from the parent of a developer build directory.
        if (Path.GetFileName(directory).Equals("app", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(directory);
            if (parent != null)
            {
                string launcher = Path.Combine(parent, "VoiceInput.exe");
                if (exists(launcher)) return launcher;
            }
        }
        string localLauncher = Path.Combine(directory, "VoiceInput.exe");
        if (exists(localLauncher)) return localLauncher;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            Path.GetFileName(processPath).Equals("RealtimeTranscription.exe", StringComparison.OrdinalIgnoreCase) && exists(processPath))
            return Path.GetFullPath(processPath);
        string app = Path.Combine(directory, "RealtimeTranscription.exe");
        if (exists(app)) return app;
        throw new InvalidOperationException("未找到完整的程序文件，请先将压缩包完整解压到固定位置。");
    }

    internal static string BuildCommand(string executablePath)
    {
        if (!Path.IsPathFullyQualified(executablePath) || executablePath.IndexOfAny(['\"', '\r', '\n', '\0']) >= 0 ||
            !Path.GetExtension(executablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("自启动程序路径无效。");
        string command = "\"" + Path.GetFullPath(executablePath) + "\" --startup";
        // Run/RunOnce has a documented 260-character command limit. Refuse a
        // broken entry rather than silently offering startup that cannot work.
        if (command.Length > 260) throw new InvalidOperationException("程序路径过长，无法登记开机自启动。请移到较短路径后重新开启。");
        return command;
    }

    internal static string? ParseOwnedCommand(string? command)
    {
        const string suffix = "\" --startup";
        if (command == null || !command.StartsWith('"') || !command.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        string path = command[1..^suffix.Length];
        try { return Same(BuildCommand(path), command) ? path : null; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException) { return null; }
    }

    internal StartupStatus Read()
    {
        string? command = ReadString(runPath, ValueName);
        if (command == null)
        {
            using var run = root.OpenSubKey(runPath);
            return run?.GetValue(ValueName) == null
                ? new(StartupState.Disabled, "登录 Windows 后自动在托盘运行；默认关闭，不会自动录音。")
                : new(StartupState.ForeignEntry, "同名启动项已被其他程序使用，未作修改。");
        }
        if (!Owns(command)) return new(StartupState.ForeignEntry, "同名启动项已被其他程序使用，未作修改。");
        if (disabledByPolicy()) return new(StartupState.DisabledByWindows, "Windows 管理策略已禁用此类启动项，请联系设备管理员。");
        if (!ApprovalAllowsStartup()) return new(StartupState.DisabledByWindows, "Windows 已禁用此启动项，请在“启动应用”中启用 VoiceInput。");
        if (!Same(command, Command)) return new(StartupState.OtherLocation, "自启动目前指向其他位置的 VoiceInput；开启此开关可改用当前版本。");
        return new(StartupState.Enabled, "已开启：登录 Windows 后在托盘运行，不弹出主窗口、不自动录音。");
    }

    internal StartupStatus SetEnabled(bool enabled)
    {
        string? previous = ReadString(runPath, ValueName);
        using (var existing = root.OpenSubKey(runPath))
            if (existing?.GetValue(ValueName) != null && (previous == null || !Owns(previous)))
                throw new InvalidOperationException("同名启动项已被其他程序使用，无法覆盖。请先在 Windows 启动应用中检查。");
        if (enabled)
        {
            if (!fileExists(ExecutablePath)) throw new InvalidOperationException("程序文件已移动或删除，请在当前位置重新打开完整程序后设置。");
            if (disabledByPolicy()) return new(StartupState.DisabledByWindows, "Windows 管理策略已禁用此类启动项，请联系设备管理员。");
            if (!ApprovalAllowsStartup()) return new(StartupState.DisabledByWindows, "Windows 已禁用此启动项，请在“启动应用”中启用 VoiceInput。");
            // Open both writable handles before mutating either key. Record
            // ownership first; a failed Run write never claims startup enabled.
            using var run = root.CreateSubKey(runPath, writable: true) ?? throw new IOException("无法写入开机自启动设置。");
            using var metadata = root.CreateSubKey(metadataPath, writable: true) ?? throw new IOException("无法保存自启动登记信息。");
            string? oldOwner = metadata.GetValue("RegisteredCommand") as string;
            metadata.SetValue("RegisteredCommand", Command, RegistryValueKind.String);
            try { run.SetValue(ValueName, Command, RegistryValueKind.String); }
            catch
            {
                try { if (oldOwner == null) metadata.DeleteValue("RegisteredCommand", false); else metadata.SetValue("RegisteredCommand", oldOwner); }
                catch { /* Preserve the original write failure; Read() still checks Run. */ }
                throw;
            }
        }
        else
        {
            // Removing Run, rather than changing StartupApproved, preserves the
            // user's Windows-level choice across later app registrations.
            using var run = root.OpenSubKey(runPath, writable: true);
            if (previous != null && Owns(previous) && Same(run?.GetValue(ValueName) as string, previous)) run?.DeleteValue(ValueName, false);
            using var metadata = root.OpenSubKey(metadataPath, writable: true);
            if (Same(metadata?.GetValue("RegisteredCommand") as string, previous)) metadata?.DeleteValue("RegisteredCommand", false);
        }
        return Read();
    }

    internal StartupStatus RepairMovedPortable()
    {
        // Run-key programs must not rewrite Run while Windows is enumerating
        // logon entries. This method is called only after an ordinary launch.
        string? command = ReadString(runPath, ValueName);
        string? previousPath = ParseOwnedCommand(command);
        if (previousPath != null && Owns(command!) && !Same(command, Command) &&
            !fileExists(previousPath) && fileExists(ExecutablePath) && ApprovalAllowsStartup() && !disabledByPolicy())
            return SetEnabled(true);
        return Read();
    }

    private bool Owns(string command) => ParseOwnedCommand(command) != null &&
        (Same(command, Command) || Same(command, ReadString(metadataPath, "RegisteredCommand")));

    private string? ReadString(string keyPath, string name)
    {
        using var key = root.OpenSubKey(keyPath);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    private bool ApprovalAllowsStartup()
    {
        using var approved = root.OpenSubKey(approvedPath);
        object? value = approved?.GetValue(ValueName);
        if (value == null) return true;
        // StartupApproved is OS-owned. Read the known allowed states only and
        // conservatively defer unknown states to Windows; never delete/rewrite it.
        return value is byte[] { Length: >= 4 } data && BitConverter.ToUInt32(data, 0) is 2 or 6;
    }

    private static bool IsRunDisabledByPolicy()
    {
        using var user = Registry.CurrentUser.OpenSubKey(PolicyPath);
        using var machine = Registry.LocalMachine.OpenSubKey(PolicyPath);
        return user?.GetValue("DisableCurrentUserRun") is int userValue && userValue != 0 ||
            machine?.GetValue("DisableCurrentUserRun") is int machineValue && machineValue != 0;
    }

    private static bool Same(string? first, string? second) => first != null && second != null && first.Equals(second, StringComparison.OrdinalIgnoreCase);
}
