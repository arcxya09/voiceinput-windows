using System.Text;
using System.Text.RegularExpressions;

namespace RealtimeTranscription.Core;

public record ConfirmedCorrectionEdit(int Start, int Length, string Replacement, string TermId);
public record ConfirmedCorrectionResult(string Text, IReadOnlyList<ConfirmedCorrectionEdit> Edits);
public record CorrectionMappingStatus(TermData Term, bool Applicable, string Reason);
public record AppliedCorrectionOccurrence(int Start, string Text);
public record AppliedCorrectionRecord(string SegmentId, string CorrectedText, List<AppliedCorrectionOccurrence> Occurrences);

/// <summary>
/// Applies only mappings whose explicit approval the caller has verified. Alias alone is
/// not approval: imported terms and the old trace-only correction aliases must not be passed here.
/// All matches refer to the original input, so replacement text is never processed recursively.
/// </summary>
public static class ConfirmedCorrections
{
    public const int MaxRecordedOccurrences = 4096;
    private static readonly Regex Numbers = new(@"(?:(?:正|负|負|[+\-−±])[ \t]*)?(?:(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][+\-−]?[0-9]+)?|[零〇一二三四五六七八九十百千万亿两]+(?:点[零〇一二三四五六七八九]+)?)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Relations = new(@"[不没无未非否]|n['’]t(?![A-Za-z])|(?<![A-Za-z])(?:cannot|not|no|never|without|neither|nor)(?![A-Za-z])", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Quantities = new(@"大于等于|小于等于|不小于|不大于|不少于|不多于|大于|小于|等于|至少|至多|超过|低于|高于|正比|反比|增加|减少|升高|降低|上升|下降|平方|立方|次方|乘以|除以|加上|减去|分之|[正负負+\-−±<>≤≥≠=≈≃≲≳×÷^→←↔]|(?<![A-Za-z])(?:less than|greater than|at least|at most|positive|negative|squared|cubed)(?![A-Za-z])", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Units = new(@"摄氏度|华氏度|千米|厘米|毫米|微米|纳米|公斤|千克|毫克|微克|千瓦|兆瓦|毫瓦|千伏|毫伏|毫安|微安|毫秒|微秒|纳秒|小时|分钟|百分之|摄氏|华氏|欧姆|帕斯卡|电子伏|伏特|安培|瓦特|赫兹|开尔文|毫升|(?<=[零〇一二三四五六七八九十百千万亿两0-9０-９])[ \t]*(?:米|秒|度|伏|安|瓦|升|克|吨)|(?<![A-Za-z])(?:[pnumkMGTμµ]?(?:eV|Hz|Pa|Gy|Sv|W|V|A|K|J|s|m|g|b)|kg|mol|cd|rad|deg|rpm|cm|mm|km|ms|us|ns)(?![A-Za-z])|°[CF]|[%％℃℉Ω°]", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static string[] Signature(Regex pattern, string text) => pattern.Matches(text).Select(m => m.Value).ToArray();
    public static bool IsSafePair(string original, string corrected)
    {
        if (!CorrectionRules.ValidPair(original, corrected)) return false;
        try
        {
            // Compare semantic guards in compatibility-normalized form so full-width
            // letters/digits cannot hide a changed unit or English negation. Match/output
            // text itself remains byte-for-byte as explicitly approved by the user.
            string before = original.Normalize(NormalizationForm.FormKC), after = corrected.Normalize(NormalizationForm.FormKC);
            return Signature(Numbers, before).SequenceEqual(Signature(Numbers, after))
                && Signature(Relations, before).SequenceEqual(Signature(Relations, after), StringComparer.OrdinalIgnoreCase)
                && Signature(Quantities, before).SequenceEqual(Signature(Quantities, after), StringComparer.OrdinalIgnoreCase)
                && Signature(Units, before).SequenceEqual(Signature(Units, after));
        }
        catch (RegexMatchTimeoutException) { return false; }
    }

    public static IReadOnlyList<CorrectionMappingStatus> AnalyzeMappings(IReadOnlyList<TermData> terms, string projectId)
    {
        var initial = terms.Select(t => new CorrectionMappingStatus(t, false,
            t.State != TermState.Enabled ? "对应词条未启用" : t.Scope != "*" && t.Scope != projectId ? "不属于当前项目"
            : !IsSafePair(t.Alias, t.Text) ? "写法不适合自动纠正，或涉及数字、单位、否定及数量关系变化" : "")).ToArray();
        var usable = initial.Where(s => s.Reason.Length == 0).Select(s => s.Term).ToArray();
        var ambiguous = usable.GroupBy(t => t.Alias, StringComparer.Ordinal)
            .Where(g => g.Select(t => t.Text).Distinct(StringComparer.Ordinal).Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var mappings = usable.Where(t => !ambiguous.Contains(t.Alias))
            .GroupBy(t => t.Alias, StringComparer.Ordinal)
            .Select(g => g.OrderBy(t => t.Scope == projectId ? 0 : 1).ThenBy(t => t.Id, StringComparer.Ordinal).First())
            .OrderBy(t => t.Alias, StringComparer.Ordinal).ToArray();
        var conflicting = new HashSet<string>(StringComparer.Ordinal);
        var byAlias = mappings.ToDictionary(t => t.Alias, StringComparer.Ordinal);
        int maxAliasLength = mappings.Select(t => t.Alias.Length).DefaultIfEmpty(0).Max();
        foreach (var a in mappings)
            for (int start = 0; start < a.Text.Length; start++)
                for (int length = 2; length <= Math.Min(maxAliasLength, a.Text.Length - start); length++)
                    if (byAlias.TryGetValue(a.Text.Substring(start, length), out var b) && WordBoundary(a.Text, start, start + length))
                    { conflicting.Add(a.Alias); conflicting.Add(b.Alias); }
        return initial.Select(s => s.Reason.Length > 0 ? s : ambiguous.Contains(s.Term.Alias)
            ? s with { Reason = "同一旧写法对应多个标准词，需先解决冲突" }
            : conflicting.Contains(s.Term.Alias) ? s with { Reason = "与已启用规则形成替换链、循环或自扩展，需先调整映射" }
            : s with { Applicable = true }).ToArray();
    }

    public static ConfirmedCorrectionResult Apply(string raw, IReadOnlyList<TermData> approvedTerms, string projectId)
    {
        if (raw.Length == 0 || raw.Length > 60000 || approvedTerms.Count == 0) return new(raw, []);
        // Use the same analysis as the vocabulary UI; a term id may have several approved aliases.
        var mappings = AnalyzeMappings(approvedTerms, projectId).Where(s => s.Applicable).Select(s => s.Term)
            .GroupBy(t => t.Alias, StringComparer.Ordinal)
            .Select(g => g.OrderBy(t => t.Scope == projectId ? 0 : 1).ThenBy(t => t.Id, StringComparer.Ordinal).First()).ToArray();
        if (mappings.Length == 0) return new(raw, []);
        var protectedSpans = ProtectedSpans(raw);
        var matches = new List<ConfirmedCorrectionEdit>();
        foreach (var term in mappings)
        {
            int start = 0;
            while ((start = raw.IndexOf(term.Alias, start, StringComparison.Ordinal)) >= 0)
            {
                int end = start + term.Alias.Length;
                if (WordBoundary(raw, start, end) && !protectedSpans.AsSpan(start, end - start).Contains(true))
                    matches.Add(new(start, end - start, term.Text, term.Id));
                start++;
                if (matches.Count > MaxRecordedOccurrences) return new(raw, []);
            }
        }
        // Overlapping aliases are all skipped instead of silently choosing by list order.
        var ordered = matches.OrderBy(m => m.Start).ThenBy(m => m.Length).ToArray();
        var accepted = new List<ConfirmedCorrectionEdit>();
        for (int i = 0; i < ordered.Length;)
        {
            int next = i + 1, end = ordered[i].Start + ordered[i].Length;
            while (next < ordered.Length && ordered[next].Start < end)
            { end = Math.Max(end, ordered[next].Start + ordered[next].Length); next++; }
            if (next == i + 1) accepted.Add(ordered[i]);
            i = next;
        }
        var text = new StringBuilder(raw);
        if (raw.Length + accepted.Sum(e => e.Replacement.Length - e.Length) > 60000) return new(raw, []);
        foreach (var edit in accepted.AsEnumerable().Reverse()) text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
        return new(text.ToString(), accepted);
    }

    public static IReadOnlyList<AppliedCorrectionOccurrence> Remaining(AppliedCorrectionRecord record, string current)
    {
        if (record.CorrectedText.Length > 60000 || record.Occurrences.Count > MaxRecordedOccurrences) return [];
        string original = record.CorrectedText;
        int prefix = 0, suffix = 0;
        while (prefix < original.Length && prefix < current.Length && original[prefix] == current[prefix]) prefix++;
        while (suffix < original.Length - prefix && suffix < current.Length - prefix && original[original.Length - suffix - 1] == current[current.Length - suffix - 1]) suffix++;
        // Count only occurrences whose original automatic replacement can still be verified.
        // User-created copies inside the changed region never become automatic corrections.
        return record.Occurrences.Where(o => o.Start >= 0 && o.Text.Length > 0 && o.Start <= original.Length - o.Text.Length
            && original.AsSpan(o.Start, o.Text.Length).SequenceEqual(o.Text.AsSpan())
            && (o.Start + o.Text.Length <= prefix || o.Start >= original.Length - suffix)).ToArray();
    }

    private static bool LatinWord(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '+' or '#';
    private static bool WordBoundary(string text, int start, int end) =>
        !(start > 0 && LatinWord(text[start]) && LatinWord(text[start - 1]))
        && !(end < text.Length && LatinWord(text[end - 1]) && LatinWord(text[end]));

    // Scan the whole turn, not individual ASR sentences: a quote or code block may span them.
    // An unmatched opener protects the remaining text conservatively.
    internal static bool[] ProtectedSpans(string text)
    {
        var mask = new bool[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            int end = -1;
            if (Escaped(text, i)) continue;
            if (text[i] is '`' or '~' or '$')
            {
                int run = 1;
                while (i + run < text.Length && text[i + run] == text[i]) run++;
                if (text[i] == '~' && run < 3) continue;
                string marker = new(text[i], run);
                int close = FindClose(text, marker, i + run);
                end = close < 0 ? text.Length : close + run;
            }
            else
            {
                char close = text[i] switch { '"' => '"', '“' => '”', '‘' => '’', '「' => '」', '『' => '』', '(' => ')', '（' => '）', '[' => ']', '\'' => '\'', _ => '\0' };
                if (close == '\0') continue;
                // An apostrophe inside an English word does not start a quoted span.
                if (text[i] == '\'' && i > 0 && i + 1 < text.Length && char.IsLetterOrDigit(text[i - 1]) && char.IsLetterOrDigit(text[i + 1])) continue;
                int at;
                if (text[i] is '(' or '（' or '[')
                {
                    int depth = 1; at = i + 1;
                    for (; at < text.Length; at++)
                    {
                        if (Escaped(text, at)) continue;
                        if (text[at] == text[i]) depth++;
                        if (text[at] == close && --depth == 0) break;
                    }
                    if (at == text.Length) at = -1;
                }
                else at = FindClose(text, close.ToString(), i + 1);
                end = at < 0 ? text.Length : at + 1;
            }
            Array.Fill(mask, true, i, end - i);
            i = end - 1;
        }
        return mask;
    }
    private static int FindClose(string text, string marker, int start)
    {
        while ((start = text.IndexOf(marker, start, StringComparison.Ordinal)) >= 0)
        {
            if (!Escaped(text, start)) return start;
            start += marker.Length;
        }
        return -1;
    }
    private static bool Escaped(string text, int at)
    {
        int slashes = 0;
        while (at > 0 && text[--at] == '\\') slashes++;
        return slashes % 2 != 0;
    }
}
