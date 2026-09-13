using System.Text.Json;
using Microsoft.Data.Sqlite;
using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

internal static class LexiconReviewRegression
{
    private static void Check(bool value,string why="词库审查回归失败") { if(!value)throw new Exception(why); }
    private static async Task Reject(Func<Task> operation)
    {
        try { await operation(); } catch(ArgumentException) { return; } catch(InvalidOperationException) { return; }
        throw new Exception("应拒绝此操作");
    }

    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("科学词大小写在手工保存、导入、重启和识别请求中保持独立",async()=>
        {
            await using var f=await Fixture.Create();
            foreach(string word in new[]{"mA","MA","Co","CO"})await f.Repo.SaveUserTermAsync(new(){Text=word});
            await Reject(()=>f.Repo.SaveUserTermAsync(new(){Text=" mA "}));
            Check(await f.Repo.ImportUserTermsAsync(TermExchange.Parse("mA\nMA\nmW\nMW",".txt","default"))==2);
            await f.Reopen();var terms=await f.Repo.TermsAsync("default");Check(terms.Count==6);
            var selected=Lexicon.Select(terms,"default");
            using var wire=JsonDocument.Parse(BailianProtocol.Start("review",new(){LegacyEndpoint=true},selected));
            var vocabulary=wire.RootElement.GetProperty("payload").GetProperty("parameters").GetProperty("vocabulary");
            Check(vocabulary.EnumerateObject().Count()==6&&vocabulary.TryGetProperty("mA",out _)&&vocabulary.TryGetProperty("MA",out _));
            Check(Lexicon.Matches("Co 和 mA",terms,"default").Order().SequenceEqual(new[]{"Co","mA"}));
        });
        await test("不同大小写词条的使用量和最近时间不会交叉累计",async()=>
        {
            await using var f=await Fixture.Create();
            foreach(string word in new[]{"mA","MA","Co","CO"})await f.Repo.SaveUserTermAsync(new(){Text=word});
            var lower=await f.Sample("mA 和 Co");await f.Sample("MA 和 CO");await f.Sample("MA 和 CO");
            var terms=(await f.Repo.TermsAsync("default")).ToDictionary(t=>t.Text);
            Check(terms["mA"].UsageCount==1&&terms["Co"].UsageCount==1&&terms["MA"].UsageCount==2&&terms["CO"].UsageCount==2);
            await f.Repo.SaveSegmentAsync(lower.Session,lower.Segment,learnUsage:true);
            Check((await f.Repo.TermsAsync("default")).Single(t=>t.Text=="mA").UsageCount==1);
        });
        await test("AI词库分批去重保留科学大小写并过滤真正重名",async()=>
        {
            var generator=new LexiconGenerator((_,_)=>Task.FromResult("""{"terms":[{"text":"mA"},{"text":"MA"},{"text":"Co"},{"text":"CO"},{"text":" mA "}]}"""));
            var result=await generator.GenerateAsync(new("科学单位和化学式",4,"default"),null,CancellationToken.None);
            Check(result.Terms.Count==4&&result.Requests==1&&result.Rejected==1);
        });
        await test("历史提词不会把 Co 和 CO 合并或互相抑制",async()=>
        {
            await using var f=await Fixture.Create();var source=await f.Sample("Co 与 CO 的区别。");
            var deleted=await f.Repo.SaveUserTermAsync(new(){Text="Co"});await f.Repo.DeleteTermAsync(deleted);
            var slices=ExtractionPlanner.Pending(source.Session,[source.Segment]);
            var evidence=new TermEvidence(source.Session.Id,source.Segment.Id,source.Segment.SourceRevision,0,source.Segment.RawText,false);
            var result=await f.Repo.CommitExtractionAsync(source.Session,slices,[new(){Text="Co",Evidence=[evidence]},new(){Text="CO",Evidence=[evidence]}],CancellationToken.None);
            Check(result.Added==1&&result.Skipped==1&&(await f.Repo.TermsAsync("default")).Single().Text=="CO");
        });
        await test("同一标准词的多个已确认别名同时执行且各自保留来源计数",async()=>
        {
            await using var f=await Fixture.Create();var first=await f.Learn("朱娜","JUNA");var second=await f.Learn("尤娜","JUNA");
            Check((await f.Repo.TermsAsync("default")).Count==1);
            var active=await f.Repo.ActiveCorrectionTermsAsync("default");
            Check(active.Count==2&&active.Select(t=>t.Id).Distinct().Count()==1);
            Check(ConfirmedCorrections.Apply("朱娜，尤娜。",active,"default").Text=="JUNA，JUNA。");
            var rows=await f.Repo.CorrectionsAsync("default");Check(rows.All(d=>d.Count==1&&d.ReplacementValid));
            Check(rows.Select(d=>d.Id).ToHashSet().SetEquals([first.Id,second.Id]));
            Check((await f.Repo.TermsAsync("default")).Single().CorrectionCount==2);
            await f.Reopen();Check((await f.Repo.ActiveCorrectionTermsAsync("default")).Count==2);
        });
        await test("逐条关闭和撤回多别名不破坏其他映射，最后撤回清理独立新词",async()=>
        {
            foreach(bool reverse in new[]{false,true})
            {
                await using var f=await Fixture.Create();var a=await f.Learn("朱娜","JUNA");var b=await f.Learn("尤娜","JUNA");
                await f.Repo.SetAutomaticCorrectionAsync(a.Id,"default",false);
                Check((await f.Repo.ActiveCorrectionTermsAsync("default")).Single().Alias=="尤娜");
                await f.Repo.SetAutomaticCorrectionAsync(a.Id,"default",true);
                var first=reverse?b:a;var last=reverse?a:b;
                await f.Repo.RevokeCorrectionAsync(first.Id,"default");
                Check((await f.Repo.TermsAsync("default")).Count==1&&(await f.Repo.ActiveCorrectionTermsAsync("default")).Single().Alias==last.Original);
                await f.Repo.RevokeCorrectionAsync(last.Id,"default");
                Check((await f.Repo.TermsAsync("default")).Count==0&&(await f.Repo.ActiveCorrectionTermsAsync("default")).Count==0);
            }
        });
        await test("撤回多别名恢复共享词条的原设置，并保护后续人工修改",async()=>
        {
            await using var f=await Fixture.Create();await f.Repo.SaveUserTermAsync(new(){Text="JUNA",Weight=2,Pinned=false,Protect=false,Alias="原追溯说明"});
            var a=await f.Learn("朱娜","JUNA");var b=await f.Learn("尤娜","JUNA");
            await f.Repo.RevokeCorrectionAsync(a.Id,"default");await f.Repo.RevokeCorrectionAsync(b.Id,"default");
            var restored=(await f.Repo.TermsAsync("default")).Single();
            Check(restored.Weight==2&&!restored.Pinned&&!restored.Protect&&restored.Alias=="原追溯说明");
            await f.Repo.SetCorrectionIgnoredAsync(a.Id,"default",false);
            await f.Repo.ConfirmCorrectionAsync(a.Id,"default",new("JUNA","朱娜","专业术语","default",4,true));
            var edited=(await f.Repo.TermsAsync("default")).Single();await f.Repo.SaveUserTermAsync(edited with{Weight=5});
            await Reject(()=>f.Repo.RevokeCorrectionAsync(a.Id,"default"));
            Check((await f.Repo.TermsAsync("default")).Single().Weight==5);
        });
        await test("旧版本多别名授权和回退链在升级后可恢复，无需重新授权已开启规则",async()=>
        {
            await using var f=await Fixture.Create();var a=await f.Learn("朱娜","JUNA");var first=(await f.Repo.TermsAsync("default")).Single();var b=await f.Learn("尤娜","JUNA");
            var rows=await f.Repo.CorrectionsAsync("default");var last=(await f.Repo.TermsAsync("default")).Single();
            await f.ReplaceDecision(rows.Single(d=>d.Id==a.Id) with{AppliedTermRevision=first.Revision,PriorTerm=null});
            await f.ReplaceDecision(rows.Single(d=>d.Id==b.Id) with{AppliedTermRevision=last.Revision,PriorTerm=first});
            await f.Reopen();var active=await f.Repo.ActiveCorrectionTermsAsync("default");Check(active.Count==2);
            await f.Repo.RevokeCorrectionAsync(a.Id,"default");await f.Repo.RevokeCorrectionAsync(b.Id,"default");
            Check((await f.Repo.TermsAsync("default")).Count==0);
        });
        await test("旧追溯记录显式启用使用自己的原写法，不采用共享词条最新别名",async()=>
        {
            await using var f=await Fixture.Create();var a=await f.Learn("朱娜","JUNA");await f.Learn("尤娜","JUNA");
            var saved=(await f.Repo.CorrectionsAsync("default")).Single(d=>d.Id==a.Id);
            await f.ReplaceDecision(saved with{LearnedAlias="",AutomaticReplacement=false});
            var term=(await f.Repo.TermsAsync("default")).Single();await f.Repo.SaveUserTermAsync(term with{Alias="另一个追溯写法"});
            Check((await f.Repo.ActiveCorrectionTermsAsync("default")).Count==1);
            await f.Repo.SetAutomaticCorrectionAsync(a.Id,"default",true);
            Check((await f.Repo.ActiveCorrectionTermsAsync("default")).Select(t=>t.Alias).ToHashSet().SetEquals(["朱娜","尤娜"]));
        });
        await test("替换链的管理状态与执行结果一致，关闭其中一条后可恢复",async()=>
        {
            await using var f=await Fixture.Create();await f.Learn("朱娜","JUNA");var chain=await f.Learn("JUNA","JUNO");
            var rows=await f.Repo.CorrectionsAsync("default");
            Check(rows.All(d=>d.AutomaticReplacement&&!d.ReplacementValid&&d.ReplacementReason.Contains("替换链")));
            Check((await f.Repo.ActiveCorrectionTermsAsync("default")).Count==0);
            await f.Repo.SetAutomaticCorrectionAsync(chain.Id,"default",false);
            var active=await f.Repo.ActiveCorrectionTermsAsync("default");Check(active.Count==1&&ConfirmedCorrections.Apply("朱娜",active,"default").Text=="JUNA");
            Check((await f.Repo.CorrectionsAsync("default")).Single(d=>d.Id!=chain.Id).ReplacementValid);
        });
        await test("同别名多目标的冲突明确显示，撤回冲突项后剩余映射恢复",async()=>
        {
            await using var f=await Fixture.Create();await f.Learn("朱娜","JUNA");var conflict=await f.Learn("朱娜","JUNO");
            Check((await f.Repo.CorrectionsAsync("default")).All(d=>!d.ReplacementValid&&d.ReplacementReason.Contains("多个标准词")));
            Check((await f.Repo.ActiveCorrectionTermsAsync("default")).Count==0);
            await f.Repo.RevokeCorrectionAsync(conflict.Id,"default");
            Check((await f.Repo.ActiveCorrectionTermsAsync("default")).Single().Text=="JUNA");
        });
        await test("多别名映射继续遵守范围、词条状态和删除，不从导入别名获得权限",async()=>
        {
            await using var f=await Fixture.Create();await f.Learn("朱娜","JUNA");await f.Learn("尤娜","JUNA");
            Check((await f.Repo.ActiveCorrectionTermsAsync("other")).Count==0);
            var word=(await f.Repo.TermsAsync("default")).Single();await f.Repo.SaveUserTermAsync(word with{State=TermState.Disabled});
            Check((await f.Repo.ActiveCorrectionTermsAsync("default")).Count==0&&(await f.Repo.CorrectionsAsync("default")).All(d=>d.ReplacementReason.Contains("未启用")));
            await f.Repo.DeleteTermAsync(word);Check((await f.Repo.CorrectionsAsync("default")).All(d=>d.State==CorrectionState.Ignored));
            await f.Repo.ImportUserTermsAsync([new(){Text="JUNA",Alias="朱娜"}]);Check((await f.Repo.ActiveCorrectionTermsAsync("default")).Count==0);
        });
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string folder=Path.Combine(Path.GetTempPath(),"LexiconReview-"+JsonCodec.Id());
        private readonly TestProtector protector=new();
        public MemoryRepository Repo {get;private set;}=null!;
        private string Database=>Path.Combine(folder,"review.db");
        public static async Task<Fixture> Create() { var f=new Fixture();f.Repo=new(f.Database,f.protector);await f.Repo.InitializeAsync();await f.Repo.SaveProjectAsync(new("default","默认项目"));return f; }
        public async Task Reopen() { await Repo.DisposeAsync();Repo=new(Database,protector);await Repo.InitializeAsync(); }
        public async Task<(SessionData Session,SegmentData Segment)> Sample(string text)
        {
            var session=new SessionData();var segment=new SegmentData{SessionId=session.Id,TaskId=JsonCodec.Id(),TaskOrder=1,SentenceId=1,RawText=text,FinalText=text,AsrState=AsrState.Confirmed,OutputState=OutputState.Published,SaveState=SaveState.Saved,SourceRevision=1};
            await Repo.SaveSegmentAsync(session,segment,learnUsage:true);return(session,segment);
        }
        public async Task<CorrectionCandidate> Learn(string original,string corrected)
        {
            var sample=await Sample(original);var at=DateTimeOffset.UtcNow;
            var change=new CorrectionChange(original,corrected,original,corrected,at);
            var segment=sample.Segment with{FinalText=corrected,EditRevision=1,Revision=2,Edits=[new(1,corrected,"编辑",at)],UndoHistory=[new(original),new(corrected,false,[change])],UndoPosition=1};
            await Repo.SaveSegmentAsync(sample.Session,segment,true,true);
            var decision=(await Repo.CorrectionsAsync("default")).Single(d=>d.Original==original&&d.Corrected==corrected);
            await Repo.ConfirmCorrectionAsync(decision.Id,"default",new(corrected,original,"专业术语","default",4,true));return decision;
        }
        public async Task ReplaceDecision(CorrectionCandidate value)
        {
            await Repo.BarrierAsync();using var db=new SqliteConnection("Data Source="+Database);db.Open();
            using var command=db.CreateCommand();command.CommandText="UPDATE corrections SET payload=$payload,revision=$revision WHERE id=$id";
            command.Parameters.AddWithValue("$payload",protector.Protect(JsonSerializer.SerializeToUtf8Bytes(value,JsonCodec.Options)));
            command.Parameters.AddWithValue("$revision",value.Revision);command.Parameters.AddWithValue("$id",value.Id);command.ExecuteNonQuery();
        }
        public async ValueTask DisposeAsync() { await Repo.DisposeAsync();Directory.Delete(folder,true); }
    }
}
