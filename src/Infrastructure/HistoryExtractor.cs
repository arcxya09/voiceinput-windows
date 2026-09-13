using RealtimeTranscription.Core;

namespace RealtimeTranscription.Infrastructure;

public record HistoryExtractionProgress(int Total,int Scanned,int SkippedSessions,int Batches,int Added,int Updated,string Note,bool Completed=false);

/// <summary>Visits every saved session in a bounded page, checkpoints each successful batch atomically.</summary>
public delegate Task<ExtractionCommit> ExtractionCommitHandler(SessionData session,IReadOnlyList<ExtractionSlice> slices,IReadOnlyList<TermData> terms,CancellationToken token);
public sealed class HistoryExtractor(MemoryRepository repository,Func<IReadOnlyList<SegmentData>,CancellationToken,Task<string>> call,ExtractionCommitHandler? commit=null)
{
    public async Task<HistoryExtractionProgress> RunAsync(IProgress<HistoryExtractionProgress>? progress,CancellationToken token,Func<SessionData,Task>? committed=null,string? sessionId=null,int? maxBatches=null)
    {
        await repository.BarrierAsync();var boundary=sessionId==null?await repository.HistoryBoundaryAsync(token):new HistoryBoundary(1,1);
        int scanned=0,skipped=0,batches=0,added=0,updated=0;long after=0;
        HistoryExtractionProgress State(string note,bool complete=false)=>new(boundary.Count,scanned,skipped,batches,added,updated,note,complete);
        try
        {
            while(true)
            {
                token.ThrowIfCancellationRequested();
                List<HistoryEntry> page;
                if(sessionId==null)page=await repository.HistoryPageAsync(after,boundary.LastRow,token);
                else page=after==0&&await repository.LoadSessionAsync(sessionId) is {} selected?[new(1,selected.Session)]:[];
                if(page.Count==0)break;
                foreach(var entry in page)
                {
                    token.ThrowIfCancellationRequested();after=entry.Row;
                    var latest=await repository.LoadSessionAsync(entry.Session.Id);if(latest==null){scanned++;skipped++;continue;}
                    var session=latest.Session;
                    if(!session.AllowLearning){scanned++;skipped++;progress?.Report(State("已跳过未允许整理的历史记录。"));continue;}
                    var pending=ExtractionPlanner.Pending(session,latest.Segments);int index=0;
                    while(index<pending.Count)
                    {
                        token.ThrowIfCancellationRequested();
                        if(maxBatches.HasValue&&batches>=maxBatches.Value)return State("本次整理批次已完成，还有内容可继续整理。");
                        var permission=await repository.LoadSessionAsync(session.Id);
                        if(permission==null||!permission.Session.AllowLearning)throw new InvalidOperationException("历史来源已删除或关闭整理许可。");
                        var batch=new List<ExtractionSlice>();int chars=0;
                        while(index<pending.Count&&batch.Count<20)
                        {
                            var slice=pending[index];if(batch.Count>0&&(chars+slice.Length>2000||batch.Any(s=>s.Segment.Id==slice.Segment.Id)))break;
                            batch.Add(slice);chars+=slice.Length;index++;
                        }
                        progress?.Report(State($"正在整理第 {scanned+1}/{boundary.Count} 条历史，第 {batches+1} 批…"));
                        var input=batch.Select(s=>s.Segment).ToArray();string json=await call(input,token);token.ThrowIfCancellationRequested();
                        var candidates=Lexicon.ParseCandidates(json,input,session.ProjectId);
                        var saved=await (commit??repository.CommitExtractionAsync)(session,batch,candidates,token);session=saved.Session;
                        added+=saved.Added;updated+=saved.Updated;batches++;
                        if(committed!=null)await committed(session);
                        progress?.Report(State($"已保存 {batches} 批进度，新增 {added} 个候选。"));
                    }
                    scanned++;progress?.Report(State("正在检查下一条历史…"));
                }
            }
            return State(sessionId==null?"全部历史整理完成。新词条已放入来源项目的词库，等待确认。":"当前会话整理完成，新词条等待确认。",true);
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){return State("已取消；已完成的批次保留，下次自动继续未整理内容。");}
        catch(Exception e)when(e is ProviderException or InvalidOperationException or FormatException or System.Text.Json.JsonException or OperationCanceledException)
        {return State((e is OperationCanceledException?"该批请求超时。":e is System.Text.Json.JsonException?"该批返回格式不完整。":e.Message)+" 已保存的进度保留，下次可继续。");}
    }
}
