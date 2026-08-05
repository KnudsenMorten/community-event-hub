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
    /// <remarks>
    /// §767 phase 2 — the session graphic is now ONE file for the whole session
    /// (<c>session:{id}</c>, no speaker in the key), so that is asked for first. The per-speaker
    /// key is still tried as a fallback: it is what the SharePoint PULL writes for artwork the
    /// operator uploaded himself, and dropping it would make his own files invisible here.
    /// </remarks>
    public async Task<BrandingGraphicRef?> GetSessionGraphicAsync(
        int eventId, int sessionId, int participantId, CancellationToken ct = default)
    {
        var asset =
            await ReleasedByKeyAsync(
                eventId, GraphicStableKey.ForSessionGraphic(sessionId), GraphicAssetType.Session, ct)
            ?? await ReleasedByKeyAsync(
                eventId, GraphicStableKey.ForSession(sessionId, participantId),
                GraphicAssetType.Session, ct);
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
/// <param name="EventDates">The human-readable date range (e.g. "9-10 Feb 2027").</param>
/// <param name="TicketUrl">The public ticket URL.</param>
/// <param name="EventLocation">
/// Where it happens (e.g. "Copenhagen, Denmark") — the second half of the §767 bottom strip,
/// which the operator settled as <c>9-10 FEB 2027 · COPENHAGEN, DENMARK</c> and nothing else.
/// </param>
public sealed record BrandingEventContext(
    string EventDisplayName,
    string EventDates,
    string TicketUrl,
    string EventLocation = "Copenhagen, Denmark")
{
    /// <summary>
    /// The default context — the shared event identity used across the share/SoMe
    /// surfaces. The ticket URL is the public, non-secret event address.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>The date range MUST match <c>dates.day1</c>/<c>dates.day2</c> in the edition config.</b>
    /// It read <i>"4-5 Feb 2027"</i> until 2026-08-01, when the operator caught it on a rendered
    /// graphic. The edition is <b>9-10 Feb 2027</b> (config: day1 2027-02-09, day2 2027-02-10).
    /// This is not decorative text — it goes out in the SoMe share drafts, so a wrong value here
    /// invites people to the wrong days.
    /// ⚠️ It is STILL A LITERAL, which is exactly how it went stale; §767 carries the follow-up to
    /// read it from the same config every other surface uses.
    /// </remarks>
    public static BrandingEventContext Default { get; } =
        new("ELDK27", "9-10 Feb 2027", "eldk27.expertslive.dk");
}
