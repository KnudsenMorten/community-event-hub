using System.Net;
using System.Text.RegularExpressions;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §989 — the ONE way CEH decides whether a long rich-text field in Zoho still matches the
/// value CEH holds (company description, session abstract, …).
/// </summary>
/// <remarks>
/// <para>🔑 <b>Why this exists as a shared helper.</b> The sponsor path
/// (<see cref="SponsorZohoSyncService"/>) had solved this correctly since §792 — strip tags,
/// decode entities, collapse whitespace, then compare — while the SESSION path
/// (<c>SessionBackstagePushService.BuildLinkedSessionDiffs</c>) carried a cruder private
/// <c>StripHtml</c> that only removed tags. Two copies of "is this the same text?" is exactly the
/// §660/§719 drift shape, and the two HAD already drifted: the session copy never decoded
/// entities, so a Zoho description of <c>&lt;p&gt;&amp;nbsp;&lt;/p&gt;</c> — which is what that
/// editor stores for a field the operator SEES as empty — normalized to the literal
/// <c>"&amp;nbsp;"</c>, read as non-blank, and the "Session Description is empty" gap was never
/// reported. That is the §322l complaint (*"session description is still missing in zoho"*).</para>
///
/// <para>⚠️ <b>Comparison is deliberately forgiving, and that is the whole design.</b> Zoho's
/// rich-text editor REFORMATS what it stores — it re-wraps in <c>&lt;p&gt;</c>, converts spacing to
/// <c>&amp;nbsp;</c>, and re-encodes typographic characters. A char-for-char compare would
/// therefore differ FOREVER on a value nobody changed, and §594 is the standing lesson on what that
/// costs: an ACTION line the operator cannot close by acting teaches him to ignore the mechanism
/// entirely. Every fold below exists to remove a difference the EDITOR introduced, never one a
/// PERSON made.</para>
///
/// <para>🔒 What is deliberately NOT folded: letter case, words, and punctuation that carries
/// meaning. Those are real edits and must still surface.</para>
/// </remarks>
public static class RichTextCompare
{
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// The comparable form of a rich-text value: tags removed, HTML entities decoded, typographic
    /// variants folded to their ASCII form, all whitespace collapsed to single spaces, trimmed.
    /// </summary>
    public static string Comparable(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;

        // Tags → a SPACE, not "": "<p>one</p><p>two</p>" must not become "onetwo".
        var text = Tags.Replace(s, " ");
        text = WebUtility.HtmlDecode(text);
        text = FoldTypography(text);
        return Whitespace.Replace(text, " ").Trim();
    }

    /// <summary>
    /// True when a value is empty as the OPERATOR sees it — nothing left once the editor's own
    /// markup and padding are removed. <c>&lt;p&gt;&amp;nbsp;&lt;/p&gt;</c> and
    /// <c>&lt;p&gt;&lt;br&gt;&lt;/p&gt;</c> are both empty; plain <see cref="string.IsNullOrWhiteSpace"/>
    /// says otherwise and is the wrong test for anything that came out of a rich-text editor.
    /// </summary>
    public static bool IsEffectivelyBlank(string? s) => Comparable(s).Length == 0;

    /// <summary>
    /// True when the Zoho-side value no longer matches the CEH value and the operator should be
    /// told to re-paste. A blank CEH value is never reported (nothing to paste).
    /// </summary>
    /// <param name="inZoho">The live value read back from Zoho.</param>
    /// <param name="inCeh">The value CEH holds — the source of truth for these fields.</param>
    /// <param name="ignoreCase">
    /// <c>true</c> for the sponsor fields (§792 precedent: URLs and company text, where a case
    /// difference is noise). <c>false</c> for prose the operator pastes verbatim, where a case
    /// change is a real edit.
    /// </param>
    public static bool DiffersFromCeh(string? inZoho, string? inCeh, bool ignoreCase)
    {
        if (string.IsNullOrWhiteSpace(inCeh)) return false;      // nothing to paste
        if (IsEffectivelyBlank(inZoho)) return true;             // blank in Zoho
        return !string.Equals(
            Comparable(inZoho), Comparable(inCeh),
            ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>
    /// Fold the characters a rich-text editor substitutes on its own — curly quotes, dashes,
    /// ellipsis, non-breaking and zero-width spaces — onto the ASCII the operator typed.
    /// </summary>
    /// <remarks>
    /// ⚠️ Without this, ONE smart-quote substitution in a 900-character abstract produces a
    /// permanent, unclosable "description differs" line: he pastes CEH's text, the editor re-curls
    /// the apostrophe on save, and the next pass reports it again. That is §594 reproduced exactly,
    /// so the fold is a correctness requirement, not a nicety.
    /// </remarks>
    private static string FoldTypography(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '‘': case '’': case '‚': case '‛':
                case '′': case '´': case '`':
                    sb.Append('\''); break;                       // ‘ ’ ‚ ‛ ′ ´ ` → '
                case '“': case '”': case '„': case '‟': case '″':
                    sb.Append('"'); break;                        // “ ” „ ‟ ″ → "
                case '‐': case '‑': case '‒': case '–':
                case '—': case '―': case '−':
                    sb.Append('-'); break;                        // ‐ ‑ ‒ – — ― − → -
                case '…':
                    sb.Append("..."); break;                      // … → ...
                case ' ': case ' ': case ' ': case ' ':
                    sb.Append(' '); break;                        // nbsp + friends → space
                case '​': case '‌': case '‍': case '﻿':
                    break;                                        // zero-width → dropped
                default:
                    sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
