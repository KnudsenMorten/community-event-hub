using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// Default <see cref="IBrandingGraphicsProvider"/> — exposes publishable branding
/// graphics (REQUIREMENTS §18) to a downstream consumer (the §19 social-media
/// queue, or any other), applying the release/visibility gate so a consumer only
/// ever sees what is safe to publish.
///
/// <para>Read-only: it queries the <see cref="GraphicAsset"/> rows, resolves the
/// speaker/session display strings, and builds the draft text through the existing
/// <see cref="GraphicsService"/> share-draft builders — so the words a consumer
/// gets match exactly the draft a speaker would self-share. It NEVER stores,
/// generates or posts, and never references a SoMe type (Graphics stays decoupled
/// from its consumers).</para>
///
/// <para><b>Event context.</b> The provider does not invent ticket URLs / dates —
/// the caller passes a <see cref="BrandingEventContext"/> (the same values used
/// across the share/SoMe surfaces); the provider supplies the speaker/session
/// names it resolves from the data.</para>
/// </summary>
public sealed class BrandingGraphicsProvider : IBrandingGraphicsProvider
{
    private readonly CommunityHubDbContext _db;
    private readonly GraphicsService _graphics;

    public BrandingGraphicsProvider(CommunityHubDbContext db, GraphicsService graphics)
    {
        _db = db;
        _graphics = graphics;
    }

    /// <inheritdoc />
    public async Task<BrandingGraphicRef?> GetSpeakerGraphicAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var asset = await ReleasedByKeyAsync(
            eventId, GraphicStableKey.ForSpeaker(participantId), GraphicAssetType.Speaker, ct);
        if (asset is null) return null;

        var speakerName = await SpeakerNameAsync(eventId, participantId, ct);
        var draft = _graphics.BuildSpeakingAnnouncementDraft(
            Context.EventDisplayName, Context.EventDates, Context.TicketUrl,
            speakerName, sessionTitle: null, graphicUrl: ImageRefOf(asset));

        return ToRef(asset, draft.Text);
    }

    /// <inheritdoc />
    public async Task<BrandingGraphicRef?> GetSessionGraphicAsync(
        int eventId, int sessionId, int participantId, CancellationToken ct = default)
    {
        var asset = await ReleasedByKeyAsync(
            eventId, GraphicStableKey.ForSession(sessionId, participantId), GraphicAssetType.Session, ct);
        if (asset is null) return null;

        var sessionTitle = await SessionTitleAsync(eventId, sessionId, ct);
        var draft = _graphics.BuildSessionShareDraft(
            Context.EventDisplayName, Context.TicketUrl, sessionTitle, graphicUrl: ImageRefOf(asset));

        return ToRef(asset, draft.Text);
    }

    /// <inheritdoc />
    public async Task<BrandingGraphicRef?> GetSponsorGraphicAsync(
        int eventId, string sponsorCompanyId, CancellationToken ct = default)
    {
        // Sponsor graphics are INTERNAL-ONLY (no speaker release gate) — return when
        // it exists regardless of Status.
        var asset = await _db.GraphicAssets
            .Where(g => g.EventId == eventId
                        && g.Type == GraphicAssetType.Sponsor
                        && g.SponsorCompanyId == sponsorCompanyId)
            .OrderByDescending(g => g.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        if (asset is null) return null;

        var draft = _graphics.BuildSessionShareDraft(
            Context.EventDisplayName, Context.TicketUrl,
            sessionTitle: $"our sponsor {sponsorCompanyId}", graphicUrl: ImageRefOf(asset));

        return ToRef(asset, draft.Text);
    }

    /// <summary>
    /// The event context (name / dates / ticket URL) the draft text is built from.
    /// Settable so a caller can supply the live edition values; defaults to the
    /// shared event identity used across the share/SoMe surfaces.
    /// </summary>
    public BrandingEventContext Context { get; set; } = BrandingEventContext.Default;

    // ----- internals -------------------------------------------------------

    private Task<GraphicAsset?> ReleasedByKeyAsync(
        int eventId, string key, GraphicAssetType type, CancellationToken ct) =>
        _db.GraphicAssets.FirstOrDefaultAsync(
            g => g.EventId == eventId
                 && g.StableKey == key
                 && g.Type == type
                 && g.Status == GraphicAssetStatus.Released, // THE GATE
            ct);

    private async Task<string> SpeakerNameAsync(int eventId, int participantId, CancellationToken ct)
    {
        var name = await _db.Participants
            .Where(p => p.EventId == eventId && p.Id == participantId)
            .Select(p => p.FullName)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(name) ? "our speaker" : name!.Trim();
    }

    private async Task<string> SessionTitleAsync(int eventId, int sessionId, CancellationToken ct)
    {
        var title = await _db.Sessions
            .Where(s => s.EventId == eventId && s.Id == sessionId)
            .Select(s => s.Title)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(title) ? "our session" : title!.Trim();
    }

    /// <summary>The branding image ref: the live URL when stored, else the stable path.</summary>
    private static string ImageRefOf(GraphicAsset asset) =>
        !string.IsNullOrWhiteSpace(asset.SharePointUrl) ? asset.SharePointUrl!
        : !string.IsNullOrWhiteSpace(asset.SharePointPath) ? asset.SharePointPath!
        : (asset.FileName ?? GraphicStableKey.FileName(asset.StableKey));

    private static BrandingGraphicRef ToRef(GraphicAsset asset, string draftText) =>
        new(asset.StableKey,
            asset.Type,
            ImageRefOf(asset),
            asset.FileName ?? GraphicStableKey.FileName(asset.StableKey),
            draftText);
}

/// <summary>
/// The event identity a branding draft text is built from (REQUIREMENTS §18) — the
/// edition display name, the human date range and the public ticket URL. Held as a
/// small value so the <see cref="BrandingGraphicsProvider"/> never hard-codes them
/// itself; a caller may override <see cref="BrandingGraphicsProvider.Context"/> with
/// the live edition values.
/// </summary>
/// <param name="EventDisplayName">The edition display name (e.g. "Experts Live Denmark 2027").</param>
/// <param name="EventDates">The human-readable date range (e.g. "4-5 Feb 2027").</param>
/// <param name="TicketUrl">The public ticket URL.</param>
public sealed record BrandingEventContext(
    string EventDisplayName,
    string EventDates,
    string TicketUrl)
{
    /// <summary>
    /// The default context — the shared event identity used across the share/SoMe
    /// surfaces. The ticket URL is the public, non-secret event address.
    /// </summary>
    public static BrandingEventContext Default { get; } =
        new("ELDK27", "4-5 Feb 2027", "eldk27.expertslive.dk");
}
