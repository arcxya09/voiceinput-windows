using System.Text.RegularExpressions;

namespace RealtimeTranscription.Core;

/// <summary>Only omit a redundant terminal full stop on a complete, short, single-clause input.</summary>
public static class SmartPunctuation
{
    public static string Format(string text, IEnumerable<string>? protectedTerms = null)
    {
        // Bound work and keep long/multi-sentence text byte-for-byte unchanged.
        if (text.Length is < 2 or > 64 || text[^1] is not ('。' or '.')) return text;
        string body = text[..^1];
        if (body.Length == 0 || !body.Any(char.IsLetter) ||
            body.Any(c => "。.!！?？，,、；;：:\r\n…".Contains(c))) return text;
        var literals = ConfirmedCorrections.ProtectedSpans(text);
        if (body.Any(c => "\"“”‘’`=\\/:()（）[]{}<>^_".Contains(c)) || literals[^1] || (protectedTerms ?? []).Any(t => t.Length > 0 && text.EndsWith(t, StringComparison.Ordinal))) return text;
        bool han = body.Any(c => c is >= '\u3400' and <= '\u9fff');
        if (han)
        {
            if (JsonCodec.Count(body) > 12) return text;
        }
        else
        {
            // Keep bare numbers, abbreviations, initials, paths and code-like expressions.
            if (body.Length > 48 || !Regex.IsMatch(body, @"^[A-Za-z]+(?:['’][A-Za-z]+)?(?: [A-Za-z]+(?:['’][A-Za-z]+)?){0,4}$",
                RegexOptions.None, TimeSpan.FromMilliseconds(100))) return text;
            string last = body.Split(' ')[^1];
            if (last.Length == 1 || new[] { "Mr", "Mrs", "Ms", "Dr", "Prof", "Sr", "Jr", "St", "vs", "etc", "Inc", "Ltd", "Co", "No", "Fig", "Dept" }
                .Contains(last, StringComparer.OrdinalIgnoreCase)) return text;
        }
        return body;
    }
}
