using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.Win32;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;
using RealtimeTranscription.Desktop.Input;
using Forms=System.Windows.Forms;
namespace RealtimeTranscription.Desktop;
public partial class MainWindow : Window
{
    private readonly AppController controller;
    private readonly VoiceOverlay overlay=new();
    private readonly DispatcherTimer render=new(){Interval=TimeSpan.FromMilliseconds(75)};
    private PushToTalkService? ptt;
    private Forms.NotifyIcon? tray;
    private TrayMenuWindow? trayMenu;
    private System.Drawing.Icon? trayIcon;
    private DeviceWatcher? devices;
    private TranscriptSnapshot? pending;
    private CancellationTokenSource? search;
    private bool ready,updating,closed,shuttingDown,microphoneTesting;
    private bool savingSettings,changingProject,changingLearning,updatingLearning,testingConnection,extractingCurrent;
    private int managementOperations;
    private bool ManagementOperationBusy=>Volatile.Read(ref managementOperations)>0||Dialogs.IsOpen||pickerOpen||correctionBusy||extractingHistory||extractingCurrent||generatingTerms||importingGenerated||testingPrompt||testingConnection||microphoneTesting||savingSettings||changingProject||changingLearning;
    private bool ManagementBusy=>ManagementOperationBusy||ptt?.Busy==true;
    private float level;
    public string CurrentProject=>controller.Settings.ProjectId;
    public MainWindow(AppController controller)
    {
        this.controller=controller;InitializeComponent();
        controller.Updated+=snapshot=>Volatile.Write(ref pending,snapshot);
        controller.TermsUpdated+=()=>UI(RefreshTerms);controller.Level+=v=>Volatile.Write(ref level,v);
        controller.Message+=m=>UI(()=>StatusText.Text=m);
        controller.CorrectionsUpdated+=()=>UI(async()=>{if(!shuttingDown&&controller.MemoryAvailable)await Safe(RefreshCorrections);});
        controller.Extracting+=v=>UI(()=>{extractingCurrent=v;ExtractButton.IsEnabled=!v;AllHistoryButton.IsEnabled=HistoryExtractAllButton.IsEnabled=!v;ExtractButton.Content=v?"正在整理…":"整理当前会话词条";});
        render.Tick+=(_,_)=>Render();render.Start();AppWindow.Closing+=ClosingWindow;
        ConfigureWindow();InitializeStartupSettings();
        SystemEvents.PowerModeChanged+=PowerChanged;SystemEvents.SessionSwitch+=SessionChanged;
    }
    private void UI(Action fn){if(!closed)DispatcherQueue.TryEnqueue(()=>{if(!closed)fn();});}
    public async void Ready()
    {
        try
        {
            controller.Log.Write("Application","ReadyStarted");
            CreateTray();
            try{await FillSettings();}catch(Exception e){controller.Log.Write("UI","LoadSettingsFailed",exception:e);StatusText.Text=AppController.SafeError(e);}
            ProjectsRefresh();RefreshTerms();
            if(controller.MemoryAvailable)
                try{await RefreshCorrections();}catch(Exception e){CorrectionStatus.Text="纠错记录暂不可用："+AppController.SafeError(e);}
            ptt=new(controller){CanStart=CanStartVoiceTurn};
            ConnectPreviewEvents(ptt);
            await ptt.InitializeAsync();
            devices=new DeviceWatcher((id,isDefault)=>{if(ptt.Busy&&((isDefault&&controller.Settings.DeviceId=="")||(!isDefault&&controller.ActiveDeviceId==id))){ptt.Cancel("麦克风设备已变化，本轮停止，确认文字可复制。");controller.RequestStopCapture();}},controller.Log);ready=true;
            controller.Log.Write("Application","ReadyCompleted");
            CompleteStartupPresentation();
            pending=await controller.SnapshotAsync();
            InitializeUpdates();
        }
        catch(Exception e){controller.Log.Write("Application","ReadyFailed",exception:e);ready=true;StatusText.Text=AppController.SafeError(e);if(Program.IsStartupLaunch)NotifyStartupError(AppController.SafeError(e));else await Dialogs.MessageAsync(this,"启动",AppController.SafeError(e));}
    }
    private void Render()
    {
        if(ManagementBusy||AppWindow.IsVisible||trayMenu?.IsOpen==true)idleUpdateSince=DateTimeOffset.UtcNow;
        ProjectBox.IsEnabled=!ManagementBusy;
        RefreshLogStatus();
        SaveSettingsButton.IsEnabled=TestBailianButton.IsEnabled=TestDeepSeekButton.IsEnabled=!ManagementBusy;
        RefreshSelectionActions();
        SessionLearningBox.IsEnabled=!ManagementBusy&&SessionLearningBox.Tag is string;
        var s=Interlocked.Exchange(ref pending,null);if(s!=null)lastSnapshot=s;RefreshLiveState();if(s==null)return;
        string body=TranscriptText.Render(s);if(!SameDisplayedText(OutputBox.Text,body)){bool follow=IsAtTranscriptEnd(OutputBox);OutputBox.Text=body;if(follow)ScrollTranscriptToEnd(OutputBox);}
        string preview=string.Join(" ",s.Segments.Where(x=>x.AsrState==AsrState.Partial).Select(x=>x.PartialText));
        if(!SameDisplayedText(PreviewBox.Text,preview)){bool follow=IsAtTranscriptEnd(PreviewBox);PreviewBox.Text=preview;if(follow)ScrollTranscriptToEnd(PreviewBox);}BodyCount.Text=$"本轮正文 · {JsonCodec.Count(body)} 字";StatusText.Text=s.Status;
        LexiconLiveStatus.Text=s.Session is {} session?$"本轮热词 {session.Hotwords.Count}/{session.EligibleHotwordCount} · {HotwordStateText(session.HotwordState)} · 保护词 {session.ProtectedTermCount} · 纠正 {session.AppliedCorrectionCount} 处":"尚无本轮词库记录";
        SaveLabel.Text=$"待润色 {s.Pending} · 未确认 {s.Segments.Count(x=>x.AsrState==AsrState.Unresolved)} · 未保存 {s.Unsaved} · {s.Session?.DeliveryReason}";
        updatingLearning=true;
        SessionLearningBox.Tag=s.Session?.Id;SessionLearningBox.IsChecked=s.Session?.AllowLearning==true;SessionLearningBox.IsEnabled=s.Session!=null&&!ManagementBusy;
        updatingLearning=false;
        RefreshPreview(s);
    }
    private void CreateTray()
    {
        using(var embedded=new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory,"Assets","AppIcon.ico"),32,32))trayIcon=(System.Drawing.Icon)embedded.Clone();
        trayMenu=new TrayMenuWindow(command=>Safe(async()=>
        {
            switch(command)
            {
                case TrayMenuCommand.OpenManager: OpenManager(); break;
                case TrayMenuCommand.ToggleEnabled: ptt?.SetEnabled(ptt?.Enabled!=true); break;
                case TrayMenuCommand.ToggleDictation: await SetDictationMode(!controller.Settings.DictationOnly); break;
                case TrayMenuCommand.Copy: await CopyCurrent(); break;
                case TrayMenuCommand.Settings: OpenManager(); ShowPage(3); break;
                case TrayMenuCommand.Exit: await RequestExitAsync(true); break;
            }
        }));
        tray=new Forms.NotifyIcon{Icon=trayIcon,Text="语音输入法 · 按住说话",Visible=true};
        tray.MouseUp+=(_,args)=>
        {
            if(args.Button!=Forms.MouseButtons.Right)return;
            UI(()=>
            {
                if(shuttingDown||Dialogs.IsOpen||pickerOpen)return;
                trayMenu.SetState(ptt?.Enabled==true,controller.Settings.DictationOnly,!ManagementBusy);
                trayMenu.ShowAtCursor();
            });
        };
        tray.DoubleClick+=(_,_)=>UI(()=>{trayMenu.HideMenu();OpenManager();});
    }
    public void OpenManager(){Show();if(AppWindow.Presenter is OverlappedPresenter presenter)presenter.Restore();Activate();}
    private void EnsureIdle(){if(Volatile.Read(ref managementOperations)>0)throw new InvalidOperationException("管理操作尚未完成，请稍候。");if(correctionBusy)throw new InvalidOperationException("纠错学习操作尚未完成，请稍候。");if(savingSettings||changingProject||changingLearning)throw new InvalidOperationException("设置或项目切换正在保存，请稍候。");if(extractingHistory)throw new InvalidOperationException("全部历史词条提取尚未结束，请先取消或等待完成。");if(generatingTerms||importingGenerated)throw new InvalidOperationException("词库生成或导入尚未结束，请稍候或先取消生成。");if(testingPrompt)throw new InvalidOperationException("提示词试用尚未结束，请稍候。");if(testingConnection)throw new InvalidOperationException("连接测试尚未结束，请稍候。");if(microphoneTesting)throw new InvalidOperationException("麦克风测试尚未结束，请稍候。");if(ptt?.Busy==true)throw new InvalidOperationException("当前输入尚未完成，请松开快捷键并等待结果后再操作。");}
    private async Task Safe(Func<Task> fn){try{await fn();}catch(OperationCanceledException){StatusText.Text="操作已取消。";controller.Log.Write("UI","OperationCancelled");}catch(Exception e){controller.Log.Write("UI","OperationFailed",exception:e);await Dialogs.MessageAsync(this,"语音输入法",AppController.SafeError(e));}}
    internal bool CanStartVoiceTurn() => !ManagementOperationBusy && !updateInstalling && !Volatile.Read(ref shuttingDown)
        && Volatile.Read(ref trayMenu)?.IsOpen != true;
    private Task Manage(Func<Task> action) => Safe(() => RunManagementOperationAsync(action));
    internal async Task RunManagementOperationAsync(Func<Task> action)
    {
        EnsureIdle();
        Interlocked.Increment(ref managementOperations);
        try { await action(); }
        finally { Interlocked.Decrement(ref managementOperations); }
    }
    private void ProjectsRefresh(){updating=true;ProjectBox.ItemsSource=controller.Projects.ToArray();ProjectBox.SelectedValue=CurrentProject;updating=false;RefreshGeneratedMatches();}
    private async void Project_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(!ready||updating||ProjectBox.SelectedValue is not string id||id==CurrentProject)return;
        await Safe(async()=>
        {
            try{EnsureIdle();changingProject=true;await controller.ChangeProjectAsync(id);}
            finally{changingProject=false;ProjectsRefresh();}
        });
    }
    private async void SessionLearning_Changed(object sender,RoutedEventArgs e)
    {
        if(!ready||updatingLearning||SessionLearningBox.Tag is not string)return;
        bool allow=SessionLearningBox.IsChecked==true;
        await Safe(async()=>
        {
            try{EnsureIdle();changingLearning=true;await controller.SetSessionLearningAsync(allow);}
            finally{changingLearning=false;pending=await controller.SnapshotAsync();}
        });
    }
    private async void NewProject_Click(object sender,RoutedEventArgs e)=>await Manage(async()=>{string? name=await Dialogs.AskAsync(this,"新建项目","项目名称");if(name!=null){await controller.CreateProjectAsync(name);ProjectsRefresh();}});
    private void Start_Click(object sender,RoutedEventArgs e){if(ptt==null){StatusText.Text="快捷键未启动，请检查启动提示后重新打开程序。";return;}ptt.SetEnabled(true);StatusText.Text=controller.Settings.DictationOnly?"请按住说话键听写，完成后自动复制正文。":"请在其他应用的文本框中按住说话键。";}
    private void Pause_Click(object sender,RoutedEventArgs e)=>ptt?.SetEnabled(false);
    private void Stop_Click(object sender,RoutedEventArgs e)=>ptt?.Cancel();
    private async void Paragraph_Click(object sender,RoutedEventArgs e)=>await Safe(()=>controller.ParagraphAsync());
    private async void Review_Click(object sender,RoutedEventArgs e)=>await Manage(async()=>{await Dialogs.ReviewAsync(this,controller);});
    private async Task ClipboardAsync(string text)
    {
        if(text.Length==0){StatusText.Text="当前没有可复制的正文。";return;}
        for(int i=0;;i++){try{var data=new Windows.ApplicationModel.DataTransfer.DataPackage();data.SetText(text);Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);Windows.ApplicationModel.DataTransfer.Clipboard.Flush();StatusText.Text="纯正文已复制。";return;}catch(System.Runtime.InteropServices.COMException)when(i<2){await Task.Delay(100*(i+1));}}
    }
    private async Task CopyCurrent()=>await ClipboardAsync(TranscriptText.Render(await controller.SnapshotAsync()));
    private async void Copy_Click(object sender,RoutedEventArgs e)=>await Safe(CopyCurrent);
    private async void StopCopy_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{ptt?.Cancel("已取消自动输入，将复制确认文字。");await ClipboardAsync(await controller.StopAndTextAsync());});
    private async Task ExportBody(string extension)
    {
        string text=TranscriptText.Render(await controller.SnapshotAsync());if(text.Length==0)throw new InvalidOperationException("当前没有可导出的正文。");
        var file=await PickSaveAsync("语音输入_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+extension,extension);
        if(file!=null){await File.WriteAllTextAsync(file,text,new UTF8Encoding(false));StatusText.Text="纯正文已导出。";}
    }
    private async void ExportTxt_Click(object sender,RoutedEventArgs e)=>await Safe(()=>ExportBody(".txt"));
    private async void ExportMd_Click(object sender,RoutedEventArgs e)=>await Safe(()=>ExportBody(".md"));
    private async void HistorySearch_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        search?.Cancel();search=new();var current=search;string? project=AllProjects.IsChecked==true?null:CurrentProject;
        DateTimeOffset? since=HistoryDays.SelectedIndex switch{1=>DateTimeOffset.UtcNow.AddDays(-7),2=>DateTimeOffset.UtcNow.AddDays(-30),_=>null};HistoryStatus.Text="正在检索…";
        try
        {
            var hits=await controller.Repository.SearchAsync(project,HistoryQuery.Text.Trim(),since,current.Token);if(search!=current)return;
            string? selected=(HistoryGrid.SelectedItem as MemoryHit)?.Session.Id;
            HistoryGrid.ItemsSource=hits;HistoryGrid.SelectedItem=hits.FirstOrDefault(hit=>hit.Session.Id==selected);
            HistoryStatus.Text=$"找到 {hits.Count} 条记录"+(hits.Count==500?"（最多显示最近 500 条，请缩小筛选范围）":"");
        }
        catch(OperationCanceledException)when(current.IsCancellationRequested){if(search==current)HistoryStatus.Text="搜索已取消。";}
        catch{if(search==current)HistoryStatus.Text="搜索未完成，请检查错误提示后重试。";throw;}
        finally{if(search==current)search=null;current.Dispose();}
    });
    private void HistoryCancel_Click(object sender,RoutedEventArgs e)=>search?.Cancel();
    private async void HistoryOpen_Click(object sender,RoutedEventArgs e)=>await Manage(async()=>{var hit=ResolveListAction<MemoryHit>(HistoryGrid,e);if(hit==null)return;await controller.LoadSessionAsync(hit.Session);ProjectsRefresh();ShowPage(0);});
    private async void HistoryExport_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        if(HistoryGrid.SelectedItem is not MemoryHit hit)return;var file=await PickSaveAsync("语音输入记录.json",".json");if(file==null)return;
        await controller.Repository.BarrierAsync();
        var latest=await controller.Repository.LoadSessionAsync(hit.Session.Id)??throw new InvalidOperationException("该历史记录已删除，请刷新列表。");
        await File.WriteAllTextAsync(file,JsonSerializer.Serialize(new{schemaVersion=3,session=latest.Session,segments=latest.Segments},new JsonSerializerOptions(JsonCodec.Options){WriteIndented=true}),new UTF8Encoding(false));
    });
    private async void HistoryDelete_Click(object sender,RoutedEventArgs e)=>await Manage(async()=>{if(HistoryGrid.SelectedItem is not MemoryHit hit)return;if(!await Dialogs.ConfirmAsync(this,"清除记忆","清除此记录及其派生词条来源？此操作不能撤销，外部文档不受影响。","清除"))return;await controller.DeleteSessionAsync(hit.Session);HistorySearch_Click(sender,e);});
    private void RefreshTerms()
    {
        if(TermsGrid==null||TermFilter==null)return;string query=TermQuery.Text.Trim();var items=controller.Terms.Where(t=>t.Text.Contains(query,StringComparison.OrdinalIgnoreCase)&&(TermFilter.SelectedIndex switch{1=>t.State==TermState.Enabled,2=>t.State==TermState.Candidate,3=>t.State==TermState.Disabled,_=>true})).OrderByDescending(t=>t.Pinned).ThenBy(t=>t.State).ThenBy(t=>t.Text).ToArray();
        var selected=TermsGrid.SelectedItems.Cast<TermData>().Select(t=>t.Id).ToHashSet();
        RefreshGeneratedMatches();TermsGrid.ItemsSource=items;
        foreach(var item in items)if(selected.Contains(item.Id))TermsGrid.SelectedItems.Add(item);
        RefreshSelectionActions();TermsCount.Text=$"当前显示 {items.Length} 个 · 全局及本项目 {controller.Terms.Count} 个 · 下轮提交 {controller.NextHotwords().Count} 个 · 待确认 {controller.Terms.Count(t=>t.State==TermState.Candidate)} 个 · "+(controller.Settings.DynamicLexicon?"动态排序已开启":"使用手动权重");
    }
    private void TermFilter_Changed(object sender,RoutedEventArgs e){if(ready)RefreshTerms();}
    private async void TermAdd_Click(object sender,RoutedEventArgs e)=>await Manage(async()=>{var term=await Dialogs.EditTermAsync(this,new(){Scope=CurrentProject});if(term!=null)await controller.SaveTermAsync(term);});
    private async void TermEdit_Click(object sender,RoutedEventArgs e)=>await Manage(async()=>
    {
        TermData? old=null;
        if(e is Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs doubleTap)
        {
            // In multiple-selection mode SelectedItem can refer to another row.
            // Resolve the actual item container without changing the batch selection.
            for(DependencyObject? source=doubleTap.OriginalSource as DependencyObject;
                source!=null&&source!=TermsGrid;
                source=Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source))
            {
                if(source is ListViewItem item&&TermsGrid.IndexFromContainer(item)>=0&&item.Content is TermData hit)
                {old=hit;break;}
            }
            if(old==null)return; // Double-tapping empty list space must not edit a selected row.
            doubleTap.Handled=true;
        }
        else
        {
            if(TermsGrid.SelectedItems.Count!=1)
                throw new InvalidOperationException("请仅选择一个词条进行编辑。");
            old=TermsGrid.SelectedItems[0] as TermData;
        }
        if(old==null)return;
        var term=await Dialogs.EditTermAsync(this,old);
        if(term!=null)await controller.SaveTermAsync(term);
    });
    private async void TermConfirm_Click(object sender,RoutedEventArgs e)=>await Manage(async()=>{foreach(var term in TermsGrid.SelectedItems.Cast<TermData>().ToArray())await controller.SaveTermAsync(term with{State=TermState.Enabled});});
    private async void TermDisable_Click(object sender,RoutedEventArgs e)=>await Manage(async()=>{foreach(var term in TermsGrid.SelectedItems.Cast<TermData>().ToArray())await controller.SaveTermAsync(term with{State=TermState.Disabled});});
    private async void TermDelete_Click(object sender,RoutedEventArgs e)=>await Manage(async()=>{var selected=TermsGrid.SelectedItems.Cast<TermData>().ToArray();if(selected.Length==0)return;if(!await Dialogs.ConfirmAsync(this,"词库",$"删除选中的 {selected.Length} 个词条？","删除"))return;foreach(var term in selected)await controller.DeleteTermAsync(term);});
    private async void TermEvidence_Click(object sender,RoutedEventArgs e){if(TermsGrid.SelectedItem is TermData term)await Dialogs.TextAsync(this,"词条来源",$"来源类型：{(term.Origin=="AiGenerated"?"AI 领域生成":term.Origin=="CorrectionLearning"?"纠错学习（来源见词库的纠错学习页）":term.Origin)}\n旧写法：{term.Alias}\n"+(term.GenerationRequirement.Length>0?$"生成要求：{term.GenerationRequirement}\n":"")+"\n"+(term.Evidence.Count==0?"此词条没有自动提取证据。":string.Join("\n\n",term.Evidence.Select(v=>$"会话 {v.SessionId}\n片段 {v.SegmentId} · 原文版本 {v.SourceRevision} · 编辑版本 {v.EditRevision}\n{v.Quote}"))));}
    private async void TermImport_Click(object sender,RoutedEventArgs e)=>await Manage(async()=>
    {
        var file=await PickOpenAsync(".txt",".csv",".json");if(file==null)return;if(new FileInfo(file).Length>5*1024*1024)throw new ArgumentException("导入文件最多 5 MiB。");
        var terms=TermExchange.Parse(await File.ReadAllTextAsync(file),Path.GetExtension(file),CurrentProject);
        if(!await Dialogs.ConfirmAsync(this,"导入词库",$"已校验 {terms.Count} 个词条，将按文件状态导入；相同范围的重名项会跳过。","导入"))return;
        int count=await controller.ImportTermsAsync(terms);StatusText.Text=$"已导入 {count} 个词条，重复项未覆盖。";
    });
    private async void TermExport_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{var file=await PickSaveAsync("个人词库.json",".json");if(file!=null)await File.WriteAllTextAsync(file,TermExchange.Export(controller.Terms),new UTF8Encoding(false));});
    private async void Extract_Click(object sender,RoutedEventArgs e)=>await Manage(async()=>{await controller.ExtractAsync();});
    private void ExtractCancel_Click(object sender,RoutedEventArgs e)=>controller.CancelExtraction();
    private async Task FillSettings()
    {
        var s=controller.Settings;BailianKeyBox.Password=controller.Keys.BailianKey;DeepSeekKeyBox.Password=controller.Keys.DeepSeekKey;WorkspaceBox.Text=s.WorkspaceId;RegionBox.SelectedIndex=s.Region=="cn-beijing"?0:1;LegacyBox.IsChecked=s.LegacyEndpoint;
        AutoCheckUpdateBox.IsChecked=s.AutoCheckUpdates;AutoDownloadUpdateBox.IsChecked=s.AutoDownloadUpdates;AutoInstallUpdateBox.IsChecked=s.AutoOpenUpdateInstaller;
        SmartPunctuationBox.IsChecked=s.SmartPunctuationEnabled;
        AdaptiveAsrBox.IsChecked=s.AdaptiveAsrEnabled;
        PolishPromptBox.Text=s.EffectivePolishPrompt;GenerationRequirementBox.Text=s.GenerationRequirement;GenerationCountBox.Text=s.GenerationCount.ToString();GenerationScopeBox.SelectedIndex=s.GenerationGlobal?1:0;
        PolishBox.IsChecked=s.PolishEnabled;FastDeliveryBox.IsChecked=s.PreferFastDelivery;PreviousBox.IsChecked=s.PreviousContext;MemoryBox.IsChecked=s.SaveMemory;LearningBox.IsChecked=s.AllowLearning;CorrectionLearningBox.IsChecked=s.LearnCorrections;UseTermsBox.IsChecked=s.UseLexicon;DynamicTermsBox.IsChecked=s.DynamicLexicon;AutoExtractBox.IsChecked=s.AutoExtract;AsrContextBox.IsChecked=s.AsrContext;
        GenerationMaxThinkingBox.IsChecked=s.GenerationMaxThinking;ParagraphBox.IsChecked=s.AutoParagraph;TrayBox.IsChecked=s.CloseToTray;RetentionBox.Text=s.RetentionDays?.ToString()??"";BudgetBox.Text=s.DailyExtractionTokens.ToString();SilenceBox.Text=s.SilenceMs.ToString();HoldBox.Text=s.HoldMs.ToString();MaxHoldBox.Text=s.MaxHoldSeconds.ToString();HotkeyBox.SelectedIndex=s.Hotkey switch{"F8"=>1,"F9"=>2,_=>0};await DevicesRefresh();
    }
    private async Task DevicesRefresh()
    {
        var list=await Task.Run(()=>AudioCapture.Devices(controller.Log));
        string selected=controller.Settings.DeviceId;
        // Keep the saved selection visible. Both the local test and real capture
        // now resolve this same ID, including the explicit default fallback.
        if(selected.Length>0&&!list.Any(x=>x.Id==selected))
            list.Add(new(selected,"已保存的麦克风不可用（临时使用 Windows 默认通信麦克风）"));
        DeviceBox.ItemsSource=list;DeviceBox.SelectedValue=selected;
    }
    private async void Devices_Click(object sender,RoutedEventArgs e)=>await Safe(DevicesRefresh);
    private async void TestMicrophone_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        EnsureIdle();microphoneTesting=true;bool enabled=ptt?.Enabled==true;ptt?.SetEnabled(false);TestMicrophoneButton.IsEnabled=false;
        try
        {
            string id=DeviceBox.SelectedValue as string??"";
            StatusText.Text="本地麦克风测试：请说话，3 秒后结束。音频不会上传。";
            string result=await controller.TestMicrophoneAsync(id);
            StatusText.Text=result;await Dialogs.TextAsync(this,"麦克风测试",result+"\n\n"+controller.Diagnostic);
        }
        finally{TestMicrophoneButton.IsEnabled=true;microphoneTesting=false;if(enabled)ptt?.SetEnabled(true);}
    });
    private async void Diagnostics_Click(object sender,RoutedEventArgs e)=>await Dialogs.TextAsync(this,"录音诊断（可复制）",controller.Diagnostic);

    private async Task SaveSettings()
    {
        EnsureIdle();savingSettings=true;
        try
        {
        int Parse(TextBox box,string name){if(!int.TryParse(box.Text.Trim(),out int value))throw new ArgumentException(name+"应为整数。");return value;}
        var s=controller.Settings with{SchemaVersion=4,AutoCheckUpdates=AutoCheckUpdateBox.IsChecked==true,AutoDownloadUpdates=AutoDownloadUpdateBox.IsChecked==true,AutoOpenUpdateInstaller=AutoInstallUpdateBox.IsChecked==true,Region=RegionBox.SelectedIndex==0?"cn-beijing":"ap-southeast-1",WorkspaceId=WorkspaceBox.Text.Trim(),LegacyEndpoint=LegacyBox.IsChecked==true,DeviceId=DeviceBox.SelectedValue as string??"",SmartPunctuationEnabled=SmartPunctuationBox.IsChecked==true,AdaptiveAsrEnabled=AdaptiveAsrBox.IsChecked==true,PolishEnabled=PolishBox.IsChecked==true,PreferFastDelivery=FastDeliveryBox.IsChecked==true,PolishPrompt=PolishRules.ResolvePrompt(PolishPromptBox.Text),GenerationRequirement=GenerationRequirementBox.Text.Trim(),GenerationCount=Parse(GenerationCountBox,"目标词数"),GenerationGlobal=GenerationScopeBox.SelectedIndex==1,GenerationMaxThinking=GenerationMaxThinkingBox.IsChecked==true,PreviousContext=PreviousBox.IsChecked==true,SaveMemory=MemoryBox.IsChecked==true,AllowLearning=LearningBox.IsChecked==true,LearnCorrections=CorrectionLearningBox.IsChecked==true,UseLexicon=UseTermsBox.IsChecked==true,DynamicLexicon=DynamicTermsBox.IsChecked==true,AutoExtract=AutoExtractBox.IsChecked==true,AsrContext=AsrContextBox.IsChecked==true,AutoParagraph=ParagraphBox.IsChecked==true,CloseToTray=TrayBox.IsChecked==true,RetentionDays=string.IsNullOrWhiteSpace(RetentionBox.Text)?null:Parse(RetentionBox,"保留天数"),DailyExtractionTokens=Parse(BudgetBox,"整理预算"),SilenceMs=Parse(SilenceBox,"断句停顿"),HoldMs=Parse(HoldBox,"长按阈值"),MaxHoldSeconds=Parse(MaxHoldBox,"最长按住时间"),Hotkey=HotkeyBox.SelectedIndex switch{1=>"F8",2=>"F9",_=>"RightCtrl"}};
        await controller.SaveSettingsAsync(s,new(BailianKeyBox.Password.Trim(),DeepSeekKeyBox.Password.Trim()));ptt?.Configure();StatusText.Text="设置已保存。";
        }
        finally{savingSettings=false;}
    }
    private async void SettingsSave_Click(object sender,RoutedEventArgs e)=>await Safe(SaveSettings);
    private async void RetrySave_Click(object sender,RoutedEventArgs e)=>await Manage(async()=>{await controller.RetrySaveAsync();});
    private async Task TestConnectionAsync(bool bailian)
    {
        await SaveSettings();testingConnection=true;
        try
        {
            StatusText.Text=bailian?"正在测试百炼…":"正在测试 DeepSeek…";
            if(bailian)await controller.TestBailianAsync();else await controller.TestDeepSeekAsync();
            StatusText.Text=bailian?"百炼短任务测试完成。":"DeepSeek 请求测试完成。";
        }
        finally{testingConnection=false;}
    }
    private async void TestBailian_Click(object sender,RoutedEventArgs e)=>await Safe(()=>TestConnectionAsync(true));
    private async void TestDeepSeek_Click(object sender,RoutedEventArgs e)=>await Safe(()=>TestConnectionAsync(false));
    private async void DeleteProject_Click(object sender,RoutedEventArgs e)=>await Manage(async()=>{if(!await Dialogs.ConfirmAsync(this,"删除项目","删除当前项目及其全部记忆和项目词条？全局手动词条保留。","删除项目"))return;await controller.DeleteCurrentProjectAsync();ProjectsRefresh();});
    private async void Usage_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{var usage=await controller.Repository.UsageAsync();UsageBox.Text=usage.Count==0?"尚无用量记录。":string.Join("\n",usage.Select(u=>$"{u.At:yyyy-MM-dd} · {u.Purpose} · 输入 {u.InputTokens:N0} / 输出 {u.OutputTokens:N0} Token · ASR {u.AudioSeconds:F1} 秒"+(u.Unknown?" · 包含未知用量":"")))+"\n\nterm_budget 是预算占用，已知用量会结算，未知请求保留预留；不与 term_extraction / term_generation 相加作为账单。以服务商账单为准。";});
    private void PowerChanged(object sender,PowerModeChangedEventArgs e){controller.Log.Write("Application","PowerChanged",fields:new Dictionary<string,object?>{["mode"]=e.Mode});if(e.Mode==PowerModes.Suspend){ptt?.Cancel("系统睡眠，自动输入已取消。");controller.Suspend();}}
    private void SessionChanged(object sender,SessionSwitchEventArgs e){controller.Log.Write("Application","SessionChanged",fields:new Dictionary<string,object?>{["reason"]=e.Reason});if(e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect){ptt?.Cancel("Windows 会话已锁定或断开，自动输入已取消。");controller.Suspend();}}
    private async void ClosingWindow(AppWindow sender,AppWindowClosingEventArgs e)
    {
        e.Cancel=true;
        await RequestExitAsync(false);
    }
    private async Task RequestExitAsync(bool explicitExit)
    {
        if(closed||shuttingDown||closingPrompt)return;
        if(Dialogs.IsOpen||pickerOpen){StatusText.Text="请先关闭当前对话框。";return;}
        if(!explicitExit&&controller.Settings.CloseToTray&&tray!=null){Hide();return;}
        if(controller.FailedSaveCount>0)OpenManager();
        closingPrompt=true;
        try
        {
            if(controller.FailedSaveCount>0&&!await Dialogs.ConfirmAsync(this,"仍有正文未保存",$"仍有 {controller.FailedSaveCount} 项保存失败。退出后未保存的部分可能无法恢复。\n取消可返回重试保存、复制或导出正文。","继续退出"))return;
        }
        finally{closingPrompt=false;}
        shuttingDown=true;ready=false;updateTimer.Stop();updateCancellation?.Cancel();exportCancellation?.Cancel();generationCancel?.Cancel();promptPreviewCancel?.Cancel();controller.CancelGeneration();controller.CancelExtraction();
        ptt?.SetEnabled(false);StatusText.Text="正在退出并保存…";
        controller.Log.Write("Application","ShutdownRequested");
        try{async Task Clean(){if(ptt!=null)await ptt.DisposeAsync();await controller.DisposeAsync();}await Clean().WaitAsync(TimeSpan.FromSeconds(12));}catch(Exception error){controller.Log.Write("Application","ShutdownCleanupFailed",exception:error);}
        search?.Cancel();render.Stop();SystemEvents.PowerModeChanged-=PowerChanged;SystemEvents.SessionSwitch-=SessionChanged;tray?.Dispose();trayMenu?.Dispose();trayIcon?.Dispose();devices?.Dispose();overlay.Close();closed=true;Microsoft.UI.Xaml.Application.Current.Exit();
    }
}
