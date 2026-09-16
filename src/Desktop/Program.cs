using System.Runtime.InteropServices;
using System.Security.Principal;
using RealtimeTranscription.Desktop.Input;
using RealtimeTranscription.Infrastructure;

namespace RealtimeTranscription.Desktop;

internal static class Program
{
    internal static bool IsStartupLaunch { get; private set; }
    internal static RuntimeLog? Log { get; private set; }
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RealtimeTranscription");
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    private static int Main(string[] args)
    {
        if (InputDeliverySmoke.IsTarget(args)) return InputDeliverySmoke.RunTarget(args);
        // Handle the private worker before creating WinUI, a window, credentials,
        // or a mutex. UI Automation must run on the dedicated MTA task thread.
        if (UiaWorker.IsWorker(args))
        {
            Task.Run(UiaWorker.Run).GetAwaiter().GetResult();
            return 0;
        }

        IsStartupLaunch = StartupService.IsStartup(args);
        _ = SetCurrentProcessExplicitAppUserModelID("VoiceInput.Desktop");

        Mutex? instance = null;
        bool ownsMutex = false;
        try
        {
            if (!DesktopSmoke.IsSmoke(args) && !StartupServiceSmoke.IsStartupSmoke(args))
            {
                string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
                instance = new Mutex(true, "Local\\RealtimeTranscription." + user, out ownsMutex);
                if (!ownsMutex)
                {
                    // Logon must not bring an already-running manager to the
                    // foreground. An explicit second launch still opens it.
                    if (!IsStartupLaunch)
                    {
                        var window = FindWindow(null, "语音输入法");
                        if (window != IntPtr.Zero) { ShowWindow(window, 9); SetForegroundWindow(window); }
                    }
                    return 0;
                }
            }

            if (!DesktopSmoke.IsSmoke(args) && !StartupServiceSmoke.IsStartupSmoke(args))
            {
                Log = new RuntimeLog(Path.Combine(DataDirectory,"logs"));
                Log.Write("Application","ProcessStarted",fields:new Dictionary<string,object?>
                {
                    ["version"]=typeof(Program).Assembly.GetName().Version?.ToString(3),
                    ["windowsVersion"]=Environment.OSVersion.Version.ToString(),
                    ["osArchitecture"]=RuntimeInformation.OSArchitecture,
                    ["processArchitecture"]=RuntimeInformation.ProcessArchitecture,
                    ["runtime"]=RuntimeInformation.FrameworkDescription,
                    ["audioLibraryVersion"]=typeof(NAudio.CoreAudioApi.AudioClient).Assembly.GetName().Version?.ToString(),
                    ["startupLaunch"]=IsStartupLaunch
                });
                AppDomain.CurrentDomain.UnhandledException += (_,eventArgs) =>
                {
                    Log.Write("Application","UnhandledProcessException",fields:new Dictionary<string,object?>
                        { ["terminating"]=eventArgs.IsTerminating },exception:eventArgs.ExceptionObject as Exception);
                    try { Log.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); } catch { }
                };
                TaskScheduler.UnobservedTaskException += (_,eventArgs) =>
                    Log.Write("Application","UnobservedTaskException",exception:eventArgs.Exception);
            }

            // Windows App SDK 2.4 exposes this generated entry point. It keeps the
            // SDK's COM wrappers, DispatcherQueue synchronization context, and
            // Application.Start sequence intact after our early worker dispatch.
            // Undocked reg-free WinRT is initialized by the SDK module initializer.
            XamlGeneratedProgram.XamlGeneratedMain();
            return Environment.ExitCode;
        }
        catch(Exception error)
        {
            Log?.Write("Application","ProcessFailed",exception:error);
            throw;
        }
        finally
        {
            if(Log is {} log)
            {
                log.Write("Application","ProcessExiting",fields:new Dictionary<string,object?>{["exitCode"]=Environment.ExitCode});
                try { log.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); } catch { }
            }
            if (ownsMutex) { try { instance?.ReleaseMutex(); } catch (ApplicationException) { } }
            instance?.Dispose();
        }
    }
}
