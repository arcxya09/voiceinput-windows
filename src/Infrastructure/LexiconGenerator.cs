using System.Text.Json;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Infrastructure;

/// <summary>Bounded, cancellable generation. Completed batches stay in the preview on interruption.</summary>
public sealed class LexiconGenerator(Func<TermGenerationBatch,CancellationToken,Task<string>> call)
{
    public async Task<GeneratedLexicon> GenerateAsync(TermGenerationOptions options,IProgress<TermGenerationProgress>? progress,CancellationToken token)
    {
        options.Validate();var terms=new List<TermData>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int requests=0,rejected=0,stalled=0,maxRequests=(options.Count+49)/50+2;
        string note="";bool cancelled=false;
        try
        {
            while(terms.Count<options.Count&&requests<maxRequests&&stalled<2)
            {
                token.ThrowIfCancellationRequested();
                int count=Math.Min(50,options.Count-terms.Count);
                var batch=new TermGenerationBatch(options.Requirement.Trim(),count,terms.Select(t=>t.Text).ToArray(),options.MaxThinking);
                requests++;string json=await call(batch,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
                var parsed=TermGenerationRules.Parse(json,options,count);rejected+=parsed.Rejected;int before=terms.Count;
                foreach(var term in parsed.Terms)if(seen.Add(term.Text))terms.Add(term);else rejected++;
                stalled=terms.Count==before?stalled+1:0;
                progress?.Report(new(terms.ToArray(),options.Count,requests,rejected));
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){cancelled=true;note="已取消，完成的批次可继续预览和导入。";}
        catch(OperationCanceledException){note="生成等待超时，完成的批次可继续预览和导入。";}
        catch(ProviderException e){note=e.Message;}
        catch(Exception e)when(e is JsonException or FormatException){note="该批 AI 词库格式不完整，已保留此前完成的批次。";}
        if(note.Length==0)note=terms.Count==options.Count?"生成完成，请检查后导入。":$"本次生成 {terms.Count}/{options.Count} 个有效词条，已停止补充。可调整要求后再次生成。";
        return new(terms.ToArray(),options.Count,requests,rejected,cancelled,note);
    }
}
