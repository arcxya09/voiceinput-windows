using Microsoft.Data.Sqlite;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Infrastructure;

public sealed partial class MemoryRepository
{
    private List<CorrectionCandidate> ReadCorrections(SqliteConnection c,string project)
    {
        using var query=Command(c,"SELECT c.payload,COUNT(s.segment) FROM corrections c LEFT JOIN correction_sources s ON s.candidate=c.id WHERE c.project=$p GROUP BY c.id ORDER BY COUNT(s.segment) DESC,c.rowid DESC",("$p",project));
        using var reader=query.ExecuteReader();var rows=new List<CorrectionCandidate>();
        while(reader.Read())rows.Add(Unpack<CorrectionCandidate>((byte[])reader[0]) with{Count=reader.GetInt32(1)});
        return rows;
    }
    public Task<List<CorrectionCandidate>> CorrectionsAsync(string project)=>Task.Run(()=>
    {
        using var c=Open();var rows=ReadCorrections(c,project);var terms=ReadTerms(c).ToDictionary(t=>t.Id);
        return rows.Select(d=>d with{ReplacementValid=d.TermId!=null&&terms.TryGetValue(d.TermId,out var term)
            &&term.State==TermState.Enabled&&term.Scope==d.LearnedScope&&term.Text==d.LearnedText&&term.Alias==d.LearnedAlias
            &&ConfirmedCorrections.IsSafePair(term.Alias,term.Text)}).ToList();
    });
    public Task<List<CorrectionEvidence>> CorrectionEvidenceAsync(string id)=>Task.Run(()=>
    {
        using var c=Open();using var query=Command(c,"SELECT payload FROM correction_sources WHERE candidate=$id ORDER BY created DESC LIMIT 30",("$id",id));
        using var reader=query.ExecuteReader();var list=new List<CorrectionEvidence>();while(reader.Read())list.Add(Unpack<CorrectionEvidence>((byte[])reader[0]));return list;
    });
    private CorrectionCandidate ReadCorrection(SqliteConnection c,string id)
    {
        using var query=Command(c,"SELECT payload FROM corrections WHERE id=$id",("$id",id));
        return query.ExecuteScalar() is byte[] bytes?Unpack<CorrectionCandidate>(bytes):throw new InvalidOperationException("该纠错候选已失效，请刷新列表。");
    }
    private void SaveCorrection(SqliteConnection c,CorrectionCandidate value)
    {
        using var command=Command(c,"INSERT INTO corrections(id,project,revision,payload) VALUES($id,$p,$r,$b) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,payload=excluded.payload",("$id",value.Id),("$p",value.ProjectId),("$r",value.Revision),("$b",Pack(value)));
        command.ExecuteNonQuery();
    }
    private static void PruneCorrections(SqliteConnection c)
    {
        // An ignored pair is an explicit preference. Confirmed terms are independently managed vocabulary.
        using var command=Command(c,"DELETE FROM corrections WHERE id NOT IN(SELECT candidate FROM correction_sources) AND id NOT IN(SELECT id FROM correction_decisions)");command.ExecuteNonQuery();
    }
    private static void MarkCorrectionDecision(SqliteConnection c,string id,bool decided)
    {
        using var command=Command(c,decided?"INSERT OR IGNORE INTO correction_decisions(id) VALUES($id)":"DELETE FROM correction_decisions WHERE id=$id",("$id",id));command.ExecuteNonQuery();
    }
    private static void RemoveCorrectionSession(SqliteConnection c,string sessionId)
    {
        using(var command=Command(c,"DELETE FROM correction_sources WHERE session=$id",("$id",sessionId)))command.ExecuteNonQuery();PruneCorrections(c);
    }
    private void SyncCorrections(SqliteConnection c,SessionData incoming,SegmentData segment,bool collect)
    {
        using(var clear=Command(c,"DELETE FROM correction_sources WHERE segment=$id",("$id",segment.Id)))clear.ExecuteNonQuery();
        using var permission=Command(c,"SELECT payload FROM sessions WHERE id=$id",("$id",incoming.Id));
        var session=permission.ExecuteScalar() is byte[] bytes?Unpack<SessionData>(bytes):null;
        if(!collect||session?.AllowLearning!=true||segment.AsrState!=AsrState.Confirmed||segment.SessionId!=session.Id)
        {PruneCorrections(c);return;}
        var existing=ReadCorrections(c,session.ProjectId);
        foreach(var change in CorrectionRules.Active(segment))
        {
            if(!CorrectionRules.ValidPair(change.Original,change.Corrected)||!Lexicon.ContainsTerm(segment.FinalText,change.Corrected))continue;
            var candidate=existing.FirstOrDefault(x=>CorrectionRules.SamePair(x,change));
            if(candidate==null)
            {
                if(existing.Count>=5000)continue;
                candidate=new(){ProjectId=session.ProjectId,Original=change.Original,Corrected=change.Corrected};
                SaveCorrection(c,candidate);existing.Add(candidate);
            }
            string after=ContextAround(segment.FinalText,change.Corrected);
            var evidence=new CorrectionEvidence(session.Id,segment.Id,segment.SourceRevision,segment.EditRevision,change.BeforeContext,after,change.At);
            using var insert=Command(c,"INSERT OR REPLACE INTO correction_sources(candidate,segment,session,created,payload) VALUES($c,$s,$session,$at,$b)",("$c",candidate.Id),("$s",segment.Id),("$session",session.Id),("$at",change.At.ToUniversalTime().ToString("O")),("$b",Pack(evidence)));insert.ExecuteNonQuery();
        }
        PruneCorrections(c);
    }
    private static string ContextAround(string text,string word)
    {
        int index=text.IndexOf(word,StringComparison.Ordinal);if(index<0)return "";
        var left=text[..index].EnumerateRunes().TakeLast(24);var right=text[index..].EnumerateRunes().Take(JsonCodec.Count(word)+24);
        return string.Concat(left.Concat(right).Select(r=>r.ToString()));
    }
    private List<CorrectionEvidence> LiveCorrectionEvidence(SqliteConnection c,CorrectionCandidate candidate)
    {
        using var query=Command(c,"SELECT e.payload,s.payload,p.payload FROM correction_sources e JOIN sessions s ON s.id=e.session JOIN segments p ON p.id=e.segment WHERE e.candidate=$id",("$id",candidate.Id));
        using var reader=query.ExecuteReader();var result=new List<CorrectionEvidence>();
        while(reader.Read())
        {
            var evidence=Unpack<CorrectionEvidence>((byte[])reader[0]);var session=Unpack<SessionData>((byte[])reader[1]);var segment=Unpack<SegmentData>((byte[])reader[2]);
            if(session.AllowLearning&&session.ProjectId==candidate.ProjectId&&segment.AsrState==AsrState.Confirmed&&segment.OutputState==OutputState.Published&&segment.EditRevision==evidence.EditRevision&&segment.SourceRevision==evidence.SourceRevision)result.Add(evidence);
        }
        return result;
    }
    public async Task<TermData> ConfirmCorrectionAsync(string id,string project,CorrectionApproval approval,bool learnUsage=true)
    {
        TermData? result=null;
        await WriteAsync(c=>
        {
            using var tx=c.BeginTransaction();var candidate=ReadCorrection(c,id);
            if(candidate.ProjectId!=project||Dead(c,"project",project))throw new InvalidOperationException("项目已变化，请重新打开纠错候选。");
            if(candidate.State!=CorrectionState.Pending)throw new InvalidOperationException("请先选择待确认的候选；已忽略项可以恢复候选。");
            var sources=LiveCorrectionEvidence(c,candidate);if(sources.Count==0)throw new InvalidOperationException("纠错来源已撤销、删除或关闭学习许可。");
            if(approval.Scope!="*"&&approval.Scope!=project)throw new ArgumentException("词条范围无效。");
            string word=TermGenerationRules.Normalize(approval.Text);string alias=approval.Original.Trim();
            var requested=new TermData{Scope=approval.Scope,Text=word,Alias=alias,Category=approval.Category.Trim(),Weight=approval.Weight,Origin="CorrectionLearning",Pinned=true,Protect=true};requested.Validate();
            if(alias.Any(char.IsControl))throw new ArgumentException("旧写法不能包含控制字符。");
            var existing=ReadTerms(c,requested.Scope);var old=existing.FirstOrDefault(t=>SameWord(t,requested));
            if(old?.State==TermState.Disabled)throw new InvalidOperationException("词库中已有禁用的同名词条。请先在词库中启用，再确认学习。");
            if(old==null&&existing.Count>=5000)throw new InvalidOperationException("此范围达到 5,000 个词条上限。");
            // Explicit correction approval makes an extracted term independent of its old evidence.
            result=old==null?requested:old with{Text=word,Alias=alias,Origin=old.Origin=="Extracted"?"CorrectionLearning":old.Origin,State=TermState.Enabled,Weight=Math.Max(old.Weight,requested.Weight),Pinned=true,Protect=true,Revision=old.Revision+1,UpdatedAt=DateTimeOffset.UtcNow};
            SaveTerm(c,result);
            // Keep only vocabulary settings for undo; do not duplicate historic source text in a backup snapshot.
            SaveCorrection(c,candidate with{State=CorrectionState.Learned,Revision=candidate.Revision+1,LearnedText=word,LearnedScope=approval.Scope,LearnedAlias=alias,AutomaticReplacement=approval.AutomaticReplacement,TermId=result.Id,AppliedTermRevision=result.Revision,PriorTerm=old is null?null:old with{Evidence=[]}});
            MarkCorrectionDecision(c,id,true);
            foreach(var source in learnUsage?sources:[])
            {
                using var sample=Command(c,"SELECT s.payload,p.payload FROM sessions s JOIN segments p ON p.session=s.id WHERE s.id=$s AND p.id=$p",("$s",source.SessionId),("$p",source.SegmentId));
                SessionData? session=null;SegmentData? segment=null;
                using(var reader=sample.ExecuteReader())if(reader.Read()){session=Unpack<SessionData>((byte[])reader[0]);segment=Unpack<SegmentData>((byte[])reader[1]);}
                if(session!=null&&segment!=null)SyncTermObservations(c,session,segment,true);
            }
            tx.Commit();
        });
        return result!;
    }
    public Task<List<TermData>> ActiveCorrectionTermsAsync(string project)=>Task.Run(()=>
    {
        using var c=Open();
        // Payloads are encrypted; resolve ids from the decrypted decisions, never from imported aliases.
        var decisions=ReadAllCorrectionDecisions(c);var terms=ReadTerms(c).ToDictionary(t=>t.Id);
        return decisions.Where(d=>d.State==CorrectionState.Learned&&d.AutomaticReplacement)
            .Where(d=>d.TermId!=null&&terms.TryGetValue(d.TermId,out var term)&&term.State==TermState.Enabled&&(term.Scope=="*"||term.Scope==project)&&term.Scope==d.LearnedScope&&term.Text==d.LearnedText&&term.Alias==d.LearnedAlias)
            .Select(d=>terms[d.TermId!]).DistinctBy(t=>t.Id).ToList();
    });
    private List<CorrectionCandidate> ReadAllCorrectionDecisions(SqliteConnection c)
    {
        using var command=Command(c,"SELECT payload FROM corrections");using var reader=command.ExecuteReader();var list=new List<CorrectionCandidate>();
        while(reader.Read())list.Add(Unpack<CorrectionCandidate>((byte[])reader[0]));return list;
    }
    public Task SetAutomaticCorrectionAsync(string id,string project,bool enabled)=>WriteAsync(c=>
    {
        using var tx=c.BeginTransaction();var decision=ReadCorrection(c,id);
        if(decision.ProjectId!=project||decision.State!=CorrectionState.Learned)throw new InvalidOperationException("请选择本项目已学习的纠错记录。");
        using var query=Command(c,"SELECT payload FROM terms WHERE id=$id",("$id",decision.TermId));
        var term=query.ExecuteScalar() is byte[] bytes?Unpack<TermData>(bytes):throw new InvalidOperationException("对应词条已删除。");
        if(enabled&&(term.State!=TermState.Enabled||term.Text!=decision.LearnedText||term.Scope!=decision.LearnedScope||term.Alias.Length==0))throw new InvalidOperationException("词条已改变或禁用，请先重新确认标准写法。");
        SaveCorrection(c,decision with{AutomaticReplacement=enabled,LearnedAlias=term.Alias,Revision=decision.Revision+1});tx.Commit();
    });
    public Task SetCorrectionIgnoredAsync(string id,string project,bool ignored)=>WriteAsync(c=>
    {
        using var tx=c.BeginTransaction();var value=ReadCorrection(c,id);
        if(value.ProjectId!=project||Dead(c,"project",project))throw new InvalidOperationException("纠错来源项目已变化。");
        if(value.State==CorrectionState.Learned)throw new InvalidOperationException("已学习项请使用“撤销学习”，或到词库调整。");
        if(!ignored&&LiveCorrectionEvidence(c,value).Count==0)throw new InvalidOperationException("此项已没有有效来源，可在词库中手动添加词条。");
        SaveCorrection(c,value with{State=ignored?CorrectionState.Ignored:CorrectionState.Pending,Revision=value.Revision+1});MarkCorrectionDecision(c,id,ignored);tx.Commit();
    });
    public Task RevokeCorrectionAsync(string id,string project)=>WriteAsync(c=>
    {
        using var tx=c.BeginTransaction();var value=ReadCorrection(c,id);
        if(value.ProjectId!=project||value.State!=CorrectionState.Learned)throw new InvalidOperationException("请选择本项目已学习的纠错记录。");
        using var query=Command(c,"SELECT payload FROM terms WHERE id=$id",("$id",value.TermId));
        var current=query.ExecuteScalar() is byte[] bytes?Unpack<TermData>(bytes):null;
        if(current!=null)
        {
            if(current.Revision!=value.AppliedTermRevision)throw new InvalidOperationException("该词条在学习后又被修改，已保留最新设置。请到词库中手动调整或删除。");
            if(value.PriorTerm!=null)SaveTerm(c,value.PriorTerm with{Evidence=current.Evidence,Revision=current.Revision+1,UpdatedAt=DateTimeOffset.UtcNow});
            else{Tombstone(c,"term",current.Id);using var remove=Command(c,"DELETE FROM terms WHERE id=$id",("$id",current.Id));remove.ExecuteNonQuery();}
        }
        SaveCorrection(c,value with{State=CorrectionState.Ignored,Revision=value.Revision+1,TermId=null,PriorTerm=null,AppliedTermRevision=0,LearnedText="",LearnedScope=""});MarkCorrectionDecision(c,id,true);tx.Commit();
    });
    private void UnlinkCorrectionTerm(SqliteConnection c,string termId)
    {
        using var query=Command(c,"SELECT payload FROM corrections");var all=new List<CorrectionCandidate>();
        using(var reader=query.ExecuteReader())while(reader.Read())all.Add(Unpack<CorrectionCandidate>((byte[])reader[0]));
        foreach(var value in all.Where(x=>x.TermId==termId))
        {SaveCorrection(c,value with{State=CorrectionState.Ignored,Revision=value.Revision+1,TermId=null,PriorTerm=null,AppliedTermRevision=0,LearnedText="",LearnedScope=""});MarkCorrectionDecision(c,value.Id,true);}
    }
}
