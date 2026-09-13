using System.Text.Json;
using Microsoft.Data.Sqlite;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

static class TermUsageRegression
{
    static void Check(bool value,string reason="词频统计断言失败"){if(!value)throw new Exception(reason);}
    static SegmentData Part(SessionData session,string raw,int sentence=1)=>new()
    {
        SessionId=session.Id,TaskId=session.Id,SentenceId=sentence,TaskOrder=1,
        RawText=raw,FinalText=raw,AsrState=AsrState.Confirmed,OutputState=OutputState.Published,SourceRevision=1
    };
    static async Task<TermData> Term(MemoryRepository repo,TermData term,string project="default")=>(await repo.TermsAsync(project)).Single(t=>t.Id==term.Id);
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("真实词频按会话去重，重试与分片不增频且不修改词条修订",async()=>
        {
            await using var f=await Fixture.Create();var word=new TermData{Text="JUNA"};await f.Repo.SaveTermAsync(word);
            var first=new SessionData{CreatedAt=DateTimeOffset.UtcNow.AddDays(-2)};var part=Part(first,"JUNA 与 JUNA");
            await f.Repo.SaveSegmentAsync(first,part,learnUsage:true);await f.Repo.SaveSegmentAsync(first,part,learnUsage:true);
            await f.Repo.SaveSegmentAsync(first,Part(first,"JUNA",2),learnUsage:true);
            var saved=await Term(f.Repo,word);Check(saved.UsageCount==1&&saved.LastUsedAt==first.CreatedAt&&saved.Revision==word.Revision);
            var second=new SessionData();await f.Repo.SaveSegmentAsync(second,Part(second,"JUNA"),learnUsage:true);
            saved=await Term(f.Repo,word);Check(saved.UsageCount==2&&saved.CorrectionCount==0&&saved.LastUsedAt==second.CreatedAt&&saved.LastCorrectedAt==null);
        });
        await test("词频只采集允许学习的已确认已发布正文，机器改写不自增",async()=>
        {
            await using var f=await Fixture.Create();var word=new TermData{Text="JUNA"};await f.Repo.SaveTermAsync(word);
            foreach(var mode in new[]{"partial","waiting","deleted","disabled","unpermitted","automatic","boundary"})
            {
                var session=new SessionData{AllowLearning=mode!="disabled"};var part=Part(session,mode=="automatic"?"朱娜":mode=="boundary"?"SuperJUNA":"JUNA") with
                {
                    FinalText="JUNA",AsrState=mode=="partial"?AsrState.Partial:AsrState.Confirmed,
                    OutputState=mode=="waiting"?OutputState.Waiting:mode=="deleted"?OutputState.Deleted:OutputState.Published
                };
                await f.Repo.SaveSegmentAsync(session,part,learnUsage:mode!="unpermitted");
                Check((await Term(f.Repo,word)).UsageCount==0,mode);
            }
            var valid=new SessionData();await f.Repo.SaveSegmentAsync(valid,Part(valid,"JUNA"),learnUsage:true);Check((await Term(f.Repo,word)).UsageCount==1);
        });
        await test("人工修改统计最终词条并记录纠错，撤销及删除重算来源",async()=>
        {
            await using var f=await Fixture.Create();var word=new TermData{Text="DeepSeek"};await f.Repo.SaveTermAsync(word);
            var session=new SessionData();var source=Part(session,"使用 DeapSeek。");var engine=new TranscriptEngine(session);engine.Restore([source]);
            var edited=engine.Edit(source.Id,"使用 DeepSeek。","编辑",true);await f.Repo.SaveSegmentAsync(engine.Session,edited,true,true);
            var saved=await Term(f.Repo,word);Check(saved.UsageCount==1&&saved.CorrectionCount==1&&saved.LastCorrectedAt==CorrectionRules.Active(edited).Single().At);
            await f.Repo.SaveSegmentAsync(session,Part(session,"DeepSeek",2),learnUsage:true);
            var undo=engine.Edit(source.Id,"","撤销",true);await f.Repo.SaveSegmentAsync(engine.Session,undo,true,true);
            saved=await Term(f.Repo,word);Check(saved.UsageCount==1&&saved.CorrectionCount==0&&saved.LastCorrectedAt==null);
            var second=(await f.Repo.SegmentsAsync(session.Id)).Single(x=>x.SentenceId==2);
            await f.Repo.SaveSegmentAsync(session,second with{OutputState=OutputState.Deleted,EditRevision=1,Revision=2},learnUsage:true);
            saved=await Term(f.Repo,word);Check(saved.UsageCount==0&&saved.LastUsedAt==null);
        });
        await test("旧片段写回不清除新词频，过期保存不覆盖学习撤回",async()=>
        {
            await using var f=await Fixture.Create();var word=new TermData{Text="JUNA"};await f.Repo.SaveTermAsync(word);
            var session=new SessionData();var source=Part(session,"JUNA") with{Revision=5};await f.Repo.SaveSegmentAsync(session,source,learnUsage:true);
            await f.Repo.SaveSegmentAsync(session,source with{Revision=4,RawText="其他",FinalText="其他"},learnUsage:true);Check((await Term(f.Repo,word)).UsageCount==1);
            await f.Repo.SaveLearningPermissionAsync(session with{AllowLearning=false,LearningRevision=2,Revision=3});Check((await Term(f.Repo,word)).UsageCount==0);
            await f.Repo.SaveSegmentAsync(session with{Revision=10},source with{Revision=6},learnUsage:true);
            Check((await Term(f.Repo,word)).UsageCount==0&&!(await f.Repo.LoadSessionAsync(session.Id))!.Session.AllowLearning);
        });
        await test("全局词频按项目隔离，本项目规范化同名词优先且禁用词不采集",async()=>
        {
            await using var f=await Fixture.Create();var global=new TermData{Text="JUNA",Scope="*"};var local=new TermData{Text=" JUNA ",Scope="default"};var disabled=new TermData{Text="DeepSeek",State=TermState.Disabled};
            await f.Repo.ImportTermsAsync([global,local,disabled]);
            var p=new SessionData();await f.Repo.SaveSegmentAsync(p,Part(p,"JUNA DeepSeek"),learnUsage:true);
            Check((await Term(f.Repo,local)).UsageCount==1&&(await Term(f.Repo,global)).UsageCount==0&&(await Term(f.Repo,disabled)).UsageCount==0);
            var q=new SessionData{ProjectId="q"};await f.Repo.SaveSegmentAsync(q,Part(q,"JUNA"),learnUsage:true);
            Check((await Term(f.Repo,global,"q")).UsageCount==1&&(await Term(f.Repo,global)).UsageCount==0);
            Check((await Term(f.Repo,global,"unused-project")).UsageCount==0);
        });
        await test("删除历史及项目级联清理词频，删除词条清理观察记录",async()=>
        {
            await using var f=await Fixture.Create();var word=new TermData{Text="JUNA",Scope="*"};await f.Repo.SaveTermAsync(word);
            var p=new SessionData();var q=new SessionData{ProjectId="q"};
            await f.Repo.SaveSegmentAsync(p,Part(p,"JUNA"),learnUsage:true);await f.Repo.SaveSegmentAsync(q,Part(q,"JUNA"),learnUsage:true);
            await f.Repo.DeleteSessionAsync(p.Id);Check((await Term(f.Repo,word)).UsageCount==0&&(await Term(f.Repo,word,"q")).UsageCount==1);
            await f.Repo.DeleteProjectAsync("q");Check((await Term(f.Repo,word,"q")).UsageCount==0);
            var extra=new SessionData();await f.Repo.SaveSegmentAsync(extra,Part(extra,"JUNA"),learnUsage:true);await f.Repo.DeleteTermAsync(word);
            using var db=new SqliteConnection("Data Source="+f.Path);db.Open();using var query=db.CreateCommand();query.CommandText="SELECT COUNT(*) FROM term_observations";Check(Convert.ToInt64(query.ExecuteScalar())==0);
        });
        await test("修改权重保留真实词频，改名或迁移范围清零旧词统计",async()=>
        {
            await using var f=await Fixture.Create();var word=new TermData{Text="JUNA"};await f.Repo.SaveTermAsync(word);var session=new SessionData();
            await f.Repo.SaveSegmentAsync(session,Part(session,"JUNA"),learnUsage:true);
            var weighted=await f.Repo.SaveUserTermAsync(word with{Weight=5});Check((await Term(f.Repo,word)).UsageCount==1);
            var renamed=await f.Repo.SaveUserTermAsync(weighted with{Text="SuperJUNA"});Check((await Term(f.Repo,word)).UsageCount==0);
            var newer=new SessionData();await f.Repo.SaveSegmentAsync(newer,Part(newer,"SuperJUNA"),learnUsage:true);Check((await Term(f.Repo,word)).UsageCount==1);
            await f.Repo.SaveUserTermAsync(renamed with{Scope="*"});Check((await Term(f.Repo,word)).UsageCount==0);
        });
        await test("导入词条无法伪造使用统计，用户字段不会把统计写进备份",async()=>
        {
            await using var f=await Fixture.Create();var value=JsonSerializer.Deserialize<TermData>("{\"text\":\"JUNA\",\"usageCount\":9999,\"correctionCount\":9999,\"lastUsedAt\":\"2030-01-01T00:00:00Z\"}",JsonCodec.Options)!;
            Check(value.UsageCount==0&&value.CorrectionCount==0&&value.LastUsedAt==null);
            await f.Repo.ImportUserTermsAsync([value with{UsageCount=9999,CorrectionCount=9999,LastUsedAt=DateTimeOffset.UtcNow}]);var actual=(await f.Repo.TermsAsync("default")).Single();
            Check(actual.UsageCount==0&&actual.CorrectionCount==0&&actual.LastUsedAt==null);
            string json=JsonSerializer.Serialize(value with{UsageCount=100},JsonCodec.Options);Check(!json.Contains("usageCount")&&!json.Contains("correctionCount")&&!json.Contains("lastUsedAt"));
        });
        await test("版本二数据库升级保存原数据且历史不会被默认回填词频",async()=>
        {
            await using var f=await Fixture.Create();var word=new TermData{Text="JUNA"};await f.Repo.SaveTermAsync(word);var session=new SessionData();await f.Repo.SaveSegmentAsync(session,Part(session,"JUNA"));
            using(var db=new SqliteConnection("Data Source="+f.Path)){db.Open();using var command=db.CreateCommand();command.CommandText="DROP TABLE term_observations;PRAGMA user_version=2;";command.ExecuteNonQuery();}
            await f.Repo.InitializeAsync();Check((await f.Repo.LoadSessionAsync(session.Id))!.Segments.Single().RawText=="JUNA"&&(await Term(f.Repo,word)).UsageCount==0);
            using(var db=new SqliteConnection("Data Source="+f.Path)){db.Open();using var command=db.CreateCommand();command.CommandText="PRAGMA user_version";Check(Convert.ToInt64(command.ExecuteScalar())==3);}
        });
        await test("正文保存失败与词频同事务回滚，恢复后重试只计一次",async()=>
        {
            await using var f=await Fixture.Create();var word=new TermData{Text="JUNA"};await f.Repo.SaveTermAsync(word);var session=new SessionData();var part=Part(session,"JUNA");
            f.Protector.Fail=x=>x.TryGetProperty("finalText",out _);bool rejected=false;
            try{await f.Repo.SaveSegmentAsync(session,part,learnUsage:true);}catch(IOException){rejected=true;}
            Check(rejected&&(await Term(f.Repo,word)).UsageCount==0&&await f.Repo.LoadSessionAsync(session.Id)==null);
            f.Protector.Fail=null;await f.Repo.SaveSegmentAsync(session,part,learnUsage:true);await f.Repo.SaveSegmentAsync(session,part,learnUsage:true);Check((await Term(f.Repo,word)).UsageCount==1);
        });
    }
    sealed class Fixture:IAsyncDisposable
    {
        readonly string folder=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"TermUsage-"+JsonCodec.Id());
        public string Path=>System.IO.Path.Combine(folder,"test.db");
        public FaultProtector Protector {get;}=new();
        public MemoryRepository Repo {get;private set;}=null!;
        public static async Task<Fixture> Create(){var result=new Fixture();result.Repo=new(result.Path,result.Protector);await result.Repo.InitializeAsync();return result;}
        public async ValueTask DisposeAsync(){await Repo.DisposeAsync();SqliteConnection.ClearAllPools();Directory.Delete(folder,true);}
    }
}
