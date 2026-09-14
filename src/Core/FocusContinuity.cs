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

public static class InputSafety
{
    public const int BatchCharacters = 128;
    public static bool HasOtherModifier(int trigger, Func<int, bool> down)
        => new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5b, 0x5c }.Any(key => key != trigger && down(key));

    // Every batch may require three bounded accessibility queries and two short retries.
    // Include per-character yielding at Windows' coarse timer resolution, while
    // retaining the overall cap for a slow or unresponsive target.
    public static long DeliveryBudgetMilliseconds(int characters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(characters);
        long batches = ((long)characters + BatchCharacters - 1) / BatchCharacters;
        long pacing = Math.Max(0L, (long)characters - 1) * 16;
        return Math.Clamp(2000 + batches * 4000 + pacing, 2000, 600000);
    }

    public static async Task<T?> RecoverInitialFocusAsync<T>(Func<(FocusObservation Observation, T? Target)> sample,
        Func<bool> valid, CancellationToken token, int graceMilliseconds = 500) where T : class
    {
        long deadline = Environment.TickCount64 + Math.Max(0, graceMilliseconds);
        while (!token.IsCancellationRequested && valid())
        {
            var read = sample();
            if (read.Observation == FocusObservation.Changed) return null;
            if (read.Observation == FocusObservation.Stable && read.Target != null) return read.Target;
            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) return null;
            try { await Task.Delay((int)Math.Min(25, remaining), token); }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }
}

// Queries run in a killable process, not a cancellable wait around an unkillable COM call.
// StopAsync must confirm process exit; an unconfirmed stop never permits a second worker.
public interface IIsolatedInputWorker : IAsyncDisposable
{
    Task<string?> QueryAsync(string request, CancellationToken token);
    Task<bool> StopAsync();
}
public record IsolatedInputReply(string Generation, string Value);
public sealed class IsolatedInputQuery(Func<IIsolatedInputWorker> create) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IIsolatedInputWorker? worker;
    private string generation = "";
    private bool retired, disposed;
    public async Task<IsolatedInputReply?> RunAsync(string request, TimeSpan timeout, CancellationToken token = default, string? expectedGeneration = null)
    {
        try { if (!await gate.WaitAsync(timeout, token)) return null; }
        catch (OperationCanceledException) { return null; }
        try
        {
            if (disposed || token.IsCancellationRequested) return null;
            if (retired && !await StopWorker()) return null;
            // Never recapture a selection after losing the process that owns the original range.
            if (expectedGeneration != null && (worker == null || generation != expectedGeneration)) return null;
            worker ??= CreateWorker();
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(timeout);
                var reply = await worker.QueryAsync(request, limit.Token).WaitAsync(timeout, token);
                if (reply == null) { await StopWorker(); return null; }
                return new(generation, reply);
            }
            catch { await StopWorker(); return null; }
        }
        catch { return null; }
        finally { gate.Release(); }
    }
    private IIsolatedInputWorker CreateWorker()
    {
        var next = create(); generation = Guid.NewGuid().ToString("N"); retired = false; return next;
    }
    private async Task<bool> StopWorker()
    {
        if (worker == null) return true;
        retired = true;
        bool stopped;
        try { stopped = await worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { stopped = false; }
        if (!stopped) return false;
        try { await worker.DisposeAsync(); } catch { }
        worker = null; generation = ""; retired = false; return true;
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { disposed = true; await StopWorker(); }
        finally { gate.Release(); }
    }
}
