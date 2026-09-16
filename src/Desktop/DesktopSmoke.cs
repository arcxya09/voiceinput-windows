using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
using Windows.UI.ViewManagement;

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
        var desktopImages = new List<Pixels>();
        var stages = new List<string>();
        string stage = "Starting isolated smoke verification";
        void Stage(string value)
        {
            stage = value;
            stages.Add(value);
            Console.WriteLine("SMOKE STAGE: " + value);
        }
        string Describe(Exception exception, string context)
            => context + " [HRESULT 0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture) + "] " + exception;
        bool InspectFrameInvariant(Action assertion, string name)
        {
            try { assertion(); return true; }
            catch (Exception exception)
            {
                string diagnostic = Describe(exception, name + " during " + stage);
                errors.Add(diagnostic); Console.Error.WriteLine(diagnostic);
                return false;
            }
        }
        bool popupNativeFramesPassed = true, trayDesktopSurfacePassed = true;
        MainWindow? window = null;
        VoiceOverlay? overlay = null;
        TrayMenuWindow? trayMenu = null;
        AppController? controller = null;
        Microsoft.UI.Xaml.UnhandledExceptionEventHandler unhandled = (_, e) =>
        {
            string diagnostic = Describe(e.Exception, "WinUI dispatcher during " + stage);
            errors.Add(diagnostic); Console.Error.WriteLine(diagnostic); e.Handled = true;
        };
        void BindingFailed(object sender, BindingFailedEventArgs e) => errors.Add("WinUI binding during " + stage + ": " + e.Message);
        Application.Current.UnhandledException += unhandled;
        Application.Current.DebugSettings.BindingFailed += BindingFailed;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            Directory.CreateDirectory(folder);
            Stage("Initialize isolated controller, database, and credentials");
            // Ready() intentionally remains uncalled: it owns global hooks, device
            // watchers and real capture. These checks use an isolated database and
            // a handler that rejects any accidental outbound provider request.
            var microphoneOpening = new OfflineMicrophoneOpening();
            controller = new AppController(folder, null, new NoNetwork(), microphoneOpening.Open,
                receive => new BailianClient(receive));
            microphoneOpening.Log = controller.Log;
            await controller.InitializeAsync();
            await controller.SaveSettingsAsync(controller.Settings, new("SMOKE_LOCAL_ONLY", "SMOKE_LOCAL_ONLY"));
            var credentials = new SettingsStore(folder, new WindowsProtector()).LoadCredentials();
            Require(credentials == new Credentials("SMOKE_LOCAL_ONLY", "SMOKE_LOCAL_ONLY"), "Windows DPAPI credential round-trip failed.");
            checks.Add("Windows DPAPI credentials and SQLite initialized in an isolated temporary directory");
            Stage("Verify startup registry behavior in temporary keys");
            checks.AddRange(StartupServiceSmoke.RunIsolatedChecks());

            Stage("Construct MainWindow, including auxiliary window fields and XAML initialization");
            window = new MainWindow(controller);
            Stage("Activate MainWindow");
            window.Activate();
            Stage("Resize MainWindow to 1040 by 760 physical pixels");
            window.AppWindow.Resize(new SizeInt32(1040, 760));
            Stage("Attach and measure MainWindow XAML layout");
            await LayoutAsync(window);
            Stage("Verify MainWindow native frame");
            CheckMainWindowFrame(WinRT.Interop.WindowNative.GetWindowHandle(window));
            checks.Add("Main window retains its native resizable frame and taskbar system menu");

            Stage("Load offline transcript and lexicon fixtures");
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
            var output = Find<TextBox>(window, "OutputBox");
            await UntilAsync(() => CanonicalLines(output.Text) == CanonicalLines(text),
                () => "The displayed transcript differs from the loaded history. Expected fixture: " + JsonSerializer.Serialize(text) +
                    "; actual fixture: " + JsonSerializer.Serialize(output.Text));
            Require(Find<TextBlock>(window, "BodyCount").Text.Contains(JsonCodec.Count(text).ToString()), "The Unicode body count did not update.");
            await UntilAsync(() => Find<ListView>(window, "TermsGrid").Items.Count == 1, "The visible lexicon did not refresh.");
            await UntilAsync(() => Find<TextBlock>(window, "StatusText").Text == "设置已保存。",
                "The initial settings snapshot did not reach the live view.");

            // WinUI may expose CR where the transcript uses CRLF. Replaying an
            // identical production snapshot must not assign Text again and lose
            // the user's selection. The status below is written after Render's
            // body update, so observing it proves that the new snapshot rendered.
            int selectionStart = output.Text.IndexOf("JUNA", StringComparison.Ordinal);
            Require(selectionStart >= 0, "The selection fixture is missing from the displayed transcript.");
            output.Focus(FocusState.Programmatic);
            output.Select(selectionStart, 4);
            Require(output.SelectedText == "JUNA", "The transcript selection fixture could not be established.");
            await controller.LoadSessionAsync(session);
            await UntilAsync(() => Find<TextBlock>(window, "StatusText").Text == "已载入历史供查看与编辑；历史文字不会自动输入。",
                "The repeated history snapshot did not reach the live view.");
            Require(output.SelectionStart == selectionStart && output.SelectionLength == 4 && output.SelectedText == "JUNA",
                "Refreshing an unchanged transcript reset the user's text selection.");
            Require(CanonicalLines(output.Text) == CanonicalLines(text),
                "The repeated history snapshot changed the fixture text. Expected fixture: " + JsonSerializer.Serialize(text) +
                    "; actual fixture: " + JsonSerializer.Serialize(output.Text));
            string version = typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
            await UntilAsync(() => Find<TextBlock>(window, "VersionInfo").Text.Contains(version) && Find<TextBlock>(window, "HotkeyHint").Text.Contains("F9"),
                "The displayed version or configured hotkey is stale.");
            checks.Add("Production controller updates transcript, Unicode count, and native WinUI lexicon list");
            checks.Add("Repeated history snapshots preserve exact text and paragraphs across native line endings without resetting the user's selection");
            checks.Add("Version and dictation hotkey labels follow current production settings");

            Stage("Export production runtime logs while offline microphone preparation is blocked");
            await CheckRuntimeLogExportAsync(window, controller, microphoneOpening, folder, text);
            checks.Add("The real Settings export button is enabled and invokable while preparation is blocked; production export completes without waiting for capture or management guards");
            checks.Add("Exported JSONL preserves ordered controller/audio events and an earlier microphone HRESULT, with no credentials, transcript, or lexicon fixture strings");

            Stage("Verify production WASAPI loop with anomalous Win10-style packet metadata");
            await AudioCompatibilitySmoke.RunAsync(controller.Log);
            checks.Add("Production capture loop preserves PCM for repeated positions, discontinuities and invalid clocks; stop-and-drain releases leases on its owner thread and preserves real HRESULT failures");

            Stage("Verify the production management guard and dispatched final-preview event bridge");
            await CheckManagementOperationGuardAsync(window);
            await CheckPreviewEventBridgeAsync(window, controller);
            checks.Add("Management operations block a new voice turn for their entire await and release the guard on failure");
            checks.Add("Production controller snapshots and the real MainWindow event bridge preserve final-only and canceled text, reject old turns, and dismiss once");

            window.ShowPage(1);
            Stage("Invoke native history search");
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
                Stage("Capture and inspect full-size " + pageNames[i]);
                window.ShowPage(i);
                await LayoutAsync(window);
                // Preserve the current native page before any geometry gate, so
                // a clipped-control failure still leaves its actual image behind.
                var capture = await CaptureAsync((FrameworkElement)window.Content);
                pageImages.Add(capture);
                captures.Add(new { name = pageNames[i], width = capture.Width, height = capture.Height });
                await SaveContactSheetAsync(Path.ChangeExtension(report, ".png"), pageImages, columns: 2);
                var page = Find<FrameworkElement>(window, pageNames[i]);
                Require(page.Visibility == Visibility.Visible && page.ActualWidth > 0 && page.ActualHeight > 0, "A WinUI page did not render: " + pageNames[i]);
                for (int other = 0; other < pageNames.Length; other++)
                    if (other != i) Require(Find<FrameworkElement>(window, pageNames[other]).Visibility == Visibility.Collapsed, "An inactive page remains visible: " + pageNames[other]);
                var mainRoot = (FrameworkElement)window.Content;
                var stateLabel = Find<TextBlock>(window, "StateLabel");
                var stateBounds = stateLabel.TransformToVisual(mainRoot).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, stateLabel.ActualWidth, stateLabel.ActualHeight));
                double stateRightGap = mainRoot.ActualWidth - stateBounds.Right;
                Require(stateLabel.ActualWidth > 0 && stateRightGap >= 11.5,
                    $"The header status lacks its 12 DIP right margin on {pageNames[i]} (actual {stateRightGap:F2} DIPs).");
                if (pageNames[i] == "HelpPage")
                {
                    var help = Find<ScrollViewer>(window, "HelpPage");
                    var refresh = Find<Button>(window, "UsageRefreshButton");
                    var buttonBounds = refresh.TransformToVisual(help).TransformBounds(
                        new Windows.Foundation.Rect(0, 0, refresh.ActualWidth, refresh.ActualHeight));
                    double visibleWidth = Math.Min(help.ActualWidth, help.ViewportWidth);
                    string helpGeometry = $" ScrollViewer ActualWidth={help.ActualWidth:F2}, ViewportWidth={help.ViewportWidth:F2}, HorizontalOffset={help.HorizontalOffset:F2}, ScrollableWidth={help.ScrollableWidth:F2}.";
                    if (help.Content is FrameworkElement helpContent)
                    {
                        var contentBounds = helpContent.TransformToVisual(help).TransformBounds(
                            new Windows.Foundation.Rect(0, 0, helpContent.ActualWidth, helpContent.ActualHeight));
                        helpGeometry += $" Content ActualWidth={helpContent.ActualWidth:F2}, DesiredWidth={helpContent.DesiredSize.Width:F2}, Left={contentBounds.Left:F2}, Right={contentBounds.Right:F2}.";
                    }
                    else helpGeometry += " Content is not a FrameworkElement.";
                    Require(help.ScrollableWidth <= 1,
                        $"The help page overflows its horizontal viewport by {help.ScrollableWidth:F2} DIPs." + helpGeometry);
                    Require(refresh.ActualWidth > 0 && visibleWidth > 0 && buttonBounds.Left >= -.5 && buttonBounds.Right <= visibleWidth + .5,
                        $"The usage refresh button is horizontally clipped (bounds {buttonBounds.Left:F2}–{buttonBounds.Right:F2}; viewport {visibleWidth:F2} DIPs)." + helpGeometry);
                }
            }
            checks.Add("Header status keeps its right margin on all five pages; Help has no horizontal overflow and its refresh button stays inside the viewport");
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

            // Exercise the actual WinUI layout at a narrow supported width.
            // This is a resized-window check at the runner's current DPI, not
            // a claim of physical mixed-DPI monitor coverage.
            double mainScale = Math.Max(96, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window))) / 96.0;
            var responsiveImages = new List<Pixels>();
            foreach (var size in new[] { (Width: 700, Height: 620), (Width: 600, Height: 420) })
            {
                window.AppWindow.Resize(new SizeInt32((int)Math.Round(size.Width * mainScale), (int)Math.Round(size.Height * mainScale)));
                for (int i = 0; i < pageNames.Length; i++)
                {
                    Stage($"Inspect {pageNames[i]} at {size.Width} by {size.Height} DIPs");
                    window.ShowPage(i);
                    await LayoutAsync(window);
                    responsiveImages.Add(await CaptureAsync((FrameworkElement)window.Content));
                    await SaveContactSheetAsync(EvidencePath(report, "responsive"), responsiveImages, 2);
                    CheckHorizontalLayout(Find<FrameworkElement>(window, pageNames[i]), (FrameworkElement)window.Content, $"{pageNames[i]} at {size.Width}×{size.Height}");
                }
                window.ShowPage(2);
                for (int i = 0; i < vocabulary.TabItems.Count; i++)
                {
                    vocabulary.SelectedIndex = i;
                    await LayoutAsync(window);
                    var tab = (TabViewItem)vocabulary.SelectedItem;
                    CheckHorizontalLayout((FrameworkElement)tab.Content, (FrameworkElement)window.Content, $"Vocabulary tab {i} at {size.Width}×{size.Height}");
                    if (i > 0) responsiveImages.Add(await CaptureAsync((FrameworkElement)window.Content));
                }
                vocabulary.SelectedIndex = 0;
            }
            await SaveContactSheetAsync(EvidencePath(report, "responsive"), responsiveImages, 2);
            vocabulary.SelectedIndex = 0;
            checks.Add("All five native pages and three vocabulary tabs remain inside the horizontal viewport at 700 by 620 and the supported minimum 600 by 420 DIPs");

            // Exercise a tray-origin dialog while its owner is hidden, including
            // the production owner restore, XamlRoot and modal queue. Change only
            // the editor, then invoke Cancel; the repository must remain unchanged.
            Stage("Open native term editor from a hidden minimum-size owner");
            var originalTerm = controller.Terms.Single();
            window.Hide();
            Require(!window.AppWindow.IsVisible, "The dialog smoke precondition requires a hidden main window.");
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
            Require(window.AppWindow.IsVisible && termDialog!.XamlRoot == ((FrameworkElement)window.Content).XamlRoot && Dialogs.IsOpen,
                "The term editor did not restore its hidden owner or attach to the owner's modal queue and XamlRoot.");
            var wordEditor = Visuals<TextBox>(termDialog).FirstOrDefault(box => box.Header?.ToString() == "词条（1—64 字）")
                ?? throw new InvalidOperationException("The native term editor is missing its word input.");
            Require(wordEditor.Text == originalTerm.Text, "The term editor did not load its original word.");
            wordEditor.Text = "临时修改，不保存";
            termDialog.UpdateLayout();
            await Task.Delay(150);
            var dialogCapture = await CaptureAsync(termDialog);
            pageImages.Add(dialogCapture);
            captures.Add(new { name = "TermEditorContentDialog", width = dialogCapture.Width, height = dialogCapture.Height });
            Require(termDialog.ActualWidth <= ((FrameworkElement)window.Content).ActualWidth && termDialog.ActualHeight <= ((FrameworkElement)window.Content).ActualHeight,
                "The native dialog is larger than its narrow owner viewport.");
            var cancelButton = Visuals<Button>(termDialog).FirstOrDefault(button => button.Content?.ToString() == "取消")
                ?? throw new InvalidOperationException("The native term editor has no visible Cancel button.");
            var cancelPeer = FrameworkElementAutomationPeer.CreatePeerForElement(cancelButton) ?? new ButtonAutomationPeer(cancelButton);
            Require(cancelPeer.GetPattern(PatternInterface.Invoke) is IInvokeProvider, "The term editor Cancel button is not invokable.");
            ((IInvokeProvider)cancelPeer.GetPattern(PatternInterface.Invoke)).Invoke();
            Require(await editing.WaitAsync(TimeSpan.FromSeconds(6)) == null, "Cancel unexpectedly accepted the term editor changes.");
            Require(!Dialogs.IsOpen && controller.Terms.Single() == originalTerm, "Cancel changed the term or failed to release the modal queue.");
            checks.Add("Production ContentDialog restores its hidden owner, renders native fields on the owner XamlRoot, and cancels edits without changing the lexicon");
            window.AppWindow.Resize(new SizeInt32(1040, 760));
            await LayoutAsync(window);

            // The native menu is tested independently of the Shell icon. Its
            // injected callback records commands instead of touching clipboard,
            // hooks, settings, or shutdown. Test both visible and hidden owners.
            Stage("Construct and open native tray context menu");
            var commands = new List<TrayMenuCommand>();
            var menu = new TrayMenuWindow(command => { commands.Add(command); return Task.CompletedTask; });
            trayMenu = menu;
            menu.SetState(enabled: false, dictation: false, canChangeMode: true);
            IntPtr mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            Require(GetWindowRect(mainHwnd, out var managerBefore), "The main window bounds could not be read before opening the tray menu.");
            menu.ShowAtCursor();
            await LayoutAsync(menu);
            Require(menu.IsOpen && menu.AppWindow.IsVisible && commands.Count == 0, "Opening the native tray menu invoked a command or failed to show it.");
            IntPtr menuHwnd = WinRT.Interop.WindowNative.GetWindowHandle(menu);
            CheckTrayBounds(menuHwnd);
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                Stage("Inspect actual tray outer frame in " + theme + " theme");
                ((FrameworkElement)menu.Content).RequestedTheme = theme;
                await LayoutAsync(menu);
                desktopImages.Add(CaptureDesktopWindow(menuHwnd));
                await SaveContactSheetAsync(EvidencePath(report, "frames"), desktopImages, 2);
                // These independent, read-only invariants should all report in
                // one run. A failing style must not prevent the other theme or
                // overlay from being inspected. Errors still fail the run.
                popupNativeFramesPassed &= InspectFrameInvariant(() => CheckPopupNativeFrame(menuHwnd, "tray " + theme), "Native frame");
                trayDesktopSurfacePassed &= InspectFrameInvariant(() => CheckPopupSurfaceOnDesktop(menuHwnd, Find<Border>(menu, "TrayMenuRoot"), "tray " + theme), "Composed desktop surface");
            }
            Require(window.AppWindow.IsVisible && GetWindowRect(mainHwnd, out var managerWithMenu) && managerWithMenu.Equals(managerBefore),
                "Opening the tray menu changed the main window's visibility or bounds.");
            var menuCapture = await CaptureAsync((FrameworkElement)menu.Content);
            pageImages.Add(menuCapture);
            captures.Add(new { name = "NativeTrayMenu", width = menuCapture.Width, height = menuCapture.Height });
            SendMessage(menuHwnd, 0x0100, new IntPtr(27), IntPtr.Zero); // WM_KEYDOWN / Escape, confined to this window.
            await UntilAsync(() => !menu.IsOpen && !menu.AppWindow.IsVisible, "Escape did not dismiss the native tray menu.");
            Require(commands.Count == 0, "Escape unexpectedly invoked a tray command.");

            window.AppWindow.Hide();
            menu.ShowAtCursor();
            await LayoutAsync(menu);
            Require(!window.AppWindow.IsVisible, "Opening the tray menu unexpectedly restored the hidden main window.");
            var copyButton = Find<Button>(menu, "TrayCopy");
            var copyPeer = FrameworkElementAutomationPeer.CreatePeerForElement(copyButton) ?? new ButtonAutomationPeer(copyButton);
            Require(copyPeer.GetPattern(PatternInterface.Invoke) is IInvokeProvider, "The native tray menu Copy button is not invokable.");
            ((IInvokeProvider)copyPeer.GetPattern(PatternInterface.Invoke)).Invoke();
            await UntilAsync(() => commands.Count == 1 && !menu.IsOpen && !menu.AppWindow.IsVisible,
                "The native tray menu did not dismiss after dispatching its command.");
            Require(commands.Single() == TrayMenuCommand.Copy && !window.AppWindow.IsVisible &&
                GetWindowRect(mainHwnd, out var managerAfter) && managerAfter.Equals(managerBefore),
                "The tray menu dispatched another command or changed the hidden main window.");
            menu.Dispose(); trayMenu = null;
            window.AppWindow.Show();
            window.Activate();
            await LayoutAsync(window);
            checks.Add("Native tray menu stays within the monitor, Escape dispatches nothing, and Copy dispatches once without restoring or moving the main window");
            await SaveContactSheetAsync(Path.ChangeExtension(report, ".png"), pageImages, columns: 2);
            checks.Add("Five actual WinUI pages, the term ContentDialog and native tray menu captured with RenderTargetBitmap into windows-smoke.png");

            Stage("Construct and show recognition overlay");
            overlay = new VoiceOverlay();
            IntPtr foreground = GetForegroundWindow();
            overlay.BeginTurn();
            await LayoutAsync(overlay);
            Require(IsPreparingText(Find<TextBlock>(overlay, "OverlayPreview").Text),
                "The first-show capsule does not display a readable short listening status.");
            await CheckPreviewLifecycleAsync(overlay);
            checks.Add("First-show and empty notices display readable text; status-only cancellation/completion retain recognized words; repeated turns and hidden-window updates refresh the native preview");
            overlay.SetMeter(.62f, true);
            const string ending = "最新结果👨‍👩‍👧‍👦";
            string longText = string.Concat(Enumerable.Repeat("连续识别𠀀👩🏽‍🔬e\u0301", 40)) + ending;
            overlay.Update("正在聆听", longText);
            await LayoutAsync(overlay);
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(overlay);
            long style = GetWindowLongPtr(hwnd, -20).ToInt64();
            Require(hwnd != IntPtr.Zero && (style & 0x08000000) != 0 && (style & 0x20) != 0, "The voice overlay is missing its non-activation or pointer pass-through safeguards.");
            Require(foreground == IntPtr.Zero || GetForegroundWindow() == foreground, "The voice overlay changed foreground focus.");
            var original = CheckOverlayBounds(overlay);
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                Stage("Inspect actual recognition outer frame in " + theme + " theme");
                ((FrameworkElement)overlay.Content).RequestedTheme = theme;
                await LayoutAsync(overlay);
                CheckCapsuleNativeFrame(hwnd, "overlay " + theme);
                CheckCapsuleLayout(overlay);
                checks.Add("Capsule material in " + theme + ": " + overlay.MaterialDiagnostics + "; desktop blur remains governed by system policy");
                await CheckCapsuleSurfaceOnDesktopAsync(overlay, longText, desktopImages, report);
            }
            var preview = Find<TextBlock>(overlay, "OverlayPreview");
            string suffix = preview.Text.TrimStart('…');
            Require(preview.TextWrapping == TextWrapping.NoWrap && preview.ActualHeight <= 26, "The live preview grew beyond one line.");
            Require(suffix.EndsWith(ending, StringComparison.Ordinal) && longText.EndsWith(suffix, StringComparison.Ordinal), "The compact preview is not showing the latest transcript suffix.");
            int boundary = longText.Length - suffix.Length;
            Require(StringInfo.ParseCombiningCharacters(longText).Contains(boundary), "The compact preview split a Unicode grapheme.");
            checks.Add("Actual single-line capsule is 360 by 56 DIPs with a 12 DIP transparent shadow margin, centered above the taskbar without taking focus or pointer input");
            checks.Add("Light and dark capsule desktop captures show content inside the pill and the controlled desktop background through all outer corners; hardware Acrylic blur remains subject to Windows policy");
            if (popupNativeFramesPassed && trayDesktopSurfacePassed)
                checks.Add("The tray retains its native DWM frame and opaque light/dark surfaces; the capsule uses an independent frame-free host with no hard-edged GDI region");
            checks.Add("Long live previews stay on one line and retain complete Unicode graphemes at the transcript tail");

            var overlayImages = new List<Pixels> { CaptureDesktopWindow(hwnd) };
            overlay.SetMeter(0, false);
            overlay.Update("正在整理", "全文整理完成后将输入原位置");
            await LayoutAsync(overlay);
            Require(preview.Text == "全文整理完成后将输入原位置", "Long-to-short preview retained a stale measurement.");
            overlayImages.Add(CaptureDesktopWindow(hwnd));
            overlay.Update("已完成", "识别结果已保留，可打开管理窗口复制。", dismiss: true);
            await LayoutAsync(overlay);
            overlayImages.Add(CaptureDesktopWindow(hwnd));
            overlay.SetPersistentWarning("1 项保存失败，请打开管理窗口重试保存或复制正文。");
            await LayoutAsync(overlay);
            var warned = CheckOverlayBounds(overlay);
            Require(warned.Right - warned.Left == original.Right - original.Left && warned.Bottom - warned.Top == original.Bottom - original.Top,
                "A persistent save warning changed the compact overlay dimensions.");
            Require(Find<TextBlock>(overlay, "OverlayWarning").Visibility == Visibility.Visible, "The persistent save warning is hidden.");
            Require(foreground == IntPtr.Zero || GetForegroundWindow() == foreground, "An overlay state transition changed foreground focus.");
            overlayImages.Add(CaptureDesktopWindow(hwnd));
            await SaveContactSheetAsync(EvidencePath(report, "overlay"), overlayImages, columns: 1);
            await Task.Delay(3450);
            Require(overlay.AppWindow.IsVisible && Find<TextBlock>(overlay, "OverlayWarning").Visibility == Visibility.Visible,
                "A pending save warning disappeared with the ordinary completion timer.");
            overlay.SetPersistentWarning("");
            await UntilAsync(() => !overlay.AppWindow.IsVisible,
                "Clearing the last save warning did not resume normal capsule dismissal.");
            checks.Add("Listening, processing, completion and save warnings keep the same capsule footprint and focus; save warnings stay visible until cleared");
            checks.Add("Terminal badges distinguish sent, dictation-only, blocked, canceled, partial, unknown and empty results without inferring success from status text");
            overlay.Clear();
            Stage("Verify recognition capsule remains above competing native windows");
            await CheckOverlayTopmostAsync(overlay);
            checks.Add("Actual capsule Z order recovers from another topmost window and native demotion without taking focus; completion remains topmost, hides on time, stops maintenance and can show again");
            Stage("Verify production whole-text paste against isolated external controls");
            checks.AddRange(await InputDeliverySmoke.CheckAsync());
            Stage("Desktop UI verification completed");
        }
        catch (Exception e)
        {
            string diagnostic = Describe(e, "Smoke failure during " + stage);
            errors.Add(diagnostic); Console.Error.WriteLine(diagnostic);
        }
        finally
        {
            try { trayMenu?.Dispose(); } catch (Exception e) { errors.Add(Describe(e, "Tray menu shutdown")); }
            try { overlay?.Close(); } catch (Exception e) { errors.Add(Describe(e, "Overlay shutdown")); }
            try { window?.AppWindow.Hide(); } catch (Exception e) { errors.Add(Describe(e, "Main window shutdown")); }
            if (controller != null)
                try { await controller.DisposeAsync(); } catch (Exception e) { errors.Add(Describe(e, "Isolated controller shutdown")); }
            Application.Current.DebugSettings.BindingFailed -= BindingFailed;
            Application.Current.UnhandledException -= unhandled;
            try { Directory.Delete(folder, recursive: true); } catch { /* OS file cleanup can lag process shutdown. */ }
        }
        bool passed = errors.Count == 0;
        try
        {
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { passed, checks, errors, captures, stages, lastStage = stage,
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

    private static string CanonicalLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string EvidencePath(string report, string kind)
    {
        string stem = Path.GetFileNameWithoutExtension(report);
        if (stem.EndsWith("-smoke", StringComparison.Ordinal)) stem = stem[..^6];
        return Path.Combine(Path.GetDirectoryName(report)!, stem + "-" + kind + ".png");
    }

    private static void CheckHorizontalLayout(FrameworkElement page, FrameworkElement root, string name)
    {
        foreach (var control in Visuals<FrameworkElement>(page).Where(element =>
            element is Button or ComboBox or TextBox or PasswordBox))
        {
            if (control.ActualWidth <= 0 || control.ActualHeight <= 0 || !IsVisibleWithin(control, page)) continue;
            var bounds = control.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, control.ActualWidth, control.ActualHeight));
            Require(bounds.Left >= -.5 && bounds.Right <= root.ActualWidth + .5,
                $"{name}: {control.Name} ({control.GetType().Name}) is horizontally clipped ({bounds.Left:F2}–{bounds.Right:F2}, window {root.ActualWidth:F2} DIPs).");
        }
        foreach (var scroll in Visuals<ScrollViewer>(page))
        {
            if (scroll.ViewportWidth <= 0 || !IsVisibleWithin(scroll, page) || scroll.HorizontalScrollMode != ScrollMode.Disabled) continue;
            Require(scroll.ScrollableWidth <= 1.5,
                $"{name}: a disabled horizontal viewport still overflows by {scroll.ScrollableWidth:F2} DIPs.");
        }
    }

    private static bool IsVisibleWithin(FrameworkElement element, FrameworkElement root)
    {
        for (DependencyObject? current = element; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement view && view.Visibility != Visibility.Visible) return false;
            if (ReferenceEquals(current, root)) return true;
        }
        return false;
    }

    private static Task UntilAsync(Func<bool> condition, string failure) => UntilAsync(condition, () => failure);

    private static async Task UntilAsync(Func<bool> condition, Func<string> failure)
    {
        long end = Environment.TickCount64 + 6000;
        while (!condition())
        {
            if (Environment.TickCount64 >= end) throw new InvalidOperationException(failure());
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
                System.Buffer.BlockCopy(source.Data, row * source.Width * 4, pixels, ((y + row) * width + x) * 4, source.Width * 4);
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

    private static async Task CheckRuntimeLogExportAsync(MainWindow window, AppController controller,
        OfflineMicrophoneOpening microphone, string folder, string transcript)
    {
        microphone.PrivateFixture = transcript;
        string first = await controller.TestMicrophoneAsync("").WaitAsync(TimeSpan.FromSeconds(5));
        Require(first.Contains("0x80070490", StringComparison.Ordinal), "The offline microphone failure lost its HRESULT.");
        await controller.Log.FlushAsync();

        window.ShowPage(3);
        await LayoutAsync(window);
        var exportButton = Find<Button>(window, "ExportLogButton");
        var releaseManagement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? management = null;
        Task<string> preparing = controller.TestMicrophoneAsync("");
        string exportPath = Path.Combine(folder, "log-export.log");
        try
        {
            await microphone.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!preparing.IsCompleted, "The offline microphone preparation did not remain blocked.");
            management = window.RunManagementOperationAsync(() => releaseManagement.Task);
            await UntilAsync(() => !Find<ComboBox>(window, "ProjectBox").IsEnabled,
                "The management guard did not render before the log export check.");
            Require(exportButton.IsEnabled, "Runtime log export was disabled while microphone preparation or management was pending.");
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(exportButton) ?? new ButtonAutomationPeer(exportButton);
            Require(peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider, "The runtime log export button is not invokable.");
            // Invoke the same production operation after destination selection. Opening the native
            // picker would make this unattended check interactive; the picker uses PickSaveAsync.
            await window.ExportRuntimeLogAsync(exportPath).WaitAsync(TimeSpan.FromSeconds(5));
            Require(!preparing.IsCompleted && !management.IsCompleted,
                "Log export waited for the pending microphone or management operation.");
            Require(exportButton.IsEnabled, "The runtime log export button did not remain available after export.");
        }
        finally
        {
            microphone.Release.TrySetResult();
            releaseManagement.TrySetResult();
            try { await preparing.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { if (management != null) await management.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        string exported = await File.ReadAllTextAsync(exportPath);
        Require(exported.Length > 0 && exported.EndsWith('\n'), "Runtime log export ended with an incomplete JSONL record.");
        var records = new List<JsonElement>();
        var sequences = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (string line in exported.Split('\n').SkipLast(1))
        {
            Require(!string.IsNullOrWhiteSpace(line), "Runtime log export contains an empty JSONL record.");
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            Require(record.ValueKind == JsonValueKind.Object, "Runtime log export contains a non-object JSONL record.");
            string instance = record.GetProperty("instanceId").GetString()!;
            long sequence = record.GetProperty("sequence").GetInt64();
            if (sequence > 0)
            {
                Require(!sequences.TryGetValue(instance, out long previous) || sequence > previous,
                    "Runtime log records are duplicated or out of order within an app instance.");
                sequences[instance] = sequence;
            }
            CheckLogStrings(record, ["SMOKE_LOCAL_ONLY", "JUNA", "离线界面验收", "第二段核对完整正文", "12C(α,γ)16O", transcript]);
            records.Add(record.Clone());
        }
        int initialized = records.FindIndex(r => LogEventIs(r, "Controller", "InitializeCompleted"));
        int opened = records.FindIndex(r => LogEventIs(r, "Audio", "SmokeOpenStarted"));
        int audioFailed = records.FindIndex(r => LogEventIs(r, "Audio", "SmokeOpenFailed"));
        int failed = records.FindIndex(r => LogEventIs(r, "Controller", "Diagnostic")
            && r.GetProperty("fields").GetProperty("Stage").GetString() == "MicrophoneTestFailed"
            && r.TryGetProperty("exception", out var error) && error.GetProperty("hresult").GetString() == "0x80070490");
        int blocked = records.FindLastIndex(r => LogEventIs(r, "Audio", "SmokeOpenStarted"));
        int requested = records.FindIndex(r => LogEventIs(r, "Application", "LogExportRequested"));
        Require(initialized >= 0 && opened > initialized && audioFailed > opened && failed > audioFailed
            && blocked > failed && requested > blocked,
            "Runtime log export lost the earlier microphone failure or the ordered production controller/audio/export events.");
        Require(records[audioFailed].GetProperty("exception").GetProperty("hresult").GetString() == "0x80070490",
            "Runtime log export lost the earlier audio exception HRESULT.");
    }

    private static bool LogEventIs(JsonElement record, string component, string eventName)
        => record.GetProperty("component").GetString() == component && record.GetProperty("event").GetString() == eventName;

    private static void CheckLogStrings(JsonElement value, string[] privateStrings)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString()!;
            Require(!privateStrings.Any(item => text.Contains(item, StringComparison.Ordinal)),
                "Runtime log export contains a private credential, transcript, or lexicon fixture string.");
        }
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) CheckLogStrings(property.Value, privateStrings);
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckLogStrings(item, privateStrings);
    }

    private sealed class OfflineMicrophoneOpening
    {
        public RuntimeLog? Log { get; set; }
        public string PrivateFixture { get; set; } = "";
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int attempts;

        public IAudioCapture Open(string device, Func<byte[], CancellationToken, ValueTask> send,
            Action<string> fault, Action<float> level)
        {
            int attempt = Interlocked.Increment(ref attempts);
            AudioLog.Write(Log, "SmokeOpenStarted", null, null, ("attempt", attempt));
            if (attempt > 1)
            {
                Blocked.TrySetResult();
                Release.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            }
            var error = new COMException("SMOKE_LOCAL_ONLY JUNA " + PrivateFixture, unchecked((int)0x80070490));
            error.Data["privateFixture"] = PrivateFixture;
            AudioLog.Write(Log, "SmokeOpenFailed", null, error, ("attempt", attempt));
            throw error;
        }
    }

    private static async Task CheckManagementOperationGuardAsync(MainWindow window)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var action = window.RunManagementOperationAsync(() => release.Task);
        Require(!window.CanStartVoiceTurn(), "A management operation allowed a new voice turn while its await was pending.");
        await Task.Delay(100);
        Require(!Find<ComboBox>(window, "ProjectBox").IsEnabled, "Project selection remained enabled during a pending management operation.");
        release.SetResult();
        await action;
        Require(window.CanStartVoiceTurn(), "The management guard was not released after completion.");
        try { await window.RunManagementOperationAsync(() => throw new InvalidOperationException("Offline guard fixture")); }
        catch (InvalidOperationException e) when (e.Message == "Offline guard fixture") { }
        Require(window.CanStartVoiceTurn(), "The management guard remained held after a failed operation.");
    }

    private static async Task CheckPreviewEventBridgeAsync(MainWindow window, AppController controller)
    {
        var original = await controller.SnapshotAsync();
        var source = new OfflinePreviewEvents();
        window.ConnectPreviewEvents(source);
        var overlay = window.RecognitionOverlay;
        var preview = Find<TextBlock>(overlay, "OverlayPreview");
        var status = Find<TextBlock>(overlay, "OverlayStatus");
        var session = new SessionData { Title = "Final preview event fixture", ProjectId = controller.Settings.ProjectId };
        var segment = new SegmentData { SessionId = session.Id, TaskId = "preview-smoke", TaskOrder = 1, SentenceId = 1,
            RawText = "你好", FinalText = "你好", AsrState = AsrState.Confirmed, OutputState = OutputState.Published, SourceRevision = 1 };
        try
        {
            await controller.Repository.SaveSegmentAsync(session, segment);
            source.Start(session.Id);
            // No partial preview is emitted. The latest controller snapshot is
            // already terminal when the next 75 ms render consumes it.
            await controller.LoadSessionAsync(session);
            await controller.SetDeliveryAsync("Dictated", "听写完成", turnId: session.Id);
            var completed = new VoiceTurnCompletion(session.Id, "听写完成", TranscriptText.Render(await controller.SnapshotAsync()));
            source.Complete(completed);
            await UntilAsync(() => preview.Text == "你好" && status.Text == "听写完成", "The production event bridge lost a final-only short utterance.");

            await Task.Delay(1600);
            source.Complete(completed);
            source.Remind(new("上一段仍在整理", session.Id));
            await controller.SetDeliveryAsync("Pending", "迟到的状态快照", turnId: session.Id);
            await Task.Delay(1850);
            Require(!overlay.AppWindow.IsVisible, "A duplicate completion, in-turn notice or late snapshot restarted the three-second hide timer.");

            const string nextTurn = "next-preview-turn";
            source.Start(nextTurn);
            source.Complete(completed);
            source.Remind(new("旧轮次提示", session.Id));
            await UntilAsync(() => IsPreparingText(preview.Text), "An older turn contaminated the next turn's empty preview.");
            Require(status.Text == "准备麦克风…", "A late completion changed the next turn's status.");
            source.Complete(new(nextTurn, "本轮已取消", "取消后保留最后一句"));
            await UntilAsync(() => preview.Text == "取消后保留最后一句", "Cancellation lost its final confirmed words.");

            source.Start("empty-preview-turn");
            source.Complete(new("empty-preview-turn", "短按已取消", ""));
            await UntilAsync(() => IsEmptyCompletionText(preview.Text, "短按已取消"), "An empty canceled turn retained the preceding turn's words.");
        }
        finally
        {
            overlay.Clear();
            await controller.DeleteSessionAsync(session);
            if (original.Session != null) await controller.LoadSessionAsync(original.Session);
        }
    }

    private sealed class OfflinePreviewEvents : IVoicePreviewEvents
    {
        public event Action<string>? TurnStarted;
        public event Action<VoiceTurnCompletion>? TurnCompleted;
        public event Action<VoiceInputNotice>? Notice;
        public void Start(string id) => TurnStarted?.Invoke(id);
        public void Complete(VoiceTurnCompletion result) => TurnCompleted?.Invoke(result);
        public void Remind(VoiceInputNotice notice) => Notice?.Invoke(notice);
    }

    private static async Task CheckPreviewLifecycleAsync(VoiceOverlay overlay)
    {
        var preview = Find<TextBlock>(overlay, "OverlayPreview");
        var panel = (TailPreviewPanel)VisualTreeHelper.GetParent(preview);
        var startupPreview = new VoicePreviewState();
        const string startupTurn = "startup-feedback-smoke";
        startupPreview.Begin(startupTurn);
        var connecting = new TranscriptSnapshot(new SessionData { Id = startupTurn }, [], CaptureState.Connecting, 0, 0, "识别服务尚未连接");
        foreach (var phase in new[]
        {
            (Snapshot: connecting, Text: "准备麦克风…"),
            (Snapshot: connecting with { LocalAudioReady = true }, Text: "正在聆听"),
            (Snapshot: connecting with { LocalAudioReady = true, CaptureReleased = true }, Text: "正在整理…")
        })
        {
            var frame = startupPreview.Snapshot(phase.Snapshot, true, true, false)!;
            overlay.Update(frame.Status, frame.Text, frame.Dismiss);
            await LayoutAsync(overlay);
            Require(preview.Text == phase.Text, "The native capsule waited for cloud readiness or restored listening after release.");
        }
        foreach (string sample in new[] { "你好", "正在测试实时文字预览", "12C(α,γ)16O", "新的结果" })
        {
            overlay.Update("正在聆听", sample);
            await LayoutAsync(overlay);
            Require(preview.Text == sample, "The native preview did not render the complete short text: " + sample);
        }
        string recognized = preview.Text;
        overlay.Update("检测到其他按键或鼠标操作，本轮停止自动输入。", dismiss: true);
        await LayoutAsync(overlay);
        Require(preview.Text == recognized, "A status-only cancellation erased the recognized preview.");
        overlay.Update("已完成", dismiss: true);
        await LayoutAsync(overlay);
        Require(preview.Text == recognized, "A status-only completion erased the recognized preview.");

        var badge = Find<TextBlock>(overlay, "OverlayElapsed");
        foreach (var terminal in new[]
        {
            (State: "Sent", Label: "已输入"), (State: "PasteSent", Label: "已发起粘贴"), (State: "Dictated", Label: "待复制"),
            (State: "Cancelled", Label: "已取消"), (State: "Blocked", Label: "待复制"),
            (State: "Partial", Label: "部分输入"), (State: "Unknown", Label: "请核对"),
            (State: "Empty", Label: "无文字")
        })
        {
            overlay.Update("终态反馈", recognized, true, terminal.State);
            await LayoutAsync(overlay);
            Require(preview.Text == recognized && badge.Text == terminal.Label,
                "The terminal capsule lost the transcript or misreported delivery state " + terminal.State + ".");
            Require(!badge.IsTextTrimmed, "The capsule clipped its terminal label " + terminal.State + ".");
            Require(!IsVisibleWithin(Find<TextBlock>(overlay, "OverlayStatus"), (FrameworkElement)overlay.Content),
                "A terminal result restored the old second status row.");
            CheckCapsuleLayout(overlay);
        }
        overlay.Update("听写完成", recognized, dismiss: true);
        await LayoutAsync(overlay);
        Require(badge.Text == "结束", "A notice without a delivery state guessed a successful input result.");

        double fontSize = preview.FontSize;
        double capsuleViewport = panel.ActualWidth;
        Require(capsuleViewport > 0 && capsuleViewport < 280, "The single-line preview did not reserve space for its audio meter and timer.");
        const string tail = "最新词";
        string repeated = string.Concat(Enumerable.Repeat("中文测量👩🏽‍🔬e\u0301", 20)) + tail;
        foreach (double multiplier in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            preview.FontSize = fontSize * multiplier;
            panel.InvalidateTextMetrics();
            overlay.Update("正在聆听", repeated);
            foreach (double width in new[] { 0.0, 160.0, capsuleViewport, 240.0, capsuleViewport })
            {
                panel.InvalidateMeasure();
                panel.Measure(new Windows.Foundation.Size(width, 80));
                panel.Arrange(new Windows.Foundation.Rect(0, 0, width, 80));
                if (width > 0)
                {
                    string suffix = preview.Text.TrimStart('…');
                    Require(suffix.EndsWith(tail, StringComparison.Ordinal) && repeated.EndsWith(suffix, StringComparison.Ordinal),
                        $"Preview lost its real text at {width} DIPs and font multiplier {multiplier}.");
                    Require(preview.DesiredSize.Width <= width + .5,
                        $"Preview overflowed its measured width at font multiplier {multiplier}.");
                }
            }
            overlay.Update("正在聆听", "你好");
            panel.InvalidateMeasure();
            panel.Measure(new Windows.Foundation.Size(capsuleViewport, 80));
            panel.Arrange(new Windows.Foundation.Rect(0, 0, capsuleViewport, 80));
            Require(preview.Text == "你好", "Long-to-short text retained an old width or an ellipsis.");
        }
        preview.FontSize = fontSize;
        panel.InvalidateTextMetrics();
        overlay.Clear();
        overlay.Update("正在聆听", "隐藏后重新显示");
        await LayoutAsync(overlay);
        Require(preview.Text == "隐藏后重新显示", "Showing a hidden overlay retained stale preview layout.");
        overlay.BeginTurn();
        await LayoutAsync(overlay);
        Require(IsPreparingText(preview.Text), "A new turn retained the preceding turn's words.");
        overlay.Update("本轮已取消", text: "", dismiss: true);
        await LayoutAsync(overlay);
        Require(IsEmptyCompletionText(preview.Text, "本轮已取消"), "An empty canceled turn showed an ellipsis instead of readable feedback.");
        overlay.BeginTurn();
        await LayoutAsync(overlay);
        Console.WriteLine($"Preview regression: rooted WinUI layout at 0/160/240 and the actual {capsuleViewport:F2} DIP capsule viewport, font multipliers 1/1.25/1.5/2 passed. These are layout tests, not physical monitor-DPI changes.");
    }

    private static bool IsPreparingText(string text) => text is "准备麦克风…" or "正在聆听";

    private static bool IsEmptyCompletionText(string text, string status)
        => text == status || text is "本轮尚未识别到文字" or "未识别到文字";

    private static Rect CheckOverlayBounds(VoiceOverlay overlay)
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(overlay);
        Require(GetWindowRect(hwnd, out var rectangle), "The native overlay bounds could not be read.");
        var monitor = MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        Require(GetMonitorInfo(monitor, ref info), "The overlay monitor work area could not be read.");
        var root = Find<Border>(overlay, "OverlayRoot");
        var host = Find<FrameworkElement>(overlay, "OverlayHost");
        double scale = Math.Max(96, GetDpiForWindow(hwnd)) / 96.0;
        int width = rectangle.Right - rectangle.Left, height = rectangle.Bottom - rectangle.Top;
        double textScale = new UISettings().TextScaleFactor;
        if (textScale <= 1.01)
        {
            Require(Math.Abs(root.ActualWidth - 360) <= 1 && Math.Abs(root.ActualHeight - 56) <= 1,
                $"The visible capsule does not fit its 360 by 56 DIP footprint ({root.ActualWidth:F2} by {root.ActualHeight:F2}).");
            Require(Math.Abs(width / scale - 384) <= 2 && Math.Abs(height / scale - 80) <= 2,
                "The transparent host does not leave a 12 DIP shadow margin around the capsule.");
        }
        else Require(root.ActualWidth >= 359 && root.ActualHeight >= 55,
            "Windows text scaling made the capsule smaller than its readable default.");
        var surfaceBounds = root.TransformToVisual(host).TransformBounds(
            new Windows.Foundation.Rect(0, 0, root.ActualWidth, root.ActualHeight));
        Require(Math.Abs(surfaceBounds.Left - 12) <= 1 && Math.Abs(surfaceBounds.Top - 12) <= 1 &&
            Math.Abs(host.ActualWidth - surfaceBounds.Right - 12) <= 1 &&
            Math.Abs(host.ActualHeight - surfaceBounds.Bottom - 12) <= 1,
            "The capsule does not have symmetric transparent space for its shadow.");
        Require(Math.Abs((rectangle.Left + rectangle.Right) - (info.Work.Left + info.Work.Right)) <= 2,
            "The overlay is not horizontally centered on its monitor work area.");
        double visibleBottom = rectangle.Top + surfaceBounds.Bottom * scale;
        Require(rectangle.Top >= info.Work.Top && Math.Abs((info.Work.Bottom - visibleBottom) / scale - 24) <= 2,
            "The visible capsule is not 24 DIPs above the taskbar.");
        return rectangle;
    }

    private static void CheckCapsuleLayout(VoiceOverlay overlay)
    {
        var root = Find<Border>(overlay, "OverlayRoot");
        var host = Find<FrameworkElement>(overlay, "OverlayHost");
        var preview = Find<TextBlock>(overlay, "OverlayPreview");
        var panel = (TailPreviewPanel)VisualTreeHelper.GetParent(preview);
        var timer = Find<TextBlock>(overlay, "OverlayElapsed");
        var status = Find<TextBlock>(overlay, "OverlayStatus");
        Require(!IsVisibleWithin(status, host), "The old separate status row remains visible in the single-line capsule.");
        Require(preview.Opacity == 1 && preview.TextWrapping == TextWrapping.NoWrap && preview.MaxLines == 1,
            "The capsule's text is translucent or can grow into a second line.");
        Require(root.CornerRadius.TopLeft >= root.ActualHeight / 2 - 1 &&
            root.CornerRadius.TopRight >= root.ActualHeight / 2 - 1 &&
            root.CornerRadius.BottomLeft >= root.ActualHeight / 2 - 1 &&
            root.CornerRadius.BottomRight >= root.ActualHeight / 2 - 1,
            "The capsule does not have semicircular ends.");
        var textBounds = preview.TransformToVisual(root).TransformBounds(
            new Windows.Foundation.Rect(0, 0, preview.ActualWidth, preview.ActualHeight));
        var panelBounds = panel.TransformToVisual(root).TransformBounds(
            new Windows.Foundation.Rect(0, 0, panel.ActualWidth, panel.ActualHeight));
        Require(Math.Abs((panelBounds.Left + panelBounds.Right) / 2 - root.ActualWidth / 2) <= 1 &&
            Math.Abs((panelBounds.Top + panelBounds.Bottom) / 2 - root.ActualHeight / 2) <= 1,
            "The preview viewport is not centered inside the capsule.");
        Require(textBounds.Left >= panelBounds.Left - 1 && textBounds.Right <= panelBounds.Right + 1 &&
            textBounds.Top >= -1 && textBounds.Bottom <= root.ActualHeight + 1,
            "The rendered preview exceeds the capsule's text viewport.");
        if (IsVisibleWithin(timer, host) && timer.ActualWidth > 0)
        {
            var timerBounds = timer.TransformToVisual(root).TransformBounds(
                new Windows.Foundation.Rect(0, 0, timer.ActualWidth, timer.ActualHeight));
            Require(timerBounds.Left >= panelBounds.Right - 1 && timerBounds.Right <= root.ActualWidth + 1,
                "The elapsed time overlaps the recognized words or escapes the capsule.");
        }
        Require(AutomationProperties.GetName(root).Length > 0,
            "The status hidden from the single visual line is unavailable to assistive technology.");
    }

    private static void CheckCapsuleNativeFrame(IntPtr hwnd, string name)
    {
        long style = GetWindowLongPtr(hwnd, -16).ToInt64();
        long extended = GetWindowLongPtr(hwnd, -20).ToInt64();
        Require((style & (0x00C00000L | 0x00040000L | 0x00080000L | 0x00030000L)) == 0,
            $"The {name} transparent host exposes a system caption, resize frame or window control (style {style:X}).");
        Require((extended & (0x08000000L | 0x80L | 0x20L)) == (0x08000000L | 0x80L | 0x20L),
            $"The {name} is missing tool-window, no-activation or pointer pass-through flags (extended {extended:X}).");
        Require((extended & 0x8L) != 0, $"The {name} native HWND is not topmost (extended {extended:X}).");
        Require(GetWindowRect(hwnd, out var bounds), "The transparent capsule's outer bounds could not be measured.");
        Require(GetClientRect(hwnd, out var client), "The transparent capsule's client bounds could not be measured.");
        var origin = new Point();
        Require(ClientToScreen(hwnd, ref origin) && origin.X == bounds.Left && origin.Y == bounds.Top &&
            client.Right == bounds.Right - bounds.Left && client.Bottom == bounds.Bottom - bounds.Top,
            "The transparent host contains an unwanted native client inset.");
        IntPtr region = CreateRectRgn(0, 0, 0, 0);
        try { Require(region != IntPtr.Zero && GetWindowRgn(hwnd, region) == 0,
            "The transparent capsule still uses a hard-edged GDI window region."); }
        finally { if (region != IntPtr.Zero) DeleteObject(region); }
        int x = bounds.Left + (bounds.Right - bounds.Left) / 2, y = bounds.Top + (bounds.Bottom - bounds.Top) / 2;
        var point = new IntPtr(unchecked((int)((uint)(ushort)x | ((uint)(ushort)y << 16))));
        Require(SendMessage(hwnd, 0x0084, IntPtr.Zero, point).ToInt64() == -1 &&
            SendMessage(hwnd, 0x0021, IntPtr.Zero, IntPtr.Zero).ToInt64() == 3,
            "The capsule exposes a pointer target or mouse-activation path.");
        Console.WriteLine($"Capsule host {name}: DPI={GetDpiForWindow(hwnd)}, frame=none, region=none, pointer=transparent, activation=disabled.");
    }

    private static async Task CheckOverlayTopmostAsync(VoiceOverlay overlay)
    {
        // Exercise real native Z order, independently of presenter properties and
        // ASR snapshots. No Update/Position call is allowed while awaiting repair.
        var competitor = new Window { Content = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.SteelBlue) } };
        try
        {
            overlay.BeginTurn();
            await LayoutAsync(overlay);
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(overlay);
            IntPtr other = WinRT.Interop.WindowNative.GetWindowHandle(competitor);
            var bounds = CheckOverlayBounds(overlay);
            var presenter = Microsoft.UI.Windowing.OverlappedPresenter.Create();
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = presenter.IsMinimizable = presenter.IsMaximizable = false;
            presenter.SetBorderAndTitleBar(false, false);
            competitor.AppWindow.SetPresenter(presenter);
            competitor.AppWindow.IsShownInSwitchers = false;
            competitor.AppWindow.MoveAndResize(new RectInt32(bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));
            competitor.Activate();
            await LayoutAsync(competitor);
            Require(GetForegroundWindow() == other, "The topmost regression could not activate its isolated competing window.");

            void CoverCapsule()
            {
                Require(SetWindowPos(other, new IntPtr(-1), 0, 0, 0, 0, 0x0213) && IsWindowAbove(other, hwnd),
                    "The regression did not place a real topmost window above the capsule.");
            }
            async Task RequireRecovery(string phase)
            {
                await UntilAsync(() => overlay.AppWindow.IsVisible &&
                    (GetWindowLongPtr(hwnd, -20).ToInt64() & 0x8L) != 0 && IsWindowAbove(hwnd, other),
                    "The capsule failed to recover actual native Z order during " + phase + ".");
                Require(GetForegroundWindow() == other, "Topmost recovery stole foreground focus during " + phase + ".");
            }

            CoverCapsule();
            await RequireRecovery("recording without new snapshots");
            Require(SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, 0x0213) &&
                (GetWindowLongPtr(hwnd, -20).ToInt64() & 0x8L) == 0,
                "The regression did not demote the capsule's actual native HWND.");
            await RequireRecovery("native TOPMOST demotion");

            overlay.Update("已完成", "置顶回归完成", dismiss: true, deliveryState: "PasteSent");
            CoverCapsule();
            await RequireRecovery("the completion hold");
            await UntilAsync(() => !overlay.AppWindow.IsVisible, "Topmost maintenance restarted the completion hide timer.");
            Require(!overlay.IsTopmostMaintenanceRunning, "The hidden capsule retained its topmost maintenance timer.");
            Require(SetWindowPos(other, new IntPtr(-1), 0, 0, 0, 0, 0x0213), "The hidden-window regression could not reorder its competitor.");
            await Task.Delay(550);
            Require(!overlay.AppWindow.IsVisible && !overlay.IsTopmostMaintenanceRunning,
                "A competing window revived the hidden capsule or restarted background maintenance.");

            overlay.BeginTurn();
            await RequireRecovery("hide then show");
            CoverCapsule();
            await RequireRecovery("competition after showing again");
            overlay.Clear();
            Require(!overlay.AppWindow.IsVisible && !overlay.IsTopmostMaintenanceRunning,
                "Clearing a turn left its capsule or maintenance running.");
        }
        finally
        {
            overlay.Clear();
            competitor.Close();
        }
    }

    private static bool IsWindowAbove(IntPtr expectedAbove, IntPtr expectedBelow)
    {
        IntPtr candidate = GetWindow(expectedBelow, 3); // GW_HWNDPREV
        for (int visited = 0; candidate != IntPtr.Zero && candidate != expectedBelow && visited < 256; visited++)
        {
            if (candidate == expectedAbove) return true;
            candidate = GetWindow(candidate, 3);
        }
        return false;
    }

    private static void CheckMainWindowFrame(IntPtr hwnd)
    {
        long style = GetWindowLongPtr(hwnd, -16).ToInt64();
        const long required = 0x00C00000L | 0x00040000L | 0x00080000L | 0x00020000L | 0x00010000L;
        Require((style & required) == required && GetSystemMenu(hwnd, false) != IntPtr.Zero,
            "The main window lost its native caption, resize controls, or taskbar system menu.");
    }

    private static void CheckPopupNativeFrame(IntPtr hwnd, string name)
    {
        var failures = new List<string>();
        void Check(bool condition, string message) { if (!condition) failures.Add(message); }
        bool overlay = name.StartsWith("overlay", StringComparison.Ordinal);
        long style = GetWindowLongPtr(hwnd, -16).ToInt64();
        long extendedStyle = GetWindowLongPtr(hwnd, -20).ToInt64();
        const long nativeFrame = 0x00C00000L | 0x00040000L;
        Check((style & nativeFrame) == nativeFrame && (style & 0x80000000L) != 0 &&
            (style & (0x00080000L | 0x00030000L)) == 0 &&
            (extendedStyle & (0x1L | 0x200L | 0x20000L)) == 0 && (extendedStyle & 0x180) == 0x180,
            $"The {name} HWND lacks the required native DWM frame hints or has unwanted window controls (style {style:X}; extended {extendedStyle:X}).");
        Check(GetWindowRect(hwnd, out var bounds), "The " + name + " outer frame could not be measured.");
        Check(GetClientRect(hwnd, out var client), "The " + name + " client frame could not be measured.");
        var origin = new Point();
        Check(ClientToScreen(hwnd, ref origin), "The " + name + " client origin could not be measured.");
        Check(origin.X == bounds.Left && origin.Y == bounds.Top && client.Right == bounds.Right - bounds.Left && client.Bottom == bounds.Bottom - bounds.Top,
            $"The {name} content has an unwanted title/resize inset: window {bounds.Right - bounds.Left}×{bounds.Bottom - bounds.Top}, client {client.Right}×{client.Bottom}, offset {origin.X - bounds.Left},{origin.Y - bounds.Top}.");
        IntPtr region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            Check(region != IntPtr.Zero && GetWindowRgn(hwnd, region) == 0,
                "The " + name + " still applies a hard-edged GDI window region, which disables native rounding.");
        }
        finally { if (region != IntPtr.Zero) DeleteObject(region); }
        Check(DwmGetWindowAttribute(hwnd, 1, out int nonClientEnabled, sizeof(int)) == 0 && nonClientEnabled != 0,
            "The " + name + " does not enable DWM non-client rendering.");
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            Check(DwmGetWindowAttribute(hwnd, 33, out int corners, sizeof(int)) == 0 && corners == (overlay ? 2 : 3),
                "The " + name + " did not retain its native corner preference.");
            Check(DwmGetWindowAttribute(hwnd, 20, out int dark, sizeof(int)) == 0 && dark == (name.EndsWith("Dark", StringComparison.Ordinal) ? 1 : 0),
                "The " + name + " native frame did not follow its XAML theme.");
        }
        int x = bounds.Left + 2, y = bounds.Top + (bounds.Bottom - bounds.Top) / 2;
        var point = new IntPtr(unchecked((int)((uint)(ushort)x | ((uint)(ushort)y << 16))));
        Check(SendMessage(hwnd, 0x0084, IntPtr.Zero, point).ToInt64() == (overlay ? -1 : 1),
            "The " + name + " native border exposes an unexpected resize or activation hit target.");
        Require(failures.Count == 0, string.Join(" | ", failures));
        Console.WriteLine($"Native frame {name}: DPI={GetDpiForWindow(hwnd)}, region=none, DWM non-client={nonClientEnabled}. Corner rendering follows Windows desktop/VM policy.");
    }

    private static Pixels CaptureDesktopWindow(IntPtr hwnd)
    {
        Require(GetWindowRect(hwnd, out var frame), "The desktop capture window bounds could not be read.");
        int padding = Math.Max(12, (int)Math.Round(GetDpiForWindow(hwnd) / 96.0 * 16));
        int virtualLeft = GetSystemMetrics(76), virtualTop = GetSystemMetrics(77);
        int left = Math.Max(virtualLeft, frame.Left - padding), top = Math.Max(virtualTop, frame.Top - padding);
        int right = Math.Min(virtualLeft + GetSystemMetrics(78), frame.Right + padding);
        int bottom = Math.Min(virtualTop + GetSystemMetrics(79), frame.Bottom + padding);
        int width = right - left, height = bottom - top;
        Require(width > 0 && height > 0, "The desktop capture has empty bounds.");
        IntPtr screen = GetDC(IntPtr.Zero), memory = IntPtr.Zero, bitmap = IntPtr.Zero, original = IntPtr.Zero;
        try
        {
            Require(screen != IntPtr.Zero, "The composed desktop DC could not be opened.");
            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, width, height);
            Require(memory != IntPtr.Zero && bitmap != IntPtr.Zero, "The desktop bitmap could not be allocated.");
            original = SelectObject(memory, bitmap);
            DwmFlush();
            Require(BitBlt(memory, 0, 0, width, height, screen, left, top, 0x40CC0020), "The composed desktop window could not be copied.");
            SelectObject(memory, original); original = IntPtr.Zero;
            var header = new BitmapInfoHeader { Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
            byte[] pixels = new byte[checked(width * height * 4)];
            Require(GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref header, 0) == height, "The composed desktop pixels could not be read.");
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return new Pixels(width, height, pixels);
        }
        finally
        {
            if (original != IntPtr.Zero && memory != IntPtr.Zero) SelectObject(memory, original);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static void CheckPopupSurfaceOnDesktop(IntPtr hwnd, Border root, string name)
    {
        Require(GetWindowRect(hwnd, out var frame), "The " + name + " visible bounds could not be read.");
        Require(root.Background is SolidColorBrush, "The " + name + " surface has no opaque background brush.");
        var expected = ((SolidColorBrush)root.Background).Color;
        int inset = Math.Max(3, (int)Math.Round(GetDpiForWindow(hwnd) / 96.0 * 4));
        int width = frame.Right - frame.Left, height = frame.Bottom - frame.Top;
        IntPtr dc = GetDC(IntPtr.Zero);
        Require(dc != IntPtr.Zero, "The composed " + name + " desktop could not be inspected.");
        try
        {
            DwmFlush();
            foreach (var point in new[] { (width / 2, inset), (inset, height / 2), (width - inset, height / 2) })
            {
                uint color = GetPixel(dc, frame.Left + point.Item1, frame.Top + point.Item2);
                Require(color != uint.MaxValue && Math.Abs((int)(color & 255) - expected.R) <= 8 &&
                    Math.Abs((int)((color >> 8) & 255) - expected.G) <= 8 && Math.Abs((int)((color >> 16) & 255) - expected.B) <= 8,
                    "The actual desktop does not show the " + name + " surface at its outer content edge.");
            }
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    private static void CheckTrayBounds(IntPtr hwnd)
    {
        Require(GetWindowRect(hwnd, out var rectangle), "The native tray menu bounds could not be read.");
        var monitor = MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        Require(GetMonitorInfo(monitor, ref info), "The tray menu monitor work area could not be read.");
        double scale = Math.Max(96, GetDpiForWindow(hwnd)) / 96.0;
        int width = rectangle.Right - rectangle.Left, height = rectangle.Bottom - rectangle.Top;
        Require(width > 0 && height > 0 && width / scale <= 360 && height / scale <= 450,
            "The native tray menu exceeds its compact size budget.");
        Require(rectangle.Left >= info.Work.Left && rectangle.Top >= info.Work.Top && rectangle.Right <= info.Work.Right && rectangle.Bottom <= info.Work.Bottom,
            "The native tray menu extends outside its monitor work area.");
    }

    private static async Task CheckCapsuleSurfaceOnDesktopAsync(VoiceOverlay overlay, string transcript,
        List<Pixels> desktopImages, string report)
    {
        // RenderTargetBitmap cannot establish whether an external system
        // backdrop really reached the desktop. Put a controlled opaque surface
        // behind this HWND and compare the composed desktop with/without it.
        // This proves transparency and presentation, not hardware blur on a VM.
        var backdrop = new Window
        {
            Content = new Grid
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 47, 106, 151)),
                IsHitTestVisible = false
            }
        };
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(overlay);
            var frame = CheckOverlayBounds(overlay);
            var presenter = Microsoft.UI.Windowing.OverlappedPresenter.Create();
            presenter.IsResizable = presenter.IsMinimizable = presenter.IsMaximizable = false;
            presenter.SetBorderAndTitleBar(false, false);
            backdrop.AppWindow.SetPresenter(presenter);
            backdrop.AppWindow.IsShownInSwitchers = false;
            IntPtr behind = WinRT.Interop.WindowNative.GetWindowHandle(backdrop);
            overlay.AppWindow.Hide();
            backdrop.AppWindow.Show(false);
            Require(SetWindowPos(behind, hwnd, frame.Left - 40, frame.Top - 40,
                frame.Right - frame.Left + 80, frame.Bottom - frame.Top + 80, 0x0010),
                "The controlled capsule backdrop could not be positioned without activation.");
            await LayoutAsync(backdrop);
            DwmFlush();

            var host = Find<FrameworkElement>(overlay, "OverlayHost");
            var root = Find<Border>(overlay, "OverlayRoot");
            var bounds = root.TransformToVisual(host).TransformBounds(
                new Windows.Foundation.Rect(0, 0, root.ActualWidth, root.ActualHeight));
            double scale = Math.Max(96, GetDpiForWindow(hwnd)) / 96.0;
            int width = frame.Right - frame.Left, height = frame.Bottom - frame.Top;
            int left = (int)Math.Round(bounds.Left * scale), top = (int)Math.Round(bounds.Top * scale);
            int right = (int)Math.Round(bounds.Right * scale), bottom = (int)Math.Round(bounds.Bottom * scale);
            int curveInset = Math.Max(2, (int)Math.Round(3 * scale));
            int bodyInset = Math.Max(4, (int)Math.Round(6 * scale));
            var samples = new (int X, int Y)[]
            {
                (1, 1), (width - 2, 1), (1, height - 2), (width - 2, height - 2),
                (left + curveInset, top + curveInset), (right - curveInset, top + curveInset),
                (left + curveInset, bottom - curveInset), (right - curveInset, bottom - curveInset),
                ((left + right) / 2, top + bodyInset), ((left + right) / 2, bottom - bodyInset),
                (left + bodyInset, (top + bottom) / 2), (right - bodyInset, (top + bottom) / 2)
            };
            uint[] background = ReadDesktopSamples(frame, samples);
            Require(background.All(color => ChannelDistance(color, 0x00976A2F) <= 8),
                "The controlled background is not visible at every capsule sample; transparency cannot be evaluated.");
            overlay.Update("正在聆听", transcript);
            await LayoutAsync(overlay);
            await Task.Delay(150);
            DwmFlush();
            uint[] visible = ReadDesktopSamples(frame, samples);
            desktopImages.Add(CaptureDesktopWindow(hwnd));
            await SaveContactSheetAsync(EvidencePath(report, "frames"), desktopImages, 2);
            for (int i = 0; i < 4; i++)
                Require(ChannelDistance(background[i], visible[i]) <= 8,
                    "The capsule's transparent host shows an opaque rectangle in its outer corner " + i + ".");
            for (int i = 4; i < 8; i++)
                Require(ChannelDistance(background[i], visible[i]) <= 24,
                    "The capsule's semicircular cutout is filled or its corner shadow is excessively dark at sample " + i + ".");
            Require(Enumerable.Range(8, 4).All(i => ChannelDistance(background[i], visible[i]) >= 12),
                "The actual desktop does not show a tinted capsule surface at every interior sample.");
            Console.WriteLine("Capsule desktop comparison: transparent host corners, semicircular cutouts and tinted interior passed in " +
                root.ActualTheme + ". Acrylic blur intensity is not asserted on the Windows runner.");
        }
        finally { backdrop.Close(); }
    }

    private static uint[] ReadDesktopSamples(Rect frame, (int X, int Y)[] samples)
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        Require(dc != IntPtr.Zero, "The composed desktop could not be inspected.");
        try
        {
            DwmFlush();
            return samples.Select(point =>
            {
                uint color = GetPixel(dc, frame.Left + point.X, frame.Top + point.Y);
                Require(color != uint.MaxValue, "A composed desktop sample could not be read.");
                return color;
            }).ToArray();
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    private static int ChannelDistance(uint first, uint second)
        => Math.Max(Math.Abs((int)(first & 255) - (int)(second & 255)),
            Math.Max(Math.Abs((int)((first >> 8) & 255) - (int)((second >> 8) & 255)),
                Math.Abs((int)((first >> 16) & 255) - (int)((second >> 16) & 255))));

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Network access is prohibited during the offline desktop smoke test.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool revert);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);
}
