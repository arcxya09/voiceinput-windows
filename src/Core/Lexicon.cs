using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RealtimeTranscription.Core;

public static class Lexicon
{
    // Scientific terms retain letter case: mA/MA and Co/CO identify different terms.
    public static string WordKey(string text) => text.Trim().Normalize(NormalizationForm.FormC);
    public static bool SameWord(string left, string right) => string.Equals(WordKey(left), WordKey(right), StringComparison.Ordinal);
    /// <summary>
    /// Selects at most 200 enabled terms for the current project. A project entry overrides
    /// the same normalized global word; finite pin/project bonuses leave room for actively
    /// used global terms. The returned records retain their stored manual weights.
    /// </summary>
    public static IReadOnlyList<TermData> Select(IEnumerable<TermData> all, string project, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        return all.Where(t => t.State == TermState.Enabled && (t.Scope == "*" || t.Scope == project))
            .GroupBy(t => WordKey(t.Text), StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(t => t.Scope == project).ThenByDescending(t => t.UpdatedAt).ThenBy(t => t.Id, StringComparer.Ordinal).First())
            .OrderByDescending(t => RankingScore(t, project, at))
            .ThenByDescending(t => t.UpdatedAt).ThenBy(t => t.Id, StringComparer.Ordinal)
            .Take(200).ToArray();
    }

    /// <summary>
    /// Manual priority contributes 10–50 points, pinning 8, and project scope 4.
    /// Learning contributes at most 54 points, using capped lifetime counts discounted by
    /// time since the last observation. These are not rolling-window frequency statistics.
    /// </summary>
    public static double RankingScore(TermData term, string project, DateTimeOffset? now = null) =>
        Math.Clamp(term.Weight, 1, 5) * 10 + (term.Pinned ? 8 : 0) + (term.Scope == project ? 4 : 0)
        + LearningScore(term, now ?? DateTimeOffset.UtcNow);

    /// <summary>
    /// Computes the ASR weight without changing the user's saved weight. Learned scores of
    /// 18 and 32 add one and two levels respectively, with an overall range of 1–5.
    /// </summary>
    public static int EffectiveWeight(TermData term, DateTimeOffset? now = null)
    {
        double learned = LearningScore(term, now ?? DateTimeOffset.UtcNow);
        return Math.Clamp(Math.Clamp(term.Weight, 1, 5) + (learned >= 32 ? 2 : learned >= 18 ? 1 : 0), 1, 5);
    }

    private static double LearningScore(TermData term, DateTimeOffset now) =>
        ActivityScore(term.UsageCount, term.LastUsedAt, now, 24, 12, 30)
        + ActivityScore(term.CorrectionCount, term.LastCorrectedAt, now, 12, 6, 90);

    private static double ActivityScore(long count, DateTimeOffset? last, DateTimeOffset now, double countCap, double recentCap, double halfLifeDays)
    {
        if (count <= 0) return 0;
        double freshness = last is { } at ? Math.Pow(0.5, Math.Max(0, (now - at).TotalDays) / halfLifeDays) : 0;
        // Quarter strength retains bounded lifetime experience; the remainder ages out.
        // Convert before adding one so even a saturated long counter stays finite.
        double frequency = Math.Min(countCap, 4 * Math.Log2(1 + (double)count));
        return frequency * (0.25 + 0.75 * freshness) + recentCap * freshness;
    }
    public static string[] Matches(string raw, IEnumerable<TermData> all, string project) => all.Where(t => t.State == TermState.Enabled && t.Protect && (t.Scope == "*" || t.Scope == project) && ContainsTerm(raw, t.Text)).Select(t => t.Text).Distinct(StringComparer.Ordinal).ToArray();
    public static bool ContainsTerm(string text, string term)
    {
        if (term.Length == 0) return false;
        int start = 0;
        while ((start = text.IndexOf(term, start, StringComparison.Ordinal)) >= 0)
        {
            bool word = term.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
            if (!word || ((start == 0 || !AsciiWord(text[start-1])) && (start + term.Length == text.Length || !AsciiWord(text[start+term.Length])))) return true;
            start += term.Length;
        }
        return false;
    }
    private static bool AsciiWord(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_';
    public static string Context(IEnumerable<TermData> terms)
    {
        string result = "领域词：";
        foreach (var term in terms) { string next = result + (result.Length > 4 ? "、" : "") + term.Text; if (JsonCodec.Count(next) > 399) break; result = next; }
        return result == "领域词：" ? "" : result + "。";
    }
    public static List<TermData> ParseCandidates(string json, IReadOnlyList<SegmentData> sources, string project)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
        if (!doc.RootElement.TryGetProperty("terms", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 20) throw new FormatException("词条整理返回结构不正确。");
        var result = new List<TermData>();
        var map = sources.ToDictionary(s => s.Id);
        foreach (var item in array.EnumerateArray())
        {
            try
            {
                string text = item.GetProperty("text").GetString()?.Trim() ?? "";
                string category = item.GetProperty("category").GetString() ?? "其他";
                if (JsonCodec.Count(text) < 2 || text.All(char.IsDigit) || !new[] { "人名", "机构", "地名", "专业术语", "产品型号", "固定表达", "其他" }.Contains(category)) continue;
                var evidence = item.GetProperty("evidence");
                if (evidence.ValueKind != JsonValueKind.Array || evidence.GetArrayLength() is < 1 or > 3) continue;
                var refs = new List<TermEvidence>();
                foreach (var e in evidence.EnumerateArray())
                {
                    string id = e.GetProperty("source_segment_id").GetString() ?? "";
                    string quote = e.GetProperty("evidence_text").GetString() ?? "";
                    if (!map.TryGetValue(id, out var source) || source.AsrState != AsrState.Confirmed || source.OutputState == OutputState.Deleted || source.SaveState != SaveState.Saved) continue;
                    string content = source.EditRevision > 0 ? source.FinalText : source.RawText;
                    if (quote.Length == 0 || !quote.Contains(text, StringComparison.Ordinal) || !content.Contains(quote, StringComparison.Ordinal)) continue;
                    refs.Add(new(source.SessionId, id, source.SourceRevision, source.EditRevision, quote, source.InjectedTerms.Contains(text), source.ExtractionStart));
                }
                if (refs.Count == 0) continue;
                var term = new TermData { Scope = project, Text = text, Category = category, State = TermState.Candidate, Origin = "Extracted", Evidence = refs.DistinctBy(e => (e.SegmentId, e.SourceRevision, e.EditRevision, e.SliceStart)).ToList() };
                term.Validate(); result.Add(term);
            }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or ArgumentException) { }
        }
        return result;
    }
}
