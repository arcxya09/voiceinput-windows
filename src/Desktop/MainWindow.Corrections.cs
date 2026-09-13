using System.Windows;
using System.Windows.Controls;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Desktop;

public partial class MainWindow
{
    private bool correctionBusy;
    private int correctionRefresh;
    private async Task RefreshCorrections()
    {
        if(CorrectionsGrid==null||shuttingDown)return;
        if(!controller.MemoryAvailable)
        {
            ++correctionRefresh;CorrectionsGrid.ItemsSource=Array.Empty<CorrectionCandidate>();
            CorrectionTabLabel.Text="纠错学习";CorrectionStatus.Text=controller.MemoryStatus;
            return;
        }
        int serial=++correctionRefresh;string project=CurrentProject;string? selection=(CorrectionsGrid.SelectedItem as CorrectionCandidate)?.Id;
        var all=await controller.Repository.CorrectionsAsync(project);
        if(serial!=correctionRefresh||CurrentProject!=project||shuttingDown)return;
        int filter=CorrectionFilter.SelectedIndex;string search=CorrectionQuery.Text.Trim();
        var rows=all.Where(c=>(filter==3||(filter==0&&c.State==CorrectionState.Pending)||(filter==1&&c.State==CorrectionState.Learned)||(filter==2&&c.State==CorrectionState.Ignored))
            &&(c.Original.Contains(search,StringComparison.OrdinalIgnoreCase)||c.Corrected.Contains(search,StringComparison.OrdinalIgnoreCase)||c.LearnedText.Contains(search,StringComparison.OrdinalIgnoreCase))).ToArray();
        CorrectionsGrid.ItemsSource=rows;CorrectionsGrid.SelectedItem=rows.FirstOrDefault(c=>c.Id==selection);
        int pending=all.Count(c=>c.State==CorrectionState.Pending);
        CorrectionTabLabel.Text=pending>0?$"纠错学习 · {pending}":"纠错学习";
        CorrectionStatus.Text=$"本项目待确认 {pending} 个 · 已学习 {all.Count(c=>c.State==CorrectionState.Learned)} 个 · 当前显示 {rows.Length} 个。"+(all.Count>=5000?" 已达记录上限，请整理或清除不再需要的来源项目。":"");
        CorrectionHint.Text=!controller.Settings.LearnCorrections?"纠错学习已暂停，可在设置中开启。":!controller.Settings.SaveMemory||!controller.Settings.AllowLearning?"请启用文本记忆和词条整理，保存手动修订后才会发现候选。":"保存手动修订后自动发现候选；确认前可调整词条边界。次数按不同片段统计，三次及以上标记为“多次纠正”。";
    }
    private async void CorrectionFilter_Changed(object sender,RoutedEventArgs e){if(ready)await Safe(RefreshCorrections);}
    private async void CorrectionRefresh_Click(object sender,RoutedEventArgs e)=>await Safe(RefreshCorrections);
    private async Task WithCorrection(Func<CorrectionCandidate,Task> action)
    {
        EnsureIdle();if(CorrectionsGrid.SelectedItem is not CorrectionCandidate candidate)throw new InvalidOperationException("请先选择一条纠错记录。");
        correctionBusy=true;CorrectionActions.IsEnabled=false;
        try{await action(candidate);}
        finally{correctionBusy=false;CorrectionActions.IsEnabled=true;await RefreshCorrections();}
    }
    private async void CorrectionLearn_Click(object sender,RoutedEventArgs e)=>await Safe(()=>WithCorrection(async candidate=>
    {
        if(candidate.State!=CorrectionState.Pending)throw new InvalidOperationException("请选择待确认项。已忽略项可先恢复候选。");
        var evidence=await controller.Repository.CorrectionEvidenceAsync(candidate.Id);
        var approval=CorrectionDialog.Show(this,candidate,evidence);
        if(approval!=null)await controller.ConfirmCorrectionAsync(candidate,approval);
    }));
    private async void CorrectionIgnore_Click(object sender,RoutedEventArgs e)=>await Safe(()=>WithCorrection(c=>controller.SetCorrectionIgnoredAsync(c,true)));
    private async void CorrectionRestore_Click(object sender,RoutedEventArgs e)=>await Safe(()=>WithCorrection(c=>controller.SetCorrectionIgnoredAsync(c,false)));
    private async void CorrectionRevoke_Click(object sender,RoutedEventArgs e)=>await Safe(()=>WithCorrection(c=>controller.RevokeCorrectionAsync(c)));
    private async void CorrectionEvidence_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        if(CorrectionsGrid.SelectedItem is not CorrectionCandidate c)return;
        var sources=await controller.Repository.CorrectionEvidenceAsync(c.Id);
        Dialogs.Text(this,"纠错来源",$"{c.Original} → {c.DisplayText}\n状态：{c.StateLabel} · 不同片段：{c.Count}\n自动纠正：{c.ReplacementLabel}\n{c.ReplacementReason}\n\n"+
            (sources.Count==0?"原始来源已删除、撤销或关闭学习许可。已确认词条和忽略偏好仍可单独管理。":string.Join("\n\n",sources.Select(s=>$"{s.At.LocalDateTime:g}\n修改前：{s.BeforeContext}\n修改后：{s.AfterContext}\n会话：{s.SessionId}\n片段：{s.SegmentId} · 版本：{s.EditRevision}")))+
            (c.Count>30?"\n\n仅显示最近 30 个来源，统计包含全部有效来源。":""));
    });
    public void OpenCorrections(){Tabs.SelectedIndex=2;VocabularyTabs.SelectedIndex=1;}
}

internal static class CorrectionDialog
{
    public static CorrectionApproval? Show(Window owner,CorrectionCandidate candidate,IReadOnlyList<CorrectionEvidence> evidence)
    {
        var win=new Window{Owner=owner,Title="确认纠错学习",Width=680,MaxHeight=780,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var panel=new StackPanel{Margin=new Thickness(22)};win.Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        panel.Children.Add(new TextBlock{Text=$"已在 {candidate.Count} 个片段中纠正这组写法。请检查标准词条，再确认学习。",TextWrapping=TextWrapping.Wrap});
        TextBox Field(string label,string value,int max){panel.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,14,0,6)});var box=new TextBox{Text=value,MaxLength=max};panel.Children.Add(box);return box;}
        var correct=Field("标准写法（可修改，1—64 字）",candidate.Corrected,128);
        var original=Field("旧写法（请核对自动纠正的来源写法）",candidate.Original,128);
        var category=Field("类别", "专业术语",32);
        var global=new CheckBox{Content="加入全局词库（默认只用于本项目）"};panel.Children.Add(global);
        var replace=new CheckBox{Content="自动纠正这组已确认写法（保留原始识别文本）",IsChecked=true};panel.Children.Add(replace);
        var weight=Field("识别权重（1—5）",candidate.Count>=3?"5":"4",1);
        if(evidence.FirstOrDefault() is {} sample)
        {
            panel.Children.Add(new TextBlock{Text="最近一次修改",Margin=new Thickness(0,14,0,6)});
            panel.Children.Add(new TextBox{IsReadOnly=true,Text=$"修改前：{sample.BeforeContext}\n\n修改后：{sample.AfterContext}",TextWrapping=TextWrapping.Wrap,AcceptsReturn=true,MaxHeight=150,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        }
        panel.Children.Add(new TextBlock{Text="确认后优先加入识别热词，并在润色时保护标准写法。同名词条合并，保留原有分类；禁用词条需先在词库启用。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,14)});
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};panel.Children.Add(actions);
        var cancel=new Button{Content="取消",IsCancel=true};actions.Children.Add(cancel);var confirm=new Button{Content="确认学习",IsDefault=true};actions.Children.Add(confirm);
        CorrectionApproval? result=null;
        confirm.Click+=(_,_)=>
        {
            try
            {
                if(!int.TryParse(weight.Text,out int level))throw new ArgumentException("识别权重应为 1—5。");
                var term=new TermData{Text=correct.Text.Trim(),Alias=original.Text.Trim(),Weight=level};term.Validate();
                result=new(term.Text,term.Alias,category.Text.Trim(),global.IsChecked==true?"*":candidate.ProjectId,level,replace.IsChecked==true);win.DialogResult=true;
            }
            catch(ArgumentException e){MessageBox.Show(win,e.Message,"纠错学习");}
        };
        return win.ShowDialog()==true?result:null;
    }
}
