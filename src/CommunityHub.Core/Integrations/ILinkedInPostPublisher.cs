namespace CommunityHub.Core.Integrations;

/// <summary>
/// One post to publish to a LinkedIn company page, flattened for the
/// <see cref="ILinkedInPostPublisher"/> seam (REQUIREMENTS §19).
/// </summary>
/// <param name="OrganizationUrnOrId">
/// The LinkedIn company page / organization the post targets (the organization
/// URN or id resolved from the operator-config company-page URL/id). The live
/// publisher posts to <c>urn:li:organization:{id}</c> via the Posts/UGC API with
/// the <c>w_organization_social</c> scope.
/// </param>
/// <param name="Text">The post body that actually publishes (override ?? auto).</param>
/// <param name="ImageRef">Optional branding image reference (URL / SharePoint path); null = text-only.</param>
/// <param name="Tags">The compliance-aware tag set (member/org handles or URNs).</param>
public sealed record LinkedInPost(
    string OrganizationUrnOrId,
    string Text,
    string? ImageRef,
    IReadOnlyList<string> Tags);

/// <summary>The outcome of one publish attempt.</summary>
/// <param name="Published">True only when the post was actually posted to LinkedIn.</param>
/// <param name="ExternalPostId">The LinkedIn share/post id on success (the sent-marker); null otherwise.</param>
/// <param name="Message">Human-readable status / reason (e.g. "not configured", or the error).</param>
public sealed record LinkedInPublishResult(
    bool Published,
    string? ExternalPostId,
    string Message);

/// <summary>
/// The gated seam for posting to a LinkedIn <b>company page</b> (REQUIREMENTS §19).
/// NOTHING posts until this is wired AND enabled: the default
/// <see cref="NullLinkedInPostPublisher"/> is a no-op (<see cref="CanPublish"/> =
/// false) that performs NO LinkedIn call. This mirrors the established repo
/// gated-seam pattern (cf. <see cref="IMasterClassBookingFetcher"/> /
/// <see cref="Graphics.ISharePointFileStore"/>).
///
/// <b>Distinct from <see cref="Graphics.ISocialShareGateway"/>:</b> that seam
/// builds per-user DRAFT share-intent links a speaker finalizes himself; THIS
/// seam is the organizer-curated SCHEDULED QUEUE that publishes to the event's
/// own company page on a timer.
///
/// HONEST STATUS (🟡 live wiring pending): the LinkedIn company-page URL /
/// organization id is operator config (NOT a secret) and the LinkedIn OAuth
/// access token IS a secret (read from Key Vault by the live publisher, secret
/// name only in committed files — never the value). Until both are wired the
/// default registration is the Null publisher (<see cref="CanPublish"/> = false)
/// — the dispatcher reports "not configured" rather than faking a post. A live
/// publisher (LinkedIn Posts/UGC API, <c>w_organization_social</c>) returns a
/// real post id; no caller changes.
/// </summary>
public interface ILinkedInPostPublisher
{
    /// <summary>
    /// Whether this implementation can actually post to LinkedIn. False for the
    /// null default (no wired page/token) — the dispatcher then records "not
    /// configured" and leaves the post Queued rather than faking a publish.
    /// </summary>
    bool CanPublish { get; }

    /// <summary>
    /// Publish one post to the LinkedIn company page. Only called when
    /// <see cref="CanPublish"/> is true (and SoMe posting is enabled with a
    /// configured page).
    /// </summary>
    Task<LinkedInPublishResult> PublishAsync(LinkedInPost post, CancellationToken ct = default);
}

/// <summary>
/// Default no-op publisher: there is no wired LinkedIn company page / OAuth token
/// for the SoMe queue (the page id is operator config; the token is a Key Vault
/// secret — neither is in this repo), so this cannot publish and performs no
/// call. The queue, schedule, gates and notifications are all real and tested
/// offline; only the actual LinkedIn POST is inert (🟡 pending) — and nothing,
/// not even a post id, is ever faked.
/// </summary>
public sealed class NullLinkedInPostPublisher : ILinkedInPostPublisher
{
    public bool CanPublish => false;

    public Task<LinkedInPublishResult> PublishAsync(
        LinkedInPost post, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            "No wired LinkedIn company page / OAuth token for the SoMe queue "
            + "(company-page id is operator config; the access token is a Key Vault "
            + "secret — neither is in this repo). Do not call PublishAsync when "
            + "CanPublish is false.");
}
