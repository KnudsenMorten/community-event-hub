using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using CommunityHub.Core.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms.Steps;

/// <summary>§400 — one task/deadline row shown on the wizard's deadlines step.</summary>
/// <param name="TaskId">Used by the per-task "add to my calendar" button.</param>
/// <param name="HasDueDate">Only a DATED task can become a calendar entry.</param>
/// <param name="Description">§449 — shown in a fold-out under the title so the reader can learn
/// what the item means WITHOUT leaving the last wizard step.</param>
public sealed record DeadlineRow(
    int TaskId, string Title, DateOnly? DueDate, bool Done, bool Overdue, bool HasDueDate, string? Link,
    string? Description = null);

/// <summary>
/// §400 — the render model for the "Your tasks &amp; deadlines" wizard step.
/// </summary>
public sealed class DeadlinesFormModel
{
    /// <summary>Everything still open, soonest deadline first, undated last.</summary>
    public IReadOnlyList<DeadlineRow> Pending { get; private set; } = Array.Empty<DeadlineRow>();

    /// <summary>Already completed — shown so the list is the whole picture, not just the nagging half.</summary>
    public IReadOnlyList<DeadlineRow> Completed { get; private set; } = Array.Empty<DeadlineRow>();

    /// <summary>True when at least one PENDING row carries a date (so "add them all" means something).</summary>
    public bool AnyDated => Pending.Any(p => p.HasDueDate);

    internal void Fill(IReadOnlyList<DeadlineRow> pending, IReadOnlyList<DeadlineRow> completed)
    {
        Pending = pending;
        Completed = completed;
    }
}

/// <summary>
/// §400 — load for the wizard's "Your tasks &amp; deadlines" step (operator 2026-07-26: <i>"we need
/// to have 1 extra step in the get started wizard which lists all tasks + deadlines outside of the
/// get started wizard and explain that you will get reminders … Calendar Reminder per task + Send
/// Calendar invites for all tasks (one button) … also explain that people will get reminders until
/// completed and marked as completed"</i>).
///
/// <para><b>Read-only by design.</b> The step SHOWS what is coming and offers calendar invites; it
/// never marks anything done. Its job is that nobody finishes Get Started believing there is nothing
/// left — the dated obligations (slide uploads, travel invoice, promotion) live OUTSIDE the wizard
/// and were previously invisible at exactly the moment someone felt finished.</para>
///
/// <para><b>Same source as My Tasks.</b> Rows come from <see cref="ParticipantChecklistBuilder"/>,
/// so this step, the hub card and /Tasks cannot disagree about what is outstanding.</para>
/// </summary>
public sealed class DeadlinesFormService : IWizardFormService
{
    private readonly CommunityHubDbContext _db;
    private readonly ParticipantChecklistBuilder _checklist;

    public DeadlinesFormService(CommunityHubDbContext db, ParticipantChecklistBuilder checklist)
    {
        _db = db;
        _checklist = checklist;
    }

    public async Task<DeadlinesFormModel> LoadAsync(
        int eventId, int participantId, CancellationToken ct)
    {
        var model = new DeadlinesFormModel();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var checklist = await _checklist.BuildAsync(eventId, participantId, ct);

        // ChecklistRow already carries the deep-link and the overdue count, so nothing is
        // recomputed here — this step must agree with My Tasks by construction, not by copying
        // its rules.
        DeadlineRow Map(ChecklistRow i, bool done) => new(
            i.Id,
            i.Title,
            i.DueDate,
            done,
            Overdue: !done && i.DaysOverdue is > 0,
            HasDueDate: i.DueDate is not null,
            Link: i.Link,
            Description: i.Description);

        // §410 — only tasks that live OUTSIDE Get Started. §173e mirrors every wizard STEP with a
        // task, so an unfiltered checklist listed the person's own wizard steps back at them one
        // screen after they filled them in (an attendee's only tasks are party-form +
        // masterclass-form, i.e. entirely mirrors).
        var outside = await OutsideKeysAsync(eventId, participantId, ct);
        bool Keep(ChecklistRow i) => outside.Contains(i.Id);

        // Dated first and soonest-first — the order someone actually needs to act in. Undated items
        // still appear, at the end: "no date" is not "not required".
        var pending = checklist.Pending
            .Where(Keep)
            .Select(i => Map(i, done: false))
            .OrderBy(r => r.DueDate is null)
            .ThenBy(r => r.DueDate)
            .ToList();

        var completed = checklist.Completed
            .Where(Keep)
            .Select(i => Map(i, done: true))
            .OrderBy(r => r.DueDate is null)
            .ThenBy(r => r.DueDate)
            .ToList();

        model.Fill(pending, completed);
        return model;
    }

    /// <summary>
    /// Nothing to persist — the step is informational. Always advances, so it can never block
    /// completion of the wizard (§148 NotRelevant is for steps that do not apply; this one always
    /// applies, it simply has no answer to save).
    /// </summary>
    public Task<WizardStepOutcome> SaveAsync(
        DeadlinesFormModel model, ModelStateDictionary modelState, CancellationToken ct) =>
        Task.FromResult(WizardStepOutcome.Advance);

    /// <summary>The fields a calendar invitation needs from a dated task.</summary>
    public sealed record DeadlineTask(int Id, string Title, string? Description, DateOnly DueDate);

    /// <summary>
    /// Every DATED, still-open task this participant can see — what "invite me to all" means.
    /// </summary>
    public async Task<List<int>> DatedPendingTaskIdsAsync(
        int eventId, int participantId, CancellationToken ct)
    {
        var sponsorCompanyId = await SponsorCompanyIdAsync(participantId, ct);
        return await Visible(eventId, participantId, sponsorCompanyId)
            .OrderBy(t => t.DueDate)
            .Select(t => t.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// One dated task, or null when it is not this participant's to see. The per-task button and
    /// the "all" button resolve through the SAME predicate, so a task that is listed can always be
    /// invited — the two cannot drift apart.
    /// </summary>
    public async Task<DeadlineTask?> DatedTaskAsync(
        int eventId, int participantId, int taskId, CancellationToken ct)
    {
        var sponsorCompanyId = await SponsorCompanyIdAsync(participantId, ct);
        return await Visible(eventId, participantId, sponsorCompanyId)
            .Where(t => t.Id == taskId)
            .Select(t => new DeadlineTask(t.Id, t.Title, t.Description, t.DueDate!.Value))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The sponsor company whose COMPANY tasks this participant should see on their own deadlines
    /// step — or null when company tasks are not theirs.
    ///
    /// <para>§456 (operator 2026-07-27): *"test-speaker-exhibitor-mainday lists all sponsor tasks in
    /// the get started for the speaker … only task relevant for a sponsor (exhibitor) speaker is the
    /// task for upload final presentation"*. A SPEAKER who works for an exhibitor carries that
    /// company's <c>SponsorCompanyId</c>, so the old unconditional lookup pulled the company's booth
    /// deliverables — booth layout, sponsor wall, TV rental, "Initial onboarding of sponsor" — into
    /// the speaker's PERSONAL onboarding. Twelve items, none of them his.</para>
    ///
    /// <para>Company tasks belong to the people acting for the company, so the link is now made only
    /// for a <see cref="ParticipantRole.Sponsor"/> participant. A speaker keeps exactly what is
    /// ASSIGNED to them. Deliberately fixed HERE rather than at the two call sites: both
    /// <see cref="Visible"/> and <see cref="OutsideKeysAsync"/> resolve through this one method, so
    /// the listing and the calendar-invite gate cannot drift apart — the §428 lesson.</para>
    /// </summary>
    private async Task<string?> SponsorCompanyIdAsync(int participantId, CancellationToken ct)
    {
        var p = await _db.Participants
            .Where(x => x.Id == participantId)
            .Select(x => new { x.Role, x.SponsorCompanyId })
            .FirstOrDefaultAsync(ct);
        return p is { Role: ParticipantRole.Sponsor } ? p.SponsorCompanyId : null;
    }

    /// <summary>
    /// The SAME visibility rule <see cref="ParticipantChecklistBuilder"/> uses — assigned to me, or
    /// belonging to my sponsor company — narrowed to still-open and dated. Written once here
    /// because a sponsor sees company tasks on the step, and an ownership-only check would have
    /// listed them and then refused to send the invitation.
    /// </summary>
    // §1081 — VisibleTo is the shared "assigned to them OR their sponsor company" predicate.
    private IQueryable<ParticipantTask> Visible(int eventId, int participantId, string? sponsorCompanyId) =>
        _db.Tasks.Where(t => t.EventId == eventId)
                 .VisibleTo(participantId, sponsorCompanyId)
                 .Where(t => t.State != TaskState.Done && t.DueDate != null);

    /// <summary>
    /// §410 — the ids of this participant's tasks that live OUTSIDE the wizard. Evaluated in memory
    /// because <see cref="OutsideWizardTasks.IsOutsideWizard"/> is a C# predicate, not translatable
    /// SQL; the set is one person's tasks, so it is small by construction.
    /// </summary>
    private async Task<HashSet<int>> OutsideKeysAsync(int eventId, int participantId, CancellationToken ct)
    {
        var sponsorCompanyId = await SponsorCompanyIdAsync(participantId, ct);
        var rows = await _db.Tasks.AsNoTracking()
            .Where(t => t.EventId == eventId)
            .VisibleTo(participantId, sponsorCompanyId)
            .Select(t => new { t.Id, t.SourceKey })
            .ToListAsync(ct);

        return rows.Where(r => OutsideWizardTasks.IsOutsideWizard(r.SourceKey))
                   .Select(r => r.Id)
                   .ToHashSet();
    }

    /// <summary>
    /// §410 — does this participant have ANY task outside the wizard? The wizard services call this
    /// to decide whether the deadlines step is offered at all: a role with nothing outside Get
    /// Started (attendee, media, event partner, most volunteers) should not see an empty step.
    /// </summary>
    public async Task<bool> HasAnyOutsideWizardAsync(int eventId, int participantId, CancellationToken ct) =>
        (await OutsideKeysAsync(eventId, participantId, ct)).Count > 0;
}
