using System.Text;

namespace RealtimeTranscription.Core;

public record WholePolishWork(string SessionId,long Generation,long Operation,long Deadline,string Raw,string[] ProtectedTerms)
{
    public int MaxTokens=>Math.Clamp(Encoding.UTF8.GetByteCount(Raw)+512,1536,98304);
}

public sealed partial class TranscriptEngine
{
    public WholePolishWork? BeginWholePolish(bool enabled,string[] protectedTerms)
    {
        if(tasks.Count==0||tasks.Values.Any(t=>!t.Sealed))throw new InvalidOperationException("收齐尾句后才能润色全文。");
        if(Session.WholePolishState!="None")return null;
        string raw=TranscriptText.Render(Segments);
        bool complete=Session.Gaps.Count==0&&!Segments.Any(s=>s.AsrState==AsrState.Unresolved);
        bool eligible=enabled&&complete&&raw.Length>0&&JsonCodec.Count(raw)<=PolishRules.MaxTextLength&&!Segments.Any(s=>s.UserLocked);
        Session=Session with{WholePolishState=eligible?"Waiting":"Fallback",WholePolishReason=!complete?"识别未完整结束，保留确认原文":!enabled?"全文润色未启用":raw.Length==0?"没有正文":eligible?"正在润色本次完整输入":"全文超出处理范围，保留完整原文",WholePolishOperation=Session.WholePolishOperation+1,Revision=Session.Revision+1};
        if(!eligible)return null;
        int timeout=Math.Clamp(15000+JsonCodec.Count(raw)*6,20000,120000);
        return new(Session.Id,Session.Generation,Session.WholePolishOperation,clock()+timeout,raw,protectedTerms);
    }
    public bool IsCurrent(WholePolishWork work)=>Session.Id==work.SessionId&&Session.Generation==work.Generation&&Session.WholePolishOperation==work.Operation&&Session.WholePolishState=="Waiting";
    public bool CompleteWholePolish(WholePolishWork work,string? candidate,string? error=null)
    {
        if(!IsCurrent(work))return false;
        ValidationResult check;
        try{check=candidate is null||clock()>=work.Deadline?new ValidationResult(false,work.Raw,error??"全文润色超时，保留原文"):PolishRules.Validate(work.Raw,candidate,work.ProtectedTerms);}
        catch(System.Text.RegularExpressions.RegexMatchTimeoutException){check=new(false,work.Raw,"全文校验超时，保留原文");}
        Session=Session with{WholePolishState=check.Accepted?"Completed":"Fallback",WholePolishText=check.Accepted?check.Text:"",WholePolishReason=check.Reason,Revision=Session.Revision+1};
        return true;
    }
}
