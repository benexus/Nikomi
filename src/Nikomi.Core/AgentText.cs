using System.Globalization;
using System.Text;

namespace Nikomi.Core;

/// <summary>
/// Cleans text that a program or an agent wrote before it is shown to the user or handed to an agent.
/// Such text is untrusted: it can hide words with zero-width or tag characters, reorder what is shown
/// with bidi controls, or be long enough to bury everything around it.
/// </summary>
public static class AgentText
{
    /// <summary>Descriptions, company names and service names.</summary>
    public const int MaxLabel = 200;
    /// <summary>An agent's reason for an end request.</summary>
    public const int MaxReason = 400;

    /// <summary>
    /// Drops invisible and direction-changing characters, turns control characters and line breaks into
    /// spaces, trims, and cuts the result to <paramref name="max"/> characters (with " ..." after a cut).
    /// Null when nothing visible is left.
    /// </summary>
    public static string? Clean(string? text, int max = MaxLabel, bool collapseSpaces = false)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var sb = new StringBuilder(Math.Min(text.Length, max));
        bool lastSpace = false, cut = false;
        foreach (var rune in text.EnumerateRunes())   // a broken surrogate comes as U+FFFD
        {
            if (IsHidden(rune)) continue;
            bool space = Rune.IsWhiteSpace(rune) || Rune.IsControl(rune) || IsLineBreak(rune);
            if (space && (sb.Length == 0 || collapseSpaces && lastSpace)) continue;
            if (sb.Length + rune.Utf16SequenceLength > max) { cut = true; break; }   // never half a character
            if (space) sb.Append(' ');
            else sb.Append(rune.ToString());
            lastSpace = space;
        }
        string clean = sb.ToString().TrimEnd();
        if (clean.Length == 0) return null;
        return cut ? clean + " ..." : clean;
    }

    /// <summary>An agent's reason: cleaned, runs of spaces made one, at most <see cref="MaxReason"/> characters.</summary>
    public static string Reason(string? text) => Clean(text, MaxReason, collapseSpaces: true) ?? "";

    private static bool IsLineBreak(Rune r) => r.Value is 0x2028 or 0x2029 or 0x85;

    /// <summary>
    /// Characters that show nothing or change how the text around them is shown: format characters
    /// (zero-width spaces and joiners, bidi marks, embeddings, overrides and isolates, soft hyphen,
    /// byte-order mark, the invisible tag characters), variation selectors, and the Hangul fillers and
    /// other blank letters that render as nothing.
    /// </summary>
    private static bool IsHidden(Rune r) =>
        Rune.GetUnicodeCategory(r) == UnicodeCategory.Format
        || r.Value is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF   // variation selectors
        || r.Value is 0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x180B or 0x180C or 0x180D or 0x180E or 0x180F
            or 0x3164 or 0xFFA0 or 0xFFF9 or 0xFFFA or 0xFFFB;
}
