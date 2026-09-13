using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
    private System.Drawing.Icon? trayIcon;
    private DeviceWatcher? devices;
    private TranscriptSnapshot? pending;
    private CancellationTokenSource? search;
    private bool ready,updating,exiting,closed,shuttingDown,microphoneTesting;
    private bool savingSettings,changingProject,changingLearning,updatingLearning;
    private bool ManagementBusy=>correctionBusy||extractingHistory||generatingTerms||importingGenerated||testingPrompt||microphoneTesting||savingSettings||changingProject||changingLearning||ptt?.Busy==true;
    private float level;
    public string CurrentProject=>controller.Settings.ProjectId;
    public MainWindow(AppController controller)
    {
        this.controller=controller;InitializeComponent();
        controller.Updated+=snapshot=>Volatile.Write(ref pending,snapshot);
        controller.TermsUpdated+=()=>UI(RefreshTerms);controller.Level+=v=>Volatile.Write(ref level,v);
        controller.Message+=m=>UI(()=>StatusText.Text=m);
        controller.CorrectionsUpdated+=()=>UI(async()=>{if(!shuttingDown)await Safe(RefreshCorrections);});
        controller.Extracting+=v=>UI(()=>{ExtractButton.IsEnabled=!v;AllHistoryButton.IsEnabled=HistoryExtractAllButton.IsEnabled=!v;ExtractButton.Content=v?"正在整理…":"整理当前会话词条";});
        render.Tick+=(_,_)=>Render();render.Start();Closing+=ClosingWindow;
        SystemEvents.PowerModeChanged+=PowerChanged;SystemEvents.SessionSwitch+=SessionChanged;
    }
    private void UI(Action fn){if(!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(fn);}
    public async void Ready()
    {
        try
        {
            await FillSettings();ProjectsRefresh();RefreshTerms();await RefreshCorrections();CreateTray();
            ptt=new(controller);ptt.Notice+=text=>UI(()=>{overlay.Update(text,dismiss:true);StatusText.Text=text;});
            ptt.Listening+=active=>UI(()=>{if(active)overlay.Update("准备麦克风…");if(tray!=null)tray.Text=active?"语音输入法 · 正在录音":"语音输入法 · 按住说话";});
            await ptt.InitializeAsync();
            devices=new DeviceWatcher((id,isDefault)=>{if(ptt.Busy&&((isDefault&&controller.Settings.DeviceId=="")||(!isDefault&&controller.ActiveDeviceId==id))){ptt.Cancel("麦克风设备已变化，本轮停止，确认文字可复制。");controller.RequestStopCapture();}});ready=true;
            if(controller.Keys.BailianKey.Length==0)Tabs.SelectedIndex=3;
            else{Hide();tray?.ShowBalloonTip(2500,"语音输入法已就绪","在文本框中按住右侧 Ctrl 说话，松开输入。",Forms.ToolTipIcon.Info);}
            pending=await controller.SnapshotAsync();
        }
        catch(Exception e){ready=true;StatusText.Text=AppController.SafeError(e);MessageBox.Show(this,AppController.SafeError(e),"启动",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private void Render()
    {
        ProjectBox.IsEnabled=!ManagementBusy;
        SessionLearningBox.IsEnabled=!ManagementBusy&&SessionLearningBox.Tag is string;
        var s=Interlocked.Exchange(ref pending,null);MicLevel.Value=Volatile.Read(ref level);if(s==null)return;
        string body=TranscriptText.Render(s);if(OutputBox.Text!=body)OutputBox.Text=body;
        string preview=string.Join(" ",s.Segments.Where(x=>x.AsrState==AsrState.Partial).Select(x=>x.PartialText));
        PreviewBox.Text=preview;BodyCount.Text=$"本轮正文 · {JsonCodec.Count(body)} 字";StatusText.Text=s.Status;
        StateLabel.Text=s.State switch{CaptureState.Connecting=>"准备 / 连接",CaptureState.Recording=>"正在听",CaptureState.Draining=>"尾句处理中",CaptureState.Faulted=>"需处理",_=>ptt?.Busy==true?"整理 / 输入":ptt?.Enabled==false?"快捷键已暂停":"按住说话已就绪"};
        SaveLabel.Text=$"待润色 {s.Pending} · 未确认 {s.Segments.Count(x=>x.AsrState==AsrState.Unresolved)} · 未保存 {s.Unsaved} · {s.Session?.DeliveryReason}";
        updatingLearning=true;
        SessionLearningBox.Tag=s.Session?.Id;SessionLearningBox.IsChecked=s.Session?.AllowLearning==true;SessionLearningBox.IsEnabled=s.Session!=null&&!ManagementBusy;
        updatingLearning=false;
        if(ptt?.Busy==true)overlay.Update(s.Status,preview.Length>0?JsonCodec.Take(preview,120):JsonCodec.Take(body,120));
    }
    private void CreateTray()
    {
        using(var stream=System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico")).Stream)
        using(var embedded=new System.Drawing.Icon(stream,32,32))trayIcon=(System.Drawing.Icon)embedded.Clone();
        tray=new Forms.NotifyIcon{Icon=trayIcon,Text="语音输入法 · 按住说话",Visible=true};var menu=new Forms.ContextMenuStrip();
        menu.Items.Add("打开管理",null,(_,_)=>UI(OpenManager));menu.Items.Add("启用 / 暂停按住说话",null,(_,_)=>UI(()=>ptt?.SetEnabled(ptt?.Enabled!=true)));
        menu.Items.Add("复制最近结果",null,(_,_)=>UI(async()=>await Safe(CopyCurrent)));menu.Items.Add("设置",null,(_,_)=>UI(()=>{OpenManager();Tabs.SelectedIndex=3;}));
        menu.Items.Add(new Forms.ToolStripSeparator());menu.Items.Add("退出",null,(_,_)=>UI(()=>{exiting=true;Close();}));tray.ContextMenuStrip=menu;tray.DoubleClick+=(_,_)=>UI(OpenManager);
    }
    private void OpenManager(){Show();WindowState=WindowState.Normal;Activate();}
    private void EnsureIdle(){if(correctionBusy)throw new InvalidOperationException("纠错学习操作尚未完成，请稍候。");if(savingSettings||changingProject||changingLearning)throw new InvalidOperationException("设置或项目切换正在保存，请稍候。");if(extractingHistory)throw new InvalidOperationException("全部历史词条提取尚未结束，请先取消或等待完成。");if(generatingTerms||importingGenerated)throw new InvalidOperationException("词库生成或导入尚未结束，请稍候或先取消生成。");if(testingPrompt)throw new InvalidOperationException("提示词试用尚未结束，请稍候。");if(microphoneTesting)throw new InvalidOperationException("麦克风测试尚未结束，请稍候。");if(ptt?.Busy==true)throw new InvalidOperationException("当前输入尚未完成，请松开快捷键并等待结果后再操作。");}
    private async Task Safe(Func<Task> fn){try{await fn();}catch(OperationCanceledException){StatusText.Text="操作已取消。";}catch(Exception e){MessageBox.Show(this,AppController.SafeError(e),"语音输入法",MessageBoxButton.OK,MessageBoxImage.Information);}}
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
    private async void NewProject_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{EnsureIdle();string? name=Dialogs.Ask(this,"新建项目","项目名称");if(name!=null){await controller.CreateProjectAsync(name);ProjectsRefresh();}});
    private void Start_Click(object sender,RoutedEventArgs e){ptt?.SetEnabled(true);StatusText.Text="请在其他应用的文本框中按住说话键。";}
    private void Pause_Click(object sender,RoutedEventArgs e)=>ptt?.SetEnabled(false);
    private void Stop_Click(object sender,RoutedEventArgs e)=>ptt?.Cancel();
    private async void Paragraph_Click(object sender,RoutedEventArgs e)=>await Safe(()=>controller.ParagraphAsync());
    private async void Review_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{EnsureIdle();await Dialogs.Review(this,controller);});
    private async Task ClipboardAsync(string text)
    {
        if(text.Length==0){StatusText.Text="当前没有可复制的正文。";return;}
        for(int i=0;;i++){try{System.Windows.Clipboard.SetText(text);StatusText.Text="纯正文已复制。";return;}catch(System.Runtime.InteropServices.COMException)when(i<2){await Task.Delay(100*(i+1));}}
    }
    private async Task CopyCurrent()=>await ClipboardAsync(TranscriptText.Render(await controller.SnapshotAsync()));
    private async void Copy_Click(object sender,RoutedEventArgs e)=>await Safe(CopyCurrent);
    private async void StopCopy_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{ptt?.Cancel("已取消自动输入，将复制确认文字。");await ClipboardAsync(await controller.StopAndTextAsync());});
    private async Task ExportBody(string extension)
    {
        string text=TranscriptText.Render(await controller.SnapshotAsync());if(text.Length==0)throw new InvalidOperationException("当前没有可导出的正文。");
        var file=new SaveFileDialog{FileName="语音输入_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+extension,Filter=extension==".md"?"Markdown|*.md":"文本文件|*.txt"};
        if(file.ShowDialog(this)==true){await File.WriteAllTextAsync(file.FileName,text,new UTF8Encoding(false));StatusText.Text="纯正文已导出。";}
    }
    private async void ExportTxt_Click(object sender,RoutedEventArgs e)=>await Safe(()=>ExportBody(".txt"));
    private async void ExportMd_Click(object sender,RoutedEventArgs e)=>await Safe(()=>ExportBody(".md"));
    private async void HistorySearch_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        search?.Cancel();search=new();var current=search;string? project=AllProjects.IsChecked==true?null:CurrentProject;
        DateTimeOffset? since=HistoryDays.SelectedIndex switch{1=>DateTimeOffset.UtcNow.AddDays(-7),2=>DateTimeOffset.UtcNow.AddDays(-30),_=>null};HistoryStatus.Text="正在检索…";
        var hits=await controller.Repository.SearchAsync(project,HistoryQuery.Text.Trim(),since,current.Token);if(search!=current)return;
        HistoryGrid.ItemsSource=hits;HistoryStatus.Text=$"找到 {hits.Count} 条记录"+(hits.Count==500?"（最多显示最近 500 条，请缩小筛选范围）":"");
    });
    private void HistoryCancel_Click(object sender,RoutedEventArgs e)=>search?.Cancel();
    private async void HistoryOpen_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{EnsureIdle();if(HistoryGrid.SelectedItem is MemoryHit hit){await controller.LoadSessionAsync(hit.Session);ProjectsRefresh();Tabs.SelectedIndex=0;}});
    private async void HistoryExport_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        if(HistoryGrid.SelectedItem is not MemoryHit hit)return;var file=new SaveFileDialog{FileName="语音输入记录.json",Filter="记忆记录 JSON|*.json"};if(file.ShowDialog(this)!=true)return;
        await controller.Repository.BarrierAsync();
        var latest=await controller.Repository.LoadSessionAsync(hit.Session.Id)??throw new InvalidOperationException("该历史记录已删除，请刷新列表。");
        await File.WriteAllTextAsync(file.FileName,JsonSerializer.Serialize(new{schemaVersion=3,session=latest.Session,segments=latest.Segments},new JsonSerializerOptions(JsonCodec.Options){WriteIndented=true}),new UTF8Encoding(false));
    });
    private async void HistoryDelete_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{EnsureIdle();if(HistoryGrid.SelectedItem is not MemoryHit hit)return;if(MessageBox.Show(this,"清除此记录及其派生词条来源？此操作不能撤销，外部文档不受影响。","清除记忆",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;await controller.DeleteSessionAsync(hit.Session);HistorySearch_Click(sender,e);});
    private void RefreshTerms()
    {
        if(TermsGrid==null||TermFilter==null)return;string query=TermQuery.Text.Trim();var items=controller.Terms.Where(t=>t.Text.Contains(query,StringComparison.OrdinalIgnoreCase)&&(TermFilter.SelectedIndex switch{1=>t.State==TermState.Enabled,2=>t.State==TermState.Candidate,3=>t.State==TermState.Disabled,_=>true})).OrderByDescending(t=>t.Pinned).ThenBy(t=>t.State).ThenBy(t=>t.Text).ToArray();
        RefreshGeneratedMatches();TermsGrid.ItemsSource=items;TermsCount.Text=$"当前显示 {items.Length} 个 · 全局及本项目 {controller.Terms.Count} 个 · 下一轮最多选取 200 个已启用热词";
    }
    private void TermFilter_Changed(object sender,RoutedEventArgs e){if(ready)RefreshTerms();}
    private async void TermAdd_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{var term=Dialogs.Term(this,new(){Scope=CurrentProject});if(term!=null)await controller.SaveTermAsync(term);});
    private async void TermEdit_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{if(TermsGrid.SelectedItem is TermData old){var term=Dialogs.Term(this,old);if(term!=null)await controller.SaveTermAsync(term);}});
    private async void TermConfirm_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{foreach(var term in TermsGrid.SelectedItems.Cast<TermData>().ToArray())await controller.SaveTermAsync(term with{State=TermState.Enabled});});
    private async void TermDisable_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{foreach(var term in TermsGrid.SelectedItems.Cast<TermData>().ToArray())await controller.SaveTermAsync(term with{State=TermState.Disabled});});
    private async void TermDelete_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{var selected=TermsGrid.SelectedItems.Cast<TermData>().ToArray();if(selected.Length==0)return;if(MessageBox.Show(this,$"删除选中的 {selected.Length} 个词条？","词库",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;foreach(var term in selected)await controller.DeleteTermAsync(term);});
    private void TermEvidence_Click(object sender,RoutedEventArgs e){if(TermsGrid.SelectedItem is TermData term)Dialogs.Text(this,"词条来源",$"来源类型：{(term.Origin=="AiGenerated"?"AI 领域生成":term.Origin=="CorrectionLearning"?"纠错学习（来源见词库的纠错学习页）":term.Origin)}\n旧写法：{term.Alias}\n"+(term.GenerationRequirement.Length>0?$"生成要求：{term.GenerationRequirement}\n":"")+"\n"+(term.Evidence.Count==0?"此词条没有自动提取证据。":string.Join("\n\n",term.Evidence.Select(v=>$"会话 {v.SessionId}\n片段 {v.SegmentId} · 原文版本 {v.SourceRevision} · 编辑版本 {v.EditRevision}\n{v.Quote}"))));}
    private async void TermImport_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        EnsureIdle();var file=new OpenFileDialog{Filter="词库文件|*.txt;*.csv;*.json"};if(file.ShowDialog(this)!=true)return;if(new FileInfo(file.FileName).Length>5*1024*1024)throw new ArgumentException("导入文件最多 5 MiB。");
        var terms=TermExchange.Parse(await File.ReadAllTextAsync(file.FileName),Path.GetExtension(file.FileName),CurrentProject);
        if(MessageBox.Show(this,$"已校验 {terms.Count} 个词条，将按文件状态导入；相同范围的重名项会跳过。继续？","导入词库",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        int count=await controller.ImportTermsAsync(terms);StatusText.Text=$"已导入 {count} 个词条，重复项未覆盖。";
    });
    private async void TermExport_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{var file=new SaveFileDialog{FileName="个人词库.json",Filter="词库 JSON|*.json"};if(file.ShowDialog(this)==true)await File.WriteAllTextAsync(file.FileName,TermExchange.Export(controller.Terms),new UTF8Encoding(false));});
    private async void Extract_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{EnsureIdle();await controller.ExtractAsync();});
    private void ExtractCancel_Click(object sender,RoutedEventArgs e)=>controller.CancelExtraction();
    private async Task FillSettings()
    {
        var s=controller.Settings;BailianKeyBox.Password=controller.Keys.BailianKey;DeepSeekKeyBox.Password=controller.Keys.DeepSeekKey;WorkspaceBox.Text=s.WorkspaceId;RegionBox.SelectedIndex=s.Region=="cn-beijing"?0:1;LegacyBox.IsChecked=s.LegacyEndpoint;
        PolishPromptBox.Text=s.EffectivePolishPrompt;GenerationRequirementBox.Text=s.GenerationRequirement;GenerationCountBox.Text=s.GenerationCount.ToString();GenerationScopeBox.SelectedIndex=s.GenerationGlobal?1:0;
        PolishBox.IsChecked=s.PolishEnabled;PreviousBox.IsChecked=s.PreviousContext;MemoryBox.IsChecked=s.SaveMemory;LearningBox.IsChecked=s.AllowLearning;CorrectionLearningBox.IsChecked=s.LearnCorrections;UseTermsBox.IsChecked=s.UseLexicon;AutoExtractBox.IsChecked=s.AutoExtract;AsrContextBox.IsChecked=s.AsrContext;
        GenerationMaxThinkingBox.IsChecked=s.GenerationMaxThinking;ParagraphBox.IsChecked=s.AutoParagraph;TrayBox.IsChecked=s.CloseToTray;RetentionBox.Text=s.RetentionDays?.ToString()??"";BudgetBox.Text=s.DailyExtractionTokens.ToString();SilenceBox.Text=s.SilenceMs.ToString();HoldBox.Text=s.HoldMs.ToString();MaxHoldBox.Text=s.MaxHoldSeconds.ToString();HotkeyBox.SelectedIndex=s.Hotkey switch{"F8"=>1,"F9"=>2,_=>0};await DevicesRefresh();
    }
    private async Task DevicesRefresh(){var list=await Task.Run(AudioCapture.Devices);DeviceBox.ItemsSource=list;DeviceBox.SelectedValue=list.Any(x=>x.Id==controller.Settings.DeviceId)?controller.Settings.DeviceId:"";}
    private async void Devices_Click(object sender,RoutedEventArgs e)=>await Safe(DevicesRefresh);
    private async void TestMicrophone_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        EnsureIdle();microphoneTesting=true;bool enabled=ptt?.Enabled==true;ptt?.SetEnabled(false);TestMicrophoneButton.IsEnabled=false;
        try
        {
            string id=DeviceBox.SelectedValue as string??"";
            StatusText.Text="本地麦克风测试：请说话，3 秒后结束。音频不会上传。";
            string result=await controller.TestMicrophoneAsync(id);
            StatusText.Text=result;Dialogs.Text(this,"麦克风测试",result+"\n\n"+controller.Diagnostic);
        }
        finally{TestMicrophoneButton.IsEnabled=true;microphoneTesting=false;if(enabled)ptt?.SetEnabled(true);}
    });
    private void Diagnostics_Click(object sender,RoutedEventArgs e)=>Dialogs.Text(this,"录音诊断（可复制）",controller.Diagnostic);

    private async Task SaveSettings()
    {
        EnsureIdle();savingSettings=true;
        try
        {
        int Parse(TextBox box,string name){if(!int.TryParse(box.Text.Trim(),out int value))throw new ArgumentException(name+"应为整数。");return value;}
        var s=controller.Settings with{SchemaVersion=4,Region=RegionBox.SelectedIndex==0?"cn-beijing":"ap-southeast-1",WorkspaceId=WorkspaceBox.Text.Trim(),LegacyEndpoint=LegacyBox.IsChecked==true,DeviceId=DeviceBox.SelectedValue as string??"",PolishEnabled=PolishBox.IsChecked==true,PolishPrompt=PolishRules.ResolvePrompt(PolishPromptBox.Text),GenerationRequirement=GenerationRequirementBox.Text.Trim(),GenerationCount=Parse(GenerationCountBox,"目标词数"),GenerationGlobal=GenerationScopeBox.SelectedIndex==1,GenerationMaxThinking=GenerationMaxThinkingBox.IsChecked==true,PreviousContext=PreviousBox.IsChecked==true,SaveMemory=MemoryBox.IsChecked==true,AllowLearning=LearningBox.IsChecked==true,LearnCorrections=CorrectionLearningBox.IsChecked==true,UseLexicon=UseTermsBox.IsChecked==true,AutoExtract=AutoExtractBox.IsChecked==true,AsrContext=AsrContextBox.IsChecked==true,AutoParagraph=ParagraphBox.IsChecked==true,CloseToTray=TrayBox.IsChecked==true,RetentionDays=string.IsNullOrWhiteSpace(RetentionBox.Text)?null:Parse(RetentionBox,"保留天数"),DailyExtractionTokens=Parse(BudgetBox,"整理预算"),SilenceMs=Parse(SilenceBox,"断句停顿"),HoldMs=Parse(HoldBox,"长按阈值"),MaxHoldSeconds=Parse(MaxHoldBox,"最长按住时间"),Hotkey=HotkeyBox.SelectedIndex switch{1=>"F8",2=>"F9",_=>"RightCtrl"}};
        await controller.SaveSettingsAsync(s,new(BailianKeyBox.Password.Trim(),DeepSeekKeyBox.Password.Trim()));ptt?.Configure();StatusText.Text="设置已保存。";
        }
        finally{savingSettings=false;}
    }
    private async void SettingsSave_Click(object sender,RoutedEventArgs e)=>await Safe(SaveSettings);
    private async void RetrySave_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{EnsureIdle();await controller.RetrySaveAsync();});
    private async void TestBailian_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{await SaveSettings();StatusText.Text="正在测试百炼…";await controller.TestBailianAsync();StatusText.Text="百炼短任务测试完成。";});
    private async void TestDeepSeek_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{await SaveSettings();StatusText.Text="正在测试 DeepSeek…";await controller.TestDeepSeekAsync();StatusText.Text="DeepSeek 请求测试完成。";});
    private async void DeleteProject_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{EnsureIdle();if(MessageBox.Show(this,"删除当前项目及其全部记忆和项目词条？全局手动词条保留。","删除项目",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;await controller.DeleteCurrentProjectAsync();ProjectsRefresh();});
    private async void Usage_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>{var usage=await controller.Repository.UsageAsync();UsageBox.Text=usage.Count==0?"尚无用量记录。":string.Join("\n",usage.Select(u=>$"{u.At:yyyy-MM-dd} · {u.Purpose} · 输入 {u.InputTokens:N0} / 输出 {u.OutputTokens:N0} Token · ASR {u.AudioSeconds:F1} 秒"+(u.Unknown?" · 包含未知用量":"")))+"\n\nterm_budget 是预算占用，已知用量会结算，未知请求保留预留；不与 term_extraction / term_generation 相加作为账单。以服务商账单为准。";});
    private void PowerChanged(object sender,PowerModeChangedEventArgs e){if(e.Mode==PowerModes.Suspend){ptt?.Cancel("系统睡眠，自动输入已取消。");controller.Suspend();}}
    private void SessionChanged(object sender,SessionSwitchEventArgs e){if(e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect){ptt?.Cancel("Windows 会话已锁定或断开，自动输入已取消。");controller.Suspend();}}
    private async void ClosingWindow(object? sender,CancelEventArgs e)
    {
        if(closed)return;e.Cancel=true;if(!exiting&&controller.Settings.CloseToTray){Hide();return;}if(shuttingDown)return;shuttingDown=true;ready=false;generationCancel?.Cancel();promptPreviewCancel?.Cancel();controller.CancelGeneration();controller.CancelExtraction();
        ptt?.SetEnabled(false);StatusText.Text="正在退出并保存…";
        try{async Task Clean(){if(ptt!=null)await ptt.DisposeAsync();await controller.DisposeAsync();}await Clean().WaitAsync(TimeSpan.FromSeconds(12));}catch{}
        search?.Cancel();render.Stop();SystemEvents.PowerModeChanged-=PowerChanged;SystemEvents.SessionSwitch-=SessionChanged;tray?.Dispose();trayIcon?.Dispose();devices?.Dispose();overlay.Close();closed=true;System.Windows.Application.Current.Shutdown();
    }
}
