namespace RealtimeTranscription.Core;

// Recording may survive a transient provider/child-focus change. This policy
// never authorizes insertion: delivery still validates the original target and selection.
public sealed class RecordingFocusContinuity(long graceMilliseconds=1000)
{
    private long? uncertainSince;
    public bool ManualOnly { get; private set; }
    public bool Observe(FocusObservation window,FocusObservation focus,long now)
    {
        if(ManualOnly)return false;
        if(window==FocusObservation.Changed){ManualOnly=true;return false;}
        if(window==FocusObservation.Stable&&focus==FocusObservation.Stable){uncertainSince=null;return true;}
        uncertainSince??=now;
        if(now-uncertainSince.Value>=graceMilliseconds)ManualOnly=true;
        return !ManualOnly;
    }
}
