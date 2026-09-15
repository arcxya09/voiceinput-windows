namespace RealtimeTranscription.Core;

// One instance belongs to the physical keyboard hook thread. Keep the matching
// key-up suppressed even when delivery completes while Enter remains held.
public sealed class ReleaseEnterGuard
{
    private bool held, consumed;
    public bool Handle(bool down, bool up, bool awaitingDelivery, bool modified, out bool expedite)
    {
        expedite = false;
        if (down && !held)
        {
            held = true;
            consumed = awaitingDelivery && !modified;
            expedite = consumed;
        }
        bool suppress = held && consumed;
        if (up) { held = false; consumed = false; }
        return suppress;
    }
}
