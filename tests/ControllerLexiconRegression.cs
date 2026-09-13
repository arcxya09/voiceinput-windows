using RealtimeTranscription.Core;

static class ControllerLexiconRegression
{
    static void Check(bool value,string reason="控制器词频集成断言失败"){if(!value)throw new Exception(reason);}
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("收尾返回前刷新已提交词频，下一轮热词立即获得新权重",async()=>
        {
            await using var f=await ControllerFixture.Create();await f.App.SaveTermAsync(new(){Text="JUNA",Weight=2});
            var a=await f.Seed("JUNA");var b=await f.Seed("JUNA");await f.App.LoadSessionAsync(b.Session);
            // Simulate accepted background persistence whose UI completion has not run yet.
            await f.App.Repository.SaveSegmentAsync(a.Session,a.Segment,learnUsage:true);
            await f.App.Repository.SaveSegmentAsync(b.Session,b.Segment,learnUsage:true);
            Check(f.App.Terms.Single().UsageCount==0);
            await f.App.FinishCurrentAsync(allowPolish:false);
            Check(f.App.Terms.Single().UsageCount==2&&f.App.NextHotwords().Single().Weight==3&&f.Calls==0);
        });
        await test("撤回会话学习后立即刷新界面词频和下一轮词库",async()=>
        {
            await using var f=await ControllerFixture.Create();await f.App.SaveTermAsync(new(){Text="JUNA"});var source=await f.Seed("JUNA");
            await f.App.Repository.SaveSegmentAsync(source.Session,source.Segment,learnUsage:true);await f.App.LoadSessionAsync(source.Session);
            Check(f.App.Terms.Single().UsageCount==1);
            await f.App.SetSessionLearningAsync(false);
            Check(f.App.Terms.Single().UsageCount==0&&f.App.Terms.Single().LastUsedAt==null&&f.App.NextHotwords().Single().UsageCount==0);
        });
        await test("撤回许可保存失败后重试同步清除内存中的旧词频",async()=>
        {
            await using var f=await ControllerFixture.Create();await f.App.SaveTermAsync(new(){Text="JUNA"});var source=await f.Seed("JUNA");
            await f.App.Repository.SaveSegmentAsync(source.Session,source.Segment,learnUsage:true);await f.App.LoadSessionAsync(source.Session);
            f.Protector.Fail=x=>x.TryGetProperty("allowLearning",out var allow)&&!allow.GetBoolean();bool failed=false;
            try{await f.App.SetSessionLearningAsync(false);}catch(InvalidOperationException){failed=true;}
            Check(failed&&f.App.Terms.Single().UsageCount==1);
            f.Protector.Fail=null;await f.App.RetrySaveAsync();
            Check(f.App.Terms.Single().UsageCount==0&&!(await f.Session(source.Session.Id)).AllowLearning);
        });
        await test("关闭记忆、总学习、动态词频或会话许可均停止使用统计",async()=>
        {
            foreach(string option in new[]{"memory","global","dynamic","session"})
            {
                await using var f=await ControllerFixture.Create();await f.App.SaveTermAsync(new(){Text="JUNA"});var source=await f.Seed("使用 JUNA。");await f.App.LoadSessionAsync(source.Session);
                if(option=="session")await f.App.SetSessionLearningAsync(false);
                else await f.App.SaveSettingsAsync(f.App.Settings with{SaveMemory=option!="memory",AllowLearning=option!="global",DynamicLexicon=option!="dynamic"},f.App.Keys);
                await f.App.EditAsync(source.Segment.Id,"研究 JUNA。");await f.App.FinishCurrentAsync(allowPolish:false);
                Check(f.App.Terms.Single().UsageCount==0&&(await f.App.Repository.TermsAsync("default")).Single().UsageCount==0,option);
                if(option=="memory")Check((await f.App.Repository.LoadSessionAsync(source.Session.Id))!.Segments.Single().FinalText==source.Segment.FinalText);
            }
        });
        await test("动态词频关闭保留人工权重，重新启用恢复学习权重且禁用词库不发热词",async()=>
        {
            await using var f=await ControllerFixture.Create();await f.App.SaveTermAsync(new(){Text="JUNA",Weight=2});
            for(int i=0;i<2;i++){var source=await f.Seed("JUNA");await f.App.Repository.SaveSegmentAsync(source.Session,source.Segment,learnUsage:true);}
            await f.App.SaveSettingsAsync(f.App.Settings with{DynamicLexicon=false},f.App.Keys);Check(f.App.Terms.Single().UsageCount==2&&f.App.NextHotwords().Single().Weight==2);
            await f.App.SaveSettingsAsync(f.App.Settings with{DynamicLexicon=true},f.App.Keys);Check(f.App.NextHotwords().Single().Weight==3);
            await f.App.SaveSettingsAsync(f.App.Settings with{UseLexicon=false},f.App.Keys);Check(f.App.NextHotwords().Count==0);
        });
        await test("动态词频关闭时确认纠错只创建词条与映射，不回填统计",async()=>
        {
            await using var f=await ControllerFixture.Create();var source=await f.Seed("使用 DeapSeek。");await f.App.LoadSessionAsync(source.Session);
            await f.App.EditAsync(source.Segment.Id,"使用 DeepSeek。");await f.App.FinishCurrentAsync(allowPolish:false);
            var candidate=(await f.App.Repository.CorrectionsAsync("default")).Single();
            await f.App.SaveSettingsAsync(f.App.Settings with{DynamicLexicon=false},f.App.Keys);
            await f.App.ConfirmCorrectionAsync(candidate,new(candidate.Corrected,candidate.Original,"专业术语","default",4));
            var word=f.App.Terms.Single();Check(word.Text=="DeepSeek"&&word.UsageCount==0&&word.CorrectionCount==0);
            await f.App.SaveSettingsAsync(f.App.Settings with{DynamicLexicon=true},f.App.Keys);
            await f.App.EditAsync(source.Segment.Id,"继续使用 DeepSeek。");await f.App.FinishCurrentAsync(allowPolish:false);
            Check(f.App.Terms.Single().UsageCount==1&&f.Calls==0);
        });
    }
}
