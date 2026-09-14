using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer participant grid (v2). Builds on the v1 grid + bulk-ops:
///   - filter by status (active/inactive/all), by <b>persona</b>
///     (<see cref="ParticipantRole"/>) and, for sponsors, by <b>sponsor company</b>;
///   - "active" everywhere means the lifecycle-correct
///     <see cref="ParticipantActivation"/> rule (<c>IsActive AND
///     LifecycleState == Active</c>), with active the DEFAULT and a toggle to
///     show inactive / all;
///   - per-row organizer actions: deactivate/reactivate (the cancellation
///     switch), <b>Switch to user</b> (act-as), <b>Modify on behalf</b>, and
///     manage a participant's <b>secure link</b>;
///   - the existing multi-select bulk bar (deactivate / reactivate / change role).
/// Organizer-only and server-enforced (a real Organizer role; an acting-as
/// session can never reach here).
/// </summary>
[Authorize]
public class ParticipantsModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ParticipantBulkOperationService _bulk;
    private readonly ParticipantDeletionService _deletion;
    private readonly ParticipantDeactivationService _cascade;
    private readonly ParticipantSearchService _search;
    private readonly ImpersonationAuditService _audit;
    private readonly CommunityHub.Core.Integrations.CompanyManagerClient _companyManager;
    private readonly ILogger<ParticipantsModel> _logger;
    private readonly TimeProvider _clock;
    private readonly CommunityHub.Core.Settings.FeatureGateService? _gate;
    private readonly CommunityHub.Core.Config.EventEditionConfigLoader? _editionLoader;
    private readonly CommunityHub.Core.Config.EventConfigOptions? _editionOptions;
    private IReadOnlyList<string>? _twoDayIds;

    /// <summary>
    /// §707.50 — the edition's AUTHORITATIVE 2-day ticket class id(s), read once per request.
    /// Empty when no config is wired ⇒ the policy falls back to the name markers, its documented
    /// behaviour, so the flag still resolves rather than disappearing.
    /// </summary>
    private IReadOnlyList<string> TwoDayClassIds()
    {
        if (_twoDayIds is not null) return _twoDayIds;
        try
        {
            _twoDayIds = _editionLoader is null
                ? Array.Empty<string>()
                : _editionLoader.Load(
                        (_editionOptions ?? new CommunityHub.Core.Config.EventConfigOptions()).EventConfigPath)
                    .MasterClassTwoDayClassIds;
        }
        catch
        {
            _twoDayIds = Array.Empty<string>();   // a config read must never break the grid
        }
        return _twoDayIds;
    }

    /// <summary>
    /// §707.37 — is <c>attendee-1day-access</c> ON for this edition? Decides whether the page says
    /// 1-day attendees are excluded (they have no login) or included.
    /// </summary>
    /// <remarks>
    /// 🔒 Read LIVE rather than hard-coded into the copy. The feature is SUSPENDED by default (§242),
    /// but the moment it is switched on 1-day holders are provisioned and DO appear here — at which
    /// point a fixed "1-day attendees are excluded" sentence would be the page stating something
    /// untrue (§326bx, in words instead of a control).
    /// </remarks>
    public bool OneDayAccessEnabled { get; private set; }

    public ParticipantsModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        ParticipantBulkOperationService bulk,
        ParticipantDeletionService deletion,
        ParticipantDeactivationService cascade,
        ParticipantSearchService search,
        ImpersonationAuditService audit,
        CommunityHub.Core.Integrations.CompanyManagerClient companyManager,
        ILogger<ParticipantsModel> logger,
        TimeProvider clock,
        // §707.37 / §707.50 — optional so existing constructions and tests are unchanged; wired by DI.
        CommunityHub.Core.Settings.FeatureGateService? gate = null,
        CommunityHub.Core.Config.EventEditionConfigLoader? editionLoader = null,
        CommunityHub.Core.Config.EventConfigOptions? editionOptions = null)
    {
        _gate = gate;
        _editionLoader = editionLoader;
        _editionOptions = editionOptions;
        _db = db;
        _participant = participant;
        _bulk = bulk;
        _deletion = deletion;
        _cascade = cascade;
        _search = search;
        _audit = audit;
        _companyManager = companyManager;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Resolved sponsor company display names (companyId → name) for this request,
    /// built from Company Manager (public → legal → "Company {id}") so the grid +
    /// filter show the REAL company name instead of "Company {id}" (operator
    /// 2026-06-23). Fail-soft: a lookup miss leaves the id to fall back.
    /// </summary>
    public Dictionary<string, string> CompanyNames { get; private set; } = new();

    /// <summary>
    /// §591 — each SPEAKER's own company (<c>SpeakerProfile.CompanyName</c>), keyed by participant id,
    /// so the grid's Company column is not blank for speakers.
    /// </summary>
    /// <remarks>
    /// Operator 2026-07-28 (flagged low prio): *"if a speaker fills out the company field, it could
    /// be nice to show it here under partcipants"*. The column only ever resolved the SPONSOR
    /// company, so a speaker row stayed blank even when CEH held their employer — Per Larsen reads
    /// "Microsoft" on his speaker profile (§578).
    ///
    /// <para>Loaded in ONE query with the page, never per row: §443 — a per-row lookup is exactly
    /// what made five organizer pages take 6–8 s warm.</para>
    /// </remarks>
    public Dictionary<int, string> SpeakerCompanies { get; private set; } = new();

    /// <summary>The speaker's own company for this participant, or empty when there is none.</summary>
    public string SpeakerCompanyFor(int participantId) =>
        SpeakerCompanies.TryGetValue(participantId, out var n) ? n : string.Empty;

    /// <summary>
    /// §707.27 D — each attendee's company from their WINNING active mirror row, keyed by lowercase
    /// e-mail (the mirror keys on address, not participant id).
    /// </summary>
    public Dictionary<string, string> AttendeeCompanies { get; private set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// §707.50 — the attendee's TICKET KIND ("2-day" / "1-day") from the same winning mirror row,
    /// keyed by lowercase e-mail. Operator 2026-07-30: *"it would be nice to have flag like (2-day or
    /// 1-day here)"*.
    /// </summary>
    /// <remarks>
    /// 🔒 Decided by the ticket CLASS ID, never the display name (§707.35b) — a name is editable in
    /// Zoho, and a rename must not silently relabel people. The class also SURVIVES cancellation,
    /// where <c>TicketStatus</c> is zeroed by the sync (§707.34b), so a cancelled row still reports
    /// what it was.
    /// </remarks>
    public Dictionary<string, string> AttendeeTicketKinds { get; private set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The attendee's ticket kind for this address, or empty when there is no mirror row.</summary>
    public string AttendeeTicketKindFor(string? email) =>
        !string.IsNullOrWhiteSpace(email) && AttendeeTicketKinds.TryGetValue(email, out var k)
            ? k
            : string.Empty;

    /// <summary>The attendee's company for this address, or empty when the mirror holds none.</summary>
    public string AttendeeCompanyFor(string? email) =>
        !string.IsNullOrWhiteSpace(email) && AttendeeCompanies.TryGetValue(email, out var n)
            ? n
            : string.Empty;

    public List<Participant> Participants { get; private set; } = new();
    public bool AccessDenied { get; private set; }
    public string? ActionMessage { get; private set; }
    public bool ActionIsError { get; private set; }

    /// <summary>Distinct sponsor-company ids present in this edition (for the filter dropdown).</summary>
    public List<string> SponsorCompanyIds { get; private set; } = new();

    /// <summary>
    /// Filter: "active+1day" (default), "active", "inactive", "all". Active is lifecycle-correct.
    /// </summary>
    /// <remarks>
    /// §707.38 — the default is <c>active+1day</c> (operator 2026-07-30: *"set the new dropdown as
    /// default"*). "Active only" hid every 1-day ticket holder — inactive BY DESIGN, since they need
    /// no hub access — while "Inactive only" mixed them in with genuinely withdrawn people. Neither
    /// answered the question the page is opened to answer: *who is actually coming?*
    /// </remarks>
    [BindProperty(SupportsGet = true)]
    public string ActiveFilter { get; set; } = "active+1day";

    /// <summary>Filter by persona/role, or null for all personas.</summary>
    [BindProperty(SupportsGet = true)]
    public ParticipantRole? RoleFilter { get; set; }

    /// <summary>Filter by sponsor company id (only meaningful for sponsor rows), or null.</summary>
    [BindProperty(SupportsGet = true)]
    public string? SponsorCompanyFilter { get; set; }

    /// <summary>Optional single persona-flag filter: test | booth | signer | coordinator | speaker.</summary>
    [BindProperty(SupportsGet = true)]
    public string? FlagFilter { get; set; }

    /// <summary>§735 — narrow to ONE ring, or null for every ring.</summary>
    [BindProperty(SupportsGet = true)]
    public CommunityHub.Core.Settings.Ring? RingFilter { get; set; }

    /// <summary>Free-text search over name + email (server-side, case-insensitive).</summary>
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    /// <summary>Sort column key: name | email | persona | status. Default name.</summary>
    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name";

    /// <summary>true = descending. Default false.</summary>
    [BindProperty(SupportsGet = true)]
    public bool Desc { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNo { get; set; } = 1;

    public GridPage Paging { get; private set; }

    public bool NextDescFor(string col) => Sort == col && !Desc;
    public string SortIndicator(string col) => Sort != col ? "" : (Desc ? " ▼" : " ▲");
    public string AriaSort(string col) => Sort != col ? "none" : (Desc ? "descending" : "ascending");

    [BindProperty(SupportsGet = true)]
    public string? Msg { get; set; }

    [BindProperty]
    public List<int> SelectedIds { get; set; } = new();

    [BindProperty]
    public ParticipantRole BulkRole { get; set; }

    /// <summary>Bulk "change ring to" target (operator 2026-06-23).</summary>
    [BindProperty]
    public CommunityHub.Core.Settings.Ring BulkRing { get; set; }

    /// <summary>Resolve a sponsor company id to its display name (fallback chain).</summary>
    /// <summary>
    /// Display name for a sponsor company id — the Company-Manager-resolved name
    /// loaded this request, else the "Company {id}" fallback.
    /// </summary>
    public string CompanyDisplayName(string companyId) =>
        !string.IsNullOrWhiteSpace(companyId) && CompanyNames.TryGetValue(companyId, out var n)
            ? n
            : SponsorCompanyName.Resolve(null, null, null, companyId);

    /// <summary>Lifecycle-correct "is this participant active?" for the status badge.</summary>
    public static bool IsActive(Participant p) => ParticipantActivation.IsActive(p);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me))
        {
            AccessDenied = true;
            return Page();
        }

        if (!string.IsNullOrEmpty(Msg)) ActionMessage = Msg;
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Toggle a single participant's IsActive flag (the cancellation switch).</summary>
    public async Task<IActionResult> OnPostToggleActiveAsync(
        int participantId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) return Forbid();

        var target = await _db.Participants.FirstOrDefaultAsync(
            p => p.Id == participantId && p.EventId == me.EventId, ct);
        if (target is not null)
        {
            // Lifecycle-correct toggle via the ONE cascade service (§253 G1):
            // deactivating cancels the party RSVP, releases the room block, closes
            // open tasks and vacates shift assignments (audited); activating clears
            // BOTH the withdrawal switch and the onboarding gate + the G8 tombstone,
            // and deliberately restores nothing (re-RSVP / re-book).
            if (ParticipantActivation.IsActive(target))
            {
                await _cascade.DeactivateAsync(
                    me.EventId, target.Id, "grid toggle", me.Email, ct);
            }
            else
            {
                // §253: reactivation restores nothing, but dormant dinner/lunch/
                // swag rows (and a still-held MC seat) rejoin the live counts the
                // moment the flag flips — tell the organizer instead of leaving
                // the vendor numbers to change silently.
                var r = await _cascade.ReactivateAsync(me.EventId, target.Id, me.Email, ct);
                if (r.Found && r.AnythingResurrected)
                {
                    var msg = $"{target.FullName} re-activated. Back in the live counts: "
                              + $"{r.DinnerSignupsBackInCounts} dinner, {r.LunchSignupsBackInCounts} lunch, "
                              + $"{r.SwagPreferencesBackInCounts} swag signup(s)"
                              + (r.MasterClassSeatsStillHeld > 0
                                  ? $"; {r.MasterClassSeatsStillHeld} Master-Class seat(s) still held."
                                  : ".")
                              + " Party, room claim, tasks and shifts stay cancelled (re-RSVP / re-book).";
                    return RedirectToPage(new
                    {
                        ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo,
                        Msg = msg,
                    });
                }
            }
        }

        return RedirectToPage(new { ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo });
    }

    /// <summary>
    /// Soft-delete (deactivate) one participant via the deletion service. This is
    /// the safe per-row "Delete" the grid offers by default: the row keeps all its
    /// dependent data but can no longer sign in. Audited.
    /// </summary>
    public async Task<IActionResult> OnPostDeactivateAsync(
        int participantId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) return Forbid();

        var result = await _deletion.DeactivateAsync(me.EventId, participantId, ct);
        var msg = result.Status switch
        {
            ParticipantDeletionService.DeletionStatus.Deactivated =>
                $"{result.FullName} was deactivated and can no longer sign in.",
            ParticipantDeletionService.DeletionStatus.AlreadyInactive =>
                $"{result.FullName} was already inactive.",
            _ => "That participant could not be found in this event.",
        };

        if (result.Found)
        {
            await _audit.RecordAsync(
                me.EventId, ImpersonationActorKind.Organizer,
                actorParticipantId: me.ParticipantId, actorLabel: $"{me.FullName} ({me.Email})",
                targetParticipantId: result.ParticipantId,
                action: ImpersonationAuditService.ActionDeactivate,
                detail: $"Organizer deactivated {result.FullName}.", ct: ct);
        }

        return RedirectToPage(new { ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo, Msg = msg });
    }

    /// <summary>
    /// Per-row "Resend welcome": re-send the welcome email to THIS participant
    /// (operator 2026-06-24). Unlike the old DEV-only bulk SendWelcomeLogin page,
    /// this targets the one clicked participant and works in production. It forces
    /// past the once-ever idempotency ledger; the ring gate + email allowlist still
    /// apply (so a recipient outside the released ring is not actually mailed).
    /// </summary>
    public async Task<IActionResult> OnPostResendWelcomeAsync(
        int participantId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) return Forbid();

        var p = await _db.Participants
            .Where(x => x.EventId == me.EventId && x.Id == participantId)
            .Select(x => new { x.FullName, x.Email, x.Role })
            .FirstOrDefaultAsync(ct);

        string msg;
        if (p is null)
        {
            msg = "That participant could not be found in this event.";
        }
        else if (p.Role == ParticipantRole.Attendee)
        {
            // Attendees get their welcome via the provisioning path, not this one.
            msg = $"{p.FullName} is an attendee — their welcome is sent via attendee provisioning, not here.";
        }
        else
        {
            var welcome = HttpContext.RequestServices
                .GetRequiredService<CommunityHub.Core.Reminders.WelcomeEmailService>();
            var sent = await welcome.SendWelcomeAsync(participantId, ct, force: true);
            msg = sent
                ? $"Welcome email re-sent to {p.FullName} ({p.Email})."
                : $"Welcome to {p.FullName} was not sent — they are outside the released ring or sending is paused.";
        }

        return RedirectToPage(new { ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo, Msg = msg });
    }

    /// <summary>
    /// Per-row Delete. Safe semantics: try a HARD delete (the row was never
    /// engaged — clean its logistics links and remove it); if the participant has
    /// engagement that must not be destroyed, fall back to a SOFT delete
    /// (deactivate) and tell the organizer why. Either outcome is audited so the
    /// removal is never silent.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteAsync(
        int participantId, string? confirmPhrase, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) return Forbid();

        // §334: SERVER-verified typed confirmation. The modal's confirm() only ever ran in the
        // browser — a POST with JavaScript off, or a replayed form, executed the delete outright.
        if (!TypedConfirmation.Matches(confirmPhrase, TypedConfirmation.DeletePhrase))
        {
            return RedirectToPage(new
            {
                ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo,
                Msg = TypedConfirmation.Rejection(
                    TypedConfirmation.DeletePhrase, "delete this participant"),
            });
        }

        // 🔴 §1082 — ORGANIZERS DEACTIVATE; THEY DO NOT HARD-DELETE A PERSON.
        //
        // Operator 2026-08-13: *"i think we should remove the delete buttons for organizers, so they
        // can only deactive"*, and *"we only make things inactive by filter"* — which is what the
        // rest of the hub already does (§502's leaver, §253's tombstones, the whole IsActive /
        // LifecycleState model).
        //
        // 🔑 The hard delete removed dependent rows it classed as "safe", INCLUDING the person's
        // TASKS — on the assumption they *"carry no history worth keeping once the person is gone"*.
        // §1082 settled that completions ARE history: they record what somebody did. Deactivating
        // runs the full §253 G1 cascade instead (party RSVP cancelled, room released, open tasks
        // closed, shifts vacated, audited) and keeps every row.
        //
        // ⚠️ The trade, stated plainly: a genuinely mistaken row — a test participant, a typo'd
        // applicant — now stays in the database as INACTIVE for ever. That is the same trade §502
        // already makes, and test fixtures have their own purge (TestDataCleanupService), which is
        // scoped to rows explicitly marked as test data.
        var actorLabel = $"{me.FullName} ({me.Email})";
        var hard = await _deletion.DeactivateAsync(me.EventId, participantId, ct);

        if (hard.Status is ParticipantDeletionService.DeletionStatus.Deactivated
                        or ParticipantDeletionService.DeletionStatus.AlreadyInactive)
        {
            await _audit.RecordAsync(
                me.EventId, ImpersonationActorKind.Organizer,
                actorParticipantId: me.ParticipantId, actorLabel: actorLabel,
                targetParticipantId: hard.ParticipantId,
                action: ImpersonationAuditService.ActionDelete,
                detail: $"Organizer deactivated {hard.FullName} (rows kept).", ct: ct);
            return RedirectToPage(new
            {
                ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo,
                Msg = $"{hard.FullName} was deactivated — their history is kept.",
            });
        }

        if (hard.Status == ParticipantDeletionService.DeletionStatus.NotFound)
        {
            return RedirectToPage(new
            {
                ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo,
                Msg = "That participant could not be found in this event.",
            });
        }

        // Blocked by engagement → safe fallback: deactivate (soft-delete).
        var soft = await _deletion.DeactivateAsync(me.EventId, participantId, ct);
        await _audit.RecordAsync(
            me.EventId, ImpersonationActorKind.Organizer,
            actorParticipantId: me.ParticipantId, actorLabel: actorLabel,
            targetParticipantId: participantId,
            action: ImpersonationAuditService.ActionDeactivate,
            detail: $"Organizer delete fell back to deactivate for {soft.FullName} "
                    + $"(has {string.Join(", ", hard.BlockingDependencies)}).", ct: ct);

        var why = hard.BlockingDependencies.Count > 0
            ? $" (has {string.Join(", ", hard.BlockingDependencies)})"
            : string.Empty;
        return RedirectToPage(new
        {
            ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo,
            Msg = $"{soft.FullName} has linked data{why}, so they were deactivated "
                  + "instead of permanently deleted.",
        });
    }

    /// <summary>
    /// Start an act-as session: re-issue the cookie as the target participant,
    /// marked organizer-acting-as. Server-enforced organizer-only, and an
    /// already-acting session can never start a nested impersonation.
    /// </summary>
    public async Task<IActionResult> OnPostSwitchToUserAsync(
        int participantId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) return Forbid();

        var target = await _db.Participants.FirstOrDefaultAsync(
            p => p.Id == participantId && p.EventId == me.EventId, ct);
        if (target is null)
        {
            return RedirectToPage(new
            {
                ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo,
                Msg = "That participant could not be found in this event.",
            });
        }
        if (target.Id == me.ParticipantId)
        {
            return RedirectToPage(new
            {
                ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo,
                Msg = "You cannot switch to yourself.",
            });
        }

        var actorLabel = $"{me.FullName} ({me.Email})";
        await ImpersonationSignIn.SignInAsTargetAsync(
            HttpContext, target, ImpersonationActorKind.Organizer,
            actorParticipantId: me.ParticipantId, actorLabel: actorLabel, _clock);

        await _audit.RecordAsync(
            me.EventId, ImpersonationActorKind.Organizer,
            actorParticipantId: me.ParticipantId, actorLabel: actorLabel,
            targetParticipantId: target.Id,
            action: ImpersonationAuditService.ActionStart,
            detail: $"Organizer switched into {target.FullName}'s view.",
            ct: ct);

        // Land on the target's OWN hub home (the role-personalized "My event"
        // view) so the organizer now navigates the WHOLE app as that user —
        // every page, not the 2-field "Modify on behalf" form. This redirect
        // target is the crux of the switch-user fix and is asserted in tests.
        return LocalRedirect(SwitchToUserLandingPath);
    }

    /// <summary>
    /// Where a successful "Switch to user" lands: the hub root, i.e. the target
    /// participant's own role-personalized home — NOT <c>/Organizer/EditOnBehalf</c>.
    /// Exposed so the round-trip test can assert the landing without hard-coding
    /// the literal in two places.
    /// </summary>
    public const string SwitchToUserLandingPath = "/";

    public Task<IActionResult> OnPostBulkDeactivateAsync(CancellationToken ct) =>
        RunBulkAsync((me) => _bulk.DeactivateAsync(me.EventId, SelectedIds, ct),
            verb: "deactivated", ct);

    public Task<IActionResult> OnPostBulkReactivateAsync(CancellationToken ct) =>
        RunBulkAsync((me) => _bulk.ReactivateAsync(me.EventId, SelectedIds, ct),
            verb: "reactivated", ct);

    public Task<IActionResult> OnPostBulkChangeRoleAsync(CancellationToken ct) =>
        RunBulkAsync((me) => _bulk.ChangeRoleAsync(me.EventId, SelectedIds, BulkRole, ct),
            verb: $"moved to {BulkRole}", ct);

    public Task<IActionResult> OnPostBulkSetRingAsync(CancellationToken ct) =>
        RunBulkAsync((me) => _bulk.SetRingAsync(me.EventId, SelectedIds, BulkRing, ct),
            verb: $"set to {CommunityHub.Core.Settings.Rings.Label(BulkRing)}", ct);

    private async Task<IActionResult> RunBulkAsync(
        Func<CurrentParticipant, Task<ParticipantBulkOperationService.BulkResult>> op,
        string verb, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) return Forbid();

        var requested = SelectedIds.Where(id => id > 0).Distinct().Count();
        if (requested == 0)
        {
            return RedirectToPage(new
            {
                ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo,
                Msg = "Pick at least one participant first.",
            });
        }

        var result = await op(me);
        var skipped = result.Skipped(requested);
        var msg = $"{result.Changed} participant(s) {verb}"
            + (result.Matched - result.Changed > 0
                ? $", {result.Matched - result.Changed} already in that state"
                : string.Empty)
            + (skipped > 0 ? $", {skipped} not found" : string.Empty)
            + ".";

        return RedirectToPage(new { ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo, Msg = msg });
    }

    /// <summary>
    /// A real organizer = role Organizer AND not currently acting-as. An
    /// acting-as session (even one impersonating an organizer) must never be
    /// able to drive the organizer grid or start a nested impersonation.
    /// </summary>
    private static bool IsRealOrganizer(CurrentParticipant me) =>
        me.Role == ParticipantRole.Organizer && !me.IsActingAs;

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        // Sponsor-company filter choices: distinct ids on sponsor rows.
        SponsorCompanyIds = await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.Role == ParticipantRole.Sponsor
                        && p.SponsorCompanyId != null)
            .Select(p => p.SponsorCompanyId!)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(ct);

        // One authority for filter + sort: ParticipantSearchService. The page only
        // owns the raw query-string binding + paging; the search rules (status,
        // role, sponsor company, free-text, ordering) live in the service so the
        // grid and the global "find a person" box can never drift apart.
        var request = ParticipantSearchService.Parse(
            Search, RoleFilter, persona: null, ActiveFilter, SponsorCompanyFilter, Sort, Desc);
        var query = _search.Query(eventId, request);

        // Optional persona-flag filter (operator 2026-06-24): narrow to one flag.
        query = FlagFilter switch
        {
            "test"        => query.Where(p => p.IsTestUser),
            "booth"       => query.Where(p => p.IsBoothMember),
            "signer"      => query.Where(p => p.IsSigner),
            "coordinator" => query.Where(p => p.IsEventCoordinator),
            "speaker"     => query.Where(p => p.Role == ParticipantRole.Speaker),
            _             => query,
        };

        // §735 (operator 2026-07-31: *"feature req: add ability to filter on ring here"*). The page
        // already had a BULK "change ring to" and no way to SEE who is in a ring — so an organizer
        // assigning rings could act but not check. §721 is why that matters: the whole morning was
        // spent reasoning about which ring people were in.
        //
        // Kept in the page rather than ParticipantSearchService on purpose: the ring is not part of
        // the shared "find a person" contract the service owns, and the flag filter above sets the
        // precedent for a page-local narrowing.
        if (RingFilter is { } ring)
        {
            query = query.Where(p => p.Ring == ring);
        }

        var matched = await query.CountAsync(ct);
        Paging = GridPaging.Resolve(PageNo, GridPaging.DefaultPageSize, matched);

        Participants = await query
            .Skip(Paging.Skip).Take(Paging.PageSize)
            .ToListAsync(ct);

        // §707.37 — live feature state for the "who is on this page" line. Fail-safe: if the gate is
        // not wired or throws, fall back to the shipped default (suspended), which is the state the
        // page is describing today.
        try
        {
            OneDayAccessEnabled = _gate is not null
                && await _gate.IsFeatureEnabledAsync("attendee-1day-access", eventId, ct);
        }
        catch { OneDayAccessEnabled = false; }

        await ResolveCompanyNamesAsync(eventId, ct);
    }

    /// <summary>
    /// Resolve real company names (Company Manager) for the sponsor company ids the
    /// page references — the filter dropdown + the sponsor rows on this page.
    /// Fail-soft per company so a Company-Manager outage just leaves the "Company
    /// {id}" fallback rather than breaking the grid.
    /// </summary>
    private async Task ResolveCompanyNamesAsync(int eventId, CancellationToken ct)
    {
        var ids = SponsorCompanyIds
            .Concat(Participants
                .Where(p => p.Role == ParticipantRole.Sponsor && !string.IsNullOrWhiteSpace(p.SponsorCompanyId))
                .Select(p => p.SponsorCompanyId!))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // 🔒 §707.27 D — this early return USED to sit here as `if (ids.Count == 0) return;`, which
        // skipped the speaker lookup below as well. An edition with no sponsor company ids on the
        // page would silently lose every OTHER company value too — a column blank for a reason that
        // has nothing to do with the row. It now guards only the sponsor resolution it belongs to.
        if (ids.Count > 0)
        {
        // §443: ONE query against CEH SQL. This loop used to await a Company Manager HTTP call per
        // company — on PROD that was ~12 sequential round trips to the WordPress plugin and made
        // this page 7.4 s warm, on EVERY paging click (the ids come from the edition-wide company
        // list, so paging never reduced them). The names are synced into CEH by
        // SponsorOrderPullService through this same chain, so the local copy is the same value.
        CompanyNames = await SponsorCompanyNameService.ResolveFromLocalAsync(_db, eventId, ids, ct);
        }

        // §591 — the SPEAKERS' own companies, in the same single-query spirit: one read for the
        // whole page keyed by participant id, so the Company column can fall back to it.
        SpeakerCompanies = await _db.SpeakerProfiles.AsNoTracking()
            .Where(sp => sp.EventId == eventId
                         && sp.CompanyName != null
                         && sp.CompanyName != "")
            .Select(sp => new { sp.ParticipantId, sp.CompanyName })
            .ToDictionaryAsync(x => x.ParticipantId, x => x.CompanyName!, ct);

        await ResolveAttendeeCompaniesAsync(eventId, ct);
    }

    /// <summary>
    /// §707.27 D — each ATTENDEE's company, read from their WINNING active mirror row, so the grid's
    /// Company column is not blank for attendees (operator 2026-07-30).
    /// </summary>
    /// <remarks>
    /// 🔒 <b>The §707.22a winning-row rule, not "any row".</b> Several mirror rows per address is
    /// NORMAL — one per ticket — so "the company" is ambiguous unless the tie is broken the same way
    /// everywhere: newest <c>LastSyncedAt</c>, then highest <c>Id</c>. Picking arbitrarily would make
    /// the column flicker between two employers as rows re-sync, which reads as data loss.
    ///
    /// <para>Zoho keeps <c>Attendee.CompanyName</c> current, so this is DISPLAY-ONLY — no schema
    /// change, nothing copied onto <c>Participant</c>. A copy would be a second source of truth that
    /// goes stale the moment the buyer edits the order.</para>
    ///
    /// <para>Scoped to the addresses ON THIS PAGE (§443): the edition holds ~1500 attendee rows and
    /// the grid shows a page at a time.</para>
    /// </remarks>
    private async Task ResolveAttendeeCompaniesAsync(int eventId, CancellationToken ct)
    {
        var emails = Participants
            .Where(p => !string.IsNullOrWhiteSpace(p.Email))
            .Select(p => p.Email.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (emails.Count == 0) return;

        // 🔒 §707.41 — CANCELLED rows are included, and ACTIVE ones simply WIN.
        //
        // The first cut filtered to `MirrorState == Active`, which blanked the column for someone
        // whose ticket had just been cancelled even though the mirror still held their company
        // (operator 2026-07-30, spotting exactly that row). Discarding a value we hold, because of a
        // state change that says nothing about where the person works, is a loss for no gain — and
        // it contradicts the §707.23 rule that CEH KEEPS old references through cancellations and
        // reassignments.
        //
        // Ordering does the work: active-first, then the §707.22a winning-row rule (newest
        // LastSyncedAt, then Id). So a live ticket always beats a cancelled one and a cancelled one
        // is used only when there is nothing live — never a mix, never a flicker.
        // §707.50 — ONE read serves both the Company column and the 2-day/1-day flag, and both pick
        // from the SAME ordering, so the two can never disagree about which ticket they describe.
        // The company filter moved OUT of the query: a row with no company still tells us the ticket
        // KIND, and dropping it here would blank the flag for anyone whose company Zoho does not hold.
        var rows = await _db.Attendees.AsNoTracking()
            .Where(a => a.EventId == eventId && emails.Contains(a.Email.ToLower()))
            .Select(a => new
            {
                a.Email, a.CompanyName, a.TicketClassId, a.TicketClassName,
                a.LastSyncedAt, a.Id, a.MirrorState,
            })
            .ToListAsync(ct);

        var byEmail = rows
            .GroupBy(a => a.Email.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(a => a.MirrorState == CommunityHub.Core.Domain.MirrorState.Active)
                      .ThenByDescending(a => a.LastSyncedAt).ThenByDescending(a => a.Id)
                      .ToList(),
                StringComparer.OrdinalIgnoreCase);

        AttendeeCompanies = byEmail
            .Select(kv => new
            {
                kv.Key,
                // The winning row that actually HAS a company (§707.41: a cancelled row still counts).
                Company = kv.Value.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.CompanyName))?.CompanyName,
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Company))
            .ToDictionary(x => x.Key, x => x.Company!, StringComparer.OrdinalIgnoreCase);

        var twoDayIds = TwoDayClassIds();
        AttendeeTicketKinds = byEmail
            .Select(kv => new
            {
                kv.Key,
                // 🔒 Id-authoritative (§707.35b). The winning row is the one that decides — an active
                // ticket beats a cancelled one, so someone who cancelled a 2-day and bought a 1-day
                // reads as 1-day, which is what they are actually attending on.
                Kind = CommunityHub.Core.Domain.MasterClassTicketPolicy.IncludesMasterClass(
                           kv.Value[0].TicketClassId, kv.Value[0].TicketClassName, twoDayIds)
                       ? "2-day"
                       : (string.IsNullOrWhiteSpace(kv.Value[0].TicketClassId)
                          && string.IsNullOrWhiteSpace(kv.Value[0].TicketClassName))
                           ? string.Empty          // no class at all ⇒ say nothing rather than guess
                           : "1-day",
            })
            .Where(x => x.Kind.Length > 0)
            .ToDictionary(x => x.Key, x => x.Kind, StringComparer.OrdinalIgnoreCase);
    }

    // ===================================================================
    //  §769.11 — EXPORT EVERY PARTICIPANT (operator 2026-08-02: "i need ability to export all
    //  participants and sessions to excel file … i need id,name,email,role")
    // ===================================================================

    /// <summary>
    /// Every participant in the edition as CSV — <b>ALL of them, not the current filter</b>.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>"Export all" means all.</b> The other exports in this area deliberately follow the
    /// on-screen filter; this one deliberately does not, because he asked for the whole list and a
    /// button labelled "all" that silently honoured a filter would hand him a short file he had no
    /// reason to distrust. Inactive people are included for the same reason — a withdrawn
    /// participant is part of "all participants" and their absence would be invisible.
    /// </remarks>
    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var csv = await BuildParticipantExportCsvAsync(me.EventId, ';', ct);
        // UTF-8 BOM so Excel detects the encoding (Danish names).
        var bytes = System.Text.Encoding.UTF8.GetPreamble()
            .Concat(System.Text.Encoding.UTF8.GetBytes(csv)).ToArray();
        return File(bytes, "text/csv", "participants.csv");
    }

    /// <summary>The same rows as <see cref="OnGetExportAsync"/>, as a native .xlsx workbook.</summary>
    public async Task<IActionResult> OnGetExportXlsxAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        // ⚠️ Comma-delimited for the workbook: CsvToXlsx parses RFC-4180, while the CSV download
        // keeps its semicolon contract. ONE row builder, two renderings — so the two files can
        // never drift apart in columns or values.
        var csv = await BuildParticipantExportCsvAsync(me.EventId, ',', ct);
        return File(
            CommunityHub.Export.CsvToXlsx.Build(csv, "Participants"),
            CommunityHub.Export.CsvToXlsx.ContentType,
            "participants.xlsx");
    }

    /// <summary>
    /// The single source of truth for both downloads: id, name, email, role — the four columns he
    /// asked for — plus the active flag, because a list of people that cannot tell you who has
    /// withdrawn is a list you have to check against something else.
    /// </summary>
    private async Task<string> BuildParticipantExportCsvAsync(
        int eventId, char delimiter, CancellationToken ct)
    {
        var rows = await _db.Participants
            .AsNoTracking()
            .Where(p => p.EventId == eventId)
            .OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.FullName, p.Email, p.Role, p.IsActive })
            .ToListAsync(ct);

        var sb = new System.Text.StringBuilder();
        sb.Append("ID").Append(delimiter)
          .Append("Name").Append(delimiter)
          .Append("Email").Append(delimiter)
          .Append("Role").Append(delimiter)
          .Append("Active").AppendLine();

        foreach (var r in rows)
        {
            sb.Append(r.Id).Append(delimiter)
              .Append(CsvField(r.FullName, delimiter)).Append(delimiter)
              .Append(CsvField(r.Email, delimiter)).Append(delimiter)
              .Append(r.Role).Append(delimiter)
              .Append(r.IsActive ? "yes" : "no").AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>RFC-4180 field: quote when it contains the delimiter, a quote or a newline.</summary>
    internal static string CsvField(string? value, char delimiter)
    {
        var v = value ?? string.Empty;
        if (v.IndexOf('"') < 0 && v.IndexOf(delimiter) < 0
            && v.IndexOf('\n') < 0 && v.IndexOf('\r') < 0)
        {
            return v;
        }
        return $"\"{v.Replace("\"", "\"\"")}\"";
    }
}
