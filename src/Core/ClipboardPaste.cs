namespace RealtimeTranscription.Core;

public record PasteDispatchResult(string State, int Accepted = 0);

// Submit one paste gesture for the complete recognition result. Native event
// acceptance does not acknowledge that the target application consumed the text.
public static class ClipboardPaste
{
    public const int MaxCharacters = 1_000_000;

    public static bool IsSupportedText(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxCharacters) return false;
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (character == '\0' || char.IsLowSurrogate(character)) return false;
            if (char.IsHighSurrogate(character) &&
                (++index >= text.Length || !char.IsLowSurrogate(text[index]))) return false;
        }
        return true;
    }

    public static async Task<PasteDispatchResult> SendAsync(string text,
        Func<string, CancellationToken, Task<uint?>> prepare, Func<uint, bool> clipboardCurrent,
        Func<bool> safe, Func<int> sendShortcut, CancellationToken token,
        Func<bool>? uninterrupted = null)
    {
        if (string.IsNullOrEmpty(text)) return new("Empty");
        if (!IsSupportedText(text)) return new("Blocked");
        bool attempted = false;
        int accepted = 0;
        try
        {
            if (token.IsCancellationRequested || !safe() || token.IsCancellationRequested)
                return new("Blocked");
            // Preparation validates the original target and selection, then
            // writes the complete text once. Its sequence identifies that write.
            uint? sequence = await prepare(text, token);
            if (!sequence.HasValue || token.IsCancellationRequested || !safe() ||
                !clipboardCurrent(sequence.Value) || token.IsCancellationRequested)
                return new("Blocked");

            // Set this before entering native code: a throwing callback may have
            // submitted events. Never retry the gesture or fall back to typing.
            attempted = true;
            accepted = sendShortcut();
            if (accepted is 0 or 1) return new("Blocked", accepted);
            if (accepted != 4) return new("Unknown", accepted);

            // A full submission can still race user activity or another copy.
            // Do not recheck injected modifier states here; Ctrl may be queued.
            bool intact = !token.IsCancellationRequested && (uninterrupted?.Invoke() ?? true) &&
                clipboardCurrent(sequence.Value) && !token.IsCancellationRequested;
            return new(intact ? "Sent" : "Unknown", accepted);
        }
        catch
        {
            return new(attempted ? "Unknown" : "Blocked", accepted);
        }
    }
}
