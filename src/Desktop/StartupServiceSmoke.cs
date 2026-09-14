using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Desktop;

internal static class StartupServiceSmoke
{
    internal static bool IsStartupSmoke(string[] args) => args.Contains("--startup-smoke", StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> RunIsolatedChecks()
    {
        string branch = @"Software\VoiceInput\Smoke\" + Guid.NewGuid().ToString("N");
        string run = branch + @"\Run", approved = branch + @"\Approved", metadata = branch + @"\Metadata";
        string oldPath = @"C:\VoiceInput Smoke\原位置\VoiceInput.exe";
        string newPath = @"C:\VoiceInput Smoke\新位置\VoiceInput.exe";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { oldPath, newPath };
        bool policyDisabled = false;
        var first = new StartupService(Registry.CurrentUser, run, approved, metadata, oldPath, files.Contains, () => policyDisabled);
        var moved = new StartupService(Registry.CurrentUser, run, approved, metadata, newPath, files.Contains, () => policyDisabled);
        void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("Startup smoke: " + message); }
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return; }
            throw new InvalidOperationException("Startup smoke: " + message);
        }
        try
        {
            Require(first.Command == "\"" + oldPath + "\" --startup", "The executable path was not quoted exactly.");
            Require(StartupService.ParseOwnedCommand(first.Command) == oldPath, "Quoted Unicode paths did not round-trip.");
            Require(StartupService.IsStartup(["--startup"]) && !StartupService.IsStartup(["--startup-smoke"]), "Startup argument matching is ambiguous.");
            Reject(() => StartupService.BuildCommand("relative.exe"), "A relative startup command was accepted.");
            Reject(() => StartupService.BuildCommand("C:\\bad\" --other.exe"), "A quote-injected command was accepted.");
            Reject(() => StartupService.BuildCommand("C:\\bad\nname.exe"), "A line break in the command was accepted.");
            Reject(() => StartupService.BuildCommand("C:\\" + new string('a', 260) + ".exe"), "An oversized Run command was accepted.");
            Require(StartupService.ParseOwnedCommand(first.Command + " --unrelated") == null, "Extra arguments were considered owned.");
            Require(StartupService.ResolveExecutable(@"C:\VoiceInput Smoke\原位置\app\", @"C:\VoiceInput Smoke\原位置\app\RealtimeTranscription.exe", files.Contains) == oldPath,
                "The app/ payload did not resolve its stable parent launcher.");
            Require(!first.Read().Enabled && first.Read().State == StartupState.Disabled, "Startup was not off by default.");

            Require(first.SetEnabled(true).Enabled, "Registering startup did not read back as enabled.");
            using (var key = Registry.CurrentUser.OpenSubKey(run))
                Require((string?)key?.GetValue(StartupService.ValueName) == first.Command && key.GetValueKind(StartupService.ValueName) == RegistryValueKind.String,
                    "The Run registration is not an exact REG_SZ command.");
            Require(moved.RepairMovedPortable().State == StartupState.OtherLocation, "Launching another existing copy stole startup registration.");
            files.Remove(oldPath);
            Require(moved.RepairMovedPortable().Enabled, "An owned, moved portable registration was not repaired.");
            using (var key = Registry.CurrentUser.OpenSubKey(run)) Require((string?)key?.GetValue(StartupService.ValueName) == moved.Command, "Relocation retained the stale command.");

            byte[] veto = [3, 0, 0, 0, 9, 8, 7, 6, 5, 4, 3, 2];
            using (var key = Registry.CurrentUser.CreateSubKey(approved)) key.SetValue(StartupService.ValueName, veto, RegistryValueKind.Binary);
            Require(moved.Read().State == StartupState.DisabledByWindows && !moved.SetEnabled(true).Enabled, "Windows' disabled startup state was overridden.");
            using (var key = Registry.CurrentUser.OpenSubKey(approved)) Require(((byte[])key!.GetValue(StartupService.ValueName)!).SequenceEqual(veto), "The OS-owned approval bytes changed.");
            files.Add(oldPath); files.Remove(newPath);
            Require(!first.RepairMovedPortable().Enabled, "Moving a portable app bypassed Windows' startup veto.");
            using (var key = Registry.CurrentUser.OpenSubKey(run)) Require((string?)key?.GetValue(StartupService.ValueName) == moved.Command, "A vetoed startup entry was rewritten.");
            files.Add(newPath);
            using (var key = Registry.CurrentUser.CreateSubKey(approved)) key.SetValue(StartupService.ValueName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            Require(moved.Read().Enabled, "Windows' re-enabled startup state was not reflected.");
            policyDisabled = true;
            Require(moved.Read().State == StartupState.DisabledByWindows && !moved.SetEnabled(true).Enabled, "A policy veto was ignored.");
            policyDisabled = false;
            using (var key = Registry.CurrentUser.CreateSubKey(approved)) key.SetValue(StartupService.ValueName, new byte[] { 99, 0, 0, 0 }, RegistryValueKind.Binary);
            Require(!moved.Read().Enabled, "An unknown Windows approval state was treated as enabled.");
            using (var key = Registry.CurrentUser.CreateSubKey(approved)) key.DeleteValue(StartupService.ValueName);

            Require(!moved.SetEnabled(false).Enabled && moved.Read().State == StartupState.Disabled, "Disabling left startup active.");
            Require(moved.RepairMovedPortable().State == StartupState.Disabled, "A removed startup registration was resurrected.");
            using (var key = Registry.CurrentUser.CreateSubKey(run)) key.SetValue(StartupService.ValueName, @"C:\OtherProgram.exe --unrelated");
            Require(moved.Read().State == StartupState.ForeignEntry, "An unrelated registry command was considered owned.");
            Reject(() => moved.SetEnabled(true), "An unrelated registration was overwritten.");
            Reject(() => moved.SetEnabled(false), "An unrelated registration was deleted.");
            using (var key = Registry.CurrentUser.OpenSubKey(run)) Require((string?)key?.GetValue(StartupService.ValueName) == @"C:\OtherProgram.exe --unrelated", "A foreign entry changed.");
            using (var key = Registry.CurrentUser.CreateSubKey(run)) key.DeleteValue(StartupService.ValueName);

            using (var readOnly = Registry.CurrentUser.OpenSubKey(branch, writable: false))
            {
                var denied = new StartupService(readOnly!, "Run", "Approved", "Metadata", newPath, files.Contains, () => false);
                bool rejected = false;
                try { denied.SetEnabled(true); }
                catch (UnauthorizedAccessException) { rejected = true; }
                Require(rejected && !denied.Read().Enabled, "A denied write was reported as successful.");
            }
            return [
                "Startup commands quote Unicode paths, reject injection and oversized Run entries, and resolve the organized portable launcher",
                "Per-user startup registration, disable, moved-portable repair, foreign-entry protection, and denied writes pass under an isolated temporary registry key",
                "Windows startup disable and policy veto remain authoritative; unknown approval states and deleted entries are never silently enabled"
            ];
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(branch, throwOnMissingSubKey: false); }
    }

    internal static async Task<int> RunStartupAsync(string[] args)
    {
        int index = Array.FindIndex(args, arg => arg.Equals("--startup-smoke", StringComparison.OrdinalIgnoreCase));
        if (!Program.IsStartupLaunch || index < 0 || index + 1 >= args.Length || !Path.IsPathFullyQualified(args[index + 1])) return 2;
        string report = args[index + 1];
        string folder = Path.Combine(Path.GetTempPath(), "VoiceInputStartupSmoke-" + Guid.NewGuid().ToString("N"));
        var errors = new List<string>();
        bool visible = true, trayVisible = false, captureIdle = false, focusPreserved = false;
        MainWindow? window = null;
        AppController? controller = null;
        var network = new NoNetwork();
        try
        {
            Directory.CreateDirectory(folder);
            controller = new AppController(folder, provider: network);
            await controller.InitializeAsync();
            await controller.SaveSettingsAsync(controller.Settings, new("SMOKE_LOCAL_ONLY", "SMOKE_LOCAL_ONLY"));
            // The production helper makes the same activation decision here.
            // Ready() is intentionally skipped: the real tray is safe to create,
            // while global hotkeys and audio device watchers belong to real use.
            IntPtr foreground = GetForegroundWindow();
            window = App.CreateWindowForLaunch(controller, Program.IsStartupLaunch);
            window.PrepareStartupSmokeTray();
            IntPtr handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(80);
                if (IsWindowVisible(handle)) throw new InvalidOperationException("The startup manager became visible.");
            }
            visible = IsWindowVisible(handle);
            trayVisible = window.StartupSmokeTrayVisible;
            captureIdle = (await controller.SnapshotAsync()).State == CaptureState.Idle;
            focusPreserved = GetForegroundWindow() == foreground;
            if (!trayVisible || !captureIdle || !focusPreserved || network.Requests != 0) throw new InvalidOperationException("Startup did not remain idle in the tray without changing focus.");
        }
        catch (Exception e) { errors.Add(e.GetType().Name + ": " + e.Message); }
        finally
        {
            window?.CleanupStartupSmokeUi();
            if (controller != null)
                try { await controller.DisposeAsync(); } catch (Exception e) { errors.Add("Shutdown: " + e.GetType().Name); }
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
        bool passed = errors.Count == 0 && !visible && trayVisible && captureIdle && focusPreserved && network.Requests == 0;
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { passed, visible, trayVisible, captureIdle, focusPreserved, networkRequests = network.Requests, errors }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    private sealed class NoNetwork : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            throw new InvalidOperationException("Startup smoke must never call a provider.");
        }
    }
}
