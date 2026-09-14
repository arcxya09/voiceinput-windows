using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        var approval=await Dialogs.ConfirmCorrectionAsync(this,candidate,evidence);
        if(approval!=null)await controller.ConfirmCorrectionAsync(candidate,approval);
    }));
    private async void CorrectionIgnore_Click(object sender,RoutedEventArgs e)=>await Safe(()=>WithCorrection(c=>controller.SetCorrectionIgnoredAsync(c,true)));
    private async void CorrectionRestore_Click(object sender,RoutedEventArgs e)=>await Safe(()=>WithCorrection(c=>controller.SetCorrectionIgnoredAsync(c,false)));
    private async void CorrectionRevoke_Click(object sender,RoutedEventArgs e)=>await Safe(()=>WithCorrection(c=>controller.RevokeCorrectionAsync(c)));
    private async void CorrectionEvidence_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>
    {
        if(CorrectionsGrid.SelectedItem is not CorrectionCandidate c)return;
        var sources=await controller.Repository.CorrectionEvidenceAsync(c.Id);
        await Dialogs.TextAsync(this,"纠错来源",$"{c.Original} → {c.DisplayText}\n状态：{c.StateLabel} · 不同片段：{c.Count}\n自动纠正：{c.ReplacementLabel}\n{c.ReplacementReason}\n\n"+
            (sources.Count==0?"原始来源已删除、撤销或关闭学习许可。已确认词条和忽略偏好仍可单独管理。":string.Join("\n\n",sources.Select(s=>$"{s.At.LocalDateTime:g}\n修改前：{s.BeforeContext}\n修改后：{s.AfterContext}\n会话：{s.SessionId}\n片段：{s.SegmentId} · 版本：{s.EditRevision}")))+
            (c.Count>30?"\n\n仅显示最近 30 个来源，统计包含全部有效来源。":""));
    });
    public void OpenCorrections(){ShowPage(2);VocabularyTabs.SelectedIndex=1;}
}

