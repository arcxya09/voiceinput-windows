using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

static class CorrectionRegression
{
    static void Check(bool condition,string why="纠错学习断言失败"){if(!condition)throw new Exception(why);}
    static CorrectionApproval Approval(CorrectionCandidate c,string? scope=null)=>new(c.Corrected,c.Original,"专业术语",scope??c.ProjectId,c.Count>=3?5:4);
    static async Task<CorrectionCandidate> Candidate(ControllerFixture f,string before="请安排人材盘点。",string after="请安排人才盘点。")
    {
        var source=await f.Seed(before);await f.App.LoadSessionAsync(source.Session);await f.App.EditAsync(source.Segment.Id,after);await f.Settle();
        return (await f.App.Repository.CorrectionsAsync("default")).Single();
    }
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("纠错比较发现中文词条、机构、英文拼写和大小写，支持多个修改",()=>
        {
            foreach(var (before,after,old,word) in new[]{
                ("请安排人材盘点。","请安排人才盘点。","人材盘点","人才盘点"),
                ("原字能院。","原子能院。","原字能院","原子能院"),
                ("使用DeapSeak模型。","使用DeepSeek模型。","DeapSeak","DeepSeek"),
                ("填写kpi。","填写KPI。","kpi","KPI"),
                ("使用DeepSee。","使用DeepSeek。","DeepSee","DeepSeek"),
                ("联系吉田。","联系𠮷田。","联系吉田","联系𠮷田")})
            {
                var changes=CorrectionRules.Detect(before,after);Check(changes.Any(c=>c.Original==old&&c.Corrected==word),$"{before} → {after}: {JsonSerializer.Serialize(changes)}");
            }
            var several=CorrectionRules.Detect("请安排人材盘点，并开展新酬管理。","请安排人才盘点，并开展薪酬管理。");Check(several.Count==2);
            return Task.CompletedTask;
        });
        await test("纠错比较跳过标点、语气词删除、数值、否定和大幅改写",()=>
        {
            foreach(var (a,b) in new[]{("嗯，人才盘点。","人才盘点。"),("人才盘点，","人才盘点。"),("温度 30 度。","温度 40 度。"),("可以提交。","不可以提交。"),("就是这个。","就是那个。"),("原文。","原文。")})Check(CorrectionRules.Detect(a,b).Count==0,a);
            var watch=Stopwatch.StartNew();Check(CorrectionRules.Detect(new string('甲',20000),new string('乙',20000)).Count==0);Check(watch.Elapsed<TimeSpan.FromSeconds(3));
            Check(CorrectionRules.Detect(new string('甲',20001),"短文。").Count==0);return Task.CompletedTask;
        });
        await test("纠错差异随机位置、长上下文及 Unicode 引用均来自真实原文",()=>
        {
            var random=new Random(1743);
            for(int i=0;i<120;i++)
            {
                string prefix=new('前',random.Next(100)),suffix=new('后',random.Next(100));
                var before=prefix+"，使用DeepSeak。"+suffix;var after=prefix+"，使用DeepSeek。"+suffix;
                var pair=CorrectionRules.Detect(before,after).Single();Check(pair.Original=="DeepSeak"&&pair.Corrected=="DeepSeek"&&before.Contains(pair.BeforeContext)&&after.Contains(pair.AfterContext));
            }
            var far=CorrectionRules.Detect("DeapSeek。"+new string('中',18000)+"。Qwin。","DeepSeek。"+new string('中',18000)+"。Qwen。");Check(far.Count==2);return Task.CompletedTask;
        });
        await test("默认启用本地纠错，保存修订生成待确认项且不调用 HTTP",async()=>
        {
            await using var f=await ControllerFixture.Create();Check(f.App.Settings.LearnCorrections);
            var c=await Candidate(f);Check(c.State==CorrectionState.Pending&&c.Count==1&&f.App.Terms.Count==0&&f.Calls==0);
            var evidence=(await f.App.Repository.CorrectionEvidenceAsync(c.Id)).Single();Check(evidence.BeforeContext.Contains("人材盘点")&&evidence.AfterContext.Contains("人才盘点"));
            await f.Reopen();var saved=(await f.App.Repository.CorrectionsAsync("default")).Single();Check(saved.Id==c.Id&&saved.Count==1);
        });
        await test("按不同片段累计纠错次数，重试保存和无变化编辑不重复计数",async()=>
        {
            await using var f=await ControllerFixture.Create();var c=await Candidate(f);await f.App.RetrySaveAsync();await f.App.RetrySaveAsync();
            Check((await f.App.Repository.CorrectionsAsync("default")).Single().Count==1);
            await Candidate(f);await Candidate(f);var current=(await f.App.Repository.CorrectionsAsync("default")).Single();Check(current.Count==3&&current.FrequencyLabel=="多次纠正");
            var s=(await f.App.SnapshotAsync()).Segments.Single();await f.App.EditAsync(s.Id,s.FinalText);await f.Settle();Check((await f.App.Repository.CorrectionsAsync("default")).Single().Count==3);
        });
        await test("纠错确认后进入真实热词和润色保护，确认前不生效",async()=>
        {
            await using var f=await ControllerFixture.Create();var c=await Candidate(f);Check(Lexicon.Select(f.App.Terms,"default").Count==0);
            await f.App.ConfirmCorrectionAsync(c,Approval(c));var term=f.App.Terms.Single();Check(term.Text=="人才盘点"&&term.Origin=="CorrectionLearning"&&term.Alias=="人材盘点"&&term.Weight==4&&term.Pinned&&term.Protect);
            using var frame=JsonDocument.Parse(BailianProtocol.Start("task",f.App.Settings,Lexicon.Select(f.App.Terms,"default")));Check(frame.RootElement.GetProperty("payload").GetProperty("parameters").GetProperty("vocabulary").GetProperty("人才盘点").GetInt32()==4);
            Check(Lexicon.Matches("安排人才盘点。",f.App.Terms,"default").Single()==term.Text&&f.Calls==0);
        });
        await test("纠错确认可编辑标准词、分类和全局范围，保持旧正文",async()=>
        {
            await using var f=await ControllerFixture.Create();var c=await Candidate(f);string body=TranscriptText.Render(await f.App.SnapshotAsync());
            await f.App.ConfirmCorrectionAsync(c,new("人才盘点系统","人材盘点","产品型号","*",5));var term=f.App.Terms.Single();
            Check(term.Text=="人才盘点系统"&&term.Scope=="*"&&term.Category=="产品型号"&&term.Weight==5&&TranscriptText.Render(await f.App.SnapshotAsync())==body);
            Check((await f.App.Repository.CorrectionsAsync("default")).Single().DisplayText=="人才盘点系统");
        });
        await test("关闭纠错学习、总许可、会话许可或文本保存均不采集",async()=>
        {
            foreach(var option in new[]{"correction","global","session","memory"})
            {
                await using var f=await ControllerFixture.Create();var s=await f.Seed("人材盘点。");await f.App.LoadSessionAsync(s.Session);
                if(option=="session")await f.App.SetSessionLearningAsync(false);
                else await f.App.SaveSettingsAsync(f.App.Settings with{LearnCorrections=option!="correction",AllowLearning=option!="global",SaveMemory=option!="memory"},f.App.Keys);
                await f.App.EditAsync(s.Segment.Id,"人才盘点。");await f.App.Repository.BarrierAsync();Check((await f.App.Repository.CorrectionsAsync("default")).Count==0,option);
            }
        });
        await test("忽略候选后重复纠错继续累计，恢复候选可再次确认",async()=>
        {
            await using var f=await ControllerFixture.Create();var c=await Candidate(f);await f.App.SetCorrectionIgnoredAsync(c,true);await Candidate(f);
            var saved=(await f.App.Repository.CorrectionsAsync("default")).Single();Check(saved.State==CorrectionState.Ignored&&saved.Count==2);
            await f.App.SetCorrectionIgnoredAsync(saved,false);await f.App.ConfirmCorrectionAsync(saved,Approval(saved));Check(f.App.Terms.Count==1);
        });
        await test("撤销、恢复原文和删除片段清理未确认候选且不学习反向修改",async()=>
        {
            foreach(var action in new[]{"撤销","恢复原文","删除"})
            {
                await using var f=await ControllerFixture.Create();await Candidate(f);var s=(await f.App.SnapshotAsync()).Segments.Single();
                await f.App.EditAsync(s.Id,"",action);await f.Settle();Check((await f.App.Repository.CorrectionsAsync("default")).Count==0,action);
            }
        });
        await test("多步编辑及重启后的撤销游标同步纠错来源",async()=>
        {
            await using var f=await ControllerFixture.Create();var source=await f.Seed("请安排人材盘点，并开展新酬管理。");await f.App.LoadSessionAsync(source.Session);
            await f.App.EditAsync(source.Segment.Id,"请安排人才盘点，并开展新酬管理。");await f.Settle();
            await f.App.EditAsync(source.Segment.Id,"请安排人才盘点，并开展薪酬管理。");await f.Settle();Check((await f.App.Repository.CorrectionsAsync("default")).Count==2);
            await f.Reopen();await f.App.LoadSessionAsync(source.Session);await f.App.UndoAsync(source.Segment.Id);await f.Settle();
            var left=(await f.App.Repository.CorrectionsAsync("default")).Single();Check(left.Corrected=="人才盘点");
            await f.App.UndoAsync(source.Segment.Id);await f.Settle();Check((await f.App.Repository.CorrectionsAsync("default")).Count==0);
        });
        await test("会话撤回学习许可后候选失效，过时确认请求不写入词库",async()=>
        {
            await using var f=await ControllerFixture.Create();var c=await Candidate(f);await f.App.SetSessionLearningAsync(false);
            bool rejected=false;try{await f.App.ConfirmCorrectionAsync(c,Approval(c));}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&f.App.Terms.Count==0&&(await f.App.Repository.CorrectionsAsync("default")).Count==0);
        });
        await test("候选和正文保存整批回滚，失败重试补齐候选且不重复",async()=>
        {
            await using var f=await ControllerFixture.Create();var source=await f.Seed("人材盘点。");await f.App.LoadSessionAsync(source.Session);
            f.Protector.Fail=j=>j.TryGetProperty("corrected",out _);await f.App.EditAsync(source.Segment.Id,"人才盘点。");await f.App.Repository.BarrierAsync();
            Check((await f.App.Repository.CorrectionsAsync("default")).Count==0&&(await f.App.Repository.LoadSessionAsync(source.Session.Id))!.Segments.Single().FinalText=="人材盘点。");
            f.Protector.Fail=null;await f.App.RetrySaveAsync();Check((await f.App.Repository.CorrectionsAsync("default")).Single().Count==1&&(await f.App.SnapshotAsync()).Unsaved==0);
        });
        await test("学习确认失败时词条和状态一起回滚，重复确认不创建重名词",async()=>
        {
            await using var f=await ControllerFixture.Create();var c=await Candidate(f);f.Protector.Fail=j=>j.TryGetProperty("state",out var v)&&v.GetString()=="learned";
            bool failed=false;try{await f.App.ConfirmCorrectionAsync(c,Approval(c));}catch(IOException){failed=true;}
            Check(failed&&(await f.App.Repository.TermsAsync("default")).Count==0&&(await f.App.Repository.CorrectionsAsync("default")).Single().State==CorrectionState.Pending);
            f.Protector.Fail=null;await f.App.ConfirmCorrectionAsync(c,Approval(c));bool rejected=false;try{await f.App.ConfirmCorrectionAsync(c,Approval(c));}catch(InvalidOperationException){rejected=true;}Check(rejected&&f.App.Terms.Count==1);
        });
        await test("已有词条合并保留手工属性与证据，撤销学习恢复原设置",async()=>
        {
            await using var f=await ControllerFixture.Create();var source=await f.Seed("人才盘点。");
            var evidence=new TermEvidence(source.Session.Id,source.Segment.Id,source.Segment.SourceRevision,source.Segment.EditRevision,source.Segment.RawText,false);
            await f.App.Repository.SaveTermAsync(new(){Text="人才盘点",Weight=2,Category="固定表达",Pinned=false,Protect=false,Alias="原有别名",Evidence=[evidence]});await f.Reopen();
            var old=f.App.Terms.Single();var c=await Candidate(f);await f.App.ConfirmCorrectionAsync(c,Approval(c));Check(f.App.Terms.Single().Id==old.Id&&f.App.Terms.Single().Category==old.Category&&f.App.Terms.Single().Evidence.SequenceEqual(old.Evidence));
            await f.App.RevokeCorrectionAsync((await f.App.Repository.CorrectionsAsync("default")).Single());var restored=f.App.Terms.Single();
            Check(restored.Id==old.Id&&restored.Weight==2&&!restored.Pinned&&!restored.Protect&&restored.Alias==old.Alias&&restored.Evidence.SequenceEqual(old.Evidence));
        });
        await test("确认已有历史提取词后删除旧来源，保留明确学习的词条",async()=>
        {
            await using var f=await ControllerFixture.Create();var source=await f.Seed("人才盘点。");
            await f.App.Repository.SaveTermAsync(new(){Text="人才盘点",Origin="Extracted",State=TermState.Candidate,Evidence=[new(source.Session.Id,source.Segment.Id,source.Segment.SourceRevision,source.Segment.EditRevision,source.Segment.RawText,false)]});await f.Reopen();
            var c=await Candidate(f);await f.App.ConfirmCorrectionAsync(c,Approval(c));var learned=f.App.Terms.Single();
            Check(learned.Origin=="CorrectionLearning"&&learned.State==TermState.Enabled&&learned.Evidence.Count==1);
            await f.App.DeleteSessionAsync(source.Session);var saved=f.App.Terms.Single();
            Check(saved.Id==learned.Id&&saved.Evidence.Count==0&&(await f.App.Repository.CorrectionsAsync("default")).Single().State==CorrectionState.Learned);
        });
        await test("禁用词条不会被纠错确认重新启用",async()=>
        {
            await using var f=await ControllerFixture.Create();await f.App.SaveTermAsync(new(){Text="人才盘点",State=TermState.Disabled});var c=await Candidate(f);
            bool rejected=false;try{await f.App.ConfirmCorrectionAsync(c,Approval(c));}catch(InvalidOperationException){rejected=true;}Check(rejected&&f.App.Terms.Single().State==TermState.Disabled);
        });
        await test("撤销新词学习可删除本次新词，之后恢复候选可重新学习",async()=>
        {
            await using var f=await ControllerFixture.Create();var c=await Candidate(f);await f.App.ConfirmCorrectionAsync(c,Approval(c));string id=f.App.Terms.Single().Id;
            await f.App.RevokeCorrectionAsync(c);Check(f.App.Terms.Count==0&&(await f.App.Repository.CorrectionsAsync("default")).Single().State==CorrectionState.Ignored);
            await f.App.SetCorrectionIgnoredAsync(c,false);await f.App.ConfirmCorrectionAsync(c,Approval(c));Check(f.App.Terms.Count==1&&f.App.Terms.Single().Id!=id);
        });
        await test("学习后手工修改词条不被旧撤销覆盖，删除词条同步忽略记录",async()=>
        {
            await using var f=await ControllerFixture.Create();var c=await Candidate(f);await f.App.ConfirmCorrectionAsync(c,Approval(c));await f.App.SaveTermAsync(f.App.Terms.Single() with{Weight=5});
            bool rejected=false;try{await f.App.RevokeCorrectionAsync(c);}catch(InvalidOperationException){rejected=true;}Check(rejected&&f.App.Terms.Single().Weight==5);
            await f.App.DeleteTermAsync(f.App.Terms.Single());var saved=(await f.App.Repository.CorrectionsAsync("default")).Single();Check(saved.State==CorrectionState.Ignored&&saved.TermId==null);
        });
        await test("删除来源清理引用和待确认项，明确学习的词条独立保留",async()=>
        {
            await using var f=await ControllerFixture.Create();var c=await Candidate(f);await f.App.ConfirmCorrectionAsync(c,Approval(c));var session=(await f.App.SnapshotAsync()).Session!;
            await f.App.DeleteSessionAsync(session);Check((await f.App.Repository.CorrectionEvidenceAsync(c.Id)).Count==0&&f.App.Terms.Count==1&&(await f.App.Repository.CorrectionsAsync("default")).Single().Count==0);
        });
        await test("纠错记录按项目隔离，项目删除清理记录，全局词条可跨项目使用",async()=>
        {
            await using var f=await ControllerFixture.Create();var c=await Candidate(f);await f.App.ConfirmCorrectionAsync(c,Approval(c,"*"));
            await f.App.CreateProjectAsync("另一个项目");Check((await f.App.Repository.CorrectionsAsync(f.App.Settings.ProjectId)).Count==0&&Lexicon.Select(f.App.Terms,f.App.Settings.ProjectId).Count==1);
            bool rejected=false;try{await f.App.RevokeCorrectionAsync(c);}catch(InvalidOperationException){rejected=true;}Check(rejected);
            await f.App.ChangeProjectAsync("default");await f.App.DeleteCurrentProjectAsync();Check((await f.App.Repository.CorrectionsAsync("default")).Count==0&&f.App.Terms.Count==1);
        });
        await test("旧版数据库升级保留记录，纠错内容加密存储且设置兼容",async()=>
        {
            string folder=Path.Combine(Path.GetTempPath(),"CorrectionUpgrade-"+JsonCodec.Id());Directory.CreateDirectory(folder);string path=Path.Combine(folder,"test.db");var protector=new TestProtector();
            var session=new SessionData();var segment=new SegmentData{SessionId=session.Id,TaskId=JsonCodec.Id(),SentenceId=1,AsrState=AsrState.Confirmed,OutputState=OutputState.Published,RawText="DeapSeek",FinalText="DeapSeek"};
            try
            {
                await using(var repo=new MemoryRepository(path,protector)){await repo.InitializeAsync();await repo.SaveSegmentAsync(session,segment);}
                using(var db=new SqliteConnection("Data Source="+path)){db.Open();using var command=db.CreateCommand();command.CommandText="DROP TABLE correction_decisions;DROP TABLE correction_sources;DROP TABLE corrections;PRAGMA user_version=1;";command.ExecuteNonQuery();}
                await using(var repo=new MemoryRepository(path,protector))
                {
                    await repo.InitializeAsync();Check((await repo.LoadSessionAsync(session.Id))!.Segments.Single().RawText=="DeapSeek");
                    var engine=new TranscriptEngine(session);engine.Restore([segment]);var changed=engine.Edit(segment.Id,"DeepSeek","编辑",true);await repo.SaveSegmentAsync(engine.Session,changed,true);
                    var c=(await repo.CorrectionsAsync("default")).Single();Check(c.Corrected=="DeepSeek");
                    using var db=new SqliteConnection("Data Source="+path);db.Open();using var command=db.CreateCommand();command.CommandText="SELECT payload FROM corrections UNION ALL SELECT payload FROM correction_sources";using var reader=command.ExecuteReader();
                    while(reader.Read())Check(!Encoding.UTF8.GetString((byte[])reader[0]).Contains("DeepSeek"));
                }
                Check(JsonSerializer.Deserialize<AppSettings>("{}",JsonCodec.Options)!.LearnCorrections);
            }
            finally{SqliteConnection.ClearAllPools();Directory.Delete(folder,true);}
        });
    }
}
