using System.Text;
using System.Text.Json.Serialization;

namespace RealtimeTranscription.Core;

public record CorrectionChange(string Original, string Corrected, string BeforeContext, string AfterContext, DateTimeOffset At);
public enum CorrectionState { Pending, Learned, Ignored }
public record CorrectionEvidence(string SessionId, string SegmentId, long SourceRevision, long EditRevision, string BeforeContext, string AfterContext, DateTimeOffset At);
public record CorrectionCandidate
{
    public string Id { get; init; } = JsonCodec.Id();
    public string ProjectId { get; init; } = "default";
    public string Original { get; init; } = "";
    public string Corrected { get; init; } = "";
    public CorrectionState State { get; init; }
    public long Revision { get; init; } = 1;
    public string LearnedText { get; init; } = "";
    public string LearnedScope { get; init; } = "";
    public string LearnedAlias { get; init; } = "";
    public bool AutomaticReplacement { get; init; }
    [JsonIgnore] public bool ReplacementValid { get; init; } = true;
    [JsonIgnore] public string ReplacementReason { get; init; } = "";
    [JsonIgnore] public string ReplacementLabel => State!=CorrectionState.Learned?"—":!AutomaticReplacement?"自动纠正关闭":ReplacementValid?"自动纠正开启":"规则已失效";
    public string? TermId { get; init; }
    public long AppliedTermRevision { get; init; }
    public TermData? PriorTerm { get; init; }
    [JsonIgnore] public int Count { get; init; }
    [JsonIgnore] public string DisplayText => State == CorrectionState.Learned ? LearnedText : Corrected;
    [JsonIgnore] public string StateLabel => State switch { CorrectionState.Learned => "已学习", CorrectionState.Ignored => "已忽略", _ => "待确认" };
    [JsonIgnore] public string FrequencyLabel => Count >= 3 ? "多次纠正" : "";
}
public record CorrectionApproval(string Text, string Original, string Category, string Scope, int Weight, bool AutomaticReplacement = false);

/// <summary>Local, bounded edit comparison. Suggestions are reviewed by a person before becoming terms.</summary>
public static class CorrectionRules
{
    private record Hunk(int OldStart,int OldEnd,int NewStart,int NewEnd);
    private static readonly string[] Boundaries = ["我们","你们","他们","这个","那个","请将","请把","需要","可以","应该","进行","采用","使用","安排","开展","研究","讨论","关于","计划","希望","准备","明天","今天","昨天","请","的","了","是","在","和","与","把","将","用","做"];
    private static readonly HashSet<string> Ordinary = ["今天","明天","昨天","这里","那里","这个","那个","这样","那样","我们","你们","他们","然后","就是","其实","嗯","呃"];
    private static string Join(Rune[] runes,int start,int end)=>string.Concat(runes.Skip(start).Take(end-start).Select(r=>r.ToString()));
    private static bool Latin(Rune r)=>r.Value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    private static bool Word(Rune r)=>Rune.IsLetterOrDigit(r)||r.Value is '-' or '_' or '+' or '#';
    private static bool LatinWord(Rune r)=>Latin(r)||Rune.IsDigit(r)||r.Value is '-' or '_' or '+' or '#';
    private static bool HasLetter(string text)=>text.EnumerateRunes().Any(Rune.IsLetter);
    public static bool SamePair(CorrectionCandidate candidate,CorrectionChange change)=>string.Equals(candidate.Original,change.Original,StringComparison.Ordinal)&&candidate.Corrected==change.Corrected;
    public static IReadOnlyList<CorrectionChange> Active(SegmentData segment)=>segment.OutputState == OutputState.Published && segment.UndoHistory is { Count: > 0 } states
        && segment.UndoPosition >= 0 && segment.UndoPosition < states.Count ? states[segment.UndoPosition].Corrections ?? [] : [];

    public static List<CorrectionChange> Detect(string before,string after)
    {
        if(before==after||before.Length>40000||after.Length>40000)return [];
        before=before.Normalize(NormalizationForm.FormC);after=after.Normalize(NormalizationForm.FormC);
        var a=before.EnumerateRunes().ToArray();var b=after.EnumerateRunes().ToArray();
        if(a.Length>20000||b.Length>20000||a.Length==0||b.Length==0)return [];
        var hunks=new List<Hunk>();
        foreach(var next in Diff(a,b))
        {
            if(hunks.LastOrDefault() is {} prior && next.OldStart-prior.OldEnd==next.NewStart-prior.NewEnd
                && Join(a,prior.OldEnd,next.OldStart).EnumerateRunes().All(LatinWord)
                && (Join(a,prior.OldStart,next.OldEnd)+Join(b,prior.NewStart,next.NewEnd)).EnumerateRunes().Any(Latin))
                hunks[^1]=new(prior.OldStart,next.OldEnd,prior.NewStart,next.NewEnd);
            else hunks.Add(next);
        }
        var result=new List<CorrectionChange>();var at=DateTimeOffset.UtcNow;
        foreach(var h in hunks)
        {
            string oldChange=Join(a,h.OldStart,h.OldEnd),newChange=Join(b,h.NewStart,h.NewEnd);
            if(!HasLetter(oldChange+newChange))continue;
            if((oldChange+newChange).Any(c=>"不没无未非否".Contains(c)))continue;
            var oldNumbers=oldChange.EnumerateRunes().Where(Rune.IsDigit);
            if(!oldNumbers.SequenceEqual(newChange.EnumerateRunes().Where(Rune.IsDigit)))continue;
            bool latin=(oldChange+newChange).EnumerateRunes().Any(Latin);
            // Pure Chinese insertion/deletion generally changes wording, rather than fixing a term.
            if(!latin&&(h.OldStart==h.OldEnd||h.NewStart==h.NewEnd))continue;
            int left=0,right=0;
            while(h.OldStart-left>0&&h.NewStart-left>0&&a[h.OldStart-left-1]==b[h.NewStart-left-1]
                &&(latin?LatinWord(b[h.NewStart-left-1]):Word(b[h.NewStart-left-1]))&&left<32)left++;
            while(h.OldEnd+right<a.Length&&h.NewEnd+right<b.Length&&a[h.OldEnd+right]==b[h.NewEnd+right]
                &&(latin?LatinWord(b[h.NewEnd+right]):Word(b[h.NewEnd+right]))&&right<32)right++;
            if(!latin)
            {
                string prefix=Join(b,h.NewStart-left,h.NewStart);
                int boundary=0;
                foreach(string word in Boundaries){int pos=prefix.LastIndexOf(word,StringComparison.Ordinal);if(pos>=0)boundary=Math.Max(boundary,pos+word.Length);}
                left=JsonCodec.Count(prefix[boundary..]);
                string suffix=Join(b,h.NewEnd,h.NewEnd+right);int end=suffix.Length;
                foreach(string word in Boundaries){int pos=suffix.IndexOf(word,StringComparison.Ordinal);if(pos>=0)end=Math.Min(end,pos);}
                right=JsonCodec.Count(suffix[..end]);
                if(left+right+Math.Max(h.OldEnd-h.OldStart,h.NewEnd-h.NewStart)>16)continue;
            }
            string original=Join(a,h.OldStart-left,h.OldEnd+right).Trim(),corrected=Join(b,h.NewStart-left,h.NewEnd+right).Trim();
            // Numerical corrections and changed logical/quantity relations are edits to the
            // statement, not reusable spelling rules. Share the execution-time guard so a
            // candidate cannot later turn a one-off value change into a global replacement.
            if(!ConfirmedCorrections.IsSafePair(original,corrected))continue;
            // Do not combine a second changed span into this suggestion through an expanded boundary.
            if(hunks.Any(x=>x!=h&&x.NewStart<h.NewEnd+right&&x.NewEnd>h.NewStart-left))continue;
            string contextBefore=Join(a,Math.Max(0,h.OldStart-left-24),Math.Min(a.Length,h.OldEnd+right+24));
            string contextAfter=Join(b,Math.Max(0,h.NewStart-left-24),Math.Min(b.Length,h.NewEnd+right+24));
            result.Add(new(original,corrected,contextBefore,contextAfter,at));
            if(result.Count>=8)break;
        }
        return result.DistinctBy(c=>(c.Original,c.Corrected)).ToList();
    }
    public static bool ValidPair(string original,string corrected)=>original!=corrected&&JsonCodec.Count(original) is >=2 and <=32
        &&JsonCodec.Count(corrected) is >=2 and <=32&&HasLetter(original)&&HasLetter(corrected)
        &&!original.Any(char.IsControl)&&!corrected.Any(char.IsControl)&&!Ordinary.Contains(original)&&!Ordinary.Contains(corrected);

    // Myers diff with a fixed edit-distance budget; large rewrites are intentionally skipped.
    private static List<Hunk> Diff(Rune[] a,Rune[] b)
    {
        const int limit=128,offset=limit+1;var v=new int[2*limit+3];Array.Fill(v,-1);v[offset+1]=0;
        var trace=new List<int[]>();int distance=-1;
        for(int d=0;d<=limit&&distance<0;d++)
        {
            trace.Add((int[])v.Clone());
            for(int k=-d;k<=d;k+=2)
            {
                int index=offset+k;int x=k==-d||(k!=d&&v[index-1]<v[index+1])?v[index+1]:v[index-1]+1;int y=x-k;
                while(x<a.Length&&y<b.Length&&x>=0&&y>=0&&a[x]==b[y]){x++;y++;}
                v[index]=x;if(x>=a.Length&&y>=b.Length){distance=d;break;}
            }
        }
        if(distance<0)return [];
        int ax=a.Length,by=b.Length;var operations=new List<char>();
        for(int d=distance;d>=0;d--)
        {
            var previous=trace[d];int k=ax-by;
            int pk=k==-d||(k!=d&&previous[offset+k-1]<previous[offset+k+1])?k+1:k-1;
            int px=previous[offset+pk],py=px-pk;
            while(ax>px&&by>py){operations.Add('=');ax--;by--;}
            if(d==0)break;
            if(ax==px){operations.Add('+');by--;}else{operations.Add('-');ax--;}
        }
        operations.Reverse();var result=new List<Hunk>();int x0=0,y0=0,os=-1,ns=-1;
        foreach(char op in operations)
        {
            if(op=='='){if(os>=0){result.Add(new(os,x0,ns,y0));os=ns=-1;}x0++;y0++;}
            else{if(os<0){os=x0;ns=y0;}if(op=='-')x0++;else y0++;}
        }
        if(os>=0)result.Add(new(os,x0,ns,y0));return result;
    }
}
