using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
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
    private readonly CommunityHub.Core.Domain.SessionEvaluationService _eval;
    private readonly CommunityHub.Core.Organizer.SessionDeletionService _deletion;
    private readonly CommunityHub.Core.Organizer.SessionBulkOperationService _bulk;
    private readonly TimeProvider _clock;
    private readonly IStringLocalizer<SharedResource> _loc;
    private readonly CommunityHub.Core.Settings.FeatureGateService _gate;
    // §299.8/b7 + §299.6/b5: the per-edition length quick-picks / max and the
    // config room registry (unknown-room badge + room-name datalist).
    private readonly CommunityHub.Core.Config.SessionOptionsService _sessionOptions;
    private readonly CommunityHub.Core.Config.RoomRegistryService _roomRegistry;

    public SessionsModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SessionManagementService mgmt,
        SessionEvaluationMailService evalMail,
        MasterClassLogisticsService logistics,
        CommunityHub.Core.Domain.SessionEvaluationService eval,
        CommunityHub.Core.Organizer.SessionDeletionService deletion,
        CommunityHub.Core.Organizer.SessionBulkOperationService bulk,
        TimeProvider clock,
        IStringLocalizer<SharedResource> loc,
        CommunityHub.Core.Settings.FeatureGateService gate,
        CommunityHub.Core.Config.SessionOptionsService sessionOptions,
        CommunityHub.Core.Config.RoomRegistryService roomRegistry)
    {
        _db = db;
        _participant = participant;
        _mgmt = mgmt;
        _evalMail = evalMail;
        _logistics = logistics;
        _eval = eval;
        _deletion = deletion;
        _bulk = bulk;
        _clock = clock;
        _loc = loc;
        _gate = gate;
        _sessionOptions = sessionOptions;
        _roomRegistry = roomRegistry;
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
            var token = await _eval.EnsurePublicTokenAsync(SessionId, ct);
            EvaluateLink = Url.Page("/Sessions/Evaluate", null, new { token }, Request.Scheme);
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
            q = q.Where(x => x.Title.Contains(s)
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
                EvaluationCount = _db.SessionEvaluations.Count(e => e.SessionId == s.Id),
                Speakers = s.SessionSpeakers
                    .Select(ss => ss.Participant.FullName)
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
        SlideStats = await _db.SessionSlideStats
            .AsNoTracking()
            .Where(x => x.EventId == eventId && rowIds.Contains(x.SessionId))
            .ToDictionaryAsync(x => x.SessionId, x => x.Count, ct);
        SlideStatsTotal = await _db.SessionSlideStats
            .AsNoTracking()
            .Where(x => x.EventId == eventId)
            .SumAsync(x => (int?)x.Count, ct) ?? 0;
    }

    /// <summary>§322h: per-row slides open-count for the grid (no entry = 0).</summary>
    public IReadOnlyDictionary<int, int> SlideStats { get; private set; } = new Dictionary<int, int>();

    /// <summary>§322h: the edition-wide slides open-count (the "motion number").</summary>
    public int SlideStatsTotal { get; private set; }
}
