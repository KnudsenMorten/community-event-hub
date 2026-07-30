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
// §322i: the per-session slide uploads live ON the My Sessions cards — same 1 GB relay cap
// as the sponsor uploads (SharePoint side is chunked, §322b).
[RequestSizeLimit(1_073_741_824)]
[RequestFormLimits(MultipartBodyLengthLimit = 1_073_741_824)]
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
    private readonly SessionEvalPdfService _evalPdf;
    private readonly CommunityHub.Core.Email.CalendarInviteEmailService _calendarInvite;
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
        SessionEvalPdfService evalPdf,
        CommunityHub.Core.Email.CalendarInviteEmailService calendarInvite,
        ZohoOptions zohoOptions,
        ILogger<IndexModel> logger,
        // §322h: optional + last (unit tests need not construct it; DI injects in prod).
        CommunityHub.Core.Integrations.Graphics.SpeakerPresentationService? presentations = null)
    {
        _db = db;
        _participant = participant;
        _speakerDeadlines = speakerDeadlines;
        _formTaskReconciler = formTaskReconciler;
        _logistics = logistics;
        _sessions = sessions;
        _publicSessions = publicSessions;
        _qr = qr;
        _evalPdf = evalPdf;
        _calendarInvite = calendarInvite;
        _zohoOptions = zohoOptions;
        _logger = logger;
        _presentations = presentations;
    }

    private readonly CommunityHub.Core.Integrations.Graphics.SpeakerPresentationService? _presentations;

    /// <summary>§322h: per-session slides views-or-downloads count (no entry = 0).</summary>
    public IReadOnlyDictionary<int, int> SlideStats { get; private set; } =
        new Dictionary<int, int>();

    /// <summary>§322h: the edition-wide total — the shared "motion number".</summary>
    public int SlideStatsTotal { get; private set; }

    /// <summary>§322i: per-session latest deck names (preview/final) for the upload fold-out.</summary>
    public IReadOnlyDictionary<int, CommunityHub.Core.Integrations.Graphics.PresentationSessionSlot> SlideFiles
    { get; private set; } = new Dictionary<int, CommunityHub.Core.Integrations.Graphics.PresentationSessionSlot>();

    /// <summary>§322i: true when the SharePoint folders are wired (the upload fold-out shows).</summary>
    public bool SlideUploadsConfigured { get; private set; }

    /// <summary>§322i: flash from the per-session slide upload postback.</summary>
    [TempData] public string? SlideUploadMessage { get; set; }
    [TempData] public bool SlideUploadIsError { get; set; }

    /// <summary>§322i: upload one session's PREVIEW/FINAL deck straight from its My Sessions
    /// card. Same rules as the retired /Speaker/Presentations page: own-session gate +
    /// PDF/PPTX/ZIP (server-side, in the service), §68-style auto-versioning, task
    /// auto-complete. Fail-soft with a flash.</summary>
    public async Task<IActionResult> OnPostUploadSlidesAsync(
        string kind, int sessionId, IFormFile? file, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (_presentations is null) return RedirectToPage();

        var presentationKind = string.Equals(kind, "final", StringComparison.OrdinalIgnoreCase)
            ? CommunityHub.Core.Integrations.Graphics.PresentationKind.Final
            : CommunityHub.Core.Integrations.Graphics.PresentationKind.Preview;

        if (file is null || file.Length == 0)
        {
            SlideUploadMessage = "Choose a PDF, PPTX or ZIP file first.";
            SlideUploadIsError = true;
            return RedirectToPage();
        }
        if (!_presentations.CanUpload(presentationKind))
        {
            SlideUploadMessage = "Uploads aren't configured yet — please contact the organizers.";
            SlideUploadIsError = true;
            return RedirectToPage();
        }

        // §461: a SPONSOR-category speaker owns the FINAL deck only (§456). The preview button is
        // hidden for them, but hiding is not a gate — the handler refuses it too, or a stale page
        // or a hand-made POST would still be accepted. Same lesson as §299 7.1: the server-side
        // check is what actually holds.
        if (presentationKind == CommunityHub.Core.Integrations.Graphics.PresentationKind.Preview
            && await _db.SpeakerProfiles.AnyAsync(
                sp => sp.EventId == me.EventId && sp.ParticipantId == me.ParticipantId
                      && sp.Category == SpeakerCategory.Sponsor, ct))
        {
            SlideUploadMessage = "Only the final presentation is required for your session.";
            SlideUploadIsError = true;
            return RedirectToPage();
        }

        try
        {
            // §455 (operator 2026-07-27, after a speaker's upload failed): this used to copy the
            // WHOLE deck into a MemoryStream and then call ToArray() — holding a multi-hundred-MB
            // file in RAM twice, on a shared instance, before a byte reached SharePoint. The
            // request body now streams straight into Graph's chunked upload session, so peak
            // memory is one 10 MiB chunk however big the deck is.
            await using var upload = file.OpenReadStream();
            var stored = await _presentations.UploadStreamAsync(
                me.EventId, me.ParticipantId, sessionId, presentationKind,
                file.FileName, upload, file.Length, ct);
            SlideUploadMessage =
                $"✅ Uploaded {stored} — a re-upload becomes the next version automatically, and the newest version is what attendees see.";
            SlideUploadIsError = false;
        }
        catch (InvalidOperationException ex)
        {
            SlideUploadMessage = ex.Message;   // wrong file type / not your session
            SlideUploadIsError = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Slide upload failed for participant {Pid} (session {SessionId}, {Kind}).",
                me.ParticipantId, sessionId, presentationKind);
            SlideUploadMessage = "The upload failed — please try again. If it keeps failing, contact the organizers.";
            SlideUploadIsError = true;
        }
        return RedirectToPage();
    }

    /// <summary>Set after an "Email me a calendar invite" POST so the view can confirm it.</summary>
    [TempData] public string? CalendarInviteMessage { get; set; }

    /// <summary>
    /// §193: e-mail the signed-in speaker a calendar INVITATION for one of their own
    /// sessions (replacing the old "Calendar sync" .ics download). Scoped server-side
    /// to a session they actually speak at; the invite goes to their chosen calendar /
    /// override e-mail. 404-safe + fail-safe.
    /// </summary>
    public async Task<IActionResult> OnPostSendSessionInviteAsync(int sessionId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var host = Request.Host.Value ?? "communityhub";
        var session = await _db.SessionSpeakers
            .AsNoTracking()
            .Where(ss => ss.ParticipantId == me.ParticipantId
                         && ss.Session.EventId == me.EventId
                         && ss.SessionId == sessionId
                         && ss.Session.StartsAt != null)
            .Select(ss => new
            {
                ss.Session.Id,
                ss.Session.Title,
                ss.Session.Room,
                ss.Session.StartsAt,
                ss.Session.EndsAt,
            })
            .FirstOrDefaultAsync(ct);

        if (session is null || session.StartsAt is null)
        {
            CalendarInviteMessage = "That session isn't scheduled yet, so there's nothing to add to your calendar.";
            return RedirectToPage();
        }

        try
        {
            var sent = await _calendarInvite.SendItemInviteAsync(
                me.ParticipantId,
                uid: $"session-{session.Id}@{host}",
                summary: $"My session: {session.Title}",
                description: string.IsNullOrWhiteSpace(session.Room)
                    ? "Your session at the event."
                    : $"Your session at the event. Room: {session.Room}",
                location: session.Room,
                start: session.StartsAt.Value,
                end: session.EndsAt ?? session.StartsAt.Value.AddHours(1),
                allDay: false,
                fileName: "session.ics",
                introHtml: $"Here is a calendar invitation for your session <strong>{System.Net.WebUtility.HtmlEncode(session.Title)}</strong>.",
                ct: ct);
            CalendarInviteMessage = sent
                ? sent.Confirmation()
                : "Calendar invitations are turned off for this event.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Send session invite failed for session {SessionId}", sessionId);
            CalendarInviteMessage = "We couldn't send that invite just now — please try again.";
        }

        return RedirectToPage();
    }

    /// <summary>§326u: the edition's pre-day date (null when dates aren't configured).</summary>
    public DateOnly? PreDayDate { get; private set; }

    /// <summary>
    /// The PUBLIC Zoho Backstage session page URL for a session (§52), or null when the
    /// session has no Backstage agenda id — the caller then keeps the internal link.
    /// §326u (operator 2026-07-25, live URL sample): the public deep-link format is
    /// <c>{base}#/agenda?day={n}&amp;lang=en&amp;sessionId={id}</c> — the old
    /// <c>#/sessions/{id}</c> shape landed on a wrong page. <c>day</c> is the 1-based
    /// Zoho event day: the pre-day (Master Classes, 9 Feb) is day 1, the main day day 2 —
    /// derived from the session's (Danish-local) date against the edition's PreDayDate,
    /// falling back to the Master-Class flag when no date is known. Never fabricated for
    /// a session with no Backstage id.
    /// </summary>
    public string? BackstagePublicSessionUrl(MySpeakerSession s)
    {
        if (string.IsNullOrWhiteSpace(s.BackstageSessionId)) return null;
        var baseUrl = string.IsNullOrWhiteSpace(_zohoOptions.BackstagePublicBaseUrl)
            ? "https://eldk27.expertslive.dk/"
            : _zohoOptions.BackstagePublicBaseUrl;
        if (!baseUrl.EndsWith('/')) baseUrl += "/";

        var sessionDate = s.StartsAt is { } starts
            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
                starts, CommunityHub.Core.Integrations.EventTimezone.Tz).DateTime)
            : s.FallbackDate;
        var isPreDay = sessionDate is { } d && PreDayDate is { } pre
            ? d == pre
            : s.IsMasterClass;
        var day = isPreDay ? 1 : 2;
        return $"{baseUrl}#/agenda?day={day}&lang=en&sessionId={Uri.EscapeDataString(s.BackstageSessionId)}";
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
    /// §461 (operator 2026-07-27: *"for the My Sessions page for a sponsor speaker, dont show
    /// things like SoMe Promote, upload preview, speaker template, etc."*) — true when this
    /// speaker's category is <see cref="SpeakerCategory.Sponsor"/>.
    ///
    /// <para>A sponsor-brought speaker does not run the ELDK speaker programme, so the card
    /// actions that belong to it are hidden: SoMe promote, the speaker template, and the PREVIEW
    /// upload. The FINAL upload stays — §456: *"only task relevant for a sponsor (exhibitor)
    /// speaker is the task for upload final presentation"*. Matches §457 (menu) and §458 (task)
    /// so all three surfaces say the same thing.</para>
    /// </summary>
    public bool IsSponsorCategorySpeaker { get; private set; }

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
    /// §192d: per session, which evaluation PDFs EXIST (Score / Open-feedback). Drives the
    /// two per-session download buttons — the Score button always renders, the Open-feedback
    /// button only when an open-feedback PDF is present for that session.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlySet<EvaluationPdfKind>> EvalKinds { get; private set; } =
        new Dictionary<int, IReadOnlySet<EvaluationPdfKind>>();

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

        // §326u: the edition's pre-day date — drives the Zoho public-agenda ?day= number
        // (pre-day = Zoho day 1, main day = day 2) for "View public session page".
        PreDayDate = await _db.Events.Where(e => e.Id == me.EventId)
            .Select(e => e.PreDayDate).FirstOrDefaultAsync(ct);

        // My sessions (own-row scoped server-side) — room/time + question links.
        MySessions = await _sessions.GetMySessionsAsync(
            me.EventId, me.ParticipantId, me.Role, ct);

        // §461: only an EXPLICIT Sponsor category hides anything — an uncategorised speaker keeps
        // the full set rather than being quietly stripped of it.
        IsSponsorCategorySpeaker = await _db.SpeakerProfiles.AnyAsync(
            sp => sp.EventId == me.EventId && sp.ParticipantId == me.ParticipantId
                  && sp.Category == SpeakerCategory.Sponsor, ct);

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

        // §192d: which evaluation PDFs exist per session — the Open-feedback button only
        // renders for a session that actually has an open-feedback PDF.
        var mySessionIds = MySessions.Select(s => s.SessionId).ToList();
        EvalKinds = await _evalPdf.GetKindsForSessionsAsync(me.EventId, mySessionIds, ct);

        // §322h: slides interest — per own session + the edition-wide "motion number".
        // §322i: + each session's latest deck names for the on-card upload fold-out.
        if (_presentations is not null)
        {
            SlideStats = await _presentations.GetStatsAsync(me.EventId, mySessionIds, ct);
            SlideStatsTotal = await _presentations.GetTotalAsync(me.EventId, ct);
            SlideUploadsConfigured =
                _presentations.CanUpload(CommunityHub.Core.Integrations.Graphics.PresentationKind.Preview)
                || _presentations.CanUpload(CommunityHub.Core.Integrations.Graphics.PresentationKind.Final);
            SlideFiles = (await _presentations.GetSessionSlotsAsync(me.EventId, me.ParticipantId, ct))
                .ToDictionary(s => s.SessionId);
        }

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
