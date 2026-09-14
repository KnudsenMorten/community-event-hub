using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1062 — one registration answer that may hold SEVERAL ticked options, and the rule for counting it.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"if he ticked eldk26 and others like eldk25 or eldk24, then add
/// count to eldk26. if he ticked eldk25 and eldk24, then add it to eldk25, if he ticked only eldk24,
/// then add it to eldk24"</i> — <b>the most recent edition wins, and each respondent is counted
/// exactly once.</b></para>
///
/// <para>🔑 That choice keeps the panel readable: totals still match the attendee count and
/// percentages still sum to 100%. Counting every ticked option instead would be defensible arithmetic
/// and an unreadable card — 8 attendees, 14 counts, percentages over 100%, and no obvious denominator.</para>
///
/// <para>🔴 <b>THE SEPARATOR IS NOT A COMMA, AND CANNOT BE.</b> The option labels contain commas —
/// <i>"Yes, I attended ELDK26 (Feb 2026)"</i> — so a comma-joined answer cannot be split back without
/// shredding one real label into <i>"Yes"</i> + <i>"I attended ELDK26 (Feb 2026)"</i>. The join uses
/// ASCII Unit Separator (<c></c>), which cannot occur in text a human typed into a form.</para>
/// </remarks>
public static class MultiSelectAnswer
{
    /// <summary>The join character. Non-printing, so it can never collide with an option's own text.</summary>
    public const char Separator = '';

    /// <summary>Matches the edition year in an option label: ELDK26, ELDK 25, eldk-24.</summary>
    private static readonly Regex EditionYear = new(
        @"\bELDK\s*-?\s*(\d{2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Join several ticked options into one stored value.</summary>
    public static string Join(params string[] options) =>
        string.Join(Separator, options.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()));

    /// <summary>
    /// The ONE option this answer counts as: the most recent edition the person actually attended.
    /// A single-value answer is returned unchanged, so every existing single-select field is untouched.
    /// </summary>
    public static string Collapse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var parts = raw.Split(Separator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (parts.Count <= 1) return parts.FirstOrDefault() ?? raw.Trim();

        // ⚠️ "No, ELDK27 is my first Experts Live Denmark conference" CONTAINS the HIGHEST edition
        // number on the card. Ranking on the number alone would make "I have never been" beat "I
        // attended ELDK26" — the exact opposite of the answer. So a first-time option can never win
        // while any real attendance is ticked.
        // 🔑 The same two markers the first-timer KPI in AttendeeTelemetryService already uses, so
        // the card and the KPI cannot disagree about what "first time" looks like.
        var attended = parts.Where(p => !IsFirstTimeOption(p)).ToList();
        var pool = attended.Count > 0 ? attended : parts;

        // Most recent edition wins. An option with no year sorts last but is still eligible, so a
        // future "Yes, I attended an earlier edition" option cannot vanish from the count.
        return pool
            .OrderByDescending(YearOf)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    /// <summary>True for the "this is my first one" answer, whatever edition it names.</summary>
    private static bool IsFirstTimeOption(string option) =>
        option.Contains("my first", StringComparison.OrdinalIgnoreCase)
        || option.StartsWith("no", StringComparison.OrdinalIgnoreCase);

    /// <summary>The edition year in an option label, or -1 when it names none.</summary>
    private static int YearOf(string option)
    {
        var m = EditionYear.Match(option);
        return m.Success && int.TryParse(m.Groups[1].Value, out var y) ? y : -1;
    }
}
