using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// A publishable BRANDING graphic exposed for a downstream consumer (REQUIREMENTS
/// §18). This is the SoMe-consumable shape: a stable, read-only snapshot of "the
/// branding graphic for speaker X / sponsor Y / session Z" plus a ready
/// LinkedIn/X draft text, so the social-media scheduling queue (§19) — or any other
/// consumer — can attach the right image + prefilled words to a post WITHOUT
/// reaching into the graphics tables or the gates itself.
///
/// <para><b>Direction of the dependency.</b> Graphics exposes this; SoMe (or
/// whoever) consumes it. Graphics never references a SoMe type, so the two slices
/// stay decoupled and can be built independently.</para>
///
/// <para><b>The gate is already applied.</b> A consumer only ever sees what is
/// safe to publish — a speaker / session graphic is exposed ONLY once an organizer
/// has RELEASED it; a sponsor graphic is always available (it is internal-only and
/// generated for the organizers' own posts). The provider never returns a graphic
/// that is still behind the review gate.</para>
/// </summary>
/// <param name="StableKey">
/// The deterministic, edition-unique key of the underlying <see cref="GraphicAsset"/>
/// (e.g. <c>speaker:42</c>). Identifies the graphic stably across organizer overrules.
/// </param>
/// <param name="Type">What the graphic depicts (Speaker / Sponsor / Session).</param>
/// <param name="ImageRef">
/// The reference a consumer attaches as the post's branding image — the live
/// SharePoint URL when a store is wired, else the stable relative path. This is
/// exactly the shape a <c>SoMePost.ImageRef</c> expects; never null/blank when a
/// graphic exists.
/// </param>
/// <param name="FileName">The stable file name of the graphic (e.g. <c>speaker-42.png</c>).</param>
/// <param name="DraftText">
/// A ready, prefilled post body for the graphic — the same draft text a speaker
/// would self-share, suitable as a <c>SoMePost.AutoText</c> seed. Carries the event
/// name, the ticket URL and (for speaker/session) the session info.
/// </param>
public sealed record BrandingGraphicRef(
    string StableKey,
    GraphicAssetType Type,
    string ImageRef,
    string FileName,
    string DraftText);

/// <summary>
/// Read-only contract that EXPOSES publishable branding graphics for a consumer
/// (REQUIREMENTS §18). The social-media scheduling queue (§19) is the intended
/// consumer, but this slice does NOT wire SoMe — it only provides the contract +
/// the builder; SoMe (or any consumer) calls it.
///
/// <para>Every method is read-only and applies the release/visibility gate before
/// returning anything: a speaker / session graphic is returned ONLY when an
/// organizer has released it; a sponsor graphic is returned because it is
/// internal-only by design. A graphic still behind the gate yields <c>null</c>.</para>
/// </summary>
public interface IBrandingGraphicsProvider
{
    /// <summary>
    /// The released speaker graphic for a speaker, as a consumable branding ref, or
    /// <c>null</c> when there is none / it is not yet released.
    /// </summary>
    Task<BrandingGraphicRef?> GetSpeakerGraphicAsync(
        int eventId, int participantId, CancellationToken ct = default);

    /// <summary>
    /// The released per-session graphic for one speaker on a session, or <c>null</c>
    /// when there is none / it is not yet released.
    /// </summary>
    Task<BrandingGraphicRef?> GetSessionGraphicAsync(
        int eventId, int sessionId, int participantId, CancellationToken ct = default);

    /// <summary>
    /// The sponsor graphic for a company (internal-only — always returned when it
    /// exists; sponsor graphics are not behind a speaker release gate), or
    /// <c>null</c> when there is none.
    /// </summary>
    Task<BrandingGraphicRef?> GetSponsorGraphicAsync(
        int eventId, string sponsorCompanyId, CancellationToken ct = default);
}
