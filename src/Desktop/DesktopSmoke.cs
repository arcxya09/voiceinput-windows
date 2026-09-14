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
        bool popupNativeFramesPassed = true, trayDesktopSurfacePassed = true, overlayDesktopSurfacePassed = true;
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
            controller = new AppController(folder, provider: new NoNetwork());
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
            Require(Find<TextBlock>(overlay, "OverlayPreview").Text == "说话时会在这里显示文字",
                "The first-show placeholder collapsed into an ellipsis.");
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
            var original = CheckOverlayBounds(hwnd);
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                Stage("Inspect actual recognition outer frame in " + theme + " theme");
                ((FrameworkElement)overlay.Content).RequestedTheme = theme;
                await LayoutAsync(overlay);
                desktopImages.Add(CaptureDesktopWindow(hwnd));
                await SaveContactSheetAsync(EvidencePath(report, "frames"), desktopImages, 2);
                popupNativeFramesPassed &= InspectFrameInvariant(() => CheckPopupNativeFrame(hwnd, "overlay " + theme), "Native frame");
                overlayDesktopSurfacePassed &= InspectFrameInvariant(() => CheckOverlayVisibleOnDesktop(overlay, original), "Composed desktop surface");
            }
            var preview = Find<TextBlock>(overlay, "OverlayPreview");
            string suffix = preview.Text.TrimStart('…');
            Require(preview.TextWrapping == TextWrapping.NoWrap && preview.ActualHeight <= 26, "The live preview grew beyond one line.");
            Require(suffix.EndsWith(ending, StringComparison.Ordinal) && longText.EndsWith(suffix, StringComparison.Ordinal), "The compact preview is not showing the latest transcript suffix.");
            int boundary = longText.Length - suffix.Length;
            Require(StringInfo.ParseCombiningCharacters(longText).Contains(boundary), "The compact preview split a Unicode grapheme.");
            if (overlayDesktopSurfacePassed)
                checks.Add("Actual overlay HWND is visible on the desktop, preserves focus, passes pointers through, and is centered at 360 by 76 DIPs");
            if (popupNativeFramesPassed && trayDesktopSurfacePassed && overlayDesktopSurfacePassed)
                checks.Add("Tray and overlay use DWM native frame policy without an application region or duplicate XAML outline; full-client layout and light/dark desktop surfaces remain correct");
            checks.Add("Long live previews stay on one line and retain complete Unicode graphemes at the transcript tail");

            var overlayImages = new List<Pixels> { await CaptureAsync((FrameworkElement)overlay.Content) };
            overlay.SetMeter(0, false);
            overlay.Update("正在整理", "全文整理完成后将输入原位置");
            await LayoutAsync(overlay);
            Require(preview.Text == "全文整理完成后将输入原位置", "Long-to-short preview retained a stale measurement.");
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
            await SaveContactSheetAsync(EvidencePath(report, "overlay"), overlayImages, columns: 1);
            checks.Add("Listening, processing, complete and save-warning overlays captured without expanding the window or taking focus");
            overlay.Clear();
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

    private static async Task CheckPreviewLifecycleAsync(VoiceOverlay overlay)
    {
        var preview = Find<TextBlock>(overlay, "OverlayPreview");
        var panel = (TailPreviewPanel)VisualTreeHelper.GetParent(preview);
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

        double fontSize = preview.FontSize;
        const string tail = "最新词";
        string repeated = string.Concat(Enumerable.Repeat("中文测量👩🏽‍🔬e\u0301", 20)) + tail;
        foreach (double multiplier in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            preview.FontSize = fontSize * multiplier;
            panel.InvalidateTextMetrics();
            overlay.Update("正在聆听", repeated);
            foreach (double width in new[] { 0.0, 160.0, 328.0, 240.0, 328.0 })
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
            panel.Measure(new Windows.Foundation.Size(328, 80));
            panel.Arrange(new Windows.Foundation.Rect(0, 0, 328, 80));
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
        Require(preview.Text == "说话时会在这里显示文字", "A new turn retained the preceding turn's words.");
        overlay.Update("本轮已取消", text: "", dismiss: true);
        await LayoutAsync(overlay);
        Require(preview.Text == "本轮尚未识别到文字", "An empty canceled turn showed an ellipsis instead of readable feedback.");
        overlay.BeginTurn();
        await LayoutAsync(overlay);
        Console.WriteLine("Preview regression: real rooted WinUI layout, 0/160/240/328 DIP constraints and font multipliers 1/1.25/1.5/2 passed. These are layout tests, not physical monitor-DPI changes.");
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

    private static void CheckOverlayVisibleOnDesktop(VoiceOverlay overlay, Rect rectangle)
    {
        // RenderTargetBitmap proves that the XAML tree rendered. Also inspect
        // the composed desktop: a broken native layered-window setup can leave
        // a perfectly valid XAML bitmap while the user sees an empty rectangle.
        var expected = ((SolidColorBrush)Find<Border>(overlay, "OverlayRoot").Background).Color;
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
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool revert);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
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
