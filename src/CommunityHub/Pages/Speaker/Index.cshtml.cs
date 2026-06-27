using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Participants;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Speaker;

/// <summary>
/// The Speaker Hub -- a single, self-service milestone tracker for the
/// signed-in speaker / master-class speaker. It consolidates the speaker's
/// deadline milestones (one card each, with a live countdown and a
/// mark-done / reopen action) and the speaker-form completeness check into a
/// cohesive journey view, so a speaker sees exactly where they are in the
/// path from "accepted" to "on-stage" without hunting through the generic
/// task list. Mobile-first (works at ~360px).
///
/// Only Speakers reach the content; any other role gets a
/// friendly "not a speaker" message instead of a 403 so the nav stays simple.
/// </summary>
[Authorize]
public class IndexModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SpeakerDeadlineSeeder _speakerDeadlines;
    private readonly FormTaskReconciler _formTaskReconciler;
    private readonly MasterClassLogisticsService _logistics;
    private readonly SpeakerSessionsService _sessions;
    private readonly PublicSessionsService _publicSessions;
    private readonly SessionEvalsQrService _qr;
    private readonly ZohoOptions _zohoOptions;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SpeakerDeadlineSeeder speakerDeadlines,
        FormTaskReconciler formTaskReconciler,
        MasterClassLogisticsService logistics,
        SpeakerSessionsService sessions,
        PublicSessionsService publicSessions,
        SessionEvalsQrService qr,
        ZohoOptions zohoOptions,
        ILogger<IndexModel> logger)
    {
        _db = db;
        _participant = participant;
        _speakerDeadlines = speakerDeadlines;
        _formTaskReconciler = formTaskReconciler;
        _logistics = logistics;
        _sessions = sessions;
        _publicSessions = publicSessions;
        _qr = qr;
        _zohoOptions = zohoOptions;
        _logger = logger;
    }

    /// <summary>
    /// The PUBLIC Zoho Backstage session page URL for a session (§52), or null when the
    /// session has no Backstage agenda id — the caller then keeps the internal link. Built
    /// as <c>{BackstagePublicBaseUrl}#/sessions/{id}</c> from per-edition config; never
    /// fabricated for a session with no Backstage id.
    /// </summary>
    public string? BackstagePublicSessionUrl(MySpeakerSession s)
    {
        if (string.IsNullOrWhiteSpace(s.BackstageSessionId)) return null;
        var baseUrl = string.IsNullOrWhiteSpace(_zohoOptions.BackstagePublicBaseUrl)
            ? "https://eldk27.expertslive.dk/"
            : _zohoOptions.BackstagePublicBaseUrl;
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        return $"{baseUrl}#/sessions/{Uri.EscapeDataString(s.BackstageSessionId)}";
    }

    /// <summary>One master class this speaker is linked to, with its public logistics + landing links.</summary>
    /// <param name="SessionId">The master-class session id.</param>
    /// <param name="Title">The master-class title.</param>
    /// <param name="PublicLink">The public logistics page URL (minted on view).</param>
    /// <param name="PrepLink">The speaker prep-content editor URL (FEATURE 2).</param>
    /// <param name="LandingLink">The attendee Master Class landing-page URL (FEATURE 2).</param>
    public sealed record MyMasterClass(
        int SessionId, string Title, string Slug, string PublicLink,
        string PrepLink, string LandingLink);

    /// <summary>The signed-in speaker's master classes (with public logistics link).</summary>
    public List<MyMasterClass> MasterClasses { get; private set; } = new();

    public static readonly ParticipantRole[] EligibleRoles =
    {
        ParticipantRole.Speaker,
    };

    public bool AccessDenied { get; private set; }
    public ParticipantRole Role { get; private set; }
    public string FirstName { get; private set; } = "there";

    /// <summary>The signed-in speaker's own sessions (room/time + question links).</summary>
    public IReadOnlyList<MySpeakerSession> MySessions { get; private set; } =
        Array.Empty<MySpeakerSession>();

    /// <summary>
    /// Ids of the speaker's own sessions whose PUBLIC page (<c>/Sessions/{id}</c>)
    /// would actually resolve — the same gate <see cref="PublicSessionsService.GetByIdAsync"/>
    /// applies (in the active edition, not a service session), NOT the speaker's own
    /// profile-publish state. The view shows the "view public session page" link iff
    /// the session id is in this set.
    /// </summary>
    public IReadOnlySet<int> PubliclyViewableSessionIds { get; private set; } =
        new HashSet<int>();

    /// <summary>
    /// True once at least one attendee rating exists for the speaker's sessions —
    /// ratings only appear after attendees rate (post-event), so when false the hub
    /// shows a "not ready yet" notice instead of a click-through (operator 2026-06-25).
    /// </summary>
    public bool HasRatings { get; private set; }

    /// <summary>§124: sessionId → the matched per-room session-evaluation QR file
    /// (only sessions whose room matched a QR file are present; empty when the QR
    /// folder is not configured).</summary>
    public IReadOnlyDictionary<int, SessionEvalQrFile> RoomQr { get; private set; } =
        new Dictionary<int, SessionEvalQrFile>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        Role = me.Role;
        FirstName = me.FirstName;
        if (!EligibleRoles.Contains(me.Role))
        {
            AccessDenied = true;
            return Page();
        }

        // Make sure the speaker's milestone tasks exist before we read them --
        // a speaker imported after the last seeding run otherwise sees an empty
        // tracker on their first visit. Idempotent on SourceKey; never fails the
        // page (matches /Index's behaviour).
        try
        {
            await _speakerDeadlines.SeedAsync(me.EventId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Speaker hub: deadline seeding failed for event {EventId}", me.EventId);
        }

        await LoadAsync(me, ct);
        return Page();
    }

    private async Task LoadAsync(CurrentParticipant me, CancellationToken ct)
    {
        // Bring this speaker's OPEN logistics tasks in line with form data they have
        // already submitted (hotel/dinner/lunch/swag/travel) before anything is read.
        // Idempotent + no-op when nothing needs changing.
        await _formTaskReconciler.ReconcileAsync(me.EventId, me.ParticipantId, ct);

        // My sessions (own-row scoped server-side) — room/time + question links.
        MySessions = await _sessions.GetMySessionsAsync(
            me.EventId, me.ParticipantId, me.Role, ct);

        // Resolve, per session, whether its PUBLIC /Sessions/{id} page would actually
        // resolve — the same gate the public page uses (active edition + not a service
        // session), independent of the speaker's own profile-publish state. The view
        // only links rows that are genuinely publicly viewable.
        PubliclyViewableSessionIds = await _publicSessions.GetPubliclyViewableSessionIdsAsync(
            MySessions.Select(s => s.SessionId), ct);

        // §124: per-room session-evaluation QR per session (inert until the QR folder
        // is configured). The download itself is served by /Speaker/Evaluations?Qr.
        if (_qr.CanRead)
        {
            RoomQr = await _qr.MatchSessionsAsync(
                MySessions.Select(s => new SessionRoomRef(s.SessionId, s.Room)), ct);
        }

        // Ratings only exist once attendees have rated (post-event) — drives the
        // "not ready yet" notice vs. the click-through on the hub.
        var mySessionIds = MySessions.Select(s => s.SessionId).ToList();
        HasRatings = mySessionIds.Count > 0
            && await _db.SessionEvaluations.AnyAsync(e => mySessionIds.Contains(e.SessionId), ct);

        // Master classes this speaker is linked to — surface the "show public
        // link" affordance (REQUIREMENTS § 6c). The slug is minted on first view.
        var myMasterClasses = await _db.SessionSpeakers
            .Where(ss => ss.ParticipantId == me.ParticipantId
                         && ss.Session.EventId == me.EventId
                         && ss.Session.Type == ParticipantMasterClassType)
            .Select(ss => new { ss.Session.Id, ss.Session.Title })
            .ToListAsync(ct);

        MasterClasses = new List<MyMasterClass>();
        foreach (var mc in myMasterClasses)
        {
            var slug = await _logistics.EnsureSlugAsync(me.EventId, mc.Id, ct);
            var link = Url.PageLink(pageName: "/MasterClass/Index", values: new { slug }) ?? string.Empty;
            // FEATURE 2: the speaker prep editor + the attendee landing page (preview).
            var prep = Url.Page("/Speaker/MasterClassPrep", null, new { sessionId = mc.Id }) ?? string.Empty;
            var landing = Url.Page("/MasterClassPage", null, new { sessionId = mc.Id }) ?? string.Empty;
            MasterClasses.Add(new MyMasterClass(mc.Id, mc.Title, slug, link, prep, landing));
        }
    }

    private const SessionType ParticipantMasterClassType = SessionType.MasterClass;
}
