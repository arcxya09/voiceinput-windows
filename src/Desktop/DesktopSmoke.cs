using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace RealtimeTranscription.Desktop;

/// <summary>Offline verification of the published WinUI 3 executable and its actual visual tree.</summary>
public static class DesktopSmoke
{
    public static bool IsSmoke(string[] args) => args.Length > 0 && args[0] == "--smoke-test";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || !Path.IsPathFullyQualified(args[1])) return 2;
        string report = args[1];
        string folder = Path.Combine(Path.GetTempPath(), "VoiceInputSmoke-" + Guid.NewGuid().ToString("N"));
        var checks = new List<string>();
        var errors = new List<string>();
        var captures = new List<object>();
        MainWindow? window = null;
        VoiceOverlay? overlay = null;
        AppController? controller = null;
        UnhandledExceptionEventHandler unhandled = (_, e) => { errors.Add("WinUI dispatcher: " + e.Exception.GetType().Name + ": " + e.Exception.Message); e.Handled = true; };
        void BindingFailed(object sender, BindingFailedEventArgs e) => errors.Add("WinUI binding: " + e.Message);
        Application.Current.UnhandledException += unhandled;
        Application.Current.DebugSettings.BindingFailed += BindingFailed;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            Directory.CreateDirectory(folder);
            // Ready() intentionally remains uncalled: it owns global hooks, device
            // watchers and real capture. These checks use an isolated database and
            // a handler that rejects any accidental outbound provider request.
            controller = new AppController(folder, provider: new NoNetwork());
            await controller.InitializeAsync();
            await controller.SaveSettingsAsync(controller.Settings, new("SMOKE_LOCAL_ONLY", "SMOKE_LOCAL_ONLY"));
            var credentials = new SettingsStore(folder, new WindowsProtector()).LoadCredentials();
            Require(credentials == new Credentials("SMOKE_LOCAL_ONLY", "SMOKE_LOCAL_ONLY"), "Windows DPAPI credential round-trip failed.");
            checks.Add("Windows DPAPI credentials and SQLite initialized in an isolated temporary directory");

            window = new MainWindow(controller);
            window.Activate();
            window.AppWindow.Resize(new SizeInt32(1040, 760));
            await LayoutAsync(window);

            const string text = "离线界面验收：JUNA 测量 12C(α,γ)16O。\r\n\r\n第二段核对完整正文。";
            var session = new SessionData { Title = "离线界面验收", WholePolishState = "Fallback", DeliveryState = "NotRequested" };
            var segment = new SegmentData { SessionId = session.Id, TaskId = "smoke", TaskOrder = 1, SentenceId = 1,
                RawText = text, FinalText = text, AsrState = AsrState.Confirmed, OutputState = OutputState.Published, SourceRevision = 1 };
            await controller.Repository.SaveSessionAsync(session);
            await controller.Repository.SaveSegmentAsync(session, segment);
            await controller.LoadSessionAsync(session);
            await controller.SaveTermAsync(new() { Text = "JUNA", Scope = "default" });
            await controller.SaveSettingsAsync(controller.Settings with { Hotkey = "F9", DictationOnly = true }, controller.Keys);
            window.ShowPage(0);
            await UntilAsync(() => Find<TextBox>(window, "OutputBox").Text == text, "The displayed transcript differs from the loaded history.");
            Require(Find<TextBlock>(window, "BodyCount").Text.Contains(JsonCodec.Count(text).ToString()), "The Unicode body count did not update.");
            await UntilAsync(() => Find<ListView>(window, "TermsGrid").Items.Count == 1, "The visible lexicon did not refresh.");
            string version = typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
            Require(Find<TextBlock>(window, "VersionInfo").Text.Contains(version) && Find<TextBlock>(window, "HotkeyHint").Text.Contains("F9"), "The displayed version or configured hotkey is stale.");
            checks.Add("Production controller updates transcript, Unicode count, and native WinUI lexicon list");
            checks.Add("Version and dictation hotkey labels follow current production settings");

            window.ShowPage(1);
            await LayoutAsync(window);
            var searchButton = Find<Button>(window, "HistorySearchButton");
            var searchPeer = FrameworkElementAutomationPeer.CreatePeerForElement(searchButton) ?? new ButtonAutomationPeer(searchButton);
            Require(searchPeer.GetPattern(PatternInterface.Invoke) is IInvokeProvider, "The history search button is not invokable.");
            ((IInvokeProvider)searchPeer.GetPattern(PatternInterface.Invoke)).Invoke();
            await UntilAsync(() => Find<ListView>(window, "HistoryGrid").Items.Count == 1, "The real history query did not display its saved session.");
            checks.Add("Native history search invokes its production handler and displays the saved session");

            var pageImages = new List<Pixels>();
            string[] pageNames = ["LivePage", "HistoryPage", "VocabularyPage", "SettingsPage", "HelpPage"];
            Require(Find<NavigationView>(window, "Navigation") != null, "Native navigation is missing.");
            for (int i = 0; i < pageNames.Length; i++)
            {
                window.ShowPage(i);
                await LayoutAsync(window);
                var page = Find<FrameworkElement>(window, pageNames[i]);
                Require(page.Visibility == Visibility.Visible && page.ActualWidth > 0 && page.ActualHeight > 0, "A WinUI page did not render: " + pageNames[i]);
                for (int other = 0; other < pageNames.Length; other++)
                    if (other != i) Require(Find<FrameworkElement>(window, pageNames[other]).Visibility == Visibility.Collapsed, "An inactive page remains visible: " + pageNames[other]);
                var capture = await CaptureAsync((FrameworkElement)window.Content);
                pageImages.Add(capture);
                captures.Add(new { name = pageNames[i], width = capture.Width, height = capture.Height });
            }
            window.ShowPage(2);
            var vocabulary = Find<TabView>(window, "VocabularyTabs");
            Require(vocabulary.TabItems.Count >= 3, "The vocabulary workspace is missing a tab.");
            for (int i = 0; i < vocabulary.TabItems.Count; i++)
            {
                vocabulary.SelectedIndex = i;
                await LayoutAsync(window);
                Require(vocabulary.SelectedItem is TabViewItem tab && tab.Content is FrameworkElement content && content.ActualWidth > 0 && content.ActualHeight > 0,
                    "A native vocabulary tab did not render: " + i);
            }
            vocabulary.SelectedIndex = 0;
            checks.Add("All five WinUI navigation pages and every native vocabulary tab render actual content");

            // Exercise the production dialog factory, owner XamlRoot and modal
            // queue. Change only the editor, then invoke its actual Cancel button.
            // The isolated repository must keep the original term unchanged.
            var originalTerm = controller.Terms.Single();
            Task<TermData?> editing = Dialogs.EditTermAsync(window, originalTerm);
            ContentDialog? termDialog = null;
            await UntilAsync(() =>
            {
                termDialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(((FrameworkElement)window.Content).XamlRoot)
                    .Where(popup => popup.Child != null)
                    .Select(popup => Visuals<ContentDialog>(popup.Child).FirstOrDefault())
                    .FirstOrDefault(dialog => dialog != null);
                return termDialog != null && termDialog.ActualWidth > 0 && termDialog.ActualHeight > 0;
            }, "The native term editor ContentDialog did not open.");
            Require(termDialog!.XamlRoot == ((FrameworkElement)window.Content).XamlRoot && Dialogs.IsOpen,
                "The term editor is not attached to the owner's modal queue and XamlRoot.");
            var wordEditor = Visuals<TextBox>(termDialog).FirstOrDefault(box => box.Header?.ToString() == "词条（1—64 字）")
                ?? throw new InvalidOperationException("The native term editor is missing its word input.");
            Require(wordEditor.Text == originalTerm.Text, "The term editor did not load its original word.");
            wordEditor.Text = "临时修改，不保存";
            termDialog.UpdateLayout();
            await Task.Delay(150);
            var dialogCapture = await CaptureAsync(termDialog);
            pageImages.Add(dialogCapture);
            captures.Add(new { name = "TermEditorContentDialog", width = dialogCapture.Width, height = dialogCapture.Height });
            var cancelButton = Visuals<Button>(termDialog).FirstOrDefault(button => button.Content?.ToString() == "取消")
                ?? throw new InvalidOperationException("The native term editor has no visible Cancel button.");
            var cancelPeer = FrameworkElementAutomationPeer.CreatePeerForElement(cancelButton) ?? new ButtonAutomationPeer(cancelButton);
            Require(cancelPeer.GetPattern(PatternInterface.Invoke) is IInvokeProvider, "The term editor Cancel button is not invokable.");
            ((IInvokeProvider)cancelPeer.GetPattern(PatternInterface.Invoke)).Invoke();
            Require(await editing.WaitAsync(TimeSpan.FromSeconds(6)) == null, "Cancel unexpectedly accepted the term editor changes.");
            Require(!Dialogs.IsOpen && controller.Terms.Single() == originalTerm, "Cancel changed the term or failed to release the modal queue.");
            checks.Add("Production ContentDialog opens on the owner XamlRoot, renders native fields, and cancels edits without changing the lexicon");
            await SaveContactSheetAsync(Path.ChangeExtension(report, ".png"), pageImages, columns: 2);
            checks.Add("Five actual WinUI pages and the term ContentDialog captured with RenderTargetBitmap into windows-smoke.png");

            overlay = new VoiceOverlay();
            IntPtr foreground = GetForegroundWindow();
            overlay.BeginTurn();
            overlay.SetMeter(.62f, true);
            const string ending = "最新结果👨‍👩‍👧‍👦";
            string longText = string.Concat(Enumerable.Repeat("连续识别𠀀👩🏽‍🔬e\u0301", 40)) + ending;
            overlay.Update("正在聆听", longText);
            await LayoutAsync(overlay);
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(overlay);
            long style = GetWindowLongPtr(hwnd, -20).ToInt64();
            Require(hwnd != IntPtr.Zero && (style & 0x08000000) != 0 && (style & 0x20) != 0, "The voice overlay is missing its non-activation or pointer pass-through safeguards.");
            Require(foreground == IntPtr.Zero || GetForegroundWindow() == foreground, "The voice overlay changed foreground focus.");
            var original = CheckOverlayBounds(hwnd);
            CheckOverlayVisibleOnDesktop(overlay, original);
            var preview = Find<TextBlock>(overlay, "OverlayPreview");
            string suffix = preview.Text.TrimStart('…');
            Require(preview.TextWrapping == TextWrapping.NoWrap && preview.ActualHeight <= 26, "The live preview grew beyond one line.");
            Require(suffix.EndsWith(ending, StringComparison.Ordinal) && longText.EndsWith(suffix, StringComparison.Ordinal), "The compact preview is not showing the latest transcript suffix.");
            int boundary = longText.Length - suffix.Length;
            Require(StringInfo.ParseCombiningCharacters(longText).Contains(boundary), "The compact preview split a Unicode grapheme.");
            checks.Add("Actual overlay HWND is visible on the desktop, preserves focus, passes pointers through, and is centered at 360 by 76 DIPs");
            checks.Add("Long live previews stay on one line and retain complete Unicode graphemes at the transcript tail");

            var overlayImages = new List<Pixels> { await CaptureAsync((FrameworkElement)overlay.Content) };
            overlay.SetMeter(0, false);
            overlay.Update("正在整理", "全文整理完成后将输入原位置");
            await LayoutAsync(overlay);
            overlayImages.Add(await CaptureAsync((FrameworkElement)overlay.Content));
            overlay.Update("已完成", "识别结果已保留，可打开管理窗口复制。", dismiss: true);
            await LayoutAsync(overlay);
            overlayImages.Add(await CaptureAsync((FrameworkElement)overlay.Content));
            overlay.SetPersistentWarning("1 项保存失败，请打开管理窗口重试保存或复制正文。");
            await LayoutAsync(overlay);
            var warned = CheckOverlayBounds(hwnd);
            Require(warned.Right - warned.Left == original.Right - original.Left && warned.Bottom - warned.Top == original.Bottom - original.Top,
                "A persistent save warning changed the compact overlay dimensions.");
            Require(Find<TextBlock>(overlay, "OverlayWarning").Visibility == Visibility.Visible, "The persistent save warning is hidden.");
            Require(foreground == IntPtr.Zero || GetForegroundWindow() == foreground, "An overlay state transition changed foreground focus.");
            overlayImages.Add(await CaptureAsync((FrameworkElement)overlay.Content));
            await SaveContactSheetAsync(Path.Combine(Path.GetDirectoryName(report)!, "windows-overlay.png"), overlayImages, columns: 1);
            checks.Add("Listening, processing, complete and save-warning overlays captured without expanding the window or taking focus");
            overlay.Clear();
        }
        catch (Exception e) { errors.Add(e.GetType().Name + ": " + e.Message); }
        finally
        {
            overlay?.Close();
            window?.AppWindow.Hide();
            if (controller != null)
                try { await controller.DisposeAsync(); } catch (Exception e) { errors.Add("Shutdown: " + e.GetType().Name); }
            Application.Current.DebugSettings.BindingFailed -= BindingFailed;
            Application.Current.UnhandledException -= unhandled;
            try { Directory.Delete(folder, recursive: true); } catch { /* OS file cleanup can lag process shutdown. */ }
        }
        bool passed = errors.Count == 0;
        try
        {
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { passed, checks, errors, captures,
                version = typeof(App).Assembly.GetName().Version?.ToString(), framework = "Microsoft.UI.Xaml (WinUI 3)",
                microphone = "not started", cloud = "blocked", globalInputHooks = "not installed" }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { return 3; }
        return passed ? 0 : 1;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task UntilAsync(Func<bool> condition, string failure)
    {
        long end = Environment.TickCount64 + 6000;
        while (!condition())
        {
            if (Environment.TickCount64 >= end) throw new InvalidOperationException(failure);
            await Task.Delay(75);
        }
    }

    private static async Task LayoutAsync(Window window)
    {
        var root = (FrameworkElement)window.Content;
        await UntilAsync(() => root.XamlRoot != null && root.ActualWidth > 0 && root.ActualHeight > 0, "The WinUI window content did not attach to a visible XamlRoot.");
        root.UpdateLayout();
        await Task.Delay(150);
        root.UpdateLayout();
    }

    private static T Find<T>(Window window, string name) where T : FrameworkElement
    {
        var root = (FrameworkElement)window.Content;
        return root.FindName(name) as T ?? FindVisual<T>(root, name) ?? throw new InvalidOperationException("Missing native WinUI control: " + name);
    }

    private static T? FindVisual<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T found && found.Name == name) return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(root, i), name) is { } child) return child;
        return null;
    }

    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) yield return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private sealed record Pixels(int Width, int Height, byte[] Data);

    private static async Task<Pixels> CaptureAsync(FrameworkElement root)
    {
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root, (int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight));
        var buffer = await bitmap.GetPixelsAsync();
        byte[] bytes = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
        Require(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0 && bytes.Length == bitmap.PixelWidth * bitmap.PixelHeight * 4,
            "WinUI RenderTargetBitmap returned no image pixels.");
        // An empty or single-color capture does not count as visual verification.
        int first = BitConverter.ToInt32(bytes, 0);
        bool varied = false;
        for (int i = 4; i < bytes.Length && !varied; i += 4) varied = BitConverter.ToInt32(bytes, i) != first;
        Require(varied, "The actual WinUI capture contains no visible content.");
        return new(bitmap.PixelWidth, bitmap.PixelHeight, bytes);
    }

    private static async Task SaveContactSheetAsync(string path, IReadOnlyList<Pixels> images, int columns)
    {
        const int gap = 16;
        int cellWidth = images.Max(x => x.Width), cellHeight = images.Max(x => x.Height);
        int width = columns * cellWidth + gap * (columns + 1);
        int height = ((images.Count + columns - 1) / columns) * (cellHeight + gap) + gap;
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 230; pixels[i + 1] = 230; pixels[i + 2] = 230; pixels[i + 3] = 255; }
        for (int index = 0; index < images.Count; index++)
        {
            var source = images[index];
            int x = gap + index % columns * (cellWidth + gap), y = gap + index / columns * (cellHeight + gap);
            for (int row = 0; row < source.Height; row++)
                Buffer.BlockCopy(source.Data, row * source.Width * 4, pixels, ((y + row) * width + x) * 4, source.Width * 4);
        }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync(checked((uint)stream.Size));
        byte[] png = new byte[checked((int)stream.Size)];
        reader.ReadBytes(png);
        await File.WriteAllBytesAsync(path, png);
    }

    private static Rect CheckOverlayBounds(IntPtr hwnd)
    {
        Require(GetWindowRect(hwnd, out var rectangle), "The native overlay bounds could not be read.");
        var monitor = MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        Require(GetMonitorInfo(monitor, ref info), "The overlay monitor work area could not be read.");
        double scale = Math.Max(96, GetDpiForWindow(hwnd)) / 96.0;
        int width = rectangle.Right - rectangle.Left, height = rectangle.Bottom - rectangle.Top;
        Require(Math.Abs(width / scale - 360) <= 2 && Math.Abs(height / scale - 76) <= 2, "The overlay does not fit its 360 by 76 DIP footprint.");
        Require(Math.Abs((rectangle.Left + rectangle.Right) - (info.Work.Left + info.Work.Right)) <= 2,
            "The overlay is not horizontally centered on its monitor work area.");
        Require(rectangle.Top >= info.Work.Top && Math.Abs((info.Work.Bottom - rectangle.Bottom) / scale - 24) <= 2,
            "The overlay is not safely placed above the taskbar.");
        return rectangle;
    }

    private static void CheckOverlayVisibleOnDesktop(VoiceOverlay overlay, Rect rectangle)
    {
        // RenderTargetBitmap proves that the XAML tree rendered. Also inspect
        // the composed desktop: a broken native layered-window setup can leave
        // a perfectly valid XAML bitmap while the user sees an empty rectangle.
        var expected = ((SolidColorBrush)((Border)overlay.Content).Background).Color;
        IntPtr dc = GetDC(IntPtr.Zero);
        Require(dc != IntPtr.Zero, "The composed desktop could not be inspected.");
        try
        {
            DwmFlush();
            int width = rectangle.Right - rectangle.Left, height = rectangle.Bottom - rectangle.Top;
            foreach (var point in new[] { (width / 2, height / 10), (width / 2, height * 9 / 10), (width * 97 / 100, height / 2) })
            {
                uint color = GetPixel(dc, rectangle.Left + point.Item1, rectangle.Top + point.Item2);
                Require(color != uint.MaxValue && Math.Abs((int)(color & 255) - expected.R) <= 8 &&
                    Math.Abs((int)((color >> 8) & 255) - expected.G) <= 8 && Math.Abs((int)((color >> 16) & 255) - expected.B) <= 8,
                    "The actual desktop does not show the overlay's rendered surface.");
            }
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Network access is prohibited during the offline desktop smoke test.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
