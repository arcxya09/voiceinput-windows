using System.Runtime.InteropServices;
using System.Security.Principal;
using RealtimeTranscription.Desktop.Input;

namespace RealtimeTranscription.Desktop;

internal static class Program
{
    internal static bool IsStartupLaunch { get; private set; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    private static int Main(string[] args)
    {
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

            // Windows App SDK 2.4 exposes this generated entry point. It keeps the
            // SDK's COM wrappers, DispatcherQueue synchronization context, and
            // Application.Start sequence intact after our early worker dispatch.
            // Undocked reg-free WinRT is initialized by the SDK module initializer.
            XamlGeneratedProgram.XamlGeneratedMain();
            return Environment.ExitCode;
        }
        finally
        {
            if (ownsMutex) { try { instance?.ReleaseMutex(); } catch (ApplicationException) { } }
            instance?.Dispose();
        }
    }
}
