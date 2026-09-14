namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1159 — pick the Zoho record that belongs to a company, when the only thing Zoho gives us to
/// match on is the company NAME.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Why this exists.</b> Operator 2026-08-31 collapsed four separate webshop companies
/// (the same group's Danish, Norwegian, Swedish and Finnish entities) to ONE public name, which is
/// right for the sponsor wall. Every place that linked a CEH company to its Zoho record by name then
/// had four companies matching the same record, and no way to tell them apart.</para>
///
/// <para>🔑 <b>The id is the identity; the name is only a hint.</b> CEH already stores each
/// company's Zoho ids (<see cref="SponsorInfo.ZohoSponsorId"/>,
/// <c>ZohoSponsorLinksJson</c>, <c>ZohoExhibitorId</c>) keyed by company id — that is the authority.
/// Name matching is a BOOTSTRAP for a company that has no id yet, and nothing more.</para>
///
/// <para>🔒 <b>It refuses rather than guesses.</b> A record already claimed by a different company is
/// removed from the candidates, and if more than one candidate still matches the name the answer is
/// "cannot tell" — not "take the first". Taking the first is what silently points two companies at
/// one Zoho record: one company's website and description then overwrite the other's on every
/// reconcile, and the loser looks like a sync that keeps reverting.</para>
/// </remarks>
public static class ZohoRecordMatch
{
    /// <param name="Id">The single unambiguous match, or null.</param>
    /// <param name="RefusalReason">
    /// Set only when candidates existed but could not be told apart — so the caller can REPORT the
    /// ambiguity instead of creating a duplicate record. Null when there was simply no match.
    /// </param>
    public sealed record Outcome(string? Id, string? RefusalReason);

    private static readonly Outcome NoMatch = new(null, null);

    /// <summary>
    /// The one record named <paramref name="wantedName"/> that no other company has claimed.
    /// </summary>
    /// <param name="candidates">Every record in Zoho, as (id, company name).</param>
    /// <param name="wantedName">The company name to match.</param>
    /// <param name="claimedByOthers">
    /// Zoho ids already recorded against a DIFFERENT CEH company. ⚠️ Must include ids assigned
    /// earlier in the same run, or the first two companies sharing a name still collide.
    /// </param>
    public static Outcome ByName(
        IEnumerable<(string Id, string Name)> candidates,
        string? wantedName,
        IReadOnlySet<string> claimedByOthers)
    {
        if (string.IsNullOrWhiteSpace(wantedName)) return NoMatch;

        var named = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Id) && NameEq(c.Name, wantedName))
            .ToList();
        if (named.Count == 0) return NoMatch;

        var free = named.Where(c => !claimedByOthers.Contains(c.Id)).ToList();

        // Every candidate is already someone else's. That is not an error and not a match: this
        // company simply has no record yet, and the caller should create one.
        if (free.Count == 0) return NoMatch;

        if (free.Count == 1) return new Outcome(free[0].Id, null);

        return new Outcome(
            null,
            $"{free.Count} Zoho records are named '{wantedName.Trim()}' and none is linked to a "
            + "company yet — cannot tell which belongs here, so nothing was linked or created. "
            + "Link the right one by hand in Backstage, or give the companies distinct public names.");
    }

    private static bool NameEq(string? a, string? b) =>
        string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
}
