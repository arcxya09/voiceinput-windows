using System.Buffers;
using System.Text;

namespace RealtimeTranscription.Core;

public record PacedInputResult(string State, int Accepted = 0);

// Keep application edit queues from receiving an entire sentence in one burst.
// Native acceptance only acknowledges queued events, not the target's final text.
public static class PacedTextInput
{
    public static async Task<PacedInputResult> SendAsync(string text, Func<string, int> send,
        Func<bool> safe, Func<CancellationToken, Task<bool>> validate, CancellationToken token,
        Func<CancellationToken, Task>? pace = null, Func<long>? clock = null)
    {
        if (text.Length == 0) return new("Empty");
        pace ??= cancellation => Task.Delay(1, cancellation);
        clock ??= () => Environment.TickCount64;
        int accepted = 0;
        PacedInputResult Stopped() => new(accepted > 0 ? "Partial" : "Blocked", accepted);
        try
        {
            long deadline = clock() + InputSafety.DeliveryBudgetMilliseconds(text.Length);
            bool Ready() => !token.IsCancellationRequested && safe() && clock() < deadline;
            int sinceValidation = 0;
            for (int offset = 0; offset < text.Length;)
            {
                if (!Ready()) return Stopped();
                if (Rune.DecodeFromUtf16(text.AsSpan(offset), out _, out int length) != OperationStatus.Done)
                    return Stopped();
                // Preserve the former 128-unit accessibility cadence without
                // splitting a supplementary character across validation calls.
                if (sinceValidation + length > InputSafety.BatchCharacters)
                {
                    if (!await validate(token)) return Stopped();
                    sinceValidation = 0;
                    if (!Ready()) return Stopped();
                }
                int count = send(text.Substring(offset, length));
                if (count < 0 || count > length * 2) return Stopped();
                accepted += count;
                // A short send is terminal. The native adapter may release an
                // unmatched key-down, but must never replay any text events.
                if (count != length * 2) return Stopped();
                offset += length;
                sinceValidation += length;
                if (offset < text.Length) await pace(token);
            }
            return Ready() ? new("Sent", accepted) : Stopped();
        }
        catch
        {
            // Cancellation, provider failures and pacing errors after accepted
            // events must not erase partial delivery or permit an automatic retry.
            return Stopped();
        }
    }
}
