using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

namespace RealtimeTranscription.Desktop;

/// <summary>Offline verification of the actual Windows executable and WPF resources.</summary>
public static class DesktopSmoke
{
    public static bool IsSmoke(string[] args) => args.Length > 0 && args[0] == "--smoke-test";
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || !Path.IsPathFullyQualified(args[1])) return 2;
        string report = args[1];
        string folder = Path.Combine(Path.GetTempPath(), "VoiceInputSmoke-" + Guid.NewGuid().ToString("N"));
        var checks = new List<string>(); var errors = new List<string>();
        MainWindow? window = null; VoiceOverlay? overlay = null; AppController? controller = null;
        var binding = new BindingErrors(errors);
        DispatcherUnhandledExceptionEventHandler unhandled = (_, e) => { errors.Add("Dispatcher: " + e.Exception.GetType().Name); e.Handled = true; };
        System.Windows.Application.Current.DispatcherUnhandledException += unhandled;
        PresentationTraceSources.DataBindingSource.Listeners.Add(binding);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            Directory.CreateDirectory(folder);
            // Real Windows DPAPI and SQLite; outbound HTTP is rejected if any UI path
            // unexpectedly tries to call a provider. Ready() is deliberately not called:
            // it owns global input hooks, device watchers, and the capture workflow.
            controller = new AppController(folder, provider: new NoNetwork());
            await controller.InitializeAsync();
            await controller.SaveSettingsAsync(controller.Settings, new("SMOKE_LOCAL_ONLY", "SMOKE_LOCAL_ONLY"));
            var credentials = new SettingsStore(folder, new WindowsProtector()).LoadCredentials();
            if (credentials != new Credentials("SMOKE_LOCAL_ONLY", "SMOKE_LOCAL_ONLY")) throw new InvalidOperationException("Windows DPAPI credential round-trip failed.");
            checks.Add("Windows DPAPI credentials and SQLite initialized in an isolated temporary directory");
            window = new MainWindow(controller);
            window.Show();
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
            var tabs = Find<TabControl>(window, "Tabs");
            for (int i = 0; i < tabs.Items.Count; i++)
            {
                tabs.SelectedIndex = i;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                if (tabs.SelectedContent is not FrameworkElement content || content.ActualWidth <= 0 || content.ActualHeight <= 0)
                    throw new InvalidOperationException("A management tab did not render: " + i);
            }
            var vocabulary = Find<TabControl>(window, "VocabularyTabs");
            tabs.SelectedIndex = 2;
            for (int i = 0; i < vocabulary.Items.Count; i++)
            {
                vocabulary.SelectedIndex = i;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
            }
            checks.Add("Every management and vocabulary tab instantiated its actual WPF content");

            const string text = "离线界面验收：JUNA 测量 12C(α,γ)16O。\r\n\r\n第二段核对完整正文。";
            var session = new SessionData { Title = "离线界面验收", WholePolishState = "Fallback", DeliveryState = "NotRequested" };
            var segment = new SegmentData { SessionId = session.Id, TaskId = "smoke", TaskOrder = 1, SentenceId = 1,
                RawText = text, FinalText = text, AsrState = AsrState.Confirmed, OutputState = OutputState.Published, SourceRevision = 1 };
            await controller.Repository.SaveSessionAsync(session);
            await controller.Repository.SaveSegmentAsync(session, segment);
            await controller.LoadSessionAsync(session);
            await controller.SaveTermAsync(new() { Text = "JUNA", Scope = "default" });
            await controller.SaveSettingsAsync(controller.Settings with { Hotkey = "F9", DictationOnly = true }, controller.Keys);
            tabs.SelectedIndex = 0;
            // Allow the real DispatcherTimer to consume the controller's pending snapshot.
            await Task.Delay(350);
            window.UpdateLayout();
            if (Find<TextBox>(window, "OutputBox").Text != text) throw new InvalidOperationException("The displayed transcript differs from the loaded history.");
            if (!Find<TextBlock>(window, "BodyCount").Text.Contains(JsonCodec.Count(text).ToString())) throw new InvalidOperationException("The body count did not update.");
            if (Find<DataGrid>(window, "TermsGrid").Items.Count != 1) throw new InvalidOperationException("The visible lexicon did not refresh.");
            string version = typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
            if (!Find<TextBlock>(window, "VersionInfo").Text.Contains(version) || !Find<TextBlock>(window, "HotkeyHint").Text.Contains("F9"))
                throw new InvalidOperationException("The displayed version or configured hotkey is stale.");
            checks.Add("Production controller updates transcript, Unicode count, and lexicon controls");
            checks.Add("Version and dictation hotkey labels follow current production settings");

            overlay = new VoiceOverlay();
            IntPtr foreground = GetForegroundWindow();
            overlay.Update("离线悬浮提示验收", "模拟识别结果；不启动录音。", dismiss: false);
            overlay.UpdateLayout();
            var handle = new WindowInteropHelper(overlay).Handle;
            long style = GetWindowLongPtr(handle, -20).ToInt64();
            if (handle == IntPtr.Zero || (style & 0x08000000) == 0 || overlay.ShowActivated || overlay.Focusable)
                throw new InvalidOperationException("The voice overlay is missing its non-activation safeguards.");
            if (foreground != IntPtr.Zero && GetForegroundWindow() != foreground) throw new InvalidOperationException("The voice overlay changed foreground focus.");
            checks.Add("Actual overlay HWND has WS_EX_NOACTIVATE and preserves the current foreground window");
            overlay.Clear();

            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
            if (window.ActualWidth <= 0 || window.ActualHeight <= 0) throw new InvalidOperationException("Minimum-size layout failed.");
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.ChangeExtension(report, ".png"))) png.Save(stream);
            checks.Add("Minimum-size WPF window rendered to a PNG for review");
        }
        catch (Exception e) { errors.Add(e.GetType().Name + ": " + e.Message); }
        finally
        {
            overlay?.Close(); window?.Hide();
            if (controller != null) { try { await controller.DisposeAsync(); } catch (Exception e) { errors.Add("Shutdown: " + e.GetType().Name); } }
            PresentationTraceSources.DataBindingSource.Listeners.Remove(binding);
            System.Windows.Application.Current.DispatcherUnhandledException -= unhandled;
            try { Directory.Delete(folder, recursive: true); } catch { /* OS file cleanup can lag process shutdown. */ }
        }
        bool passed = errors.Count == 0;
        try
        {
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { passed, checks, errors,
                version = typeof(App).Assembly.GetName().Version?.ToString(), microphone = "not started", cloud = "blocked", globalInputHooks = "not installed" }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { return 3; }
        return passed ? 0 : 1;
    }

    private static T Find<T>(MainWindow window, string name) where T : FrameworkElement
        => window.FindName(name) as T ?? throw new InvalidOperationException("Missing WPF control: " + name);
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Network access is prohibited during the offline desktop smoke test.");
    }
    private sealed class BindingErrors(List<string> errors) : TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        { if (eventType is TraceEventType.Error or TraceEventType.Critical) errors.Add("WPF binding: " + message); }
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
        { if (eventType is TraceEventType.Error or TraceEventType.Critical) errors.Add("WPF binding: " + format); }
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
}
