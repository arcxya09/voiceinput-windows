namespace RealtimeTranscription.Core;

public record VoiceTurnCompletion(string TurnId, string Status, string Text)
{
    public string? DeliveryState { get; init; }
}
public record VoiceInputNotice(string Status, string? TurnId = null);
public record VoicePreviewFrame(string Status, string Text, bool Dismiss)
{
    public string? DeliveryState { get; init; }
}

/// <summary>Feedback events carry their originating turn across the UI dispatcher.</summary>
public interface IVoicePreviewEvents
{
    event Action<string>? TurnStarted;
    event Action<VoiceTurnCompletion>? TurnCompleted;
    event Action<VoiceInputNotice>? Notice;
}

/// <summary>Accepts feedback for one turn and seals it after its final result is shown.</summary>
public sealed class VoicePreviewState
{
    public string? TurnId { get; private set; }
    public bool IsCompleted { get; private set; }

    public bool Begin(string turnId)
    {
        if (string.IsNullOrEmpty(turnId) || TurnId == turnId) return false;
        TurnId = turnId;
        IsCompleted = false;
        return true;
    }

    public VoicePreviewFrame? Snapshot(TranscriptSnapshot snapshot, bool busy, bool enabled, bool dictationOnly)
    {
        if (!busy || IsCompleted || snapshot.Session is not { } session || session.Id != TurnId || TurnId == null
            || session.DeliveryState is not ("Pending" or "Sending")) return null;
        string partial = string.Join(" ", snapshot.Segments.Where(s => s.AsrState == AsrState.Partial).Select(s => s.PartialText));
        return new(UiPresentation.Phase(snapshot, busy, enabled, dictationOnly),
            partial.Length > 0 ? partial : TranscriptText.Render(snapshot), false);
    }

    public VoicePreviewFrame? Complete(VoiceTurnCompletion result)
    {
        if (TurnId != result.TurnId || IsCompleted) return null;
        IsCompleted = true;
        return new(result.Status, result.Text, true) { DeliveryState = result.DeliveryState };
    }

    // In-turn reminders must not dismiss a recording, overwrite its final result,
    // or restart its hide timer after a delayed dispatcher callback.
    public bool CanShowNotice(VoiceInputNotice notice)
        => notice.TurnId == null && (TurnId == null || IsCompleted);
}
