using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1060(a) — IS THIS TEXT A REAL DESCRIPTION, OR A NOTE SAYING ONE IS COMING?
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"ai must not approve a speaker session if text is blank or something
/// tbd or we are working on it and will release session description soon, then it must hold
/// back"</i>.</para>
///
/// <para>🔴 <b>Why "not empty" was never enough.</b> §926 already blocks a blank abstract, because an
/// announcement is WRITTEN FROM the description and an empty one produces an invented post rather
/// than a short one. A placeholder is the same defect wearing a disguise: <c>TBD</c> is not a
/// description, but it is not empty either, so it sailed through the one check that existed — and
/// with §1060's lead-time window dropped, the approval gate is the only thing left between a
/// half-written session and LinkedIn.</para>
///
/// <para>🔒 <b>The tuning is deliberately asymmetric, and this is the decision this class holds.</b>
/// A false NEGATIVE (a placeholder slips through) publishes one weak post, which is visible and
/// fixable. A false POSITIVE (a real abstract is called a placeholder) withholds a genuine session
/// from the campaign SILENTLY — nobody is told their talk was skipped. ⇒ When in doubt this returns
/// FALSE. Every rule below is therefore bounded: a long text is never judged on a word it happens to
/// contain.</para>
///
/// <para>⚠️ The case that drove the length bound: <i>"…a deep dive into what's <b>coming soon</b> in
/// Azure…"</i> is a perfectly good abstract for a real talk. Any rule that keys on "coming soon"
/// alone would refuse to announce it.</para>
/// </remarks>
public static class SoMePlaceholderText
{
    /// <summary>
    /// The whole text is one of these (after normalisation) ⇒ placeholder. Matched WHOLE, never as a
    /// substring: "none" as an entire abstract is a placeholder; "none of this is magic" is prose.
    /// </summary>
    private static readonly string[] WholeTextPlaceholders =
    [
        "tbd", "tba", "t b d", "n/a", "n / a", "n a", "na", "none", "nothing", "todo", "to do",
        "test", "placeholder", "pending", "unknown", "xxx", "xx", "x", "?", "??", "...", "-", "--", "---",
        "to be announced", "to be advised", "to be decided", "to be confirmed", "to be added",
        "to be written", "to be provided", "coming soon", "description coming soon",
        "more to follow", "more info to follow", "stay tuned", "watch this space",
        "will follow", "follows", "abstract follows", "description follows",
        // Short "…to follow" forms. These live HERE, as whole-text matches, rather than in the
        // sentence rule — see DescriptionWord for why "details" cannot be a keyword there.
        "details to follow", "details follow", "info to follow", "more details to follow",
    ];

    /// <summary>
    /// ⚠️ The bound that keeps a real abstract safe. Comfortably above the longest placeholder
    /// SENTENCE (his own — <i>"we are working on it and will release session description soon"</i> —
    /// is 61 characters) and comfortably below a genuine session abstract. Beyond this, the text is
    /// treated as prose no matter what words it contains.
    /// <para>🔑 Was 300, and 300 was wrong: a real 279-character abstract tripped the sentence rule.
    /// Tuned DOWN rather than papered over, because the asymmetry says so — see the class remarks.</para>
    /// </summary>
    private const int SentenceRuleMaxLength = 140;

    /// <summary>
    /// Words naming the deliverable ITSELF as an artifact — "description", "abstract", "synopsis".
    /// </summary>
    /// <remarks>
    /// 🔴 <b>Deliberately NOT "details", "summary", "text" or "content".</b> Those are ordinary prose
    /// in a real abstract, and pairing them with a pending-word refuses genuine sessions. My own
    /// false-positive test caught it: <i>"We will cover the <b>details</b> of Conditional Access
    /// design, then look at what is changing <b>soon</b> in token protection…"</i> is a real talk,
    /// and the first version of this rule called it a placeholder. Short "details to follow" forms
    /// are handled as WHOLE-TEXT matches instead, where they cannot swallow prose.
    /// </remarks>
    private static readonly Regex DescriptionWord = new(
        @"\b(descriptions?|abstracts?|synopsis|blurb|session\s+(info(rmation)?|details?)|more\s+info(rmation)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Words saying it is not here yet — "soon", "to follow", "will be added", …</summary>
    private static readonly Regex PendingWord = new(
        @"\b(soon|shortly|coming|pending|later|tbd|tba|to\s+follow|follows|missing|awaiting|"
        + @"will\s+(be\s+)?(add|added|release|released|provide|provided|update|updated|come|follow|share|shared|publish|published)|"
        + @"to\s+be\s+(added|released|provided|written|confirmed|announced|decided|completed))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>"we are working on it", "work in progress", "still writing this".</summary>
    private static readonly Regex WorkingOnIt = new(
        @"\b(we(\s+a|')?re\s+(still\s+)?working\s+on|working\s+on\s+(it|this|the)|"
        + @"work\s+in\s+progress|still\s+writing|being\s+written|not\s+(yet\s+)?(written|ready|finalis|finaliz))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="text"/> is missing, or is a note saying the real text is coming.
    /// </summary>
    public static bool IsMissingOrPlaceholder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        // Collapse whitespace and strip the punctuation people decorate a placeholder with
        // ("TBD.", "- TBD -", "(tbd)") so the whole-text comparison meets them.
        var normalised = Whitespace.Replace(text, " ").Trim();
        var stripped = normalised.Trim(' ', '.', ',', ':', ';', '!', '(', ')', '[', ']', '"', '\'', '*', '_').Trim();

        if (stripped.Length == 0) return true;   // punctuation only — "..." , "---"

        var lower = stripped.ToLowerInvariant();
        if (WholeTextPlaceholders.Contains(lower, StringComparer.Ordinal)) return true;

        // 🔒 Everything past here is bounded by length. A long text is prose, whatever it contains.
        if (stripped.Length > SentenceRuleMaxLength) return false;

        // "…will release session description soon" — the deliverable AND its absence, in one breath.
        // Order-independent on purpose: "description follows" and "awaiting the abstract" are the
        // same statement, and a regex pinned to one word order would catch only the phrasing I
        // happened to think of.
        if (DescriptionWord.IsMatch(lower) && PendingWord.IsMatch(lower)) return true;

        return WorkingOnIt.IsMatch(lower);
    }

    /// <summary>Convenience inverse — reads better at the call sites that ask for the good case.</summary>
    public static bool IsRealDescription(string? text) => !IsMissingOrPlaceholder(text);
}
