using RealtimeTranscription.Core;

namespace RealtimeTranscription.Desktop;

public sealed partial class AppController
{
    private IReadOnlyList<TermData> approvedCorrections=[];
    public IReadOnlyList<TermData> NextHotwords()
    {
        if(!MemoryAvailable||!Settings.UseLexicon)return [];
        var at=DateTimeOffset.UtcNow;
        IEnumerable<TermData> candidates=Settings.DynamicLexicon?terms:terms.Select(t=>t with{UsageCount=0,CorrectionCount=0,LastUsedAt=null,LastCorrectedAt=null});
        return Lexicon.Select(candidates,Settings.ProjectId,at).Select(t=>t with{Weight=Settings.DynamicLexicon?Lexicon.EffectiveWeight(t,at):t.Weight}).ToArray();
    }
    private int EligibleHotwords()=>terms.Where(t=>t.State==TermState.Enabled&&(t.Scope=="*"||t.Scope==Settings.ProjectId))
        .Select(t=>TermGenerationRules.Normalize(t.Text)).Distinct(StringComparer.Ordinal).Count();
    public async Task<string> LexiconReportAsync()
    {
        var snapshot=await SnapshotAsync();var s=snapshot.Session;
        if(s==null)return "尚无本轮语音记录。词库页可查看下一轮将选取的热词。";
        string state=s.HotwordState switch{"Sent"=>"已向 ASR 提交","Prepared"=>"已选取，尚未确认提交","Failed"=>"任务启动失败，未确认提交","Disabled"=>"本轮词库已关闭","Unavailable"=>"本地记忆不可用，本轮未使用词库",_=>"旧记录无热词快照"};
        return $"{state}：{s.Hotwords.Count} / {s.EligibleHotwordCount} 个\n本轮匹配的保护词：{s.ProtectedTermCount} 个\n已应用确认纠错：{s.AppliedCorrectionCount} 处\n"+
            (s.AppliedCorrectionTerms.Count>0?"纠正为："+string.Join("、",s.AppliedCorrectionTerms)+"\n":"")+
            "\n提交热词及实际权重（不代表云端识别准确率）：\n"+string.Join("\n",s.Hotwords.Select(t=>$"{t.Text}    {t.Weight}"));
    }
    public async Task SetAutomaticCorrectionAsync(CorrectionCandidate candidate,bool enabled)
    {
        await knowledgeGate.WaitAsync();
        try
        {
            if(Settings.ProjectId!=candidate.ProjectId)throw new InvalidOperationException("项目已切换，请重新选择记录。");
            await Repository.SetAutomaticCorrectionAsync(candidate.Id,Settings.ProjectId,enabled);await ReloadTerms();
            await OnActor(()=>{knowledgeEpoch++;Status(enabled?"已启用这组确认写法的自动纠正，下次语音输入生效。":"已关闭这组写法的自动纠正，热词仍保留。");});
        }
        finally{knowledgeGate.Release();}
    }
}
