using System.Net;
using System.Text;
using System.Text.Json;
using RealtimeTranscription.Core;
using RealtimeTranscription.Desktop;
using RealtimeTranscription.Infrastructure;

static class ControllerRegression
{
    static void Check(bool condition,string message="回归断言失败"){if(!condition)throw new Exception(message);}
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("R01 会话禁用学习落库，切换重启及保存提示词不重置许可",async()=>
        {
            await using var f=await ControllerFixture.Create();var source=await f.Seed("不应发送给词条整理的历史。");
            await f.App.LoadSessionAsync(source.Session);await f.App.SetSessionLearningAsync(false);
            await f.App.SavePolishPromptAsync(PolishRules.SystemPrompt+"\n保留句式。");
            Check(!(await f.Session(source.Session.Id)).AllowLearning&&!(await f.App.SnapshotAsync()).Session!.AllowLearning);
            await f.App.ChangeProjectAsync("default");await f.Reopen();
            var result=await f.App.ExtractAllHistoryAsync(null,CancellationToken.None);
            Check(result.Completed&&result.SkippedSessions==1&&f.Calls==0);
        });
        await test("R01 总开关与当前会话许可分开保存",async()=>
        {
            await using var f=await ControllerFixture.Create();var source=await f.Seed("历史。");await f.App.LoadSessionAsync(source.Session);
            await f.App.SetSessionLearningAsync(false);
            await f.App.SaveSettingsAsync(f.App.Settings with{AllowLearning=false},f.App.Keys);
            await f.App.SaveSettingsAsync(f.App.Settings with{AllowLearning=true},f.App.Keys);
            Check(!(await f.Session(source.Session.Id)).AllowLearning);
            await f.App.SetSessionLearningAsync(true);Check((await f.Session(source.Session.Id)).AllowLearning);
        });
        await test("R01 关闭文本保存时只修改历史许可，不泄漏本地未保存修订",async()=>
        {
            await using var f=await ControllerFixture.Create();var source=await f.Seed("原来保存的正文。",true);await f.App.LoadSessionAsync(source.Session);
            await f.App.SaveSettingsAsync(f.App.Settings with{SaveMemory=false},f.App.Keys);
            await f.App.EditAsync(source.Segment.Id,"仅在内存里的修订。");await f.App.SetSessionLearningAsync(false);
            var saved=await f.App.Repository.LoadSessionAsync(source.Session.Id);
            Check(!saved!.Session.AllowLearning&&saved.Session.WholePolishText==source.Session.WholePolishText&&saved.Segments.Single().FinalText==source.Segment.FinalText);
        });
        await test("R01 旧会话保存不能覆盖新许可和已提交提取游标",async()=>
        {
            await using var f=await ControllerFixture.Create();var source=await f.Seed("人才盘点。");
            var secure=source.Session with{AllowLearning=false,LearningRevision=3,Revision=3,LearnedVersions=["checkpoint"]};await f.App.Repository.SaveSessionAsync(secure);
            await f.App.Repository.SaveSessionAsync(source.Session with{Revision=10,DeliveryState="Sent"});
            var saved=await f.Session(source.Session.Id);Check(!saved.AllowLearning&&saved.LearningRevision==3&&saved.LearnedVersions.Contains("checkpoint")&&saved.DeliveryState=="Sent");
        });
        await test("R02 缓存历史行重新载入和导出快照均使用最新编辑及删除",async()=>
        {
            await using var f=await ControllerFixture.Create();var source=await f.Seed("人才盘点安排在周一。",true);
            var cached=(await f.App.Repository.SearchAsync(null,"",null,CancellationToken.None)).Single();await f.App.LoadSessionAsync(cached.Session);
            await f.App.EditAsync(source.Segment.Id,"人才盘点安排在周二。");await f.Settle();await f.App.LoadSessionAsync(cached.Session);
            Check(TranscriptText.Render(await f.App.SnapshotAsync()).Contains("周二"));
            var export=await f.App.Repository.LoadSessionAsync(cached.Session.Id);Check(export!.Session.WholePolishState=="Fallback"&&export.Segments.Single().FinalText.Contains("周二"));
            await f.App.EditAsync(source.Segment.Id,"","删除");await f.Settle();await f.App.LoadSessionAsync(cached.Session);
            Check(TranscriptText.Render(await f.App.SnapshotAsync())=="");
        });
        await test("R02 已删除的缓存历史行不能重新载入",async()=>
        {
            await using var f=await ControllerFixture.Create();var source=await f.Seed("已删除历史。");await f.App.DeleteSessionAsync(source.Session);
            bool rejected=false;try{await f.App.LoadSessionAsync(source.Session);}catch(InvalidOperationException){rejected=true;}Check(rejected);
        });
        await test("R04 全文保存失败计入未保存，持续失败不报成功，恢复后重试持久化",async()=>
        {
            await using var f=await ControllerFixture.Create((_,_)=>Task.FromResult(ControllerFixture.Reply("这个方案我们先试一下。")));
            var source=await f.Seed("嗯，这个方案我们先试一下。");await f.App.LoadSessionAsync(source.Session);
            f.Protector.Fail=j=>j.TryGetProperty("wholePolishState",out var s)&&s.GetString()=="Completed";
            await f.App.FinishCurrentAsync();Check((await f.App.SnapshotAsync()).Unsaved>0);
            await f.App.RetrySaveAsync();Check((await f.App.SnapshotAsync()).Unsaved>0&&(await f.App.SnapshotAsync()).Status.Contains("未保存"));
            f.Protector.Fail=null;await f.App.RetrySaveAsync();
            Check((await f.App.SnapshotAsync()).Unsaved==0&&(await f.Session(source.Session.Id)).WholePolishText=="这个方案我们先试一下。");
        });
        await test("R04 投递信息独立写入失败后重试保存最新状态",async()=>
        {
            await using var f=await ControllerFixture.Create();var source=await f.Seed("投递状态。");await f.App.LoadSessionAsync(source.Session);
            f.Protector.Fail=j=>j.TryGetProperty("deliveryState",out var v)&&v.GetString()=="Sent";
            await f.App.SetDeliveryAsync("Sent","投递完成",18);Check((await f.App.SnapshotAsync()).Unsaved>0);
            f.Protector.Fail=null;await f.App.RetrySaveAsync();var saved=await f.Session(source.Session.Id);Check(saved.DeliveryState=="Sent"&&saved.AcceptedInputEvents==18);
        });
        await test("R04 切换会话后仍能重试前一个会话失败的片段",async()=>
        {
            await using var f=await ControllerFixture.Create();var a=await f.Seed("第一条。");var b=await f.Seed("第二条。");await f.App.LoadSessionAsync(a.Session);
            f.Protector.Fail=j=>j.TryGetProperty("finalText",out var v)&&v.GetString()=="修订保存失败。";
            await f.App.EditAsync(a.Segment.Id,"修订保存失败。");await f.App.Repository.BarrierAsync();await f.App.SnapshotAsync();
            await f.App.LoadSessionAsync(b.Session);f.Protector.Fail=null;await f.App.RetrySaveAsync();
            Check((await f.App.Repository.LoadSessionAsync(a.Session.Id))!.Segments.Single().FinalText=="修订保存失败。");
        });
        await test("R03a 历史分批提交后手动新增同名词被拒绝",async()=>
        {
            await WithPausedHistory(async(f,_)=>
            {
                bool rejected=false;try{await f.App.SaveTermAsync(new(){Text="人才盘点"});}catch(ArgumentException){rejected=true;}
                Check(rejected&&(await f.App.Repository.TermsAsync("default")).Count==1&&f.App.Terms.Count==1);
            },false);
        });
        await test("R03b 历史提取后使用旧界面对象改权重仍保留最新证据",async()=>
        {
            await WithPausedHistory(async(f,old)=>
            {
                await f.App.SaveTermAsync(old! with{Weight=5});var saved=(await f.App.Repository.TermsAsync("default")).Single();
                Check(saved.Weight==5&&saved.Evidence.Count==1&&saved.Revision>=3);
            },true);
        });
        await test("R03 手工导入在写事务中忽略规范化重名，无效记录整批回滚",async()=>
        {
            await using var f=await ControllerFixture.Create();await f.App.SaveTermAsync(new(){Text="KPI"});
            Check(await f.App.ImportTermsAsync([new(){Text=" kpi "},new(){Text="人才盘点"}])==1);
            var invalid=new TermData{Text="坏权重",Weight=6};bool rejected=false;
            try{await f.App.Repository.ImportUserTermsAsync([new(){Text="应回滚"},invalid]);}catch(ArgumentException){rejected=true;}
            Check(rejected&&!(await f.App.Repository.TermsAsync("default")).Any(t=>t.Text=="应回滚"));
        });
        await test("R03 升级启动合并旧版重复词条，保留手工设置、禁用状态及证据",async()=>
        {
            await using var f=await ControllerFixture.Create();var s=await f.Seed("人才盘点。");
            var manual=new TermData{Text="人才盘点",Origin="Manual",Weight=5};var ai=new TermData{Text="人才盘点",Origin="Extracted",State=TermState.Disabled,Evidence=[new(s.Session.Id,s.Segment.Id,1,0,s.Segment.RawText,false)]};
            await f.App.Repository.ImportTermsAsync([manual,ai]);await f.Reopen();
            var saved=f.App.Terms.Single();Check(saved.Id==manual.Id&&saved.Weight==5&&saved.State==TermState.Disabled&&saved.Evidence.Count==1);
            await f.Reopen();Check(f.App.Terms.Count==1);
        });
        await test("R05 撤销、恢复原文及删除与来源证据清理原子保存",async()=>
        {
            foreach(string action in new[]{"撤销","恢复原文","删除"})
            {
                await using var f=await ControllerFixture.Create(async(r,t)=>ControllerFixture.Reply(await ControllerFixture.Candidate(r,"人才盘点",t)));
                var source=await f.Seed("请复核招聘计划。");await f.App.LoadSessionAsync(source.Session);await f.App.EditAsync(source.Segment.Id,"请复核人才盘点。");await f.Settle();
                await f.App.ExtractAsync();var term=f.App.Terms.Single();await f.App.SaveTermAsync(term with{State=TermState.Enabled});
                await f.App.EditAsync(source.Segment.Id,action=="删除"?"":source.Segment.RawText,action);await f.Settle();
                Check((await f.App.Repository.TermsAsync("default")).Count==0,action+" 未清理来源");
            }
        });
        await test("R05 旧片段写入回执不会清理新版本有效证据",async()=>
        {
            await using var f=await ControllerFixture.Create();var source=await f.Seed("旧版。");var newer=source.Segment with{FinalText="人才盘点。",Revision=5,EditRevision=2};await f.App.Repository.SaveSegmentAsync(source.Session,newer);
            await f.App.Repository.SaveTermAsync(new(){Text="人才盘点",Origin="Extracted",Evidence=[new(source.Session.Id,source.Segment.Id,1,2,"人才盘点。",false)]});
            await f.App.Repository.SaveSegmentAsync(source.Session,source.Segment with{Revision=4,EditRevision=1});
            Check((await f.App.Repository.TermsAsync("default")).Single().Evidence.Single().EditRevision==2);
        });
        await test("R06 连续撤销、重启续撤销、编辑分支及删除恢复",async()=>
        {
            await using var f=await ControllerFixture.Create();var source=await f.Seed("甲。");await f.App.LoadSessionAsync(source.Session);
            await f.App.EditAsync(source.Segment.Id,"乙。");await f.App.EditAsync(source.Segment.Id,"丙。");await f.Settle();await f.Reopen();await f.App.LoadSessionAsync(source.Session);
            await f.App.UndoAsync(source.Segment.Id);Check(TranscriptText.Render(await f.App.SnapshotAsync())=="乙。");
            await f.App.UndoAsync(source.Segment.Id);Check(TranscriptText.Render(await f.App.SnapshotAsync())=="甲。");
            await f.App.UndoAsync(source.Segment.Id);Check(TranscriptText.Render(await f.App.SnapshotAsync())=="甲。");
            await f.App.EditAsync(source.Segment.Id,"丁。");await f.App.EditAsync(source.Segment.Id,"","删除");await f.App.UndoAsync(source.Segment.Id);Check(TranscriptText.Render(await f.App.SnapshotAsync())=="丁。");
            await f.App.UndoAsync(source.Segment.Id);Check(TranscriptText.Render(await f.App.SnapshotAsync())=="甲。");await f.Settle();
        });
        await test("当前会话沿用五批上限，全部历史继续剩余批次且不重复",async()=>
        {
            await using var f=await ControllerFixture.Create((_,_)=>Task.FromResult(ControllerFixture.Reply("{\"terms\":[]}")));
            var source=await f.Seed(new string('词',14001));await f.App.LoadSessionAsync(source.Session);await f.App.ExtractAsync();
            Check(f.Calls==5&&(await f.Session(source.Session.Id)).LearnedVersions.Count==5);
            var rest=await f.App.ExtractAllHistoryAsync(null,CancellationToken.None);Check(rest.Completed&&rest.Batches==3&&f.Calls==8);
        });
        await test("当前会话候选和游标一起回滚，失败恢复后可重新提取",async()=>
        {
            await using var f=await ControllerFixture.Create(async(r,t)=>ControllerFixture.Reply(await ControllerFixture.Candidate(r,"人才盘点",t)));
            var source=await f.Seed("人才盘点。");await f.App.LoadSessionAsync(source.Session);
            f.Protector.Fail=j=>j.TryGetProperty("learnedVersions",out var a)&&a.GetArrayLength()>0;
            try{await f.App.ExtractAsync();}catch(IOException){}
            Check((await f.App.Repository.TermsAsync("default")).Count==0&&(await f.Session(source.Session.Id)).LearnedVersions.Count==0);
            f.Protector.Fail=null;await f.App.ExtractAsync();Check(f.App.Terms.Count==1&&(await f.Session(source.Session.Id)).LearnedVersions.Count==1);
        });
        await test("R07 UTC、东八区和负时区的预算与日期保持一致",async()=>
        {
            string? old=Environment.GetEnvironmentVariable("TZ");
            try
            {
                foreach(var zone in new[]{"Etc/UTC","Asia/Shanghai","America/Los_Angeles"})
                {
                    Environment.SetEnvironmentVariable("TZ",zone);TimeZoneInfo.ClearCachedData();
                    await using var f=await ControllerFixture.Create();await f.App.SaveSettingsAsync(f.App.Settings with{DailyExtractionTokens=50000},f.App.Keys);
                    var at=DateTimeOffset.UtcNow;await f.App.Repository.SaveUsageAsync(new("term_budget",50000,0,false,at));
                    Check((await f.App.Repository.UsageAsync()).Single().At.UtcDateTime.Date==at.UtcDateTime.Date,zone);
                    var result=await f.App.GenerateLexiconAsync(new("HR",1,"default"),f.App.Keys.DeepSeekKey,null,CancellationToken.None);
                    Check(result.Terms.Count==0&&f.Calls==0&&result.Note.Contains("预算"),zone);
                    var s=await f.Seed("预算用尽，不应发送。");await f.App.LoadSessionAsync(s.Session);await f.App.ExtractAsync();await f.App.ExtractAllHistoryAsync(null,CancellationToken.None);Check(f.Calls==0,zone);
                }
            }
            finally{Environment.SetEnvironmentVariable("TZ",old);TimeZoneInfo.ClearCachedData();}
        });
        await test("R07 并发预算预留不超额且 UTC 新一天重新计数",async()=>
        {
            await using var f=await ControllerFixture.Create();var at=DateTimeOffset.UtcNow;
            async Task<bool> Reserve(){try{await f.App.Repository.ReserveTermBudgetAsync(600,1000,at,CancellationToken.None);return true;}catch(ProviderException){return false;}}
            var results=await Task.WhenAll(Reserve(),Reserve());Check(results.Count(x=>x)==1);
            await f.App.Repository.ReserveTermBudgetAsync(1000,1000,at.AddDays(1),CancellationToken.None);
            Check((await f.App.Repository.UsageAsync()).Count==2);
        });
        await test("R01 许可保存失败阻止提取，恢复后重试；关闭记忆仍仅更新许可",async()=>
        {
            foreach(bool memory in new[]{true,false})
            {
                await using var f=await ControllerFixture.Create();var source=await f.Seed("已经保存的原文。",true);await f.App.LoadSessionAsync(source.Session);
                await f.App.SaveSettingsAsync(f.App.Settings with{SaveMemory=memory},f.App.Keys);
                if(!memory)await f.App.EditAsync(source.Segment.Id,"尚未保存的私人修订。");
                f.Protector.Fail=j=>j.TryGetProperty("learningRevision",out var v)&&v.GetInt64()>0;
                bool failed=false;try{await f.App.SetSessionLearningAsync(false);}catch(InvalidOperationException){failed=true;}
                Check(failed&&(await f.App.SnapshotAsync()).Unsaved>0);
                bool blocked=false;try{await f.App.ExtractAllHistoryAsync(null,CancellationToken.None);}catch(InvalidOperationException){blocked=true;}
                Check(blocked&&f.Calls==0);await f.App.RetrySaveAsync();Check((await f.App.SnapshotAsync()).Unsaved>0);
                f.Protector.Fail=null;await f.App.RetrySaveAsync();var saved=await f.App.Repository.LoadSessionAsync(source.Session.Id);
                Check((await f.App.SnapshotAsync()).Unsaved==0&&!saved!.Session.AllowLearning&&saved.Segments.Single().FinalText==source.Segment.FinalText);
                if(!memory)
                {
                    Check(saved!.Session.WholePolishText==source.Session.WholePolishText);
                    bool rejected=false;try{await f.App.RetrySaveAsync();}catch(InvalidOperationException){rejected=true;}Check(rejected);
                }
            }
        });
        await test("R04 重新载入失败保存的历史不会覆盖内存修改，恢复后自动重试",async()=>
        {
            await using var f=await ControllerFixture.Create();var source=await f.Seed("旧正文。");await f.App.LoadSessionAsync(source.Session);
            f.Protector.Fail=j=>j.TryGetProperty("finalText",out var v)&&v.GetString()=="需要保留的修订。";
            await f.App.EditAsync(source.Segment.Id,"需要保留的修订。");
            bool blocked=false;try{await f.App.LoadSessionAsync(source.Session);}catch(InvalidOperationException){blocked=true;}
            Check(blocked&&TranscriptText.Render(await f.App.SnapshotAsync())=="需要保留的修订。");
            f.Protector.Fail=null;await f.App.LoadSessionAsync(source.Session);
            Check((await f.App.SnapshotAsync()).Unsaved==0&&TranscriptText.Render(await f.App.SnapshotAsync())=="需要保留的修订。");
        });
        await test("R04 删除项目清除其失败保存待办，重试不能恢复被删除数据",async()=>
        {
            await using var f=await ControllerFixture.Create();var source=await f.Seed("待删除项目原文。");await f.App.CreateProjectAsync("保留项目");await f.App.LoadSessionAsync(source.Session);
            f.Protector.Fail=j=>j.TryGetProperty("finalText",out var v)&&v.GetString()=="保存失败。";
            await f.App.EditAsync(source.Segment.Id,"保存失败。");await f.App.DeleteCurrentProjectAsync();f.Protector.Fail=null;
            await f.App.RetrySaveAsync();Check((await f.App.SnapshotAsync()).Unsaved==0&&await f.App.Repository.LoadSessionAsync(source.Session.Id)==null);
        });
        await test("R03 范围容量在写入时复核，跨越五千词的导入整批回滚",async()=>
        {
            await using var f=await ControllerFixture.Create();await f.App.Repository.ImportTermsAsync(Enumerable.Range(0,4999).Select(i=>new TermData{Text="词条"+i}).ToArray());
            bool rejected=false;try{await f.App.ImportTermsAsync([new(){Text="第5000词"},new(){Text="第5001词"}]);}catch(ArgumentException){rejected=true;}
            Check(rejected&&(await f.App.Repository.TermsAsync("default")).Count==4999);
            await f.App.SaveTermAsync(new(){Text="最后一个"});rejected=false;try{await f.App.SaveTermAsync(new(){Text="超过上限"});}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&(await f.App.Repository.TermsAsync("default")).Count==5000);
        });
        await test("R06 空撤销历史兼容，超过两万字的长原文仍可撤销和恢复",async()=>
        {
            await using var f=await ControllerFixture.Create();string raw=new('语',20001);var source=await f.Seed(raw);
            await f.App.Repository.SaveSegmentAsync(source.Session,source.Segment with{UndoHistory=[]});await f.App.LoadSessionAsync(source.Session);
            await f.App.EditAsync(source.Segment.Id,"短修订。");await f.App.UndoAsync(source.Segment.Id);Check(TranscriptText.Render(await f.App.SnapshotAsync())==raw);
            await f.App.EditAsync(source.Segment.Id,"另一修订。");await f.App.EditAsync(source.Segment.Id,"","恢复原文");Check(TranscriptText.Render(await f.App.SnapshotAsync())==raw);await f.Settle();
        });
        await test("当前及全部历史保留一次瞬时错误重试，候选游标不重复提交",async()=>
        {
            foreach(bool all in new[]{false,true})
            {
                int calls=0;await using var f=await ControllerFixture.Create((_,_)=>Task.FromResult(++calls==1?new HttpResponseMessage(HttpStatusCode.ServiceUnavailable){Content=new StringContent("{}")}:ControllerFixture.Reply("{\"terms\":[]}")));
                var source=await f.Seed("瞬时错误应重试。");await f.App.LoadSessionAsync(source.Session);
                if(all)Check((await f.App.ExtractAllHistoryAsync(null,CancellationToken.None)).Completed);else await f.App.ExtractAsync();
                Check(calls==2&&(await f.Session(source.Session.Id)).LearnedVersions.Count==1&&f.App.Terms.Count==0);
                Check((await f.App.Repository.UsageAsync()).Where(u=>u.Purpose=="term_budget").Sum(u=>u.InputTokens+u.OutputTokens)>100);
            }
        });
    }

    static async Task WithPausedHistory(Func<ControllerFixture,TermData?,Task> action,bool existing)
    {
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int calls=0;
        await using var f=await ControllerFixture.Create(async(r,t)=>
        {
            if(Interlocked.Increment(ref calls)==2){started.TrySetResult();await release.Task.WaitAsync(t);return ControllerFixture.Reply("{\"terms\":[]}");}
            return ControllerFixture.Reply(await ControllerFixture.Candidate(r,"人才盘点",t));
        });
        if(existing)await f.App.SaveTermAsync(new(){Text="人才盘点"});var old=f.App.Terms.SingleOrDefault();
        await f.Seed("人才盘点需要复核。");await f.Seed("第二个请求暂停等待。");var running=f.App.ExtractAllHistoryAsync(null,CancellationToken.None);
        try{await started.Task.WaitAsync(TimeSpan.FromSeconds(5));await action(f,old);}
        finally{release.TrySetResult();await running;}
    }
}

sealed class ControllerFixture:IAsyncDisposable
{
    public AppController App {get;private set;}=null!;
    public FaultProtector Protector {get;}=new();
    public int Calls;
    readonly string folder=Path.Combine(Path.GetTempPath(),"VoiceInputRegression-"+JsonCodec.Id());
    Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> handler=null!;
    public static async Task<ControllerFixture> Create(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>? handler=null)
    {
        var f=new ControllerFixture();f.handler=handler??((_,_)=>throw new Exception("Unexpected cloud request"));
        new SettingsStore(f.folder,f.Protector).Save(new(){LegacyEndpoint=true,DailyExtractionTokens=1000000},new("TEST_ONLY","TEST_ONLY"));await f.Open();return f;
    }
    async Task Open(){App=new(folder,Protector,new RegressionHandler((r,t)=>{Interlocked.Increment(ref Calls);return handler(r,t);}));await App.InitializeAsync();}
    public async Task Reopen(){await App.DisposeAsync();await Open();}
    public async Task<(SessionData Session,SegmentData Segment)> Seed(string text,bool completed=false)
    {
        var session=new SessionData{WholePolishState=completed?"Completed":"None",WholePolishText=completed?text:""};
        var part=new SegmentData{SessionId=session.Id,TaskId=JsonCodec.Id(),TaskOrder=1,SentenceId=1,RawText=text,FinalText=text,AsrState=AsrState.Confirmed,OutputState=OutputState.Published,SaveState=SaveState.Saved,SourceRevision=1};
        await App.Repository.SaveSegmentAsync(session,part);return(session,part);
    }
    public async Task<SessionData> Session(string id)=>(await App.Repository.LoadSessionAsync(id))!.Session;
    public async Task Settle()
    {
        for(int i=0;i<100;i++){await App.Repository.BarrierAsync();if((await App.SnapshotAsync()).Segments.All(s=>s.SaveState==SaveState.Saved)){await App.Repository.BarrierAsync();return;}await Task.Delay(5);}
        throw new TimeoutException("保存未完成");
    }
    public static HttpResponseMessage Reply(string text)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{finish_reason="stop",message=new{content=text}}},usage=new{prompt_tokens=50,completion_tokens=50}}),Encoding.UTF8,"application/json")};
    public static async Task<string> Candidate(HttpRequestMessage request,string word,CancellationToken token)
    {
        using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));using var input=JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);var source=input.RootElement.GetProperty("segments")[0];
        return JsonSerializer.Serialize(new{terms=new[]{new{text=word,category="专业术语",evidence=new[]{new{source_segment_id=source.GetProperty("source_segment_id").GetString(),evidence_text=source.GetProperty("text").GetString()}}}}});
    }
    public async ValueTask DisposeAsync(){await App.DisposeAsync();try{Directory.Delete(folder,true);}catch(IOException){}}
}
sealed class RegressionHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> call):HttpMessageHandler
{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>call(request,token);}
sealed class FaultProtector:IProtector
{
    readonly TestProtector crypto=new();
    public Func<JsonElement,bool>? Fail;
    public byte[] Protect(byte[] plain){using var doc=JsonDocument.Parse(plain);if(Fail?.Invoke(doc.RootElement)==true)throw new IOException("Injected write failure");return crypto.Protect(plain);}
    public byte[] Unprotect(byte[] cipher)=>crypto.Unprotect(cipher);
}
