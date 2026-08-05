using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer session management (REQUIREMENTS § session management). Lists the
/// edition's sessions (imported + hub-added) with Type/Length filters, lets the
/// organizer add a hub-only session (e.g. a sponsor session), edit a session's
/// Type/Length/Room + evaluation form URL, provision a per-room QR (SharePoint seam),
/// download a session's room QR, and email HappyOrNot evaluation results to speakers.
/// Organizer-only. Mobile-first + a11y (labelled controls, semantic table).
/// </summary>
[Authorize]
public class SessionsModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SessionManagementService _mgmt;
    private readonly SessionEvaluationMailService _evalMail;

    private readonly MasterClassLogisticsService _logistics;
    // §748.1 — the feedback link's token comes from the SAME service the ask page uses. It is one
    // token per session (Session.PublicToken); the retired 1–5 service only ever held a duplicate.
    private readonly CommunityHub.Core.Domain.SessionQuestionService _tokens;
    private readonly CommunityHub.Core.Organizer.SessionDeletionService _deletion;
    private readonly CommunityHub.Core.Organizer.SessionBulkOperationService _bulk;
    private readonly TimeProvider _clock;
    private readonly IStringLocalizer<SharedResource> _loc;
    private readonly CommunityHub.Core.Settings.FeatureGateService _gate;
    // §299.8/b7 + §299.6/b5: the per-edition length quick-picks / max and the
    // config room registry (unknown-room badge + room-name datalist).
    private readonly CommunityHub.Core.Config.SessionOptionsService _sessionOptions;
    private readonly CommunityHub.Core.Config.RoomRegistryService _roomRegistry;
    // §769.4 — the deck folders, read once per page load for the whole edition.
    private readonly CommunityHub.Core.Integrations.Graphics.SpeakerPresentationService? _decks;
    private readonly ILogger<SessionsModel>? _log;
    // §769.6 — evaluation results (DB ledger) and printed QR codes (one folder listing).
    private readonly CommunityHub.Core.Integrations.Graphics.SessionEvalPdfService? _evalPdfs;
    private readonly CommunityHub.Core.Integrations.Graphics.SessionEvalsQrService? _evalQr;
    // §6.2 — the publisher's own version function, so "stale" on the grid means what it means to
    // the job that republishes.
    private readonly CommunityHub.Core.Evaluation.EvaluationReportBuilder? _reports;

    public SessionsModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SessionManagementService mgmt,
        SessionEvaluationMailService evalMail,
        MasterClassLogisticsService logistics,
        CommunityHub.Core.Domain.SessionQuestionService tokens,
        CommunityHub.Core.Organizer.SessionDeletionService deletion,
        CommunityHub.Core.Organizer.SessionBulkOperationService bulk,
        TimeProvider clock,
        IStringLocalizer<SharedResource> loc,
        CommunityHub.Core.Settings.FeatureGateService gate,
        CommunityHub.Core.Config.SessionOptionsService sessionOptions,
        CommunityHub.Core.Config.RoomRegistryService roomRegistry,
        // §769.4 / work-order §6.2 — per-session deck state on the grid. OPTIONAL, matching this
        // page's existing test constructions: absent ⇒ the column reads "unknown", never "none".
        CommunityHub.Core.Integrations.Graphics.SpeakerPresentationService? decks = null,
        ILogger<SessionsModel>? log = null,
        // §769.6 — evaluation result + QR state. Optional for the same reason as `decks`.
        CommunityHub.Core.Integrations.Graphics.SessionEvalPdfService? evalPdfs = null,
        CommunityHub.Core.Integrations.Graphics.SessionEvalsQrService? evalQr = null,
        // §6.2 — evaluation-report staleness. Optional for the same reason as the rest: absent ⇒ no
        // stale badge, never a wrong one.
        CommunityHub.Core.Evaluation.EvaluationReportBuilder? reports = null,
        // §836 (operator 2026-08-05: "i also want for each sessions in my sessions when we will
        // announce the session"). OPTIONAL for the same reason as `decks`/`evalPdfs` above — absent
        // ⇒ the column reads "—", never a wrong date.
        CommunityHub.Core.Integrations.SoMeAnnouncementQuery? announcements = null)
    {
        _announcements = announcements;
        _db = db;
        _participant = participant;
        _mgmt = mgmt;
        _evalMail = evalMail;
        _logistics = logistics;
        _tokens = tokens;
        _deletion = deletion;
        _bulk = bulk;
        _clock = clock;
        _loc = loc;
        _gate = gate;
        _sessionOptions = sessionOptions;
        _roomRegistry = roomRegistry;
        _decks = decks;
        _log = log;
        _evalPdfs = evalPdfs;
        _evalQr = evalQr;
        _reports = reports;
    }

    /// <summary>The session ids ticked in the bulk-select grid (posted form field).</summary>
    [BindProperty] public List<int> SelectedIds { get; set; } = new();

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    /// <summary>
    /// The honest success/failure/no-op confirmation for the last send / QR
    /// provisioning action, rendered by the shared <c>_Flash</c> toast
    /// (REQUIREMENTS §21). Shaped by the pure <see cref="ActionResultSummarizer"/>
    /// so it reflects the REAL outcome (sent at &lt;time&gt; / stored at &lt;url&gt;,
    /// or a dropped/failed reason), never an optimistic assumption.
    /// </summary>
    public ActionResultSummary? Result { get; private set; }

    /// <summary>Localized format bundle for the action-result confirmations.</summary>
    private ActionResultSummarizer.Formats Formats => new(
        SentFormat: _loc["Action.Sent"].Value,
        ProvisionedFormat: _loc["Action.Provisioned"].Value,
        ProvisionedNoUrlFormat: _loc["Action.ProvisionedNoUrl"].Value,
        NoOpFormat: _loc["Action.NoOp"].Value,
        FailedFormat: _loc["Action.Failed"].Value);

    // --- Filters (querystring) ---------------------------------------------
    [BindProperty(SupportsGet = true)] public SessionType? FilterType { get; set; }
    /// <summary>§299.8/b7 — the length filter is MINUTES now (config quick-picks),
    /// not the retired enum bucket.</summary>
    [BindProperty(SupportsGet = true)] public int? FilterLength { get; set; }

    // --- Search / sort / paging (GET-bound; also re-posted as hidden fields so
    //     the grid keeps its place after a post returns Page()). Mirrors the
    //     Participants / Speakers / Attendees grids (REQUIREMENTS §20/§21). -----
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    /// <summary>Sort column key: title | type | length | room. Default title.</summary>
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "title";
    [BindProperty(SupportsGet = true)] public bool Desc { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNo { get; set; } = 1;

    public GridPage Paging { get; private set; }

    public bool NextDescFor(string col) => Sort == col && !Desc;
    public string SortIndicator(string col) => Sort != col ? "" : (Desc ? " ▼" : " ▲");
    public string AriaSort(string col) => Sort != col ? "none" : (Desc ? "descending" : "ascending");

    // --- Add hub session form ----------------------------------------------
    [BindProperty] public string? NewTitle { get; set; }
    [BindProperty] public SessionType NewType { get; set; } = SessionType.TechnicalSession;
    /// <summary>§299.8/b7 — integer minutes (source of truth); the quick-picks are
    /// config, any positive value up to the configured max is accepted.</summary>
    [BindProperty] public int NewLengthMinutes { get; set; } = 50;
    /// <summary>§299.8/b7 — REQUIRED day choice for the manual add path (imported
    /// sessions never prompt). Stamps StartsAt + sets IsDateOverridden.</summary>
    [BindProperty] public HubSessionDay? NewDay { get; set; }
    [BindProperty] public string? NewRoom { get; set; }
    [BindProperty] public string? NewAbstract { get; set; }

    // --- Edit / action inputs ----------------------------------------------
    [BindProperty] public int SessionId { get; set; }
    [BindProperty] public SessionType EditType { get; set; }
    /// <summary>§299.8/b7 — integer minutes (source of truth), see NewLengthMinutes.</summary>
    [BindProperty] public int EditLengthMinutes { get; set; }
    [BindProperty] public string? EditRoom { get; set; }
    [BindProperty] public string? EditEvaluationFormUrl { get; set; }
    /// <summary>❓OPEN-20 — optional MANUAL schedule edit (datetime-local, UTC wall
    /// time). Pre-filled with the current values; a CHANGED value sets
    /// IsDateOverridden so re-imports keep the manual date. Blank = leave as is.</summary>
    [BindProperty] public string? EditStartsAt { get; set; }
    [BindProperty] public string? EditEndsAt { get; set; }

    [BindProperty] public string? QrRoom { get; set; }
    [BindProperty] public string? QrTargetUrl { get; set; }

    [BindProperty] public string? EvalResultsText { get; set; }

    /// <summary>The public logistics link for the last session a "show link" was requested for.</summary>
    public string? PublicLink { get; private set; }
    public int? PublicLinkSessionId { get; private set; }

    /// <summary>The public evaluate link for the last session a "show evaluate link" was requested for.</summary>
    public string? EvaluateLink { get; private set; }
    public int? EvaluateLinkSessionId { get; private set; }

    public List<Row> Rows { get; private set; } = new();

    private readonly CommunityHub.Core.Integrations.SoMeAnnouncementQuery? _announcements;

    /// <summary>
    /// §836 — when each visible session gets announced on LinkedIn, keyed by session id.
    /// Held posts are included: this is the ORGANIZER's view, and a planned-but-unapproved
    /// announcement is exactly what he needs to see (the approved-only rule is §837/§838's).
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<CommunityHub.Core.Integrations.SoMeAnnouncement>>
        Announcements
    { get; private set; } =
        new Dictionary<int, IReadOnlyList<CommunityHub.Core.Integrations.SoMeAnnouncement>>();
    public record Row(
        int Id, string Title, SessionType Type, SessionLength Length,
        string? Room, bool IsHubAdded, string? RoomQrUrl, string? EvaluationFormUrl,
        DateTimeOffset? EvaluationEmailedAt, IReadOnlyList<string> Speakers,
        string? PublicSlug,
        int QuestionCount, int EvaluationCount, int SignupCount,
        bool UsedForTesting = false,
        // §299.8/b7: the source-of-truth minutes (null only for legacy imports
        // where no minutes could be derived) + the manual-schedule flag; §299.6/b5:
        // true when the non-blank room is NOT in the config registry (warn badge).
        int? LengthMinutes = null,
        DateTimeOffset? StartsAt = null,
        DateTimeOffset? EndsAt = null,
        bool IsDateOverridden = false,
        bool UnknownRoom = false)
    {
        /// <summary>
        /// True when this session can be deleted with no attendee data loss
        /// (no questions, evaluations, or master-class signups of any state).
        /// Drives whether the grid offers a delete button or a "has attendee
        /// data" note. Matches <see cref="CommunityHub.Core.Organizer.SessionDeletionService"/>.
        /// </summary>
        public bool CanDelete =>
            QuestionCount == 0 && EvaluationCount == 0 && SignupCount == 0;
    }

    public int TotalCount { get; private set; }
    public int HubAddedCount { get; private set; }

    public SelectList TypeOptions => new(
        Enum.GetValues<SessionType>().Select(t => new { Value = t, Text = Display(t) }),
        "Value", "Text");

    /// <summary>§299.8/b7 — the config length quick-picks (label + minutes) for the
    /// filter select and the add/edit minute-input datalists. Per-edition CONFIG.</summary>
    public IReadOnlyList<CommunityHub.Core.Config.SessionLengthOption> LengthQuickPicks =>
        _sessionOptions.LengthQuickPicks;

    /// <summary>§299.8/b7 — inclusive max for a custom length in minutes (config).</summary>
    public int MaxLengthMinutes => _sessionOptions.MaxMinutes;

    /// <summary>
    /// §326bk (operator 2026-07-25: "make this a simple dropdown — it is impossible to
    /// figure out"). The length picker is now a plain SELECT over the configured
    /// quick-picks. It used to be a number input wearing a datalist, which browsers
    /// render as a combo with BOTH a caret and a spinner and which reads as neither a
    /// dropdown nor a free-text box.
    ///
    /// <paramref name="current"/> keeps an EXISTING value selectable even when config
    /// does not list it — an imported session may carry any length, and re-saving such a
    /// row must never silently round it to the nearest pick. Null (the add form) yields
    /// the configured picks only. A length not offered here is now added in config
    /// (<c>sessionLengths</c>), not typed into the form.
    /// </summary>
    public SelectList LengthOptions(int? current = null)
    {
        var items = LengthQuickPicks
            .Select(q => new { Value = q.Minutes, Text = q.Label })
            .ToList();

        if (current is int m && m > 0 && items.All(i => i.Value != m))
        {
            items.Add(new { Value = m, Text = $"{m} min" });
        }

        return new SelectList(
            items.OrderBy(i => i.Value).ToList(), "Value", "Text", current);
    }

    /// <summary>§299.6/b5 — all registered room names for the room-input datalist
    /// (free text stays allowed; the registry only validates warn-only).</summary>
    public IReadOnlyList<string> RegistryRoomNames => _roomRegistry.Names;

    public static string Display(SessionType t) => t switch
    {
        SessionType.Keynote => "Keynote",
        SessionType.TechnicalSession => "Technical Session",
        SessionType.MasterClass => "Master Class",
        SessionType.AskTheExperts => "Ask the Experts",
        SessionType.PanelDiscussion => "Panel Discussion",
        SessionType.Welcome => "Welcome",
        _ => t.ToString(),
    };

    public static string Display(SessionLength l) => l switch
    {
        SessionLength.FullDay => "Full day",
        SessionLength.TwentyMin => "20 min",
        SessionLength.FiftyMin => "50 min",
        SessionLength.SixtyMin => "60 min",
        _ => l.ToString(),
    };

    /// <summary>§299.8/b7 — the grid's length label: the exact minutes when known
    /// ("37 min", "420 min"), else the legacy bucket label for old rows.</summary>
    public static string DisplayLength(int? minutes, SessionLength bucket) =>
        minutes is int m ? $"{m} min" : Display(bucket);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");

        // §299.8/b7: the manual add path REQUIRES a day choice (Pre-day / Main day);
        // imported sessions never prompt — their schedule comes from the source.
        if (NewDay is null)
        {
            Error = "Pick which day the session runs on (Pre-day or Main day).";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        try
        {
            await _mgmt.AddHubSessionAsync(
                me.EventId, NewTitle ?? string.Empty, NewType, NewLengthMinutes,
                day: NewDay, room: NewRoom, @abstract: NewAbstract, ct: ct);
            Message = "Hub session added.";
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostEditAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");

        try
        {
            // ❓OPEN-20: the schedule inputs are pre-filled with the current values;
            // only a PARSEABLE posted value participates (blank = leave as is, so a
            // schedule can't be wiped by an untouched empty field). A real change
            // sets IsDateOverridden inside UpdateSessionAsync.
            var startsAt = ParseLocalUtc(EditStartsAt);
            var endsAt = ParseLocalUtc(EditEndsAt);
            var applySchedule = startsAt is not null || endsAt is not null;

            var current = await _db.Sessions.AsNoTracking()
                .Where(s => s.Id == SessionId && s.EventId == me.EventId)
                .Select(s => new { s.StartsAt, s.EndsAt })
                .FirstOrDefaultAsync(ct);

            await _mgmt.UpdateSessionAsync(
                me.EventId, SessionId, EditType, EditLengthMinutes, EditRoom,
                EditEvaluationFormUrl,
                startsAt: startsAt ?? current?.StartsAt,
                endsAt: endsAt ?? current?.EndsAt,
                applySchedule: applySchedule,
                ct: ct);
            Message = "Session updated.";
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Parse a posted <c>datetime-local</c> value ("yyyy-MM-ddTHH:mm") as a
    /// UTC wall time (the same convention the stored schedule uses). Null when blank
    /// or unparseable.</summary>
    private static DateTimeOffset? ParseLocalUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParse(
            value.Trim(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var dt)
            ? new DateTimeOffset(dt, TimeSpan.Zero)
            : null;
    }

    /// <summary>Format a stored schedule value for a <c>datetime-local</c> input.</summary>
    public static string FormatLocalUtc(DateTimeOffset? value) =>
        value?.ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture)
        ?? string.Empty;

    /// <summary>
    /// Delete a session (REQUIREMENTS §21 organizer "Sessions delete / CRUD gap").
    /// Safe semantics live in <see cref="CommunityHub.Core.Organizer.SessionDeletionService"/>:
    /// a session with attendee engagement (questions / evaluations / bookings) is
    /// refused with a reason rather than silently destroying that data; a clean
    /// session is removed with its import-state speaker links. Organizer-only,
    /// edition-scoped; the page's confirm modal gates the click.
    /// </summary>
    /// <summary>
    /// §299 4.5/b8: toggle a session's <see cref="Session.UsedForTesting"/> flag. A
    /// flagged session never reaches the public agenda/pages and is hub-visible only
    /// to ring 0/1 (the standard RingCap=1 rule) — testable in dev AND prod.
    /// </summary>
    public async Task<IActionResult> OnPostToggleTestAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");

        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.Id == SessionId && s.EventId == me.EventId, ct);
        if (session is null)
        {
            Error = "Session not found.";
        }
        else
        {
            session.UsedForTesting = !session.UsedForTesting;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            Message = session.UsedForTesting
                ? "Marked as TEST session — hidden from the public pages and never synced to the public agenda."
                : "Test flag removed — the session is publicly visible again.";
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");

        var result = await _deletion.DeleteAsync(me.EventId, SessionId, ct);
        switch (result.Status)
        {
            case CommunityHub.Core.Organizer.SessionDeletionService.DeletionStatus.Deleted:
                Message = result.WasImported
                    ? $"\"{result.Title}\" was deleted. Note: it came from Sessionize, "
                      + "so the next import will recreate it unless it is removed there too."
                    : $"\"{result.Title}\" was deleted.";
                break;
            case CommunityHub.Core.Organizer.SessionDeletionService.DeletionStatus.Blocked:
                Error = $"\"{result.Title}\" was not deleted because it has "
                        + $"{string.Join(", ", result.BlockingDependencies)}. "
                        + "Remove or handle that attendee data first.";
                break;
            default:
                Error = "That session could not be found in this edition.";
                break;
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// Bulk-delete the ticked sessions (§20 universal CRUD + bulk). The safe
    /// semantics are the single-row ones applied row by row in
    /// <see cref="CommunityHub.Core.Organizer.SessionBulkOperationService"/>:
    /// sessions with attendee engagement (questions / evaluations / bookings) are
    /// left untouched and reported; clean sessions are removed with their speaker
    /// links; the honest result banner reports deleted / blocked / not-found and
    /// the re-import caveat. Organizer-only, edition-scoped; the page's confirm
    /// modal (live count) gates the click.
    /// </summary>
    public async Task<IActionResult> OnPostBulkDeleteAsync(string? confirmPhrase, CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");

        // §334: the modal counted rows in the BROWSER. This refuses on the server when the
        // phrase is missing or wrong, so a scripted or JS-off POST cannot skip the guard.
        if (!TypedConfirmation.Matches(confirmPhrase, TypedConfirmation.ConfirmPhrase))
        {
            Error = TypedConfirmation.Rejection(
                TypedConfirmation.ConfirmPhrase, "delete the selected sessions");
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        var requested = SelectedIds.Where(id => id > 0).Distinct().Count();
        if (requested == 0)
        {
            Error = "Pick at least one session first.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        var result = await _bulk.DeleteAsync(me.EventId, SelectedIds, ct);
        var skipped = result.Skipped(requested);

        if (result.Deleted == 0 && result.Blocked > 0)
        {
            Error = $"{result.Blocked} session(s) have attendee data (questions / "
                    + "evaluations / bookings) and were not deleted. Handle that data first.";
        }
        else
        {
            Message = $"{result.Deleted} session(s) deleted"
                + (result.ImportedDeleted > 0
                    ? $" ({result.ImportedDeleted} from Sessionize — a re-import will recreate them unless removed there too)"
                    : string.Empty)
                + (result.Blocked > 0
                    ? $", {result.Blocked} kept (have attendee data)"
                    : string.Empty)
                + (skipped > 0 ? $", {skipped} not found" : string.Empty)
                + ".";
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostProvisionQrAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");

        var result = await _mgmt.ProvisionRoomQrAsync(
            me.EventId, QrRoom ?? string.Empty, QrTargetUrl ?? string.Empty, ct);
        // Honest confirmation: success carries the stored URL + time; a not-wired /
        // missing-room outcome is reported as a no-op (NOT a green success).
        Result = ActionResultSummarizer.ForProvision(
            result.Provisioned, _clock.GetUtcNow(),
            url: result.ImageUrl, reason: result.Message, formats: Formats);

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostEmailEvalAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");

        try
        {
            var result = await _evalMail.EmailResultsToSpeakersAsync(
                SessionId, EvalResultsText ?? string.Empty, ct);
            // Honest send confirmation: a real send shows "sent at <time> — N
            // recipient(s)"; "no speaker with an address" / blank results is a no-op,
            // never reported as success.
            Result = ActionResultSummarizer.ForSend(
                anySent: result.Sent,
                recipientCount: result.Recipients.Count,
                at: _clock.GetUtcNow(),
                reason: result.Message,
                formats: Formats);
        }
        catch (Exception ex)
        {
            Result = ActionResultSummarizer.Failure(ex.Message, Formats);
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Mint + show the public logistics link for a master class (§ 6c).</summary>
    public async Task<IActionResult> OnPostShowPublicLinkAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");

        try
        {
            var slug = await _logistics.EnsureSlugAsync(me.EventId, SessionId, ct);
            PublicLink = Url.PageLink(pageName: "/MasterClass/Index", values: new { slug });
            PublicLinkSessionId = SessionId;
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// Mint (or reuse) the session's public token and show its public EVALUATE link
    /// (<c>/sessions/{token}/evaluate</c>) — this is the URL the room QR should point to
    /// so attendees can rate the session. Shares the same token as the ask page.
    /// </summary>
    public async Task<IActionResult> OnPostShowEvaluateLinkAsync(CancellationToken ct)
    {
        var me = Guard();
        if (me is null) return AccessDenied ? Page() : RedirectToPage("/Login");

        // Only mint links for sessions in this edition.
        if (await _db.Sessions.AnyAsync(s => s.Id == SessionId && s.EventId == me.EventId, ct))
        {
            // §748 — points at the LIVE four-point page, not the retired 1–5 form. The token is
            // unchanged (it is the same one the ask page uses), so nothing already shared breaks.
            var token = await _tokens.EnsurePublicTokenAsync(SessionId, ct);
            EvaluateLink = Url.Page("/Feedback", null, new { token }, Request.Scheme);
            EvaluateLinkSessionId = SessionId;
        }
        else
        {
            Error = "Session not found.";
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Serve the room QR image URL for a session (the "Download QR" button).</summary>
    public async Task<IActionResult> OnGetDownloadQrAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        // Server-enforced organizer gate — same role check the page's mutating
        // handlers apply via Guard(). This GET handler returns a file/redirect
        // rather than the page, so a non-organizer is denied with Forbid().
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.Id == id && s.EventId == me.EventId, ct);
        if (session is null || string.IsNullOrWhiteSpace(session.RoomQrUrl))
        {
            return NotFound("No QR code has been provisioned for this session's room yet.");
        }
        // The image is stored on SharePoint; redirect the speaker to the stored file.
        return Redirect(session.RoomQrUrl);
    }

    private CurrentParticipant? Guard()
    {
        var me = _participant.Current;
        if (me is null) return null;
        if (!OrganizerAuth.IsRealOrganizer(me))
        {
            AccessDenied = true;
            return null;
        }
        return me;
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        var q = _db.Sessions
            .Where(s => s.EventId == eventId && !s.IsServiceSession);
        if (FilterType is not null) q = q.Where(s => s.Type == FilterType);
        // §299.8/b7: the length filter matches the source-of-truth MINUTES; legacy
        // rows without minutes answer via the derived bucket (full-day rows match
        // any full-day-sized pick, others their bucket value).
        if (FilterLength is int flm)
        {
            q = q.Where(s =>
                s.LengthMinutes == flm
                || (s.LengthMinutes == null
                    && (s.Length == SessionLength.FullDay
                        ? flm >= 240
                        : (int)s.Length == flm)));
        }

        // Free-text search over title + room + linked speaker name — applied in
        // the database so we never materialize the whole edition (REQUIREMENTS §21).
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();

            // §769.10 — SEARCH BY CEH SESSION ID (operator 2026-08-02: "same with sessions overview
            // i need id column in view and search ability"). The session id is the key that names
            // every derived file — `{id} - Title_v3.pdf`, `session-{id}.gif`,
            // `session-{id}-…-qr.png` — so an organizer holding a file name needs to get from it
            // back to the session.
            //
            // 🔑 A digits-only term matches the id exactly AND still matches the text fields: room
            // names and titles contain numbers ("Room 16", "Top 10 tips").
            var isId = int.TryParse(s, out var idTerm);
            q = q.Where(x => (isId && x.Id == idTerm)
                             || x.Title.Contains(s)
                             || (x.Room != null && x.Room.Contains(s))
                             || x.SessionSpeakers.Any(ss => ss.Participant.FullName.Contains(s)));
        }

        var matched = await q.CountAsync(ct);
        Paging = GridPaging.Resolve(PageNo, GridPaging.DefaultPageSize, matched);

        // Stable, deterministic ordering for the chosen column (Id tiebreak so
        // paging never repeats/skips a row).
        var sorted = (Sort, Desc) switch
        {
            ("type", false)   => q.OrderBy(x => x.Type).ThenBy(x => x.Title).ThenBy(x => x.Id),
            ("type", true)    => q.OrderByDescending(x => x.Type).ThenBy(x => x.Title).ThenBy(x => x.Id),
            // §299.8/b7: length sorts by the effective MINUTES (bucket-derived for
            // legacy rows without a numeric length), not the enum ordinal.
            ("length", false) => q.OrderBy(x => x.LengthMinutes ?? (x.Length == SessionLength.FullDay ? 420 : (int)x.Length)).ThenBy(x => x.Title).ThenBy(x => x.Id),
            ("length", true)  => q.OrderByDescending(x => x.LengthMinutes ?? (x.Length == SessionLength.FullDay ? 420 : (int)x.Length)).ThenBy(x => x.Title).ThenBy(x => x.Id),
            ("room", false)   => q.OrderBy(x => x.Room).ThenBy(x => x.Title).ThenBy(x => x.Id),
            ("room", true)    => q.OrderByDescending(x => x.Room).ThenBy(x => x.Title).ThenBy(x => x.Id),
            (_, true)         => q.OrderByDescending(x => x.Title).ThenByDescending(x => x.Id),
            _                 => q.OrderBy(x => x.Title).ThenBy(x => x.Id),
        };

        var rows = await sorted
            .Skip(Paging.Skip).Take(Paging.PageSize)
            .Select(s => new
            {
                s.Id, s.Title, s.Type, s.Length, s.LengthMinutes, s.Room, s.IsHubAdded,
                s.RoomQrUrl, s.EvaluationFormUrl, s.EvaluationEmailedAt,
                s.PublicSlug, s.UsedForTesting, s.StartsAt, s.EndsAt, s.IsDateOverridden,
                // Engagement counts so the grid can offer a SAFE delete only when
                // there is no attendee data to lose (matches SessionDeletionService).
                // MC seats are CEH-owned now (MasterClassSignup), not Zoho bookings.
                SignupCount = _db.MasterClassSignups.Count(m => m.SessionId == s.Id),
                QuestionCount = s.Questions.Count,
                // §748.1 — the LIVE four-point responses. This comment says it "matches
                // SessionDeletionService", and it must keep doing so: counting the retired 1–5 table
                // showed the grid a permanent 0, offering a SAFE delete on a session that in fact
                // carried attendee feedback.
                EvaluationCount = _db.EvaluationResponses.Count(e => e.SessionId == s.Id),
                Speakers = s.SessionSpeakers
                    .Select(ss => ss.Participant.FullName)
                    .ToList(),
                // §6.2 — the line-up WITH ids, because the graphic's input hash is keyed on
                // participant id, not on the display name.
                SpeakerKeys = s.SessionSpeakers
                    .Select(ss => new { ss.ParticipantId, Name = ss.Participant.FullName })
                    .ToList(),
            })
            .ToListAsync(ct);

        Rows = rows.Select(r => new Row(
            r.Id, r.Title, r.Type, r.Length, r.Room, r.IsHubAdded,
            r.RoomQrUrl, r.EvaluationFormUrl, r.EvaluationEmailedAt, r.Speakers,
            r.PublicSlug,
            r.QuestionCount, r.EvaluationCount, r.SignupCount,
            r.UsedForTesting,
            r.LengthMinutes, r.StartsAt, r.EndsAt, r.IsDateOverridden,
            // §299.6/b5: warn-only badge — a non-blank room not in the registry.
            UnknownRoom: !string.IsNullOrWhiteSpace(r.Room)
                         && _roomRegistry.HasEntries
                         && !_roomRegistry.IsKnown(r.Room))).ToList();

        TotalCount = await _db.Sessions
            .CountAsync(s => s.EventId == eventId && !s.IsServiceSession, ct);
        HubAddedCount = await _db.Sessions
            .CountAsync(s => s.EventId == eventId && s.IsHubAdded && !s.IsServiceSession, ct);

        // §322h: public /Sessions/Slides opens (views+downloads, one number) per visible
        // row + the edition total — read straight from the counter table (no service dep).
        var rowIds = Rows.Select(r => r.Id).ToList();

        // §836 — the announcement dates for every visible session, in ONE query rather than one per
        // row (the grid can show 100+ sessions).
        if (_announcements is not null)
        {
            Announcements = await _announcements.ForSessionsAsync(
                eventId, rowIds, approvedOnly: false, ct);
        }

        // §6.2 — what each visible session's promo graphic WOULD hash to right now, so the grid can
        // tell "current" from "stale". Computed with the sweep's own formula, never a copy of it.
        var currentGraphicHash = rows.ToDictionary(
            r => r.Id,
            r => CommunityHub.Core.Integrations.Graphics.SoMeBundleBuildService
                .SessionGraphicInputHash(
                    r.Id, r.Title, r.SpeakerKeys.Select(s => (s.ParticipantId, s.Name))));
        SlideStats = await _db.SessionSlideStats
            .AsNoTracking()
            .Where(x => x.EventId == eventId && rowIds.Contains(x.SessionId))
            .ToDictionaryAsync(x => x.SessionId, x => x.Count, ct);
        SlideStatsTotal = await _db.SessionSlideStats
            .AsNoTracking()
            .Where(x => x.EventId == eventId)
            .SumAsync(x => (int?)x.Count, ct) ?? 0;

        // §769.4 / work-order §6.2 — per-session DECK STATE, so an organizer can see at a glance
        // which sessions still have no slides before the deadline. TWO cached folder listings for
        // the whole edition, not one Graph call per row.
        //
        // ⚠️ FAIL-SOFT. A library that cannot be read must not take this page down — it has fifteen
        // other jobs. It degrades to "deck state unavailable", which the view says out loud.
        // §769.5 — the SoMe promo graphic per session, from the DB (no Graph call). Only the rows on
        // screen: the edition has one graphic per session and the grid is paged.
        GraphicStates = await _db.GraphicAssets
            .AsNoTracking()
            .Where(g => g.EventId == eventId
                        && g.Type == GraphicAssetType.Session
                        && g.SessionId != null
                        && rowIds.Contains(g.SessionId!.Value))
            .Select(g => new
            {
                SessionId = g.SessionId!.Value,
                g.FileName,
                g.Status,
                g.IsOrganizerOverridden,
                g.InputHash,
                Generated = g.UpdatedAt ?? g.CreatedAt,
            })
            .ToDictionaryAsync(
                g => g.SessionId,
                g => new SessionGraphicState(
                    g.FileName,
                    // The extension IS the variant (§767 phase 2): .png single-speaker, .gif multi.
                    string.IsNullOrWhiteSpace(g.FileName) ? null : Path.GetExtension(g.FileName),
                    g.Status == GraphicAssetStatus.Released,
                    g.IsOrganizerOverridden,
                    g.Generated,
                    IsStale: CommunityHub.Core.Integrations.Graphics.SoMeBundleBuildService
                        .IsGraphicStale(
                            g.InputHash, g.IsOrganizerOverridden,
                            currentGraphicHash.GetValueOrDefault(g.SessionId))),
                ct);

        // §769.6 — evaluation RESULTS per session. Database-backed (the upload ledger), so this is
        // another free column: the PDFs' provenance is already tracked for the organizer eval page.
        if (_evalPdfs is not null)
        {
            try
            {
                EvalResultKinds = await _evalPdfs.GetKindsForSessionsAsync(eventId, rowIds, ct);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "§6.2: evaluation-result state unavailable for the grid.");
            }
        }

        // §6.2 — is a published evaluation REPORT stale: have responses arrived since it was
        // published? The comparison is the publisher's own version string, derived by the
        // publisher's own function, so "stale" here means the same thing it means to the job that
        // republishes — and a republish clears it.
        //
        // ⚠️ Deliberately NOT per row: four bulk queries for the page, not four per session.
        if (_reports is not null)
        {
            try
            {
                var evalSessions = await _db.EvaluationSessions
                    .AsNoTracking()
                    .Where(s => s.EventId == eventId
                                && s.CehSessionId != null
                                && rowIds.Contains(s.CehSessionId!.Value))
                    .Select(s => new
                    {
                        s.Id,
                        CehSessionId = s.CehSessionId!.Value,
                        s.PublishedReportVersion,
                    })
                    .ToListAsync(ct);

                var current = await _reports.VersionsAsync(
                    eventId, evalSessions.Select(s => s.Id).ToList(), ct);

                StaleReportSessionIds = evalSessions
                    // 🔒 Only a PUBLISHED report can be stale. One that was never published is
                    // "not available yet", which the column already says — calling it stale would
                    // put a warning on every session nobody has rated.
                    .Where(s => !string.IsNullOrWhiteSpace(s.PublishedReportVersion)
                                && current.TryGetValue(s.Id, out var now)
                                && !string.Equals(now, s.PublishedReportVersion, StringComparison.Ordinal))
                    .Select(s => s.CehSessionId)
                    .ToHashSet();
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "§6.2: evaluation-report staleness unavailable for the grid.");
            }
        }

        // §769.6 — evaluation QR codes. ONE folder listing for the whole edition, then matched by
        // the generator's own strict `session-{id}-` rule — never a fuzzy match, because a QR bound
        // to the wrong session is worse than one that does not resolve.
        if (_evalQr is not null && _evalQr.CanRead)
        {
            try
            {
                var qrFiles = await _evalQr.ListAllAsync(ct);
                QrSessionIds = qrFiles
                    .Where(f => f.SessionId is not null)
                    .Select(f => f.SessionId!.Value)
                    .ToHashSet();
                QrReadable = true;
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "§6.2: QR state unavailable for the grid.");
            }
        }

        DecksReadable = _decks is not null
                        && (_decks.CanRead(PresentationKind.Final)
                            || _decks.CanRead(PresentationKind.Preview));
        if (DecksReadable)
        {
            try
            {
                DeckStates = await _decks!.GetDeckStatesAsync(eventId, ct);
            }
            catch (Exception ex)
            {
                DecksReadable = false;
                _log?.LogWarning(ex,
                    "§6.2: the presentation folders could not be listed; the sessions page shows "
                    + "deck state as unavailable.");
            }
        }
    }

    /// <summary>§322h: per-row slides open-count for the grid (no entry = 0).</summary>
    public IReadOnlyDictionary<int, int> SlideStats { get; private set; } = new Dictionary<int, int>();

    /// <summary>§322h: the edition-wide slides open-count (the "motion number").</summary>
    public int SlideStatsTotal { get; private set; }

    // ===================================================================
    //  §769.11 — EXPORT EVERY SESSION (operator 2026-08-02: "export all participants and sessions
    //  to excel file" / "for sessions give fields you have")
    // ===================================================================

    /// <summary>Every session in the edition as CSV — ALL of them, not the current filter.</summary>
    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var csv = await BuildSessionExportCsvAsync(me.EventId, ';', ct);
        var bytes = System.Text.Encoding.UTF8.GetPreamble()
            .Concat(System.Text.Encoding.UTF8.GetBytes(csv)).ToArray();
        return File(bytes, "text/csv", "sessions.csv");
    }

    /// <summary>The same rows as <see cref="OnGetExportAsync"/>, as a native .xlsx workbook.</summary>
    public async Task<IActionResult> OnGetExportXlsxAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var csv = await BuildSessionExportCsvAsync(me.EventId, ',', ct);
        return File(
            CommunityHub.Export.CsvToXlsx.Build(csv, "Sessions"),
            CommunityHub.Export.CsvToXlsx.ContentType,
            "sessions.xlsx");
    }

    /// <summary>
    /// One row per session with every field the grid holds — he asked for "the fields you have".
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>Speakers are exported as names AND emails</b>, semicolon-joined inside one cell.
    /// A session's speakers are the reason most of these exports get opened, and a name without an
    /// address means going back to the other export to look each one up.</para>
    ///
    /// <para>⚠️ Service sessions are EXCLUDED, matching the grid: they are scheduling scaffolding
    /// (breaks, lunch), not talks, and their presence would make the row count disagree with the
    /// number shown on screen.</para>
    /// </remarks>
    private async Task<string> BuildSessionExportCsvAsync(
        int eventId, char delimiter, CancellationToken ct)
    {
        var rows = await _db.Sessions
            .AsNoTracking()
            .Where(s => s.EventId == eventId && !s.IsServiceSession)
            .OrderBy(s => s.Id)
            .Select(s => new
            {
                s.Id, s.Title, s.Type, s.Length, s.LengthMinutes, s.Room, s.IsHubAdded,
                s.StartsAt, s.EndsAt, s.Track, s.PublicSlug, s.SessionizeId,
                Speakers = s.SessionSpeakers
                    .Select(ss => new { ss.Participant.FullName, ss.Participant.Email })
                    .ToList(),
            })
            .ToListAsync(ct);

        var sb = new System.Text.StringBuilder();
        sb.Append("ID").Append(delimiter)
          .Append("Title").Append(delimiter)
          .Append("Type").Append(delimiter)
          .Append("Length (minutes)").Append(delimiter)
          .Append("Room").Append(delimiter)
          .Append("Track").Append(delimiter)
          .Append("Starts (UTC)").Append(delimiter)
          .Append("Ends (UTC)").Append(delimiter)
          .Append("Speakers").Append(delimiter)
          .Append("Speaker emails").Append(delimiter)
          .Append("Added in hub").Append(delimiter)
          .Append("Public slug").Append(delimiter)
          .Append("Sessionize id").AppendLine();

        foreach (var r in rows)
        {
            var minutes = r.LengthMinutes
                          ?? (r.Length == SessionLength.FullDay ? 420 : (int)r.Length);

            sb.Append(r.Id).Append(delimiter)
              .Append(ParticipantsModel.CsvField(r.Title, delimiter)).Append(delimiter)
              .Append(r.Type).Append(delimiter)
              .Append(minutes).Append(delimiter)
              .Append(ParticipantsModel.CsvField(r.Room, delimiter)).Append(delimiter)
              .Append(ParticipantsModel.CsvField(r.Track, delimiter)).Append(delimiter)
              .Append(r.StartsAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm") ?? string.Empty).Append(delimiter)
              .Append(r.EndsAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm") ?? string.Empty).Append(delimiter)
              .Append(ParticipantsModel.CsvField(
                  string.Join("; ", r.Speakers.Select(x => x.FullName)), delimiter)).Append(delimiter)
              .Append(ParticipantsModel.CsvField(
                  string.Join("; ", r.Speakers.Select(x => x.Email)), delimiter)).Append(delimiter)
              .Append(r.IsHubAdded ? "yes" : "no").Append(delimiter)
              .Append(ParticipantsModel.CsvField(r.PublicSlug, delimiter)).Append(delimiter)
              .Append(ParticipantsModel.CsvField(r.SessionizeId, delimiter)).AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>§769.4 — per-session deck state (no entry ⇒ nothing uploaded for that session).</summary>
    public IReadOnlyDictionary<int, SessionDeckState> DeckStates { get; private set; } =
        new Dictionary<int, SessionDeckState>();

    /// <summary>
    /// §769.5 / work-order §6.2 — the session's SoMe promo graphic: file, variant and whether it has
    /// been released to the speakers. Straight from the database, so it costs no Graph call.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>Generated is not Released.</b> The §767 gate is deliberate — the engine renders, an
    /// organizer releases — so a graphic that exists is not yet a graphic the speaker can see. A
    /// column showing only "exists" would report the whole edition ready while nobody had been given
    /// anything.
    /// </remarks>
    public IReadOnlyDictionary<int, SessionGraphicState> GraphicStates { get; private set; } =
        new Dictionary<int, SessionGraphicState>();

    /// <summary>One session's promo graphic, as the grid needs it.</summary>
    /// <param name="Variant">`.png` (one speaker) or `.gif` (several) — §767 phase 2.</param>
    /// <param name="IsStale">
    /// §6.2 — the session changed after this graphic was made, so the picture no longer matches the
    /// session it advertises.
    /// </param>
    public sealed record SessionGraphicState(
        string? FileName, string? Variant, bool Released, bool OrganizerOverridden,
        DateTimeOffset? GeneratedAt, bool IsStale);


    /// <summary>§769.6 — which evaluation PDFs exist per session (scores / open feedback).</summary>
    public IReadOnlyDictionary<int, IReadOnlySet<CommunityHub.Core.Domain.EvaluationPdfKind>>
        EvalResultKinds { get; private set; } =
        new Dictionary<int, IReadOnlySet<CommunityHub.Core.Domain.EvaluationPdfKind>>();

    /// <summary>§769.6 — sessions that have a printed-QR file in the library.</summary>
    public IReadOnlySet<int> QrSessionIds { get; private set; } = new HashSet<int>();

    /// <summary>
    /// §6.2 — sessions whose PUBLISHED evaluation report is out of date: responses have arrived
    /// since it was published, so the PDF on file no longer reflects the ratings.
    /// </summary>
    public IReadOnlySet<int> StaleReportSessionIds { get; private set; } = new HashSet<int>();

    /// <summary>
    /// False when the QR folder cannot be read. Same rule as the deck column: "cannot see" must not
    /// render as "not generated", or an organizer reprints codes that already exist.
    /// </summary>
    public bool QrReadable { get; private set; }

    /// <summary>
    /// False when the document library cannot be read at all. 🔒 The distinction is the point:
    /// without it, "nobody has uploaded a deck" and "CEH cannot see the library" render identically,
    /// and an organizer would start chasing forty speakers who had all in fact uploaded.
    /// </summary>
    public bool DecksReadable { get; private set; }
}
