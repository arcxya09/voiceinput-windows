using RealtimeTranscription.Core;

namespace RealtimeTranscription.Desktop;

public sealed partial class AppController
{
    public async Task ConfirmCorrectionAsync(CorrectionCandidate candidate,CorrectionApproval approval)
    {
        await knowledgeGate.WaitAsync();
        try
        {
            string project=await OnActor(()=>
            {
                if(!Settings.SaveMemory||!Settings.AllowLearning||!Settings.LearnCorrections)throw new InvalidOperationException("请先在设置中启用文本记忆、词条整理及纠错学习。");
                if(Settings.ProjectId!=candidate.ProjectId)throw new InvalidOperationException("项目已切换，请重新选择候选。");
                knowledgeEpoch++;return Settings.ProjectId;
            });
            var term=await Repository.ConfirmCorrectionAsync(candidate.Id,project,approval);await ReloadTerms();
            await OnActor(()=>Status(Settings.UseLexicon?$"已学习“{term.Text}”，下次语音输入优先使用该词条。":$"已学习“{term.Text}”。可在设置中开启“识别时使用已启用词库”。"));
        }
        finally{knowledgeGate.Release();}
    }
    public async Task SetCorrectionIgnoredAsync(CorrectionCandidate candidate,bool ignored)
    {
        await knowledgeGate.WaitAsync();
        try
        {
            string project=await OnActor(()=>{if(Settings.ProjectId!=candidate.ProjectId)throw new InvalidOperationException("项目已切换，请重新选择候选。");return Settings.ProjectId;});
            await Repository.SetCorrectionIgnoredAsync(candidate.Id,project,ignored);await ReloadTerms();
            await OnActor(()=>Status(ignored?"已忽略此写法，后续相同纠错不再进入待确认列表。":"已恢复为待确认候选。"));
        }
        finally{knowledgeGate.Release();}
    }
    public async Task RevokeCorrectionAsync(CorrectionCandidate candidate)
    {
        await knowledgeGate.WaitAsync();
        try
        {
            string project=await OnActor(()=>{if(Settings.ProjectId!=candidate.ProjectId)throw new InvalidOperationException("项目已切换，请重新选择记录。");knowledgeEpoch++;return Settings.ProjectId;});
            await Repository.RevokeCorrectionAsync(candidate.Id,project);await ReloadTerms();
            await OnActor(()=>Status("已撤销本次学习，并将该写法设为已忽略。"));
        }
        finally{knowledgeGate.Release();}
    }
}
