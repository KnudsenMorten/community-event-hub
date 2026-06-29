using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Speaker;

/// <summary>
/// The speaker's SoMe-graphics page (REQUIREMENTS §18 step 5 + §52). Shows the speaker
/// their PRE-STAGED, RELEASED graphics (their headshot graphic + per-session
/// graphics) — and ONLY released ones (the organizer review gate). For each the
/// speaker can DOWNLOAD the PNG or open a LinkedIn/X share DRAFT in their own
/// context. Also the "I'm speaking at ELDK27" button that builds a LinkedIn DRAFT.
///
/// §52 adds a "Publish to LinkedIn" action: the speaker pushes the announcement
/// (their released graphic + a generated post text) to the EVENT'S LinkedIn company
/// page through the existing gated SoMe queue/publisher path
/// (<see cref="SpeakerLinkedInPublishService"/>). It is offered only when the
/// linkedin-queue feature + the SoMe company page are configured; and it posts
/// nothing live until the LinkedIn OAuth app/credentials are wired — until then the
/// announcement is queued. The DRAFT buttons remain (never an auto-post on the
/// speaker's own profile).
/// </summary>
[Authorize]
public class GraphicsModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly GraphicsService _graphics;
    private readonly SpeakerLinkedInPublishService _linkedInPublish;

    public GraphicsModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        GraphicsService graphics,
        SpeakerLinkedInPublishService linkedInPublish)
    {
        _db = db;
        _participant = participant;
        _graphics = graphics;
        _linkedInPublish = linkedInPublish;
    }

    public static readonly ParticipantRole[] EligibleRoles =
        { ParticipantRole.Speaker };

    public bool AccessDenied { get; private set; }
    public ParticipantRole Role { get; private set; }
    public bool CanPost { get; private set; }

    /// <summary>
    /// Whether the "Publish to LinkedIn" action is offered (the linkedin-queue
    /// feature is on AND the event's LinkedIn company page is configured). The
    /// action may still QUEUE rather than post-live when credentials aren't wired —
    /// that is reported back after the click, not hidden here.
    /// </summary>
    public bool CanPublishToLinkedIn { get; private set; }

    /// <summary>Status line shown after a publish attempt.</summary>
    public string? PublishMessage { get; private set; }
    public bool PublishIsError { get; private set; }
    public bool PublishQueuedNotLive { get; private set; }

    public string EventDisplayName { get; private set; } = "ELDK27";
    public string EventDates { get; private set; } = string.Empty;
    public string TicketUrl { get; private set; } = "eldk27.expertslive.dk";

    public IReadOnlyList<GraphicCard> Cards { get; private set; } = Array.Empty<GraphicCard>();

    /// <summary>The "I'm speaking at ELDK27" announcement draft (LinkedIn).</summary>
    public SocialShareDraft? AnnouncementDraft { get; private set; }

    /// <summary>The same announcement as an X (Twitter) draft (§160 — share buttons live under the post text).</summary>
    public SocialShareDraft? AnnouncementDraftX { get; private set; }

    public sealed record GraphicCard(
        int Id,
        string Kind,
        string? Title,
        string? DownloadUrl,
        bool HasStoredFile);

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => LoadAsync(ct);

    /// <summary>
    /// §52: publish (or queue) the announcement for one of the speaker's OWN released
    /// graphics to the event's LinkedIn page, via the gated SoMe queue/publisher path.
    /// </summary>
    public async Task<IActionResult> OnPostPublishLinkedInAsync(int graphicAssetId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        Role = me.Role;
        if (!EligibleRoles.Contains(me.Role)) { AccessDenied = true; return Page(); }

        var result = await _linkedInPublish.PublishAsync(
            me.EventId, me.ParticipantId, graphicAssetId, me.FullName, me.Email, ct);

        PublishMessage = result.Message;
        PublishIsError = result.Outcome is SpeakerLinkedInPublishOutcome.Failed
            or SpeakerLinkedInPublishOutcome.GraphicNotAvailable;
        PublishQueuedNotLive =
            result.Outcome == SpeakerLinkedInPublishOutcome.QueuedAwaitingCredentials;

        return await LoadAsync(ct);
    }

    private async Task<IActionResult> LoadAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        Role = me.Role;
        if (!EligibleRoles.Contains(me.Role)) { AccessDenied = true; return Page(); }

        CanPost = _graphics.CanPostToSocial;
        CanPublishToLinkedIn = await _linkedInPublish.IsOfferedAsync(me.EventId, ct);
        await LoadEventFactsAsync(me.EventId, ct);

        var visible = await _graphics.GetSpeakerVisibleAsync(me.EventId, me.ParticipantId, ct);

        // Resolve session titles (per-session cards) + track names (§158 per-track cards). A Track
        // graphic carries a REPRESENTATIVE SessionId, so its track name is read from that session.
        var sessionIds = visible.Where(g => g.SessionId is not null).Select(g => g.SessionId!.Value).ToList();
        var sessionInfo = await _db.Sessions
            .Where(s => sessionIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => new { s.Title, s.Track }, ct);

        var cards = new List<GraphicCard>();
        foreach (var g in visible)
        {
            var (kind, title) = g.Type switch
            {
                GraphicAssetType.Session =>
                    ("Session graphic",
                     g.SessionId is not null && sessionInfo.TryGetValue(g.SessionId.Value, out var s) ? s.Title : "Session"),
                // §158: the per-track promo graphic, labelled with the track name so the speaker
                // can tell it apart from their session graphic.
                GraphicAssetType.Track =>
                    ("Track graphic",
                     g.SessionId is not null && sessionInfo.TryGetValue(g.SessionId.Value, out var ts)
                        && !string.IsNullOrWhiteSpace(ts.Track) ? ts.Track : "Track"),
                _ => ("Speaker graphic", (string?)null),
            };

            // §160: the PNG is served through the hub proxy (/speaker-graphic/{id}) — NEVER the raw
            // SharePoint URL (the speaker has no SharePoint permission). HasStoredFile keys off the
            // stored Graph item id, which is what the proxy streams.
            cards.Add(new GraphicCard(
                g.Id, kind, title,
                DownloadUrl: $"/speaker-graphic/{g.Id}",
                HasStoredFile: !string.IsNullOrEmpty(g.StorageItemId)));
        }
        Cards = cards;

        // The announcement (the speaker's "Your post") share drafts — LinkedIn + X — shown UNDER the
        // post text. No graphicUrl is attached (share-intent can't carry an arbitrary image; the
        // speaker downloads the PNG and attaches it themselves).
        AnnouncementDraft = _graphics.BuildSpeakingAnnouncementDraft(
            EventDisplayName, EventDates, TicketUrl, me.FullName, sessionTitle: null,
            graphicUrl: null, SocialNetwork.LinkedIn);
        AnnouncementDraftX = _graphics.BuildSpeakingAnnouncementDraft(
            EventDisplayName, EventDates, TicketUrl, me.FullName, sessionTitle: null,
            graphicUrl: null, SocialNetwork.X);

        return Page();
    }

    private async Task LoadEventFactsAsync(int eventId, CancellationToken ct)
    {
        var evt = await _db.Events.FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (evt is not null)
        {
            EventDisplayName = string.IsNullOrWhiteSpace(evt.DisplayName) ? "ELDK27" : evt.DisplayName;
            EventDates = $"{evt.StartDate:d MMM} – {evt.EndDate:d MMM yyyy}";
        }
    }
}
