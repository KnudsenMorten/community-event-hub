namespace CommunityHub.Core.Integrations;

/// <summary>
/// Builds the compliance-aware tag set for a queued LinkedIn company-page post
/// (REQUIREMENTS §19). Pure / static so the rules are unit-testable with no DB.
///
/// <b>The compliance rules (see DESIGN §6 "SoMe queue — tagging limits"):</b>
/// <list type="bullet">
///   <item><b>Sponsor posts</b> tag the <b>signer</b> + the <b>event
///   coordinator</b> + the <b>sponsor company</b> (company name resolved through
///   the shared <see cref="SponsorCompanyName"/> public→legal→billing chain).</item>
///   <item><b>Speaker posts</b> tag <b>organizers only</b>. The LinkedIn API
///   CANNOT tag an external speaker (they are not members of the posting
///   organization and the company-page Posts API has no person-mention for
///   non-connections), so the queue never attempts it — instead the T-5-minute
///   pre-alert emails an organizer to insert the speaker's real handle manually.</item>
///   <item><b>Ad-hoc posts</b> carry whatever tags the organizer typed (no
///   automatic tags), so this builder leaves them to the caller.</item>
/// </list>
///
/// Tags are returned as a clean, de-duplicated list of handles/URNs/names; the
/// caller stores them newline-separated on <c>SoMePost.Tags</c>.
/// </summary>
public static class SoMeTagBuilder
{
    /// <summary>
    /// Build the tag set for a <b>sponsor</b> post: signer + event coordinator +
    /// the resolved sponsor company name. Blank entries are dropped; the result
    /// is de-duplicated (case-insensitive).
    /// </summary>
    /// <param name="signerHandle">The sponsor company's signer (Company Manager role 1) handle/name.</param>
    /// <param name="eventCoordinatorHandle">The event coordinator (role 2) handle/name.</param>
    /// <param name="companyPublicName">Company Manager public name (preferred).</param>
    /// <param name="companyLegalName">Company Manager legal name (fallback).</param>
    /// <param name="companyBillingName">Webshop billing company (fallback).</param>
    /// <param name="companyId">The company id for the final "Company {id}" fallback.</param>
    public static IReadOnlyList<string> ForSponsor(
        string? signerHandle,
        string? eventCoordinatorHandle,
        string? companyPublicName,
        string? companyLegalName,
        string? companyBillingName,
        string companyId)
    {
        var companyName = SponsorCompanyName.Resolve(
            companyPublicName, companyLegalName, companyBillingName, companyId);

        return Clean(new[] { signerHandle, eventCoordinatorHandle, companyName });
    }

    /// <summary>
    /// Build the tag set for a <b>speaker</b> post: ORGANIZERS ONLY. The speaker
    /// is deliberately NOT tagged (the API can't tag external people) — the
    /// T-5-minute pre-alert handles that manually. Blank entries dropped,
    /// de-duplicated.
    /// </summary>
    /// <param name="organizerHandles">The organizer handles/names to tag.</param>
    public static IReadOnlyList<string> ForSpeaker(IEnumerable<string?> organizerHandles) =>
        Clean(organizerHandles);

    private static IReadOnlyList<string> Clean(IEnumerable<string?> values) =>
        values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
