using System.Globalization;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Infrastructure;

public record StoredSession(SessionData Session, IReadOnlyList<SegmentData> Segments);

public sealed partial class MemoryRepository
{
    public Task<StoredSession?> LoadSessionAsync(string id) => Task.Run(() =>
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        using var sessionQuery = Command(c, "SELECT payload FROM sessions WHERE id=$id", ("$id", id));
        if (sessionQuery.ExecuteScalar() is not byte[] bytes) return null;
        var session = Unpack<SessionData>(bytes);
        using var parts = Command(c, "SELECT payload FROM segments WHERE session=$id ORDER BY task_order,sentence", ("$id", id));
        var segments = new List<SegmentData>();
        using (var reader = parts.ExecuteReader())
            while (reader.Read()) segments.Add(Unpack<SegmentData>((byte[])reader[0]) with { SaveState = SaveState.Saved });
        tx.Commit(); return new StoredSession(session, segments);
    });

    // Turning off memory must not upload new local text into the existing history.
    public Task SaveLearningPermissionAsync(SessionData requested) => WriteAsync(c =>
    {
        using var tx = c.BeginTransaction();
        using var query = Command(c, "SELECT payload FROM sessions WHERE id=$id", ("$id", requested.Id));
        if (query.ExecuteScalar() is not byte[] bytes) return;
        var saved = Unpack<SessionData>(bytes);
        if (requested.LearningRevision < saved.LearningRevision) return;
        SaveSession(c, saved with { AllowLearning = requested.AllowLearning, LearningRevision = requested.LearningRevision, Revision = Math.Max(saved.Revision + 1, requested.Revision) });
        tx.Commit();
    });

    private static string UtcDay(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public Task ReserveTermBudgetAsync(long tokens, long limit, DateTimeOffset at, CancellationToken token) => WriteAsync(c =>
    {
        token.ThrowIfCancellationRequested();
        if (tokens <= 0 || limit <= 0) throw new ArgumentOutOfRangeException(nameof(tokens));
        using var tx = c.BeginTransaction();
        using var spent = Command(c, "SELECT COALESCE(SUM(input+output),0) FROM usage WHERE day=$d AND purpose='term_budget'", ("$d", UtcDay(at)));
        if (Convert.ToInt64(spent.ExecuteScalar()) + tokens > limit)
            throw new ProviderException("今日词库 AI Token 预算不足，可在设置中调整。已完成的结果保留。");
        using var reserve = Command(c, "INSERT INTO usage(day,purpose,input,output,unknown,audio) VALUES($d,'term_budget',$n,0,0,0)", ("$d", UtcDay(at)), ("$n", tokens));
        reserve.ExecuteNonQuery(); token.ThrowIfCancellationRequested(); tx.Commit();
    });
}
