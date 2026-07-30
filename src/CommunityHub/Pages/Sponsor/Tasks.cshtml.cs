using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Sponsors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// The sponsor's company-shared task list. Companion page to
/// /Sponsor (which shows the company / contacts / orders details).
/// Tasks are company-scoped per docx "Sponsors to-do" -- any contact
/// of the company may complete or reopen any task.
///
/// Company INFO collection (description / short / social) moved out of this page
/// to the dedicated /Sponsor/CompanyDetails page (operator 2026-06-24); this page
/// is now purely list + toggle + per-task .ics.
/// </summary>
[Authorize]
public class TasksModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;
    private readonly SponsorDeliverablesService _deliverables;
    private readonly CommunityHub.Core.Email.CalendarInviteEmailService _calendarInvite;
    private readonly ILogger<TasksModel> _logger;

    // §603 — the shared artefact uploader, so a task can receive its file IN PLACE instead of
    // sending the sponsor to Company Details. Deliberately the SAME uploader every other surface
    // uses (§494b) rather than a fifth copy of the upload rules.
    private readonly CommunityHub.Uploads.SponsorArtefactUploader _uploader;

    // §684 — the migrated-task body pipeline. Both are null-safe for an unmigrated task: the
    // service returns null and the row falls back to its existing rendering.
    private readonly CommunityHub.Core.Tasks.TaskBodyService _taskBodies;
    private readonly CommunityHub.Core.Tasks.SponsorTaskPlaceholderBuilder _placeholders;

    /// <summary>§687.8 — derives Done from a real webshop order rather than a self-declared tick.</summary>
    private readonly CommunityHub.Core.Tasks.PurchaseTaskReconciler _purchaseTasks;

    /// <summary>§688.12 — the SAME Zoho sync the old Company Details page uses for booth members.</summary>
    private readonly CommunityHub.Core.Integrations.SponsorZohoSyncService _zohoSync;

    // §690 — the per-tier contracted booth-member allowance lives in the sponsor edition config.
    private readonly CommunityHub.Core.Config.SponsorConfigLoader _sponsorConfig;
    private readonly CommunityHub.Core.Config.SponsorConfigOptions _sponsorConfigOptions;

    /// <summary>§688.12 — this company's booth members, for the embedded editor.</summary>
    public List<SponsorBoothMember> BoothMembers { get; private set; } = new();

    /// <summary>
    /// §690 — how many booth members this company's TIER includes by contract, or null when the
    /// edition has not stated one.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>null ≠ 0.</b> An unstated allowance omits the line entirely; it must never render as
    /// "0 included", which would read as "your package includes nobody" — absent is not zero, the
    /// same distinction §555 turns on everywhere else here.
    /// </remarks>
    public int? BoothMembersIncluded { get; private set; }

    /// <summary>
    /// §688.11 — display name of whoever completed each task, by task id. Absent when nobody is
    /// recorded (a pre-existing row, or a completion no person performed).
    /// </summary>
    public IReadOnlyDictionary<int, string> CompletedByNames { get; private set; }
        = new Dictionary<int, string>();

    public TasksModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock,
        SponsorDeliverablesService deliverables,
        CommunityHub.Core.Email.CalendarInviteEmailService calendarInvite,
        CommunityHub.Uploads.SponsorArtefactUploader uploader,
        CommunityHub.Core.Tasks.TaskBodyService taskBodies,
        CommunityHub.Core.Tasks.SponsorTaskPlaceholderBuilder placeholders,
        CommunityHub.Core.Tasks.PurchaseTaskReconciler purchaseTasks,
        CommunityHub.Core.Integrations.SponsorZohoSyncService zohoSync,
        CommunityHub.Core.Config.SponsorConfigLoader sponsorConfig,
        CommunityHub.Core.Config.SponsorConfigOptions sponsorConfigOptions,
        ILogger<TasksModel> logger)
    {
        _purchaseTasks = purchaseTasks;
        _zohoSync = zohoSync;
        _sponsorConfig = sponsorConfig;
        _sponsorConfigOptions = sponsorConfigOptions;
        _db = db;
        _participant = participant;
        _clock = clock;
        _deliverables = deliverables;
        _calendarInvite = calendarInvite;
        _uploader = uploader;
        _taskBodies = taskBodies;
        _placeholders = placeholders;
        _logger = logger;
    }

    /// <summary>§603 — the file posted by an artefact-backed task's inline upload form.</summary>
    [BindProperty] public IFormFile? TaskFile { get; set; }

    /// <summary>Set after an "Add Reminder" POST so the view can show a confirmation.</summary>
    [TempData] public string? CalendarInviteMessage { get; set; }

    public List<ParticipantTask> SponsorTasks { get; private set; } = new();
    public List<Participant> LinkedContacts { get; private set; } = new();
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    /// <summary>True when this sponsor has no company id set (see the view).</summary>
    public bool NoCompanyLink { get; private set; }

    /// <summary>
    /// §135 (operator 2026-06-27): the company's deliverables rollup (X of N done, % + the
    /// still-missing/overdue items with deep links), surfaced at the TOP of My Tasks now that
    /// the standalone /Sponsor/Deliverables nav item is removed (mirrors the speaker §138
    /// readiness move). A pure read-only AGGREGATE of existing data via
    /// <see cref="SponsorDeliverablesService"/>; null when there is no company link or the
    /// rollup could not be built (the view then omits the card).
    /// </summary>
    public SponsorDeliverables? Deliverables { get; private set; }

    /// <summary>
    /// §603 — the artefact KINDS this company has actually uploaded (§68 upload audit), loaded ONCE
    /// per page. The row uses it to DERIVE completion for artefact-backed tasks rather than trusting
    /// a self-declared "Mark complete" (§602.5), which today lets a sponsor close the sponsor-wall
    /// task with no file uploaded — and §598 showed the same lie from the other side, where a
    /// manually deleted file still read as uploaded.
    /// </summary>
    public IReadOnlySet<string> UploadedKinds { get; private set; }
        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// §684.8 — the RENDERED HTML body of every MIGRATED task on this page, by task id. A task
    /// absent from this map is not migrated and the row renders its stored
    /// <c>Description</c> exactly as before.
    /// </summary>
    /// <remarks>
    /// 🔒 Rendered HERE rather than in the row partial because a body's <c>:::data</c> directives
    /// resolve asynchronously (§684.9) — the §666 TV lookup is an HTTP call to the webshop. Doing it
    /// once per page, in the page model, keeps the renderers synchronous and means ten tasks naming
    /// the same source cost one lookup, not ten.
    /// </remarks>
    public IReadOnlyDictionary<int, string> RenderedBodies { get; private set; }
        = new Dictionary<int, string>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        await LoadAsync(me, ct);
        return Page();
    }

    /// <summary>
    /// §603 — receive an artefact-backed task's file, IN THE TASK. This is the conversion the
    /// operator asked for: *"i dont want to use this sponsor upload more prefer to use the newer
    /// method as it handles version, better performance when uploading"*.
    /// </summary>
    /// <remarks>
    /// Completion is NOT set here. It is DERIVED from the recorded upload (§602.5), so the task and
    /// the file can never disagree — which is the whole defect §598 exposed from the other side,
    /// where a deleted file still read as uploaded.
    /// </remarks>
    public async Task<IActionResult> OnPostUploadTaskFileAsync(int taskId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (companyId is null) { NoCompanyLink = true; await LoadAsync(me, ct); return Page(); }

        var task = await _db.Tasks.AsNoTracking().FirstOrDefaultAsync(
            t => t.Id == taskId
                 && t.EventId == me.EventId
                 && t.SourceKey != null
                 && t.SourceKey.StartsWith("sponsor:")
                 && t.SponsorCompanyId == companyId,
            ct);

        var kind = task is null ? null : Core.Organizer.TaskArtefactRules.UploadKindFor(task);
        if (kind is null)
        {
            Error = "That task doesn't take a file upload.";
            await LoadAsync(me, ct);
            return Page();
        }

        var sponsorName = await ResolveSponsorNameAsync(me.EventId, companyId, ct);
        var result = await _uploader.UploadAsync(
            kind, TaskFile!, me.EventId, companyId, sponsorName, me.Email, ct);

        if (result.Ok) Message = $"Thanks — we received {result.FileName}.";
        else Error = result.Error;

        await LoadAsync(me, ct);
        return Page();
    }

    /// <summary>The company's display name for the versioned file name (§593 order of preference).</summary>
    private async Task<string> ResolveSponsorNameAsync(int eventId, string companyId, CancellationToken ct)
    {
        var names = await Core.Integrations.SponsorCompanyNameService
            .ResolveFromLocalAsync(_db, eventId, new[] { companyId }, ct);
        return names.TryGetValue(companyId, out var n) && !string.IsNullOrWhiteSpace(n)
            ? n
            : companyId;
    }

    /// <summary>Mark one of this company's sponsor tasks done, or reopen it.</summary>
    public async Task<IActionResult> OnPostToggleAsync(int taskId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (companyId is null)
        {
            NoCompanyLink = true;
            await LoadAsync(me, ct);
            return Page();
        }

        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.Id == taskId
                 && t.EventId == me.EventId
                 && t.SourceKey != null
                 && t.SourceKey.StartsWith("sponsor:")
                 && t.SponsorCompanyId == companyId,
            ct);

        // 🔒 §603/§602.5 — AN ARTEFACT-BACKED TASK CANNOT BE COMPLETED BY HAND. SERVER-SIDE.
        //
        // The view hides the button, but hiding a control is not enforcing a rule: the POST is still
        // reachable by anyone who kept the page open from before this change, or who replays the
        // form. Its completion follows the uploaded file and nothing else — that is the whole point
        // of retiring "Mark complete" here (operator: "'Mark complete' retired where an artefact
        // exists - agree"). §598 is the same defect from the other side.
        if (task is not null && !Core.Organizer.TaskArtefactRules.AllowsManualCompletion(task))
        {
            Message = "This task completes automatically when you upload the file — "
                      + "there is nothing to tick off by hand.";
            await LoadAsync(me, ct);
            return Page();
        }

        if (task is not null)
        {
            if (task.State == TaskState.Done)
            {
                task.State = TaskState.Open;
                task.CompletedAt = null;
                // §688.11 — clear the attribution with the completion. Leaving a name on a reopened
                // task would claim somebody completed something that is now open again.
                task.CompletedByParticipantId = null;
                Message = "Task reopened.";
            }
            else
            {
                task.State = TaskState.Done;
                task.CompletedAt = _clock.GetUtcNow();
                // §688.11 — a company task can be ticked by any of several contacts; record WHICH.
                task.CompletedByParticipantId = me.ParticipantId;
                Message = "Task marked complete.";
            }
            await _db.SaveChangesAsync(ct);
        }

        await LoadAsync(me, ct);
        return Page();
    }

    /// <summary>
    /// §684.8/§684.9 — render every MIGRATED task's body for this page.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Fail-soft, per task.</b> §682 is the standing lesson: one bad task body took the whole
    /// sponsor order sync down. A task whose body throws here is simply left out of the map, so it
    /// falls back to its stored <c>Description</c> and the OTHER tasks still render. A sponsor
    /// seeing one plain task beats a sponsor seeing an error page.
    /// </remarks>
    private async Task<IReadOnlyDictionary<int, string>> RenderMigratedBodiesAsync(
        int eventId, string companyId, CancellationToken ct)
    {
        var migrated = SponsorTasks.Where(t => _taskBodies.IsMigrated(t)).ToList();
        if (migrated.Count == 0) return new Dictionary<int, string>();

        var placeholders = await _placeholders.BuildAsync(eventId, companyId, ct);
        var rendered = new Dictionary<int, string>();

        foreach (var task in migrated)
        {
            try
            {
                var result = await _taskBodies.RenderAsync(
                    task,
                    CommunityHub.Core.Tasks.TaskBodyFlavour.Html,
                    placeholders,
                    // §670/§676 — the STORED answer, so the body shows "you answered: …" instead of
                    // re-offering the choice as if it had never been made.
                    decision: task.DecisionAnswer,
                    ct: ct);

                if (result is null || string.IsNullOrWhiteSpace(result.Html)) continue;

                rendered[task.Id] = result.Html;
                _placeholders.ReportMissing(result.Definition.Key, result.Missing);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex, "Could not render the migrated body for task {TaskId} ('{Title}'); "
                    + "falling back to the stored description.", task.Id, task.Title);
            }
        }

        return rendered;
    }

    private async Task LoadAsync(CurrentParticipant me, CancellationToken ct)
    {
        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (companyId is null)
        {
            NoCompanyLink = true;
            SponsorTasks = new List<ParticipantTask>();
            return;
        }

        SponsorTasks = await _db.Tasks
            .Where(t => t.EventId == me.EventId
                        && t.SourceKey != null
                        && t.SourceKey.StartsWith("sponsor:")
                        && t.SponsorCompanyId == companyId)
            .OrderBy(t => t.State)
            .ThenBy(t => t.DueDate)
            .ToListAsync(ct);

        // §603 — one query for the whole page; the row reads it per task.
        UploadedKinds = await CommunityHub.Core.Organizer.TaskArtefactRules
            .UploadedKindsAsync(_db, me.EventId, companyId, ct);

        // §687.8 — derive the state of purchase-backed tasks BEFORE rendering, so the row shows the
        // truth on this page load rather than one load behind. Fail-soft: a webshop problem must not
        // stop the task list rendering (§682), and the reconciler itself already declines to change
        // anything it could not verify.
        try
        {
            await _purchaseTasks.ReconcileAsync(SponsorTasks, companyId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Purchase-backed task reconciliation failed for company {CompanyId}.", companyId);
        }

        // §688.12 — the embedded booth-members editor reads this. Active members only; tombstoned
        // rows stay out of sight but keep blocking an add-only re-pull from resurrecting them.
        BoothMembers = await _db.SponsorBoothMembers
            .Where(m => m.EventId == me.EventId
                        && m.SponsorCompanyId == companyId
                        && m.DeletedAt == null)
            .OrderBy(m => m.FirstName).ThenBy(m => m.LastName)
            .ToListAsync(ct);

        // §690 — the contracted allowance for this company's tier. Fail-soft: a missing/unreadable
        // sponsor config must not stop the task list rendering (§682), it just omits the line.
        try
        {
            var tier = await _db.SponsorInfos
                .Where(s => s.EventId == me.EventId && s.SponsorCompanyId == companyId)
                .Select(s => s.Tier)
                .FirstOrDefaultAsync(ct);

            if (tier != CommunityHub.Core.Integrations.BoothTier.None)
            {
                // Lowercased key: System.Text.Json replaces the dictionary instance and drops the
                // initializer's OrdinalIgnoreCase comparer, so a PascalCase lookup misses.
                var tiers = _sponsorConfig.Load(_sponsorConfigOptions.SponsorConfigPath)
                    .BoothWallSpecs?.Tiers;

                if (tiers is not null
                    && tiers.TryGetValue(tier.ToString().ToLowerInvariant(), out var spec)
                    && spec.BoothMembersIncluded > 0)
                {
                    BoothMembersIncluded = spec.BoothMembersIncluded;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Could not read the booth-member allowance for company {CompanyId}.", companyId);
        }

        // §688.11 — resolve "by whom" for the completed tasks, in ONE query for the page.
        var completerIds = SponsorTasks
            .Where(t => t.CompletedByParticipantId is not null)
            .Select(t => t.CompletedByParticipantId!.Value)
            .Distinct()
            .ToList();

        if (completerIds.Count > 0)
        {
            var names = await _db.Participants
                .Where(p => completerIds.Contains(p.Id))
                .Select(p => new { p.Id, p.FullName, p.Email })
                .ToDictionaryAsync(
                    p => p.Id,
                    p => string.IsNullOrWhiteSpace(p.FullName) ? p.Email : p.FullName,
                    ct);

            CompletedByNames = SponsorTasks
                .Where(t => t.CompletedByParticipantId is int id && names.ContainsKey(id))
                .ToDictionary(t => t.Id, t => names[t.CompletedByParticipantId!.Value]);
        }

        RenderedBodies = await RenderMigratedBodiesAsync(me.EventId, companyId, ct);

        // §135: build the deliverables rollup for the top of the page. Read-only AGGREGATE;
        // tolerate a build hiccup so the task list still renders (the view omits the card
        // when Deliverables is null).
        try
        {
            var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
            Deliverables = await _deliverables.BuildForCompanyAsync(me.EventId, companyId, today, ct: ct);
        }
        catch { Deliverables = null; }

        // §481 excluded booth members by filtering on `IsEventCoordinator`. §496 (operator
        // 2026-07-28: "i just made an extra evnt coordinator for 2linkit - but it is not showing")
        // shows why that was the wrong instrument:
        //
        //   IsEventCoordinator is set at sync time from Company Manager's
        //   EventCoordinationDefaultContactUserId — a SINGLE default per company. A second event
        //   coordinator can therefore NEVER carry the flag, so filtering on it hid every
        //   coordinator but one, permanently. I flagged that risk when making §481; it bit.
        //
        // The rule we actually want is "the company's contacts, excluding people who are only
        // there to staff the booth" — so exclude BOOTH MEMBERS by identity instead of relying on a
        // flag that marks at most one person. Booth members are their own entity, matched on email
        // (the key Company Manager provisions them by).
        var boothMemberEmails = await _db.SponsorBoothMembers
            .Where(m => m.EventId == me.EventId && m.SponsorCompanyId == companyId
                        && m.DeletedAt == null && m.Email != null)
            .Select(m => m.Email!.ToLower())
            .ToListAsync(ct);

        LinkedContacts = await _db.Participants
            .Where(p => p.EventId == me.EventId
                        && p.SponsorCompanyId == companyId
                        && p.Role == ParticipantRole.Sponsor
                        && p.IsActive
                        && !boothMemberEmails.Contains(p.Email.ToLower()))
            .OrderBy(p => p.FullName)
            .ToListAsync(ct);
    }

    /// <summary>
    /// §670 / §676 / §684.13 — record the sponsor's answer to a two-button decision task.
    /// </summary>
    /// <remarks>
    /// <para>Operator §670: <i>"mark complete will automatically be set once they choose one of the
    /// 2 buttons, so no need to show that"</i> — so BOTH answers complete the task, and the manual
    /// tick is gone (the definition declares <c>Completion.Decision</c>, which
    /// <c>TaskArtefactRules</c> reads).</para>
    ///
    /// <para>🔒 <b>Declining is RECORDED, not a dismissal.</b> The answer lands in its own column
    /// precisely so an organizer can tell "said no" from "never answered" — today both look like
    /// "not complete", which is the gap §670 asks to close.</para>
    ///
    /// <para>🔒 <b>Company-scoped, like every other sponsor task action on this page.</b> The lookup
    /// repeats the `SourceKey`-prefix + company filter rather than trusting the posted id: a task id
    /// is a guessable integer, and one sponsor answering another sponsor's task would be a silent
    /// cross-tenant write.</para>
    /// </remarks>
    public async Task<IActionResult> OnPostDecisionAsync(
        int taskId, string decision, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (companyId is null) return RedirectToPage();

        // Posted as "{decisionKey}:{accept|decline}" — the key travels so a body carrying two
        // decisions one day cannot have its answers crossed.
        var answer = decision?.Split(':').LastOrDefault()?.Trim().ToLowerInvariant() switch
        {
            "accept" => (TaskDecisionAnswer?)TaskDecisionAnswer.Accepted,
            "decline" => TaskDecisionAnswer.Declined,
            _ => null,
        };
        if (answer is null)
        {
            Error = "That answer was not recognised — please try again.";
            await LoadAsync(me, ct);
            return Page();
        }

        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.Id == taskId
                 && t.EventId == me.EventId
                 && t.SourceKey != null
                 && t.SourceKey.StartsWith("sponsor:")
                 && t.SponsorCompanyId == companyId, ct);

        if (task is not null)
        {
            task.DecisionAnswer = answer;
            task.DecisionAnsweredAt = _clock.GetUtcNow();
            // §688.11 — a company task can be answered by any of several contacts, so record WHICH.
            task.CompletedByParticipantId = me.ParticipantId;

            // Both answers complete it. 🔒 ClosedReason stays NULL: that column labels closures the
            // SYSTEM made on someone's behalf (§332), and this is the sponsor answering. Marking a
            // decline as "abandoned" would drop it out of the completion ratios it belongs in.
            task.State = TaskState.Done;
            task.CompletedAt ??= _clock.GetUtcNow();

            await _db.SaveChangesAsync(ct);

            Message = answer == TaskDecisionAnswer.Accepted
                ? "Thanks — we have noted that you would like to take part."
                : "Noted — we have recorded that this one is not for you. You can change it any time.";
        }

        await LoadAsync(me, ct);
        return Page();
    }

    /// <summary>
    /// §688.12 / §671 — add a booth member FROM THE TASK, without leaving it.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Deliberately the SAME behaviour as `/Sponsor/CompanyDetails`, including the
    /// immediate Zoho push and the error surfaced to the sponsor.</b> §689 found those two things
    /// existed ONLY on the old page: the wizard path wrote the row and left Zoho to a 10-minute
    /// job, with a failure invisible to the person who typed it. Embedding the editor while
    /// dropping them would have made the replacement quietly worse than the thing it replaces —
    /// and §689.1 is explicit that the new home must be PREFERABLE before the old one is
    /// decommissioned.</para>
    ///
    /// <para>Re-adding a removed member REVIVES the tombstoned row rather than duplicating it,
    /// matching the old page exactly.</para>
    /// </remarks>
    public async Task<IActionResult> OnPostAddBoothMemberAsync(
        string? firstName, string? lastName, string? email, BoothMemberRole role, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (companyId is null) return RedirectToPage();

        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName)
            || string.IsNullOrWhiteSpace(email))
        {
            Error = "First name, last name and email are required for a booth member.";
        }
        else if (!email.Contains('@') || !email.Contains('.'))
        {
            Error = "Please enter a valid email address.";
        }
        else
        {
            var em = email.Trim();
            var existing = await _db.SponsorBoothMembers.FirstOrDefaultAsync(
                m => m.EventId == me.EventId && m.SponsorCompanyId == companyId && m.Email == em, ct);

            if (existing is { DeletedAt: null })
            {
                Error = "A booth member with that email already exists.";
            }
            else
            {
                if (existing is not null)
                {
                    existing.DeletedAt = null;                       // revive, never duplicate
                    existing.FirstName = firstName.Trim();
                    existing.LastName = lastName.Trim();
                    existing.Role = role;
                    existing.UpdatedAt = _clock.GetUtcNow();
                    existing.SyncedToZoho = false;
                }
                else
                {
                    _db.SponsorBoothMembers.Add(new SponsorBoothMember
                    {
                        EventId = me.EventId,
                        SponsorCompanyId = companyId,
                        FirstName = firstName.Trim(),
                        LastName = lastName.Trim(),
                        Email = em,
                        Role = role,
                    });
                }

                await _db.SaveChangesAsync(ct);
                Message = "Booth member added. " + await SyncBoothMembersAsync(me.EventId, companyId, ct);
            }
        }

        await LoadAsync(me, ct);
        return Page();
    }

    /// <summary>
    /// §688.12 — remove a booth member from the task. 🔒 <b>This is the capability §689 flagged as
    /// existing ONLY on the old page</b>, so it had to come across before that page can be retired.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteBoothMemberAsync(int memberId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (companyId is null) return RedirectToPage();

        var member = await _db.SponsorBoothMembers.FirstOrDefaultAsync(
            m => m.Id == memberId
                 && m.EventId == me.EventId
                 && m.SponsorCompanyId == companyId
                 && m.DeletedAt == null, ct);

        if (member is not null)
        {
            // Member delete ONLY — never the exhibitor/sponsor RECORD (§56). Zoho first, fail-soft:
            // a Zoho outage must not stop the hub delete, or the sponsor cannot correct a typo.
            await _zohoSync.DeleteBoothMemberAsync(me.EventId, companyId, member.Email, ct);

            // Tombstone rather than hard-delete: the tombstone is what stops an add-only re-pull
            // resurrecting someone the sponsor deliberately removed.
            member.DeletedAt = _clock.GetUtcNow();
            member.UpdatedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            Message = "Booth member removed.";
        }

        await LoadAsync(me, ct);
        return Page();
    }

    /// <summary>Push booth members to Zoho and describe the outcome IN THE SPONSOR'S WORDS.</summary>
    private async Task<string> SyncBoothMembersAsync(int eventId, string companyId, CancellationToken ct)
    {
        var name = await _db.SponsorInfos
            .Where(s => s.EventId == eventId && s.SponsorCompanyId == companyId)
            .Select(s => s.CompanyName)
            .FirstOrDefaultAsync(ct);

        var result = await _zohoSync.SyncBoothMembersAsync(eventId, companyId, name ?? string.Empty, ct);

        return !result.Enabled ? "(Event-system sync is not enabled in this environment.)"
            : result.Error is not null ? result.Error
            : $"Synced — {result.AddedToZoho} added, {result.PulledFromZoho} pulled.";
    }

    private async Task<string?> GetCompanyIdAsync(int participantId, CancellationToken ct) =>
        await _db.Participants
            .Where(p => p.Id == participantId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// §193 "Add Reminder": e-mail the signed-in sponsor contact a calendar
    /// INVITATION for one company task's due date (replacing the old "Download
    /// .ics"). Scoped to the contact's company; the invite goes to their chosen
    /// calendar / override e-mail. Stable UID so a re-send updates the entry.
    /// </summary>
    public async Task<IActionResult> OnPostAddReminderAsync(int taskId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (companyId is null) return RedirectToPage();

        var task = await _db.Tasks.AsNoTracking().FirstOrDefaultAsync(
            t => t.Id == taskId
                 && t.EventId == me.EventId
                 && t.SourceKey != null
                 && t.SourceKey.StartsWith("sponsor:")
                 && t.SponsorCompanyId == companyId, ct);
        if (task is null)
        {
            CalendarInviteMessage = "That task could not be found.";
            return RedirectToPage();
        }

        var due = task.DueDate ?? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(7));
        var start = new DateTimeOffset(due.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        // 🔒 §684.21 — THE REGRESSION GATE. A MIGRATED task carries Description = null (§684.14:
        // rendered prose lives nowhere), so this call site had to learn the new path in the same
        // change as the seeder. Left alone it would have fallen through to the generic
        // "Deadline from your Event Hub." and silently dropped the entire body from the calendar
        // entry — a task nobody is reminded about properly is worse than an ugly task, and it would
        // have looked like nothing was wrong.
        //
        // §684.10 — the PLAIN-TEXT flavour, so a button arrives as "label: url" (there is nothing
        // to click in a calendar entry) and no emphasis marker leaks (§685).
        string desc;
        try
        {
            var rendered = await _taskBodies.RenderAsync(
                task,
                CommunityHub.Core.Tasks.TaskBodyFlavour.PlainText,
                await _placeholders.BuildAsync(me.EventId, companyId, ct),
                ct: ct);

            desc = rendered is not null && !string.IsNullOrWhiteSpace(rendered.Html)
                ? rendered.Html
                // Not migrated: the legacy path, unchanged. Strip the **bold**/__underline__/
                // [label](url) markup so the markers don't leak literally into the entry.
                : string.IsNullOrWhiteSpace(task.Description)
                    ? "Deadline from your Event Hub."
                    : CommunityHub.Core.Email.TaskMarkup.ToPlainText(task.Description);
        }
        catch (Exception ex)
        {
            // Fail-soft (§682): a body that will not render must not stop the sponsor getting the
            // DATE in their calendar, which is the point of the invite.
            _logger.LogError(
                ex, "Could not render the migrated body for task {TaskId} into a calendar invite.",
                task.Id);
            desc = string.IsNullOrWhiteSpace(task.Description)
                ? "Deadline from your Event Hub."
                : CommunityHub.Core.Email.TaskMarkup.ToPlainText(task.Description);
        }

        try
        {
            var sent = await _calendarInvite.SendItemInviteAsync(
                me.ParticipantId,
                uid: $"sponsor-task-{task.Id}@eventhub.expertslive.dk",
                summary: task.Title,
                description: desc,
                location: null,
                start: start,
                end: start.AddDays(1),
                allDay: true,
                fileName: "reminder.ics",
                introHtml: $"Here is a reminder for <strong>{System.Net.WebUtility.HtmlEncode(task.Title)}</strong>, due {due:d MMM yyyy}.",
                ct: ct);
            CalendarInviteMessage = sent
                ? sent.Confirmation("Reminder")
                : "Calendar invitations are turned off for this event.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Add Reminder failed for sponsor task {TaskId}", taskId);
            CalendarInviteMessage = "We couldn't send that reminder just now — please try again.";
        }

        return RedirectToPage();
    }
}
