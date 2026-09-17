using RealtimeTranscription.Core;
using RealtimeTranscription.Infrastructure;

namespace RealtimeTranscription.Desktop;

public sealed partial class AppController
{
    private readonly AsrReviewClient asrReview;
    private readonly SemaphoreSlim reviewGate = new(1, 1);
    private CancellationTokenSource? reviewCancellation;
    internal int AsrReviewTimeoutMs { get; set; } = 12000;

    private async Task ReviewCurrentAsync(string turnId, bool allowed, CancellationToken token, CancellationToken expedite)
    {
        await reviewGate.WaitAsync();
        byte[]? wav = null;
        Task<AsrReviewResult>? request = null;
        try
        {
            var prepared = await OnActor(() =>
            {
                if (engine?.Session.Id != turnId || captureAttempt is not {} attempt || attempt.Id != turnId
                    || state is CaptureState.Connecting or CaptureState.Recording or CaptureState.Draining or CaptureState.Closing
                    || attempt.ReviewStarted) return (Source: (TranscriptEngine?)null, Attempt: (CaptureAttempt?)null, Revision: 0L);
                attempt.ReviewStarted = true;
                engine.FinishAll();
                string reason = !attempt.Options.HighAccuracyEnabled ? "Disabled" : !allowed || token.IsCancellationRequested ? "Cancelled"
                    : expedite.IsCancellationRequested ? "Expedited" : !attempt.ReviewAudioComplete ? "IncompleteAudio"
                    : attempt.ReviewAudio?.Overflowed == true ? "AudioTooLong"
                    : engine.Session.Gaps.Count > 0 || engine.Segments.Any(s => s.AsrState != AsrState.Confirmed) ? "IncompleteTranscript"
                    : engine.Session.WholePolishState != "None" || engine.Segments.Any(s => s.UserLocked) || engine.Session.DeliveryState != "Pending" ? "AlreadyProcessed"
                    : string.IsNullOrWhiteSpace(TranscriptText.Render(engine.Segments)) ? "EmptyTranscript" : "";
                if (reason.Length == 0) wav = attempt.ReviewAudio?.TakeWav();
                attempt.ReviewAudio?.Dispose();
                if (reason.Length == 0 && wav == null) reason = "NoAudio";
                if (reason.Length > 0)
                {
                    engine.UpdateSession(engine.Session with { AsrReviewState = "Skipped", AsrReviewReason = reason, Revision = engine.Session.Revision + 1 });
                    LogEvent("AsrReviewSkipped", turnId: turnId, fields: [("Reason", reason)]);
                    return (Source: (TranscriptEngine?)null, Attempt: (CaptureAttempt?)null, Revision: 0L);
                }
                engine.UpdateSession(engine.Session with { AsrReviewState = "Waiting", Revision = engine.Session.Revision + 1 });
                Status("正在复核整段语音… 按回车可直接使用实时结果。");
                return (Source: (TranscriptEngine?)engine, Attempt: (CaptureAttempt?)attempt, Revision: engine.Session.Revision);
            });
            if (prepared.Source == null || prepared.Attempt == null || wav == null) return;
            Interlocked.Increment(ref activePolish);
            long began = Environment.TickCount64;
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token, expedite);
            cancelled.CancelAfter(AsrReviewTimeoutMs);
            reviewCancellation = cancelled;
            AsrReviewResult? result = null;
            string outcome = "Completed";
            bool sent = false;
            try
            {
                LogEvent("AsrReviewStarted", turnId: turnId, fields: [("AudioDurationMs", (wav.Length - 44) / 32), ("WaitBudgetMs", AsrReviewTimeoutMs)]);
                cancelled.Token.ThrowIfCancellationRequested();
                sent = true;
                request = asrReview.RecognizeAsync(prepared.Attempt.Options, Keys.BailianKey, wav, prepared.Source.Session.Hotwords, cancelled.Token);
                result = await request.WaitAsync(cancelled.Token);
                cancelled.Token.ThrowIfCancellationRequested();
                string original = TranscriptText.Render(prepared.Source.Segments);
                if (string.IsNullOrWhiteSpace(result.Text) || result.Text.Length > 20000
                    || result.Text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))
                    || (original.Length >= 40 && result.Text.Length < original.Length * .45)
                    || result.Text.Length > Math.Max(120, original.Length * 3))
                { outcome = "InvalidResult"; }
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested)
            { outcome = token.IsCancellationRequested || lifetime.IsCancellationRequested ? "Cancelled" : expedite.IsCancellationRequested ? "Expedited" : "TimedOut"; }
            catch (Exception e) { outcome = "ProviderFailure"; LogEvent("AsrReviewFailed", e, turnId); }
            finally
            {
                reviewCancellation = null;
                if (request is { IsCompleted: false }) _ = ObserveLateReviewAsync(request, turnId);
                else if (request?.IsFaulted == true) _ = request.Exception;
                if (sent) _ = RecordUsage(new("asr_review", 0, 0, result?.Duration == null, DateTimeOffset.UtcNow, result?.Duration ?? 0));
                await OnActor(() =>
                {
                    var source = prepared.Source;
                    bool applied = outcome == "Completed" && result != null && !cancelled.IsCancellationRequested
                        && ReferenceEquals(engine, source) && source.Session.DeliveryState == "Pending"
                        && source.ApplyAsrReview(result.Text, prepared.Revision);
                    if (!applied && outcome == "Completed") outcome = "Stale";
                    source.UpdateSession(source.Session with { AsrReviewState = applied ? "Completed" : "Fallback", AsrReviewReason = outcome, Revision = source.Session.Revision + 1 });
                    if (ReferenceEquals(engine, source)) Status(applied ? "整段语音复核完成。" : "已使用实时识别结果。");
                });
                LogEvent("AsrReviewCompleted", turnId: turnId, fields: [("Outcome", outcome), ("ElapsedMs", Environment.TickCount64 - began)]);
                Interlocked.Decrement(ref activePolish);
            }
        }
        finally { if (wav != null) Array.Clear(wav); reviewGate.Release(); }
    }
    private async Task ObserveLateReviewAsync(Task<AsrReviewResult> request, string turnId)
    {
        try { await request; LogEvent("LateAsrReviewDiscarded", turnId: turnId, fields: [("Outcome", "Completed")]); }
        catch (OperationCanceledException) { LogEvent("LateAsrReviewDiscarded", turnId: turnId, fields: [("Outcome", "Cancelled")]); }
        catch (Exception e) { LogEvent("LateAsrReviewDiscarded", e, turnId, ("Outcome", "Failed")); }
    }
}
