namespace RealtimeTranscription.Core;

public enum FocusObservation { Stable, Unavailable, Changed }

// A missing sample is not evidence of a different target. A known change is final.
public sealed class FocusContinuity(long graceMilliseconds = 500)
{
    private long? unavailableSince;
    private bool changed;
    public bool Observe(FocusObservation observation, long now)
    {
        if (changed) return false;
        if (observation == FocusObservation.Changed) { changed = true; return false; }
        if (observation == FocusObservation.Stable) { unavailableSince = null; return true; }
        unavailableSince ??= now;
        if (now - unavailableSince.Value >= graceMilliseconds) changed = true;
        return !changed;
    }
}

// Written synchronously by the physical hook, before its event is queued.
public sealed class InputActivityVersion
{
    private long version;
    public long Current => Interlocked.Read(ref version);
    public void Advance() => Interlocked.Increment(ref version);
    public bool Matches(long captured) => Current == captured;
}

// One outstanding provider call at most; a timeout never releases a still-running call.
public sealed class BoundedInputQuery
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<T?> RunAsync<T>(Func<T?> query, TimeSpan timeout, CancellationToken token = default) where T : class
    {
        long deadline = Environment.TickCount64 + Math.Max(1, (long)timeout.TotalMilliseconds);
        try
        {
            if (!await gate.WaitAsync(timeout, token)) return null;
        }
        catch (OperationCanceledException) { return null; }
        if (token.IsCancellationRequested) { gate.Release(); return null; }
        var work = Task.Run(() => { try { return query(); } catch { return null; } finally { gate.Release(); } });
        try { return await work.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1, deadline - Environment.TickCount64)), token); }
        catch (TimeoutException) { return null; }
        catch (OperationCanceledException) { return null; }
    }
}
