using Microsoft.Data.Sqlite;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Infrastructure;

public sealed partial class MemoryRepository
{
    private List<TermData> ReadTerms(SqliteConnection c, string? scope = null)
    {
        using var query = Command(c, "SELECT payload FROM terms WHERE $s IS NULL OR scope=$s", ("$s", scope));
        using var reader = query.ExecuteReader(); var result = new List<TermData>();
        while (reader.Read()) result.Add(Unpack<TermData>((byte[])reader[0]));
        return result;
    }
    private static bool SameWord(TermData a, TermData b) => a.Scope == b.Scope && string.Equals(TermGenerationRules.Normalize(a.Text), TermGenerationRules.Normalize(b.Text), StringComparison.OrdinalIgnoreCase);

    public async Task<TermData> SaveUserTermAsync(TermData requested)
    {
        TermData? saved = null;
        await WriteAsync(c =>
        {
            using var tx = c.BeginTransaction();
            requested = requested with { Text = TermGenerationRules.Normalize(requested.Text) }; requested.Validate();
            if (!Enum.IsDefined(requested.State)) throw new ArgumentException("词条状态无效。");
            TermData? old = null;
            using (var query = Command(c, "SELECT payload FROM terms WHERE id=$id", ("$id", requested.Id)))
                if (query.ExecuteScalar() is byte[] bytes) old = Unpack<TermData>(bytes);
            var scope = ReadTerms(c, requested.Scope);
            if (scope.Any(t => t.Id != requested.Id && SameWord(t, requested))) throw new ArgumentException("此范围已存在相同词条，请编辑原词条。");
            if (!scope.Any(t => t.Id == requested.Id) && scope.Count >= 5000) throw new InvalidOperationException("此范围达到 5,000 个词条上限。");
            bool renamed = old != null && old.Text != requested.Text;
            saved = requested with
            {
                Revision = (old?.Revision ?? 0) + 1, UpdatedAt = DateTimeOffset.UtcNow,
                Evidence = old?.Evidence.Where(e => e.Quote.Contains(requested.Text, StringComparison.Ordinal)).ToList() ?? [],
                Origin = renamed ? "UserCorrection" : old?.Origin ?? requested.Origin,
                GenerationRequirement = old?.GenerationRequirement ?? requested.GenerationRequirement
            };
            SaveTerm(c, saved); tx.Commit();
        });
        return saved!;
    }

    public async Task<int> ImportUserTermsAsync(IReadOnlyList<TermData> input)
    {
        int added = 0;
        await WriteAsync(c =>
        {
            using var tx = c.BeginTransaction();
            foreach (var group in input.GroupBy(t => t.Scope))
            {
                var existing = ReadTerms(c, group.Key);
                var words = existing.Select(t => TermGenerationRules.Normalize(t.Text)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var value in group)
                {
                    var term = value with { Text = TermGenerationRules.Normalize(value.Text), Evidence = [] }; term.Validate();
                    if (!Enum.IsDefined(term.State)) throw new ArgumentException("词条状态无效。");
                    if (!words.Add(term.Text)) continue;
                    if (words.Count > 5000) throw new ArgumentException("导入后词库超过范围上限，未写入任何词条。");
                    SaveTerm(c, term); added++;
                }
            }
            tx.Commit();
        });
        return added;
    }

    private void MergeDuplicateTerms(SqliteConnection c)
    {
        using var tx = c.BeginTransaction();
        foreach (var scope in ReadTerms(c).GroupBy(t => t.Scope))
        foreach (var group in scope.GroupBy(t => TermGenerationRules.Normalize(t.Text), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var winner = group.OrderByDescending(t => t.Origin is "Manual" or "UserCorrection").ThenByDescending(t => t.UpdatedAt).ThenBy(t => t.Id, StringComparer.Ordinal).First();
            var merged = winner with
            {
                Revision = group.Max(t => t.Revision) + 1,
                State = group.Any(t => t.State == TermState.Disabled) ? TermState.Disabled : group.Any(t => t.State == TermState.Enabled) ? TermState.Enabled : TermState.Candidate,
                Evidence = group.SelectMany(t => t.Evidence).Where(e => e.Quote.Contains(winner.Text, StringComparison.Ordinal)).DistinctBy(e => (e.SegmentId,e.SourceRevision,e.EditRevision,e.SliceStart)).ToList()
            };
            SaveTerm(c, merged);
            foreach (var duplicate in group.Where(t => t.Id != winner.Id))
            {
                Tombstone(c, "term", duplicate.Id);
                using var delete = Command(c, "DELETE FROM terms WHERE id=$id", ("$id", duplicate.Id)); delete.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }
}
