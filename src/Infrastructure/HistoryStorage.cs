using Microsoft.Data.Sqlite;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Infrastructure;

public record HistoryBoundary(long LastRow,int Count);
public record HistoryEntry(long Row,SessionData Session);
public record ExtractionCommit(SessionData Session,int Added,int Updated,int Skipped);

public sealed partial class MemoryRepository
{
    public Task<HistoryBoundary> HistoryBoundaryAsync(CancellationToken token)=>Task.Run(()=>
    {
        token.ThrowIfCancellationRequested();using var c=Open();using var cmd=Command(c,"SELECT COALESCE(MAX(rowid),0),COUNT(*) FROM sessions");using var r=cmd.ExecuteReader();r.Read();return new HistoryBoundary(r.GetInt64(0),r.GetInt32(1));
    },token);
    public Task<List<HistoryEntry>> HistoryPageAsync(long after,long through,CancellationToken token)=>Task.Run(()=>
    {
        using var c=Open();using var cmd=Command(c,"SELECT rowid,payload FROM sessions WHERE rowid>$after AND rowid<=$through ORDER BY rowid LIMIT 100",("$after",after),("$through",through));using var r=cmd.ExecuteReader();var result=new List<HistoryEntry>();
        while(r.Read()){token.ThrowIfCancellationRequested();result.Add(new(r.GetInt64(0),Unpack<SessionData>((byte[])r[1])));}return result;
    },token);
    public async Task<ExtractionCommit> CommitExtractionAsync(SessionData expected,IReadOnlyList<ExtractionSlice> slices,IReadOnlyList<TermData> candidates,CancellationToken token)
    {
        ExtractionCommit? committed=null;
        await WriteAsync(c=>
        {
            token.ThrowIfCancellationRequested();using var tx=c.BeginTransaction();
            SessionData current;
            using(var cmd=Command(c,"SELECT payload FROM sessions WHERE id=$id",("$id",expected.Id)))
                current=cmd.ExecuteScalar() is byte[] payload?Unpack<SessionData>(payload):throw new InvalidOperationException("历史来源已删除，停止本次整理。");
            if(!current.AllowLearning||current.ProjectId!=expected.ProjectId||Dead(c,"project",current.ProjectId))throw new InvalidOperationException("历史来源权限已变化，停止本次整理。");
            foreach(var slice in slices)
            {
                var snapshot=slice.Segment;SegmentData? live=null;
                using(var cmd=Command(c,"SELECT payload FROM segments WHERE id=$id AND session=$session",("$id",snapshot.Id),("$session",expected.Id)))
                    if(cmd.ExecuteScalar() is byte[] bytes)live=Unpack<SegmentData>(bytes);
                if(live==null||live.SupersededByAsrReview||live.AsrState!=AsrState.Confirmed||live.OutputState is not(OutputState.Published or OutputState.Suppressed)||live.SourceRevision!=snapshot.SourceRevision||live.EditRevision!=snapshot.EditRevision)
                    throw new InvalidOperationException("历史原文已编辑或删除，请重新整理。");
                string text=string.Concat((live.EditRevision>0?live.FinalText:live.RawText).EnumerateRunes().Skip(slice.Start).Take(slice.Length).Select(r=>r.ToString()));
                if(text!=snapshot.RawText)throw new InvalidOperationException("历史原文版本已变化，请重新整理。");
            }
            var existing=new List<TermData>();using(var cmd=Command(c,"SELECT payload FROM terms WHERE scope=$s",("$s",current.ProjectId)))using(var r=cmd.ExecuteReader())while(r.Read())existing.Add(Unpack<TermData>((byte[])r[0]));
            var blocked=new HashSet<string>(StringComparer.Ordinal);using(var cmd=Command(c,"SELECT payload FROM suppression WHERE scope=$s",("$s",current.ProjectId)))using(var r=cmd.ExecuteReader())while(r.Read())blocked.Add(Unpack<string>((byte[])r[0]));
            int added=0,updated=0,skipped=0;
            foreach(var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();candidate.Validate();
                if(candidate.Scope!=current.ProjectId||candidate.Evidence.Count==0||candidate.Evidence.Any(e=>e.SessionId!=current.Id||!slices.Any(s=>s.Segment.Id==e.SegmentId&&s.Segment.SourceRevision==e.SourceRevision&&s.Segment.EditRevision==e.EditRevision&&s.Start==e.SliceStart&&s.Segment.RawText.Contains(e.Quote,StringComparison.Ordinal)&&e.Quote.Contains(candidate.Text,StringComparison.Ordinal))))
                    throw new InvalidOperationException("词条来源校验失败，未保存该批。");
                var old=existing.FirstOrDefault(t=>string.Equals(t.Key,candidate.Key,StringComparison.Ordinal));
                if(blocked.Contains(candidate.Key)||old?.State==TermState.Disabled){skipped++;continue;}
                if(old==null&&existing.Count>=5000)throw new InvalidOperationException("来源项目词库已达 5,000 个上限。已完成的批次保留；清理词库后可继续。");
                var ready=old==null?candidate with{State=TermState.Candidate,Origin="Extracted"}:old with{Evidence=old.Evidence.Concat(candidate.Evidence).DistinctBy(e=>(e.SegmentId,e.SourceRevision,e.EditRevision,e.SliceStart)).ToList(),Revision=old.Revision+1};
                SaveTerm(c,ready);existing.RemoveAll(t=>t.Id==ready.Id);existing.Add(ready);if(old==null)added++;else updated++;
            }
            var session=current with{LearnedVersions=current.LearnedVersions.Concat(slices.Select(s=>s.Stamp)).Distinct().ToList(),Revision=current.Revision+1};
            SaveSession(c,session);token.ThrowIfCancellationRequested();tx.Commit();committed=new(session,added,updated,skipped);
        });
        return committed!;
    }
}
