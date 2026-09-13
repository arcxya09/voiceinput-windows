using System.Windows;
namespace RealtimeTranscription.Desktop;
public partial class MainWindow
{
    private static string HotwordStateText(string state)=>state switch{"Sent"=>"已提交","Prepared"=>"待提交","Disabled"=>"词库关闭","Failed"=>"启动失败","Unavailable"=>"本地记忆不可用",_=>"无快照"};
    private async void LexiconReport_Click(object sender,RoutedEventArgs e)=>await Safe(async()=>Dialogs.Text(this,"本轮词库使用",await controller.LexiconReportAsync()));
    private void NextHotwords_Click(object sender,RoutedEventArgs e)
    {
        var chosen=controller.NextHotwords();
        Dialogs.Text(this,"下一轮热词",$"下一轮将提交 {chosen.Count} 个热词。只选择已启用词条，最多 200 个。\n"+
            (controller.Settings.DynamicLexicon?"按手动权重、近期使用、人工纠正、项目与置顶综合排序。":"动态调整已关闭，使用手动权重。")+
            "\n统计按当前项目的不同会话计数，修改与删除来源会更新统计。\n\n"+string.Join("\n",chosen.Select((t,i)=>$"{i+1}. {t.Text}    实际权重 {t.Weight}    使用 {t.UsageCount} / 人工纠正 {t.CorrectionCount}")));
    }
    private async void CorrectionEnableReplacement_Click(object sender,RoutedEventArgs e)=>await Safe(()=>WithCorrection(async candidate=>
    {
        var term=controller.Terms.FirstOrDefault(t=>t.Id==candidate.TermId);
        if(term==null)throw new InvalidOperationException("请先确认学习该词条。");
        string original=candidate.LearnedAlias.Length>0?candidate.LearnedAlias:candidate.Original;
        if(MessageBox.Show(this,$"后续语音输入中，将确认的旧写法“{original}”纠正为“{term.Text}”？\n原始识别文本会保留。数字、否定或歧义映射会跳过。","启用自动纠正",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)
            await controller.SetAutomaticCorrectionAsync(candidate,true);
    }));
    private async void CorrectionDisableReplacement_Click(object sender,RoutedEventArgs e)=>await Safe(()=>WithCorrection(c=>controller.SetAutomaticCorrectionAsync(c,false)));
}
