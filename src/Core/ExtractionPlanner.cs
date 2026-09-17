namespace RealtimeTranscription.Core;
public record ExtractionSlice(SegmentData Segment,int Start,int Length,string Stamp);
public static class ExtractionPlanner
{
    public static List<ExtractionSlice> Pending(SessionData session,IEnumerable<SegmentData> segments)
    {
        var result=new List<ExtractionSlice>();var done=session.LearnedVersions.ToHashSet(StringComparer.Ordinal);
        foreach(var s in segments.Where(s=>!s.SupersededByAsrReview&&s.AsrState==AsrState.Confirmed&&s.OutputState is OutputState.Published or OutputState.Suppressed&&s.SaveState==SaveState.Saved))
        {
            string version=$"{s.Id}:{s.SourceRevision}:{s.EditRevision}";if(done.Contains(version))continue;
            var runes=(s.EditRevision>0?s.FinalText:s.RawText).EnumerateRunes().ToArray();
            for(int start=0;start<runes.Length;start+=2000)
            {
                int length=Math.Min(2000,runes.Length-start);string stamp=$"{version}@{start}:{length}";if(done.Contains(stamp))continue;
                string text=string.Concat(runes.Skip(start).Take(length).Select(r=>r.ToString()));
                result.Add(new(s with{RawText=text,FinalText=text,ExtractionStart=start},start,length,stamp));
            }
        }
        return result;
    }
}
