using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RealtimeTranscription.Core;
using System.Runtime.CompilerServices;

namespace RealtimeTranscription.Desktop;

/// <summary>All application dialogs share their owner's XamlRoot and one asynchronous modal queue.</summary>
internal static class Dialogs
{
    private static readonly ConditionalWeakTable<MainWindow, SemaphoreSlim> Queues = new();
    private static int pendingDialogs;
    public static bool IsOpen => Volatile.Read(ref pendingDialogs) > 0;

    private static async Task<T> ModalAsync<T>(MainWindow owner, Func<Task<T>> show)
    {
        var queue = Queues.GetValue(owner, _ => new SemaphoreSlim(1, 1));
        Interlocked.Increment(ref pendingDialogs);
        try
        {
            await queue.WaitAsync();
            try { return await show(); }
            finally { queue.Release(); }
        }
        finally { Interlocked.Decrement(ref pendingDialogs); }
    }

    private static ContentDialog Create(MainWindow owner, string title, double preferredWidth = 520)
    {
        // Create is invoked only after this owner's modal queue has been acquired.
        // Tray actions may reach us while the main window is hidden or minimized.
        owner.OpenManager();
        if (owner.Content is not FrameworkElement { XamlRoot: { } xamlRoot } root)
            throw new InvalidOperationException("窗口尚未就绪，请稍后重试。");
        var dialog = new ContentDialog
        {
            Title = title,
            XamlRoot = xamlRoot,
            RequestedTheme = root.ActualTheme,
            CloseButtonText = "关闭"
        };
        void Resize()
        {
            double availableWidth = root.ActualWidth > 0 ? Math.Max(120, root.ActualWidth - 48) : preferredWidth;
            double maximumWidth = Math.Min(preferredWidth, availableWidth);
            dialog.MaxWidth = maximumWidth;
            dialog.Resources["ContentDialogMinWidth"] = Math.Min(maximumWidth, Math.Clamp(preferredWidth - 40, 320, 480));
            dialog.Resources["ContentDialogMaxWidth"] = maximumWidth;
        }
        void SizeChanged(object sender, SizeChangedEventArgs args) => Resize();
        void ThemeChanged(FrameworkElement sender, object args) => dialog.RequestedTheme = root.ActualTheme;
        Resize();
        dialog.Opened += (_, _) => { root.SizeChanged += SizeChanged; root.ActualThemeChanged += ThemeChanged; Resize(); };
        dialog.Closed += (_, _) => { root.SizeChanged -= SizeChanged; root.ActualThemeChanged -= ThemeChanged; };
        return dialog;
    }

    private static double BodyHeight(MainWindow owner, double preferred = 520)
        => owner.Content is FrameworkElement { ActualHeight: > 0 } root
            ? Math.Clamp(root.ActualHeight - 220, 80, preferred) : preferred;

    private static ScrollViewer Scroll(MainWindow owner, UIElement content, double preferredHeight = 520)
    {
        var scroll = new ScrollViewer
        {
            Content = content,
            MaxHeight = BodyHeight(owner, preferredHeight),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        if (owner.Content is FrameworkElement root)
        {
            void Resize(object sender, SizeChangedEventArgs args) => scroll.MaxHeight = BodyHeight(owner, preferredHeight);
            scroll.Loaded += (_, _) => { root.SizeChanged += Resize; scroll.MaxHeight = BodyHeight(owner, preferredHeight); };
            scroll.Unloaded += (_, _) => root.SizeChanged -= Resize;
        }
        return scroll;
    }

    private static TextBox Editor(string text = "", bool readOnly = false, double height = 140)
    {
        var box = new TextBox
        {
            Text = text, IsReadOnly = readOnly, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MinHeight = height, MaxHeight = height
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollMode(box, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Disabled);
        return box;
    }

    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private static InfoBar Feedback() => new() { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Error };
    private static void Error(InfoBar bar, Exception error)
    {
        bar.Severity = InfoBarSeverity.Error;
        bar.Message = AppController.SafeError(error);
        bar.IsOpen = true;
        bar.DispatcherQueue.TryEnqueue(() => { bar.UpdateLayout(); bar.StartBringIntoView(); });
    }
    private static void Notice(InfoBar bar, string message)
    {
        bar.Severity = InfoBarSeverity.Success;
        bar.Message = message;
        bar.IsOpen = true;
    }
    private static TextBox Field(StackPanel panel, string label, string initial, int maxLength = 0)
    {
        var box = new TextBox { Header = label, Text = initial, MaxLength = maxLength };
        panel.Children.Add(box);
        return box;
    }
    private static NumberBox Weight(StackPanel panel, int initial)
    {
        var box = new NumberBox
        {
            Header = "识别权重", Description = "1—5，数值越高越优先。",
            Minimum = 1, Maximum = 5, Value = initial, SmallChange = 1, LargeChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        panel.Children.Add(box);
        return box;
    }
    private static int ReadWeight(NumberBox box)
    {
        double value = box.Value;
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < 1 || value > 5)
            throw new ArgumentException("识别权重应为 1—5 的整数。");
        return (int)value;
    }

    public static Task<string?> AskAsync(MainWindow owner, string title, string label, string initial = "")
        => ModalAsync<string?>(owner, async () =>
        {
            var dialog = Create(owner, title);
            var input = new TextBox { Header = label, Text = initial };
            dialog.Content = input;
            dialog.PrimaryButtonText = "确定";
            dialog.CloseButtonText = "取消";
            dialog.DefaultButton = ContentDialogButton.Primary;
            dialog.Opened += (_, _) => { input.Focus(FocusState.Programmatic); input.SelectAll(); };
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text : null;
        });

    public static Task TextAsync(MainWindow owner, string title, string text)
        => ModalAsync(owner, async () =>
        {
            var dialog = Create(owner, title, 720);
            dialog.Content = Scroll(owner, Editor(text, true, BodyHeight(owner, 420)), 420);
            await dialog.ShowAsync();
            return true;
        });

    public static Task MessageAsync(MainWindow owner, string title, string message)
        => ModalAsync(owner, async () =>
        {
            var dialog = Create(owner, title);
            dialog.Content = Scroll(owner, Label(message), 360);
            await dialog.ShowAsync();
            return true;
        });

    public static Task<bool> ConfirmAsync(MainWindow owner, string title, string message, string confirmText = "确定")
        => ModalAsync(owner, async () =>
        {
            var dialog = Create(owner, title);
            dialog.Content = Scroll(owner, Label(message), 360);
            dialog.PrimaryButtonText = confirmText;
            dialog.CloseButtonText = "取消";
            dialog.DefaultButton = ContentDialogButton.Close;
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        });

    public static Task<TermData?> EditTermAsync(MainWindow owner, TermData term)
        => ModalAsync<TermData?>(owner, async () =>
        {
            var dialog = Create(owner, "词条设置");
            var panel = new StackPanel { Spacing = 14 };
            var word = Field(panel, "词条（1—64 字）", term.Text, 128);
            var category = Field(panel, "类别", term.Category);
            var alias = Field(panel, "旧写法（仅供追溯，不自动替换）", term.Alias, 256);
            var weight = Weight(panel, term.Weight);
            var scope = new CheckBox { Content = "全局词条", IsChecked = term.Scope == "*" };
            var protect = new CheckBox { Content = "润色时保护写法", IsChecked = term.Protect };
            var pinned = new CheckBox { Content = "优先加入识别热词", IsChecked = term.Pinned };
            panel.Children.Add(scope); panel.Children.Add(protect); panel.Children.Add(pinned);
            var state = new ComboBox
            {
                Header = "词条状态", ItemsSource = new[] { "已启用", "待确认", "已禁用" },
                SelectedIndex = term.State switch { TermState.Enabled => 0, TermState.Candidate => 1, _ => 2 },
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            panel.Children.Add(state);
            var feedback = Feedback(); panel.Children.Add(feedback);
            dialog.Content = Scroll(owner, panel);
            dialog.PrimaryButtonText = "保存";
            dialog.CloseButtonText = "取消";
            dialog.DefaultButton = ContentDialogButton.Primary;
            TermData? result = null;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try
                {
                    var candidate = term with
                    {
                        Text = word.Text.Trim(), Category = category.Text.Trim(), Alias = alias.Text.Trim(),
                        Weight = ReadWeight(weight), Scope = scope.IsChecked == true ? "*" : term.Scope == "*" ? owner.CurrentProject : term.Scope,
                        Protect = protect.IsChecked == true, Pinned = pinned.IsChecked == true,
                        State = state.SelectedIndex switch { 0 => TermState.Enabled, 1 => TermState.Candidate, _ => TermState.Disabled }
                    };
                    candidate.Validate(); result = candidate;
                }
                catch (ArgumentException error) { args.Cancel = true; Error(feedback, error); }
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? result : null;
        });

    public static Task<CorrectionApproval?> ConfirmCorrectionAsync(MainWindow owner, CorrectionCandidate candidate, IReadOnlyList<CorrectionEvidence> evidence)
        => ModalAsync<CorrectionApproval?>(owner, async () =>
        {
            var dialog = Create(owner, "确认纠错学习", 620);
            var panel = new StackPanel { Spacing = 14 };
            panel.Children.Add(Label($"已在 {candidate.Count} 个片段中纠正这组写法，请核对后确认。"));
            var correct = Field(panel, "标准写法（1—64 字）", candidate.Corrected, 128);
            var original = Field(panel, "旧写法", candidate.Original, 256);
            var category = Field(panel, "类别", "专业术语", 32);
            var global = new CheckBox { Content = "加入全局词库（默认只用于本项目）" };
            var replace = new CheckBox { Content = "自动纠正这组写法，保留原始识别文本", IsChecked = true };
            panel.Children.Add(global); panel.Children.Add(replace);
            var weight = Weight(panel, candidate.Count >= 3 ? 5 : 4);
            if (evidence.FirstOrDefault() is { } sample)
            {
                panel.Children.Add(new Expander
                {
                    Header = "最近一次修改", HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = Editor($"修改前：{sample.BeforeContext}\n\n修改后：{sample.AfterContext}", true, 150)
                });
            }
            panel.Children.Add(Label("确认后优先加入识别热词，并在润色时保护标准写法。同名词条合并，保留原有分类；禁用词条需先在词库启用。"));
            var feedback = Feedback(); panel.Children.Add(feedback);
            dialog.Content = Scroll(owner, panel);
            dialog.PrimaryButtonText = "确认学习";
            dialog.CloseButtonText = "取消";
            dialog.DefaultButton = ContentDialogButton.Primary;
            CorrectionApproval? result = null;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try
                {
                    int level = ReadWeight(weight);
                    var term = new TermData { Text = correct.Text.Trim(), Alias = original.Text.Trim(), Weight = level };
                    term.Validate();
                    result = new(term.Text, term.Alias, category.Text.Trim(), global.IsChecked == true ? "*" : candidate.ProjectId, level, replace.IsChecked == true);
                }
                catch (ArgumentException error) { args.Cancel = true; Error(feedback, error); }
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? result : null;
        });

    public static Task ReviewAsync(MainWindow owner, AppController controller)
        => ModalAsync(owner, async () =>
        {
            var snapshot = await controller.SnapshotAsync();
            var dialog = Create(owner, "原文对照与编辑", 880);
            bool busy = false, openCorrections = false;
            var feedback = Feedback();
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(Label("编辑仅修改本地记录。保存片段后，本轮正文会按修订结果重新拼接。"));
            content.Children.Add(feedback);
            var tabs = new TabView { IsAddTabButtonVisible = false, CanDragTabs = false, CanReorderTabs = false };
            content.Children.Add(tabs);

            var whole = new StackPanel { Spacing = 10 };
            whole.Children.Add(Label("本轮完整识别原文"));
            var rawWhole = Editor(TranscriptText.Render(snapshot.Segments.Select(s => s with { FinalText = s.RawText })), true);
            whole.Children.Add(rawWhole);
            var wholeLabel = Label("本轮最终正文 · " + snapshot.Session?.WholePolishReason);
            whole.Children.Add(wholeLabel);
            var finalWhole = Editor(TranscriptText.Render(snapshot), true);
            whole.Children.Add(finalWhole);
            tabs.TabItems.Add(new TabViewItem { Header = "全文对照", IsClosable = false, Content = whole });

            var segment = new StackPanel { Spacing = 10 };
            var list = new ComboBox
            {
                Header = "选择片段", ItemsSource = snapshot.Segments, DisplayMemberPath = "ViewLabel",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            segment.Children.Add(list);
            var raw = Editor("", true, 110); raw.Header = "服务端原文 / 未确认草稿"; segment.Children.Add(raw);
            var final = Editor("", false, 110); final.Header = "片段修订"; segment.Children.Add(final);
            var actions = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
            for (int i = 0; i < 3; i++) actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            actions.RowDefinitions.Add(new() { Height = GridLength.Auto });
            actions.RowDefinitions.Add(new() { Height = GridLength.Auto });
            segment.Children.Add(actions);
            var history = Editor("", true, 140);
            var historyPane = new Expander
            {
                Header = "修改历史", Content = history, HorizontalAlignment = HorizontalAlignment.Stretch,
                Visibility = Visibility.Collapsed
            };
            segment.Children.Add(historyPane);
            var rememberBody = new StackPanel { Spacing = 10 };
            var rememberedWord = Field(rememberBody, "标准词条（不是整段正文）", "", 128);
            var rememberedOld = Field(rememberBody, "原写法（可留空）", "", 256);
            var rememberSave = new Button { Content = "保存到词库" }; rememberBody.Children.Add(rememberSave);
            var rememberPane = new Expander
            {
                Header = "记住此写法", Content = rememberBody, HorizontalAlignment = HorizontalAlignment.Stretch,
                Visibility = Visibility.Collapsed
            };
            segment.Children.Add(rememberPane);
            var learned = new HyperlinkButton { Content = "查看纠错候选", Padding = new Thickness(0, 8, 0, 0) };
            learned.Click += (_, _) => { openCorrections = true; dialog.Hide(); };
            segment.Children.Add(learned);
            tabs.TabItems.Add(new TabViewItem { Header = "片段修订", IsClosable = false, Content = segment });

            async Task RefreshAsync(string? selectedId)
            {
                var latest = await controller.SnapshotAsync();
                list.ItemsSource = latest.Segments;
                list.SelectedItem = latest.Segments.FirstOrDefault(s => s.Id == selectedId) ?? latest.Segments.FirstOrDefault();
                rawWhole.Text = TranscriptText.Render(latest.Segments.Select(s => s with { FinalText = s.RawText }));
                finalWhole.Text = TranscriptText.Render(latest);
                wholeLabel.Text = "本轮最终正文 · " + latest.Session?.WholePolishReason;
            }
            async Task RunAsync(Func<Task> action, string success, bool refresh = true)
            {
                if (busy) return;
                busy = true; tabs.IsEnabled = false; feedback.IsOpen = false;
                try
                {
                    string? selectedId = (list.SelectedItem as SegmentData)?.Id;
                    await action();
                    if (refresh) await RefreshAsync(selectedId);
                    Notice(feedback, success);
                }
                catch (Exception error) { Error(feedback, error); }
                finally { busy = false; tabs.IsEnabled = true; }
            }
            int actionIndex = 0;
            var editButtons = new List<Button>();
            void ActionButton(string label, Func<SegmentData, Task> action, bool needsEditable = false, bool refresh = true)
            {
                var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch };
                Grid.SetRow(button, actionIndex / 3); Grid.SetColumn(button, actionIndex++ % 3);
                actions.Children.Add(button);
                if (needsEditable) editButtons.Add(button);
                button.Click += async (_, _) =>
                {
                    if (list.SelectedItem is not SegmentData selected || busy) return;
                    await RunAsync(() => action(selected), label is "修改历史" or "记住此写法" ? "已展开，请在下方查看。" : "本地记录已更新。", refresh);
                };
            }
            ActionButton("保存编辑", selected => controller.EditAsync(selected.Id, final.Text), true);
            ActionButton("恢复原文", selected => controller.EditAsync(selected.Id, selected.RawText, "恢复原文"), true);
            ActionButton("删除片段", selected => controller.EditAsync(selected.Id, "", "删除"), true);
            ActionButton("撤销上次编辑", selected => controller.UndoAsync(selected.Id), true);
            ActionButton("修改历史", selected =>
            {
                history.Text = selected.Edits.Count == 0 ? "暂无修改历史。" : string.Join("\n\n", selected.Edits.Select(edit => $"{edit.At.LocalDateTime:g} · {edit.Action}\n{edit.Text}"));
                historyPane.Visibility = Visibility.Visible; historyPane.IsExpanded = true;
                return Task.CompletedTask;
            }, refresh: false);
            ActionButton("记住此写法", _ =>
            {
                rememberedWord.Text = final.SelectedText;
                rememberedOld.Text = raw.SelectedText;
                rememberPane.Visibility = Visibility.Visible; rememberPane.IsExpanded = true;
                return Task.CompletedTask;
            }, refresh: false);
            void RefreshSelection()
            {
                var selected = list.SelectedItem as SegmentData;
                raw.Text = selected is null ? "" : selected.RawText.Length > 0 ? selected.RawText : selected.PartialText;
                final.Text = selected?.FinalText ?? "";
                bool editable = selected?.OutputState is OutputState.Published or OutputState.Deleted;
                final.IsReadOnly = !editable;
                foreach (var button in actions.Children.OfType<Button>())
                    button.IsEnabled = selected is not null && (!editButtons.Contains(button) || editable);
            }
            list.SelectionChanged += (_, _) => RefreshSelection();
            rememberSave.Click += async (_, _) => await RunAsync(async () =>
            {
                var term = new TermData { Text = rememberedWord.Text.Trim(), Alias = rememberedOld.Text.Trim() };
                term.Validate();
                await controller.RememberAsync(term.Text, term.Alias);
            }, "词条已保存，将用于后续识别。", refresh: false);
            dialog.Closing += (_, args) => { if (busy) args.Cancel = true; };
            dialog.Content = Scroll(owner, content, 600);
            list.SelectedIndex = snapshot.Segments.Count > 0 ? 0 : -1;
            RefreshSelection();
            await dialog.ShowAsync();
            if (openCorrections) owner.OpenCorrections();
            return true;
        });
}
