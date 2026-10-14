using System.Diagnostics;
using System.IO.Compression;

namespace RealtimeTranscription.Infrastructure;

/// <summary>
/// Replaces a portable copy in place. The running application stages the verified ZIP, then starts
/// the staged <c>app\RealtimeTranscription.exe</c> with <see cref="ApplyArgument"/>. That helper waits
/// for the old process to exit, swaps only the documented top-level items and restarts the copy.
/// The helper always comes from the new version, so keep its command line stable across releases:
/// <c>--apply-portable-update &lt;parent pid&gt; &lt;portable root&gt; &lt;restart 0|1|2&gt;</c>
/// (2 restarts quietly in the tray; 2.6.0 sends only 0 or 1).
/// </summary>
public static class PortableUpdate
{
    public const string ApplyArgument = "--apply-portable-update";
    public const string FailureFile = "portable-update-error.txt";
    public const string InstallerFailureFile = "install-update-error.txt";
    private const string StagingPrefix = "portable-", BackupPrefix = ".update-backup-", IncomingPrefix = ".update-new-";
    // Matches the portable root that build.ps1 creates and WindowsSmoke.ps1 checks.
    public static readonly string[] RootItems = ["VoiceInput.exe", "app", "licenses", "README.txt", "LICENSE.txt"];

    public static bool IsApply(string[] args) => args.Length > 0 && args[0] == ApplyArgument;
    public static bool IsRoot(string root) =>
        File.Exists(Path.Combine(root, "VoiceInput.exe")) && File.Exists(Path.Combine(root, "app", "RealtimeTranscription.exe"));
    public static string[] HelperArguments(int parent, string root, bool restart, bool quiet = false) =>
        [ApplyArgument, parent.ToString(System.Globalization.CultureInfo.InvariantCulture), Path.GetFullPath(root), restart ? quiet ? "2" : "1" : "0"];

    public static void EnsureWritable(string root)
    {
        string probe = Path.Combine(root, ".voiceinput-write-" + Guid.NewGuid().ToString("N"));
        try { using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { } }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("便携版所在文件夹不可写，无法原地更新。请把便携版移到可写位置，或改用安装版。", error); }
    }

    /// <summary>Extracts a verified portable ZIP beside other downloads; returns the staged portable root.</summary>
    public static async Task<string> StageAsync(DownloadedUpdate update, string directory, CancellationToken token)
    {
        if (!update.Release.Portable) throw new InvalidOperationException("下载的不是便携版更新包。");
        string staging = Path.Combine(directory, StagingPrefix + update.Release.Version.ToString(3)), partial = staging + "." + Guid.NewGuid().ToString("N") + ".part";
        DeleteDirectory(staging);
        try
        {
            await using (var verified = await GitHubUpdates.OpenVerifiedAsync(update, token))
            {
                verified.Position = 0;
                // ExtractToDirectory rejects entries that would leave the destination.
                await Task.Run(() => ZipFile.ExtractToDirectory(verified, partial), token);
            }
            var names = Directory.EnumerateFileSystemEntries(partial).Select(path => Path.GetFileName(path)).Order(StringComparer.OrdinalIgnoreCase);
            if (!names.SequenceEqual(RootItems.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase) || !IsRoot(partial))
                throw new InvalidDataException("便携版更新包结构异常，已停止更新。");
            Directory.Move(partial, staging);
            return staging;
        }
        finally { DeleteDirectory(partial); }
    }

    /// <summary>Swaps the documented items from <paramref name="staging"/> into <paramref name="target"/>; restores the old files on failure.</summary>
    public static void Apply(string staging, string target)
    {
        if (!IsRoot(staging) || !IsRoot(target)) throw new InvalidDataException("便携版目录结构异常，已停止更新。");
        string suffix = Guid.NewGuid().ToString("N");
        string backup = Path.Combine(target, BackupPrefix + suffix), incoming = Path.Combine(target, IncomingPrefix + suffix);
        // Copy the new files into the portable folder first. The swap below is then only
        // renames within one volume, so an interrupted copy (shutdown, logoff) leaves the
        // old program untouched and the swap window is short.
        try
        {
            Directory.CreateDirectory(incoming);
            foreach (string item in RootItems) Copy(Path.Combine(staging, item), Path.Combine(incoming, item));
        }
        catch { DeleteDirectory(incoming); throw; }
        Directory.CreateDirectory(backup);
        var moved = new List<string>(); var placed = new List<string>();
        try
        {
            foreach (string item in RootItems)
            {
                string destination = Path.Combine(target, item);
                if (Exists(destination)) { Retry(() => Move(destination, Path.Combine(backup, item))); moved.Add(item); }
                Retry(() => Move(Path.Combine(incoming, item), destination)); placed.Add(item);
            }
        }
        catch (Exception error)
        {
            bool restored = true;
            foreach (string item in placed) { try { Delete(Path.Combine(target, item)); } catch { } }
            foreach (string item in moved) { try { Retry(() => Move(Path.Combine(backup, item), Path.Combine(target, item))); } catch { restored = false; } }
            // The backup may hold the only intact copy of the old program; keep it unless every item went back.
            DeleteDirectory(incoming);
            if (!restored) throw new IOException($"更新失败，且未能完全恢复原文件。原文件保留在 {backup}，请手动移回或重新下载便携包。", error);
            DeleteDirectory(backup);
            throw;
        }
        DeleteDirectory(backup); DeleteDirectory(incoming);
    }

    /// <summary>Entry point of the staged helper process. Never throws; failures are recorded beside the downloads.</summary>
    public static int Run(string[] args)
    {
        string staging = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        string? target = null; bool restart = true, quiet = false;
        try
        {
            if (args.Length != 4 || !int.TryParse(args[1], out int parent) || parent <= 0) throw new ArgumentException("便携版更新参数无效。");
            target = Path.GetFullPath(args[2]); restart = args[3] != "0"; quiet = args[3] == "2";
            WaitForExit(parent, TimeSpan.FromMinutes(2));
            Apply(staging, target);
            return Restart(target, restart, quiet) ? 0 : 1;
        }
        catch (Exception error)
        {
            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(staging)!, FailureFile), error.Message); } catch { }
            // After a timeout the old copy is still running; starting another would only focus it.
            if (error is not TimeoutException && target != null && IsRoot(target)) Restart(target, restart, quiet);
            return 1;
        }
    }

    /// <summary>Removes staged packages, leftover backups and downloads no newer than <paramref name="current"/> once the updated copy is running.</summary>
    public static void Cleanup(string directory, string? root, Version? current = null)
    {
        if (Directory.Exists(directory))
        {
            foreach (string path in Directory.EnumerateDirectories(directory, StagingPrefix + "*")) DeleteDirectory(path);
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                string name = Path.GetFileName(path);
                // Interrupted downloads, and packages for this version or older, are never used again.
                var package = System.Text.RegularExpressions.Regex.Match(name, @"^VoiceInput-(?:Setup-|Portable-x64-)(\d+\.\d+\.\d+)\.(?:exe|zip)$");
                bool stale = name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                    current != null && package.Success && Version.TryParse(package.Groups[1].Value, out var version) && version <= current;
                if (stale) try { File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        if (root != null && Directory.Exists(root))
            foreach (string path in Directory.EnumerateDirectories(root, BackupPrefix + "*").Concat(Directory.EnumerateDirectories(root, IncomingPrefix + "*"))) DeleteDirectory(path);
    }

    /// <summary>Returns and clears the reason the last portable update was rolled back, if any.</summary>
    public static string? TakeFailure(string directory)
    {
        string path = Path.Combine(directory, FailureFile);
        try { if (!File.Exists(path)) return null; string text = File.ReadAllText(path); File.Delete(path); return text; }
        catch (IOException) { return null; }
    }

    private static void WaitForExit(int id, TimeSpan timeout)
    {
        Process process;
        try { process = Process.GetProcessById(id); } catch (ArgumentException) { return; }
        using (process) if (!process.WaitForExit(timeout)) throw new TimeoutException("VoiceInput 未能按时退出，便携版更新已取消。");
    }
    private static bool Restart(string root, bool restart, bool quiet)
    {
        if (!restart) return true;
        var start = new ProcessStartInfo(Path.Combine(root, "VoiceInput.exe")) { UseShellExecute = false, WorkingDirectory = root };
        // An automatic install reopens in the tray like a logon start, without activating a window.
        if (quiet) start.ArgumentList.Add("--startup");
        try { using var process = Process.Start(start); return process != null; }
        catch { return false; }
    }
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static void Move(string source, string destination)
    {
        if (Directory.Exists(source)) Directory.Move(source, destination); else File.Move(source, destination);
    }
    private static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path);
    }
    private static void Copy(string source, string destination)
    {
        if (File.Exists(source)) { File.Copy(source, destination); return; }
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }
    private static void DeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    // Child processes such as the UI Automation worker release their image files shortly after the parent exits.
    private static void Retry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (Exception error) when (attempt < 20 && error is IOException or UnauthorizedAccessException) { Thread.Sleep(500); }
        }
    }
}
