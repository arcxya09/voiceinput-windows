namespace RealtimeTranscription.Core;

// Recording follows the original top-level window, not a particular child HWND:
// editors re-create or move child focus while a hold is in progress, and delivery
// re-reads the current focus inside that window. A brief excursion (a toast, a
// tooltip, an IME mode flyout taking the foreground) is tolerated; only a window
// that stays away for the whole grace period stops automatic input for this turn.
// This policy never authorizes insertion: delivery still validates the current target.
public sealed class RecordingFocusContinuity(long graceMilliseconds=1500)
{
    private long? awaySince;
    public bool ManualOnly { get; private set; }
    public bool Observe(FocusObservation window,long now)
    {
        if(ManualOnly)return false;
        if(window==FocusObservation.Stable){awaySince=null;return true;}
        awaySince??=now;
        if(now-awaySince.Value>=graceMilliseconds)ManualOnly=true;
        return !ManualOnly;
    }
}
