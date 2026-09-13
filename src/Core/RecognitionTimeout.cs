namespace RealtimeTranscription.Core;

public static class RecognitionTimeout
{
    public static bool Stalled(long now, long lastResponse, long lastProgress, long lastVoice,
        bool voiceSeen, bool hasPartial, int silenceMs)
    {
        if (now - lastResponse > 20000) return true;
        long wait = Math.Max(20000L, silenceMs + 10000L);
        // Quiet audio alone is not evidence that recognition is stuck. Fresh text/final
        // progress keeps the turn alive even below the local level-meter threshold.
        return voiceSeen && hasPartial && now - lastVoice > wait && now - lastProgress > wait;
    }
}
