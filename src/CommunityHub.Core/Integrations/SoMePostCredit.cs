namespace CommunityHub.Core.Integrations;

/// <summary>
/// §861 — THE ORGANIZER CREDIT IS A VARIABLE, NOT TEXT HE OWNS.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-05: <i>"i dont like that we talk about \"override\" - we have one single
/// field for a post"</i>, and <i>"we will not adjust credits (organizers). only change will be the
/// mention which hopefully will change tomorrow so you must replace it with variables"</i>.</para>
///
/// <para>🔴 <b>THE DEFECT THIS FIXES (§861.3, measured on PROD).</b> The editor loaded the FULLY
/// COMPOSED post and saved the whole thing back, so his edit captured the credit line as literal
/// text. Posts <c>383</c> and <c>495</c> already carry a frozen copy. The consequence is not that
/// names go stale — he has ruled that the names will not change — it is that <b>the credit can never
/// be UPGRADED TO @-MENTIONS</b>, which is precisely the change he expects (§858). A frozen footer
/// would silently opt those posts out of it.</para>
///
/// <para>🔑 <b>THE SEAM IS UNIFORM ACROSS ALL FIVE TYPES.</b> Every template in
/// <see cref="SoMeTemplateCatalog"/> ends with the same block — Types 1–4 via the shared
/// <c>Footer</c>, Type 5 via its own tail (§834.6). So one suffix strips and re-appends for every
/// post, with no per-type branching:</para>
/// <code>
/// \n\n{EditionCode} Organizers:\n{OrganizerCredits}
/// </code>
///
/// <para>⚠️ <b>Deliberately NOT extended to <c>{EventTags}</c>.</b> For Types 1–4 the tag block sits
/// inside the shared footer, but for Type 5 the tags are <b>his own copy</b> (§834.6 measured: all
/// 27 deck posts carry their own tag block). Splitting tags out would need per-type rules and would
/// touch text he wrote. The credit is the part he asked to become a variable; that is the part that
/// moves.</para>
/// </remarks>
public static class SoMePostCredit
{
    /// <summary>The label line, e.g. <c>"ELDK27 Organizers:"</c>.</summary>
    public static string Label(string? editionCode) =>
        $"{(editionCode ?? string.Empty).Trim()} Organizers:";

    /// <summary>
    /// The trailing credit block appended to every post at COMPOSE/PUBLISH time — never stored.
    /// Empty when there is no credit to add, so a post is never given a dangling label.
    /// </summary>
    public static string Suffix(string? editionCode, string? organizerCredits)
    {
        var credits = (organizerCredits ?? string.Empty).Trim();
        if (credits.Length == 0) return string.Empty;
        return "\n\n" + Label(editionCode) + "\n" + credits;
    }

    /// <summary>
    /// Body + credit. The one place a post becomes its published text.
    /// </summary>
    public static string Compose(string? body, string? editionCode, string? organizerCredits)
    {
        var b = (body ?? string.Empty).TrimEnd();
        var suffix = Suffix(editionCode, organizerCredits);
        return suffix.Length == 0 ? b : b + suffix;
    }

    /// <summary>
    /// Removes a trailing credit block from stored text, so the stored value is BODY ONLY.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Used by the §861.4 migration and by the editor when loading a legacy row.</b>
    ///
    /// <para>⚠️ <b>It strips by LABEL, not by exact whole-suffix match, and that is deliberate.</b>
    /// The migration must survive rows whose credit was edited by hand: post <c>495</c>'s stored
    /// text was hand-edited around the credit, so an exact comparison against the CURRENT composed
    /// suffix would refuse to strip it and would leave the footer frozen forever — the very state
    /// this is removing. Anchoring on the label line finds the block wherever it ends up.</para>
    ///
    /// <para>🔴 <b>It only ever strips a TRAILING block.</b> If the label appears mid-post it is left
    /// alone — that is his prose, not the frame. Returns <c>false</c> when nothing was stripped, so
    /// the caller can REPORT rather than assume (§854: exclude AND report).</para>
    /// </remarks>
    public static bool TryStrip(string? text, string? editionCode, out string body)
    {
        body = (text ?? string.Empty).TrimEnd();
        if (body.Length == 0) return false;

        var label = Label(editionCode);
        var idx = body.LastIndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        // Only a TRAILING block: everything after the label must be the credit line(s) — one
        // paragraph, no blank line. A blank line after it means more post follows, so leave it.
        var tail = body[(idx + label.Length)..];
        if (tail.Contains("\n\n", StringComparison.Ordinal)) return false;

        body = body[..idx].TrimEnd();
        return true;
    }
}
