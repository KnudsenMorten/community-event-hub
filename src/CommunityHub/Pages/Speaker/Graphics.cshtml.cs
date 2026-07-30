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
    // §326p (operator 2026-07-25): the §324c "Post on my LinkedIn profile" self-post
    // path is REMOVED — it never got past LinkedIn's redirect-URI gate live, and the
    // share-intent + clipboard flow is the one that works. The company-page publish
    // (§52/§324 "Publish to LinkedIn page") is untouched.
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

    /// <summary>
    /// §437: LinkedIn's composer with NOTHING prefilled — deliberately no <c>url=</c> and no
    /// <c>text=</c>. A share link that carries a URL is unfurled into a small preview CARD,
    /// which is exactly the "url-based image" the operator rejected; an empty composer has
    /// nothing to unfurl, so the PNG the speaker attaches is the post's only media and
    /// renders full-width. Public + const so the Help-Promote test can pin it.
    /// </summary>
    public const string EmptyComposerUrl = "https://www.linkedin.com/feed/?shareActive=true";

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

    /// <summary>
    /// §172: a Help-Promote graphic card. Beyond the download, a session/master-class (or
    /// track) card carries its OWN LinkedIn + X share DRAFTS, tailored to THAT session and
    /// linking to the public session page (whose OpenGraph card pulls the graphic into the
    /// post). The two share drafts are null for a plain speaker-headshot card (no session to
    /// point a share at — download-only).
    /// </summary>
    public sealed record GraphicCard(
        int Id,
        string Kind,
        string? Title,
        string? DownloadUrl,
        bool HasStoredFile,
        SocialShareDraft? LinkedInShare = null,
        SocialShareDraft? XShare = null);

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

    // §326p: OnPostSelfPostAsync + the LinkedIn OAuth callback were removed with the
    // self-post feature (see the class-level note above).

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

        // §172: build PER-CARD share drafts whose link is the PUBLIC page that carries the
        // matching OpenGraph image — absolute URL from the current request (the repo's
        // {scheme}://{host} convention).
        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        var cards = new List<GraphicCard>();
        foreach (var g in visible)
        {
            string kind;
            string? title;
            SocialShareDraft? liShare = null, xShare = null;

            switch (g.Type)
            {
                case GraphicAssetType.Session:
                    // The graphic for one of the speaker's sessions/master-classes.
                    title = g.SessionId is not null && sessionInfo.TryGetValue(g.SessionId.Value, out var s)
                        ? s.Title : "Session";
                    kind = "Session graphic";
                    // §172: per-session share — text tailored to THIS session, linking to the public
                    // session page (/Sessions/{id}) whose og:image is this very graphic.
                    if (g.SessionId is not null)
                    {
                        var sessionUrl = $"{baseUrl}/Sessions/{g.SessionId.Value}";
                        // §196: per-session promote text carries the /Sessions/{id} URL as its ONLY
                        // link, so LinkedIn/X cards the session page and shows ITS og:image graphic.
                        liShare = _graphics.BuildSessionPromoteDraft(
                            EventDisplayName, EventDates, title ?? "Session", sessionUrl, SocialNetwork.LinkedIn, TicketUrl);
                        xShare = _graphics.BuildSessionPromoteDraft(
                            EventDisplayName, EventDates, title ?? "Session", sessionUrl, SocialNetwork.X, TicketUrl);
                    }
                    break;

                case GraphicAssetType.Track:
                    // §158: the per-track promo graphic, labelled with the track name so the speaker
                    // can tell it apart from their session graphic.
                    var track = g.SessionId is not null && sessionInfo.TryGetValue(g.SessionId.Value, out var ts)
                        && !string.IsNullOrWhiteSpace(ts.Track) ? ts.Track! : "Track";
                    title = track;
                    kind = "Track graphic";
                    // §172: a track graphic isn't one session, so its share links to the public sessions
                    // list filtered to that track (the cleaner option vs. download-only — still a real,
                    // shareable destination).
                    if (!string.IsNullOrWhiteSpace(track) && track != "Track")
                    {
                        var trackUrl = $"{baseUrl}/Sessions?FilterTrack={Uri.EscapeDataString(track)}";
                        liShare = _graphics.BuildTrackPromoteDraft(
                            EventDisplayName, EventDates, TicketUrl, track, trackUrl, SocialNetwork.LinkedIn);
                        xShare = _graphics.BuildTrackPromoteDraft(
                            EventDisplayName, EventDates, TicketUrl, track, trackUrl, SocialNetwork.X);
                    }
                    break;

                default:
                    // The speaker headshot graphic — not tied to a session, so download-only (§172
                    // removed the single generic top-of-page share; there is no per-session URL to
                    // anchor a headshot share to).
                    kind = "Speaker graphic";
                    title = null;
                    break;
            }

            // §160: the PNG is served through the hub proxy (/speaker-graphic/{id}) — NEVER the raw
            // SharePoint URL (the speaker has no SharePoint permission). HasStoredFile keys off the
            // stored Graph item id, which is what the proxy streams.
            cards.Add(new GraphicCard(
                g.Id, kind, title,
                DownloadUrl: $"/speaker-graphic/{g.Id}",
                HasStoredFile: !string.IsNullOrEmpty(g.StorageItemId),
                LinkedInShare: liShare,
                XShare: xShare));
        }
        Cards = cards;

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
