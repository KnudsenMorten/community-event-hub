using System.Text;
using System.Text.RegularExpressions;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1158 — the legal form on a company name (A/S, ApS, GmbH, LLC, K/S, AG …).
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-31: <i>"the publicname in the webshop has wrong format for some sponsors …
/// it should not include legal terms like AG, Aps, A/S, LLC, K/S, Gmbh"</i> ·
/// <i>"billing company name includes these — but public name is for linkedin"</i>.</para>
///
/// <para>🔑 <b>Two names, two rules.</b> The BILLING name is the legal entity and MUST keep its form
/// — an invoice with the form stripped is wrong. The PUBLIC name is what an audience reads (the
/// sponsor wall, the event platform, LinkedIn) and must not carry it. So this is never a global
/// "clean the name" helper: it applies to the public-facing name only, and every caller has to have
/// decided which of the two it is holding.</para>
///
/// <para>⚠️ <b>TRAILING ONLY, and whole tokens only.</b> A legal form is a suffix; matching it
/// anywhere would eat real words — "AS" inside a name, "Inc" starting "Incentive". The match runs on
/// the last few tokens and compares them normalised (upper-cased, dots and inner spaces removed), so
/// <c>S.A.</c>, <c>SA</c>, <c>Sp. z o.o.</c> and <c>GmbH &amp; Co. KG</c> all resolve.</para>
/// </remarks>
public static class CompanyLegalForm
{
    /// <summary>
    /// The forms recognised. Callers may pass their own list, but nothing configures one today —
    /// this is the single source, kept in code because it is a fact about company law rather than
    /// about an edition, and a community running the hub elsewhere needs the same list.
    /// </summary>
    /// <remarks>
    /// ⚠️ Deliberately EXCLUDES highly ambiguous two-letter forms that are also ordinary words or
    /// brand fragments (e.g. "SE", "CO"). A false positive here tells him to edit a name that is
    /// already right, which is the fastest way to make him stop reading the mail.
    /// </remarks>
    public static readonly IReadOnlyList<string> DefaultForms = new[]
    {
        // Nordic
        "A/S", "ApS", "K/S", "P/S", "I/S", "IVS", "AB", "AS", "ASA", "Oy", "Oyj", "A.M.B.A.", "SMBA",
        // German-speaking
        "AG", "GmbH", "mbH", "KG", "GmbH & Co. KG", "AG & Co. KG", "OHG", "UG",
        // Anglophone
        "LLC", "L.L.C.", "LLP", "LP", "Inc", "Inc.", "Incorporated", "Corp", "Corp.", "Corporation",
        "Ltd", "Ltd.", "Limited", "PLC", "P.L.C.", "Pty", "Pty Ltd", "Pte", "Pte Ltd",
        // Rest of Europe
        "BV", "B.V.", "NV", "N.V.", "SA", "S.A.", "SAS", "SARL", "S.à r.l.", "SRL", "S.r.l.",
        "SpA", "S.p.A.", "S.L.", "d.o.o.", "d.d.", "Kft", "Zrt", "OÜ", "UAB", "SIA", "Ltda",
        // Polish. ⚠️ These STACK — a limited partnership whose general partner is a limited company
        // writes both forms in a row ("sp. z o.o. sp. k."), which is FIVE tokens. That is why the
        // trailing window below is six and not four: a four-token window silently matched none of
        // it and left the name untouched, which is exactly how this was found in production.
        "Sp. z o.o.", "Sp. k.", "Sp. j.", "S.K.A.", "Sp. z o.o. sp. k.", "Sp. z o.o. S.K.A.",
        // German extras that also stack.
        "KGaA", "AG & Co. KGaA", "GmbH & Co. KGaA", "e.K.", "e.V.",
    };

    private static readonly Regex Splitter = new(@"[\s,]+", RegexOptions.Compiled);

    /// <summary>
    /// The trailing legal form on <paramref name="name"/>, or null when there is none.
    /// </summary>
    /// <param name="name">The name to inspect.</param>
    /// <param name="forms">Recognised forms; <see cref="DefaultForms"/> when null or empty.</param>
    /// <returns>The form AS IT APPEARS IN THE NAME (e.g. "Gmbh"), so a report can quote it back.</returns>
    public static string? Detect(string? name, IEnumerable<string>? forms = null)
    {
        var (form, _) = Split(name, forms);
        return form;
    }

    /// <summary>The name with its trailing legal form removed — what the public name should read.</summary>
    /// <remarks>
    /// 🔒 Returns the name UNCHANGED when there is no form, and never returns an empty string: a name
    /// that is nothing but a legal form ("A/S") is left alone rather than blanked, because a blank
    /// public name silently falls back to the legal name and would make the problem worse.
    /// </remarks>
    public static string Strip(string? name, IEnumerable<string>? forms = null)
    {
        var (form, stripped) = Split(name, forms);
        return form is null ? (name ?? string.Empty).Trim() : stripped;
    }

    /// <summary>Split a name into (the trailing legal form, the name without it).</summary>
    private static (string? Form, string Stripped) Split(string? name, IEnumerable<string>? forms)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return (null, string.Empty);

        var known = BuildLookup(forms);
        var tokens = Splitter.Split(trimmed).Where(t => t.Length > 0).ToArray();
        if (tokens.Length == 0) return (null, trimmed);

        // Longest trailing run first: "GmbH & Co. KG" must win over a bare "KG", or the report
        // would quote half the form and the suggestion would keep the other half.
        //
        // ⚠️ SIX, not four. "sp. z o.o. sp. k." tokenises to five, so a four-token window matched
        // nothing and the name was silently left alone — it looked identical to "this name is
        // fine", which is why the first production sweep changed 10 of 17 and nobody could tell
        // from the result which of the other 7 were correct and which were unrecognised.
        var maxRun = Math.Min(6, tokens.Length);
        for (var run = maxRun; run >= 1; run--)
        {
            // 🔒 Never consume the WHOLE name — "A/S" alone is not a company with a legal form, it
            // is a name we do not understand, and stripping it would leave nothing.
            if (run == tokens.Length) continue;

            var candidate = string.Join(' ', tokens[^run..]);
            if (!known.Contains(Normalize(candidate))) continue;

            var head = trimmed[..IndexOfTrailingRun(trimmed, tokens, run)];
            return (candidate, TrimJoiners(head));
        }

        return (null, trimmed);
    }

    /// <summary>Where the trailing run of <paramref name="run"/> tokens starts in the original text.</summary>
    /// <remarks>
    /// Works on the ORIGINAL string rather than re-joining tokens, so the kept part preserves the
    /// author's own spacing and punctuation instead of being silently reformatted.
    /// </remarks>
    private static int IndexOfTrailingRun(string original, string[] tokens, int run)
    {
        var idx = original.Length;
        for (var i = 0; i < run; i++)
        {
            var token = tokens[^(i + 1)];
            idx = original.LastIndexOf(token, idx - 1, StringComparison.Ordinal);
            if (idx < 0) return original.Length;   // defensive: never crash on a name
        }
        return idx;
    }

    /// <summary>Drop the punctuation that joined the name to its form (", ", " - ", " &amp; ").</summary>
    private static string TrimJoiners(string s) => s.TrimEnd(' ', ',', '-', '–', '&', '\t');

    private static HashSet<string> BuildLookup(IEnumerable<string>? forms)
    {
        var source = forms?.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        if (source is null || source.Count == 0) source = DefaultForms.ToList();

        return source.Select(Normalize).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Upper-case, drop dots and inner whitespace — so "S.A.", "SA" and "S. A." agree.</summary>
    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is '.' or ' ' or '\t') continue;
            sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }
}
