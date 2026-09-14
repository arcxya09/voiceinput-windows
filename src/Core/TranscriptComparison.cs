namespace RealtimeTranscription.Core;

public static class TranscriptComparison
{
    /// <summary>Confirmed ASR text remains visible even when its edited output was deleted.</summary>
    public static string Original(IEnumerable<SegmentData> segments)
        => TranscriptText.Render(segments.Where(s => s.AsrState == AsrState.Confirmed)
            .Select(s => s with { FinalText = s.RawText, OutputState = OutputState.Published }));
}
