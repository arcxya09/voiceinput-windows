using RealtimeTranscription.Core;
using Microsoft.Data.Sqlite;

namespace RealtimeTranscription.Infrastructure;

public sealed partial class MemoryRepository
{
    private DomainProfile? ReadDomainProfile(SqliteConnection c, string project)
    {
        using var query = Command(c, "SELECT payload FROM domain_profiles WHERE project=$p", ("$p", project));
        return query.ExecuteScalar() is byte[] bytes ? Unpack<DomainProfile>(bytes) : null;
    }
    public Task<DomainProfile?> DomainProfileAsync(string project) => Task.Run(() => { using var c = Open(); return ReadDomainProfile(c, project); });

    public Task<List<DomainSample>> DomainSamplesAsync(string project, CancellationToken token) => Task.Run(() =>
    {
        using var c = Open(); var sessions = new List<SessionData>();
        using (var query = Command(c, "SELECT payload FROM sessions WHERE project=$p ORDER BY created DESC,id DESC LIMIT 48", ("$p", project)))
        using (var reader = query.ExecuteReader()) while (reader.Read()) { token.ThrowIfCancellationRequested(); var s = Unpack<SessionData>((byte[])reader[0]); if (s.AllowLearning) sessions.Add(s); }
        var result = new List<DomainSample>(); int recentBudget = 3000, olderBudget = 1000;
        for (int i = 0; i < sessions.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var session = sessions[i]; bool recent = i < 12;
            int budget = recent ? recentBudget : olderBudget; if (budget <= 0) continue;
            using var query = Command(c, "SELECT payload FROM segments WHERE session=$s ORDER BY task_order DESC,sentence DESC LIMIT 8", ("$s", session.Id));
            using var reader = query.ExecuteReader(); int taken = 0;
            while (reader.Read() && taken < 2 && budget > 0)
            {
                var segment = Unpack<SegmentData>((byte[])reader[0]);
                if (segment.AsrState != AsrState.Confirmed || segment.OutputState != OutputState.Published) continue;
                // Exclude generated polish/corrections; only raw speech or a user's saved revision supplies text.
                string text = JsonCodec.Take(segment.EditRevision > 0 ? segment.FinalText : segment.RawText, Math.Min(recent ? 500 : 200, budget));
                if (string.IsNullOrWhiteSpace(text)) continue;
                result.Add(new(session, segment, text, recent)); budget -= JsonCodec.Count(text); taken++;
            }
            if (recent) recentBudget = budget; else olderBudget = budget;
        }
        return result;
    }, token);

    public async Task<int> CommitDomainPredictionAsync(IReadOnlyList<DomainSample> sources, DomainPrediction prediction, CancellationToken token)
    {
        int count = 0;
        await WriteAsync(c =>
        {
            token.ThrowIfCancellationRequested(); using var tx = c.BeginTransaction(); string project = prediction.Profile.ProjectId;
            if (sources.Count == 0 || Dead(c, "project", project) || prediction.Profile.Fingerprint != DomainLexicon.Fingerprint(sources)) throw new InvalidOperationException("领域分析来源已失效。");
            foreach (var source in sources)
            {
                using var query = Command(c, "SELECT s.payload,g.payload FROM sessions s JOIN segments g ON g.session=s.id WHERE s.id=$s AND g.id=$g", ("$s", source.Session.Id), ("$g", source.Segment.Id));
                using var reader = query.ExecuteReader();
                if (!reader.Read()) throw new InvalidOperationException("领域分析来源已删除。");
                var session = Unpack<SessionData>((byte[])reader[0]); var segment = Unpack<SegmentData>((byte[])reader[1]);
                string text = segment.EditRevision > 0 ? segment.FinalText : segment.RawText;
                if (!session.AllowLearning || session.ProjectId != project || session.LearningRevision != source.Session.LearningRevision
                    || segment.SourceRevision != source.Segment.SourceRevision || segment.EditRevision != source.Segment.EditRevision
                    || segment.AsrState != AsrState.Confirmed || segment.OutputState != OutputState.Published || !text.StartsWith(source.Text, StringComparison.Ordinal))
                    throw new InvalidOperationException("领域分析期间历史或学习许可已变化，请重新分析。");
            }
            var existing = ReadTerms(c, project); var blocked = new HashSet<string>(StringComparer.Ordinal);
            using (var query = Command(c, "SELECT payload FROM suppression WHERE scope=$p OR scope='*'", ("$p", project)))
            using (var reader = query.ExecuteReader()) while (reader.Read()) blocked.Add(Unpack<string>((byte[])reader[0]).Split('\n', 2)[^1]);
            var global = ReadTerms(c, "*").Select(t => t.Text).ToHashSet(StringComparer.Ordinal);
            var active = new List<string>();
            foreach (var candidate in prediction.Terms)
            {
                candidate.Validate();
                if (candidate.Origin != "Predicted" || candidate.Scope != project || candidate.Alias.Length != 0 || candidate.Protect || candidate.Weight != 1
                    || candidate.Evidence.Count == 0 || candidate.Evidence.Any(e => !sources.Any(s => s.Session.Id == e.SessionId && s.Segment.Id == e.SegmentId
                        && s.Segment.SourceRevision == e.SourceRevision && s.Segment.EditRevision == e.EditRevision && e.Quote.Length >= 4 && s.Text.Contains(e.Quote, StringComparison.Ordinal))))
                    throw new InvalidOperationException("领域词条来源验证失败。");
                var old = existing.FirstOrDefault(t => Lexicon.SameWord(t.Text, candidate.Text));
                if (blocked.Contains(candidate.Text) || global.Contains(candidate.Text) || old != null && (old.State == TermState.Disabled || old.Origin != "Predicted")) continue;
                if (old == null && existing.Count >= 5000) continue;
                var saved = candidate with { Id = old?.Id ?? candidate.Id, Revision = (old?.Revision ?? 0) + 1 };
                SaveTerm(c, saved); existing.RemoveAll(t => t.Id == saved.Id); existing.Add(saved); active.Add(saved.Text); count++;
            }
            // Keep a small inactive pool for inspection, but never let predictions grow without bound.
            foreach (var stale in existing.Where(t => t.Origin == "Predicted" && t.State != TermState.Disabled && !active.Contains(t.Text))
                .OrderByDescending(t => t.UpdatedAt).Skip(100))
            { using var delete = Command(c, "DELETE FROM terms WHERE id=$id", ("$id", stale.Id)); delete.ExecuteNonQuery(); }
            var profile = prediction.Profile with { ActiveTerms = active };
            using var write = Command(c, "INSERT INTO domain_profiles(project,payload) VALUES($p,$b) ON CONFLICT(project) DO UPDATE SET payload=excluded.payload", ("$p", project), ("$b", Pack(profile)));
            write.ExecuteNonQuery(); token.ThrowIfCancellationRequested(); tx.Commit();
        });
        return count;
    }

    private void InvalidateDomainProfiles(SqliteConnection c, Func<TermEvidence, bool> removed)
    {
        var affected = new List<string>();
        using (var query = Command(c, "SELECT payload FROM domain_profiles"))
        using (var reader = query.ExecuteReader()) while (reader.Read()) { var p = Unpack<DomainProfile>((byte[])reader[0]); if (p.Sources.Any(removed)) affected.Add(p.ProjectId); }
        foreach (string project in affected) { using var delete = Command(c, "DELETE FROM domain_profiles WHERE project=$p", ("$p", project)); delete.ExecuteNonQuery(); }
    }
}
