using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

internal static class SmartPunctuationRegression
{
    private static void Check(bool value,string message="Smart punctuation regression failed")
    { if(!value)throw new Exception(message); }
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("2.1.12 智能标点只省略短输入的冗余句号",()=>
        {
            foreach(var (before,after) in new[]{("核天体物理。","核天体物理"),("测试一下。","测试一下"),("核天体物理.","核天体物理"),("Hello world.","Hello world"),("测试一下","测试一下")})
            { Check(SmartPunctuation.Format(before)==after,before);Check(PolishRules.Validate(before,after).Accepted,before); }
            foreach(string text in new[]{"“测试”。","E=mc²。","你好？","立即停止！","还没说完……","第一句。第二句。","先测试，再记录。","第一行\n第二行。","123.","3.14。","U.S.A.","Dr.","Meet Dr.","README.md.","他说“测试。","`测试。","电压很重要，需要记录完整的实验结果。"})
                Check(SmartPunctuation.Format(text)==text,text);
            Check(SmartPunctuation.Format("测试。",["测试。"])=="测试。");
            Check(!PolishRules.Validate("测试。","测试",smartPunctuation:false).Accepted);
            return Task.CompletedTask;
        });
        await test("2.1.12 旧默认提示词升级，自定义提示词原样保留",()=>
        {
            Check(PolishRules.ResolvePrompt(PolishRules.PreviousSystemPrompt)==PolishRules.SystemPrompt,"旧默认提示词未迁移");
            foreach(string newline in new[]{"\n","\r\n","\r"})
                Check(PolishRules.ResolvePrompt(PolishRules.PreviousSystemPrompt.ReplaceLineEndings(newline))==PolishRules.SystemPrompt,"不同换行符的旧默认提示词未迁移");
            string custom=PolishRules.PreviousSystemPrompt+"\n保留作者指定风格。";
            Check(PolishRules.ResolvePrompt(custom)==custom);
            Check(new AppSettings().SmartPunctuationEnabled);
            return Task.CompletedTask;
        });
        foreach(string mode in new[]{"disabled-polish","polished","timeout","expedite","disabled-setting"})
        await test("2.1.12 短输入全文输出与持久化："+mode,async()=>
        {
            await using var f=await ControllerFixture.Create(async(_,token)=>
            { if(mode=="timeout")await Task.Delay(5000,token);return ControllerFixture.Reply("测试一下"); });
            await f.App.SaveSettingsAsync(f.App.Settings with{SmartPunctuationEnabled=mode!="disabled-setting",PolishEnabled=mode!="disabled-polish"},f.App.Keys);
            var seed=await f.Seed("测试一下。");await f.App.LoadSessionAsync(seed.Session);
            using var expedite=new CancellationTokenSource();if(mode=="expedite")expedite.Cancel();
            await f.App.FinishCurrentAsync(seed.Session.Id,forDelivery:true,expedite:expedite.Token);
            var snap=await f.App.SnapshotAsync();string expected=mode=="disabled-setting"?"测试一下。":"测试一下";
            Check(TranscriptText.Render(snap)==expected,mode);
            Check(snap.Segments.Single().RawText=="测试一下。","原始识别结果被覆盖");
            await f.Reopen();Check(f.App.Settings.SmartPunctuationEnabled==(mode!="disabled-setting"));
            await f.App.LoadSessionAsync(await f.Session(seed.Session.Id));
            Check(TranscriptText.Render(await f.App.SnapshotAsync())==expected,"重新打开丢失最终标点选择");
            await f.App.EditAsync(seed.Segment.Id,"手动编辑。");
            Check(TranscriptText.Render(await f.App.SnapshotAsync())=="手动编辑。","手动编辑被自动删除标点");
        });
        await test("2.1.12 胶囊按投递结果区分停留时间",()=>
        {
            Check(CapsulePresentation.CompletionDuration("PasteSent").TotalMilliseconds==450);
            Check(CapsulePresentation.CompletionDuration("Copied").TotalMilliseconds==1200);
            foreach(string state in new[]{"CopyFailed","Failed","Blocked","Unknown"})Check(CapsulePresentation.CompletionDuration(state).TotalMilliseconds==3000);
            return Task.CompletedTask;
        });
    }
}
