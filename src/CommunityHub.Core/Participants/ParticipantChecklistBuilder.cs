using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Participants;

/// <summary>
/// One row of the unified participant "what do I still owe" checklist — a single
/// <see cref="ParticipantTask"/> flattened for display. <see cref="DaysOverdue"/>
/// is non-null only for a still-open task past its due date.
/// </summary>
public sealed record ChecklistRow(
    int Id,
    string Title,
    DateOnly? DueDate,
    TaskState State,
    int? DaysOverdue,
    /// <summary>Deep-link to the page that completes this task, or null.</summary>
    string? Link,
    /// <summary>§449 — the task's own description, so a surface can EXPLAIN an item IN PLACE
    /// instead of sending the reader away to find out what it means. Optional (defaulted) so
    /// existing constructions compile unchanged.</summary>
    string? Description = null);

/// <summary>
/// The participant's unified checklist: the pending + completed split, the open
/// count, and whether anything is overdue. ONE shape so the Hub home, the Tasks
/// page and the attendee My-event surface all render the SAME "what's still
/// needed" view (REQUIREMENTS §21 Participant [H] / Top-8 #7) instead of competing
/// landing pages.
/// </summary>
public sealed record ParticipantChecklist(
    IReadOnlyList<ChecklistRow> Pending,
    IReadOnlyList<ChecklistRow> Completed)
{
    public int OpenCount => Pending.Count;
    public int OverdueCount => Pending.Count(r => r.DaysOverdue is > 0);
    public bool AllComplete => Pending.Count == 0;
}

/// <summary>
/// Builds the unified participant checklist from the existing
/// <see cref="ParticipantTask"/> model (REQUIREMENTS Top-8 #7). It covers BOTH
/// personally-assigned tasks AND a sponsor contact's company-scoped tasks
/// (AssignedParticipantId = null, SponsorCompanyId set), so a sponsor's checklist
/// is not silently "all complete" while /Sponsor/Tasks shows pending work.
///
/// The same SourceKey → form-page mapping the Hub used is centralised here so the
/// deep-links stay consistent everywhere the checklist renders. Before reading, it
/// runs <see cref="FormTaskReconciler"/> so that every surface (Hub home, Tasks
/// page, attendee My-event) auto-completes tasks whose form DATA is already
/// present; that reconcile is the only write — the checklist projection itself is a
/// pure read over the DB.
/// </summary>
public sealed class ParticipantChecklistBuilder
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly FormTaskReconciler _reconciler;

    public ParticipantChecklistBuilder(
        CommunityHubDbContext db, TimeProvider clock, FormTaskReconciler reconciler)
    {
        _db = db;
        _clock = clock;
        _reconciler = reconciler;
    }

    public async Task<ParticipantChecklist> BuildAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        // Auto-complete tasks from already-submitted form data first, so every
        // checklist surface reflects the true state (idempotent; no-op when nothing
        // needs changing).
        await _reconciler.ReconcileAsync(eventId, participantId, ct);

        var sponsorCompanyId = await _db.Participants
            .Where(p => p.Id == participantId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);

        // §1081 — the shared predicate: assigned to them, OR their sponsor company's shared row.
        // Hand-written here until four copies existed and a fifth (TaskReminderBuilder) forgot it.
        // 🔴 §1082 — SYSTEM-CLOSED ROWS ARE INVISIBLE HERE, and that is the half that makes
        // "close instead of delete" safe. A retired row is State=Done, so without this filter every
        // "Completed" list would absorb it and the participant would be shown work they never did —
        // a sponsor congratulated for nine tasks they never saw. It is not open either: the question
        // simply stopped being asked, so it belongs in neither list.
        // 🔑 ClosedReason is NULL on everything a person actually completed (see /Sponsor/Tasks), so
        // "is it set" is exactly the right test.
        var all = await _db.Tasks
            .Where(t => t.EventId == eventId)
            .VisibleTo(participantId, sponsorCompanyId)
            .Where(TaskClosure.NotSystemClosed)
            .Select(t => new { t.Id, t.Title, t.DueDate, t.State, t.SourceKey, t.Description })
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

        ChecklistRow Row(
            int id, string title, DateOnly? due, TaskState state, string? sourceKey, string? description)
        {
            int? overdue = (due is not null && due < today && state != TaskState.Done)
                ? today.DayNumber - due.Value.DayNumber
                : (int?)null;
            return new ChecklistRow(
                id, title, due, state, overdue, LinkForTask(id, sourceKey), description);
        }

        var pending = all
            .Where(t => t.State != TaskState.Done)
            .Select(t => Row(t.Id, t.Title, t.DueDate, t.State, t.SourceKey, t.Description))
            // Overdue first, then by due date, then title.
            .OrderByDescending(r => r.DaysOverdue is > 0)
            .ThenBy(r => r.DueDate ?? DateOnly.MaxValue)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var completed = all
            .Where(t => t.State == TaskState.Done)
            .Select(t => Row(t.Id, t.Title, t.DueDate, t.State, t.SourceKey, t.Description))
            .OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ParticipantChecklist(pending, completed);
    }

    /// <summary>
    /// 🔒 §708.4 — WHERE A TASK'S TITLE LINKS. <b>A task links to the TASK.</b>
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>The bug this closes, third occurrence.</b> Operator 2026-07-30, with a screenshot
    /// of PROD Home as a sponsor: <i>"tasks in HOME for sponsor (and probably other roles) take to
    /// the wrong tasks … for example i see links to old company details booth member"</i>. §674 and
    /// §679 were the same complaint about the same method — each time the fix was to add or move ONE
    /// keyword, and each time the next task with an unlucky title broke again.</para>
    ///
    /// <para><b>Why keyword-guessing could never work here.</b> The <c>sponsor:</c> branch matched
    /// substrings of the title slug, so <i>"Validate booth members have lead scan app + exhibitor
    /// guide"</i> matched <c>member</c> and landed on the booth-MEMBERS section of a page with
    /// nothing to do with a lead-scan app — the row he arrowed. <i>"Initial onboarding of sponsor"</i>
    /// matched <c>onboard</c>, and <i>"Register booth members"</i> matched <c>member</c>: both landed
    /// on <c>/Sponsor/CompanyDetails</c>, whose nav entry §707.44 already REMOVED and which §689.1 is
    /// retiring. This is the same string-derived-join failure as §707.43 and §707.54a.</para>
    ///
    /// <para>🔑 <b>And the destination is now obsolete anyway.</b> Since §684/§688.12 a registry task
    /// renders its own body, its own buttons, its embedded booth-member editor and its own upload
    /// control ON ITS ROW. Sending its title somewhere else was right when the row was a bare
    /// sentence; today the row IS the place the work is done, so anywhere else is strictly worse.</para>
    ///
    /// <para>The FORM-OWNED tasks below are unchanged: <c>hotel-form:</c>, <c>signal:</c>, the §173e
    /// wizard-step rows and friends have no row of their own to land on, and their form IS the task.
    /// Only the two families that own a rendered row moved.</para>
    /// </remarks>
    public static string? LinkForTask(int taskId, string? sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return null;

        // 🔒 Anchored at the row, not the top of the list. A task page carries a dozen rows with the
        // completed ones collapsed inside a <details>, so "/Sponsor/Tasks" alone still leaves the
        // reader hunting — which is the weaker half of the same complaint (§674's fall-through).
        // _TaskListPanel stamps the matching id and opens the row.
        if (sourceKey.StartsWith("sponsor:", StringComparison.Ordinal))
            return $"/Sponsor/Tasks{TaskAnchor(taskId)}";
        if (sourceKey.StartsWith("speakerdl:", StringComparison.Ordinal))
            return $"/Speaker/Tasks{TaskAnchor(taskId)}";

        // 🔒 §708.10 — THE THIRD ROW-OWNING FAMILY. The generic roles' step tasks now render their
        // form ON THEIR ROW on the shared /Tasks page, so the row is the complete destination for
        // them too and the same rule applies: a task links to the TASK.
        //
        // These were never keyword GUESSES — each prefix mapped explicitly to /Forms/Wizard?step=…,
        // so nobody landed on the wrong page. But they still sent the reader OUT of the task list to
        // a wizard that reopens the whole journey, which is precisely what §708.2a decided
        // Get Started is for and a direct edit is not.
        if (IsEmbeddedStepKey(sourceKey))
            return $"/Tasks{TaskAnchor(taskId)}";

        return LinkForSourceKey(sourceKey);
    }

    /// <summary>The fragment identifying one task's row. ONE place, so the link and the row agree.</summary>
    public static string TaskAnchor(int taskId) => $"#task-{taskId}";

    /// <summary>
    /// §708.10 — the step-task families whose form is EMBEDDED in their row on <c>/Tasks</c>.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Membership is decided by the SourceKey PREFIX, an exact statement of what seeded
    /// the row</b> — never by a substring of the title. That is the §708.4 rule, and these prefixes
    /// are the same constants the seeders write.</para>
    ///
    /// <para>⚠️ <b>Party and Master Class are deliberately NOT here.</b> §707.57b keeps both on their
    /// own surfaces, and §351-6/§365 point their tasks at specific wizard steps for reasons the
    /// operator gave by name. Adding them would silently undo two of his decisions.</para>
    /// </remarks>
    private static bool IsEmbeddedStepKey(string sourceKey) =>
        sourceKey.StartsWith("hotel-form:", StringComparison.Ordinal)
        || sourceKey.StartsWith("dinner-form:", StringComparison.Ordinal)
        || sourceKey.StartsWith("lunch-form:", StringComparison.Ordinal)
        || sourceKey.StartsWith("swag-form:", StringComparison.Ordinal)
        || sourceKey.StartsWith("signal:", StringComparison.Ordinal)
        || sourceKey.StartsWith(WizardStepTaskKeys.ProfilePrefix, StringComparison.Ordinal)
        || sourceKey.StartsWith(WizardStepTaskKeys.AcceptPrefix, StringComparison.Ordinal)
        || sourceKey.StartsWith(WizardStepTaskKeys.AvailabilityPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Map a <see cref="ParticipantTask.SourceKey"/> to the page that completes it,
    /// so every surface deep-links a pending task to its form. Returns null when no
    /// specific form is known (UI falls back to the generic tasks list).
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Prefer <see cref="LinkForTask"/></b> — it has the task id, so the two row-owning
    /// families (<c>sponsor:</c> / <c>speakerdl:</c>) land on their own row. This overload cannot,
    /// and answers with the role's task PAGE for them rather than guessing a form from the title.
    /// </remarks>
    public static string? LinkForSourceKey(string? sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return null;
        if (sourceKey.StartsWith("lunch-form:",                   StringComparison.Ordinal)) return "/Forms/Wizard?step=lunch";
        if (sourceKey.StartsWith("swag-form:",                    StringComparison.Ordinal)) return "/Forms/Wizard?step=swag";
        if (sourceKey.StartsWith("travel:submit-ticket-invoice:", StringComparison.Ordinal)) return "/Forms/Wizard?step=travel";
        if (sourceKey.StartsWith("hotel-form:",                   StringComparison.Ordinal)) return "/Forms/Wizard?step=hotel";
        if (sourceKey.StartsWith("dinner-form:",                  StringComparison.Ordinal)) return "/Forms/Wizard?step=dinner";
        // §234 (7a): volunteer-form: is the SHIFTS wizard's task — deep-link to ITS form
        // (it previously pointed at the per-day availability page, a different live form).
        if (sourceKey.StartsWith("volunteer-form:",               StringComparison.Ordinal)) return "/Forms/VolunteerWizard";
        if (sourceKey.StartsWith("speaker-form:",                 StringComparison.Ordinal)) return "/Forms/Speaker";
        // §161: keep My-Tasks rows in step with the Get-Started cards — the manual mark-done
        // steps (Signal join §109, speaker Promote §116, Party RSVP §164) deep-link to the
        // SAME page their Get-Started card opens, so both surfaces tell one story.
        if (sourceKey.StartsWith("signal:",                       StringComparison.Ordinal)) return "/Forms/Wizard?step=signal";
        // §314: the Promote wizard step (and its /Speaker/Promote page) is retired — any
        // legacy promote: row deep-links to the Help Promote page itself.
        if (sourceKey.StartsWith("promote:",                      StringComparison.Ordinal)) return "/Speaker/Graphics";
        // §351-6 (operator 2026-07-26: "when i click the completed task for party i get to the
        // wrong party page"). The party TASK now deep-links into the wizard step — the single
        // canonical party surface, with hub chrome and a way back — not the standalone /Party page.
        if (sourceKey.StartsWith("party-form:",                   StringComparison.Ordinal)) return "/Forms/Wizard?step=party";
        // §277: the attendee "Select your Master Class" auto-task deep-links to the MC chooser,
        // so it is clickable in both pending AND completed states (like the Party task above).
        // §365 (operator 2026-07-26: "it is still showing the old menu-item to /attendee"): that
        // chooser is now the INLINE wizard step (§352), not the retired standalone /Attendee page —
        // exactly the move §351-6 made for the party task directly above. Left as it was, this task
        // was the shadow page's last live entry point inside the hub.
        if (sourceKey.StartsWith("masterclass-form:",             StringComparison.Ordinal)) return "/Forms/Wizard?step=masterclass";
        // §173e: the four DATA-signal step tasks (Calendar email, Speaker details, Profile,
        // Code of Conduct) deep-link to the SAME page their Get-Started card opens, so
        // My-Tasks mirrors the Get-Started journey exactly. (speaker-details: must be tested
        // before any broader speaker- prefix — there is none here, but keep it explicit.)
        if (sourceKey.StartsWith(WizardStepTaskKeys.SpeakerDetailsPrefix, StringComparison.Ordinal)) return "/Forms/Wizard?step=details";
        if (sourceKey.StartsWith(WizardStepTaskKeys.CalendarPrefix,       StringComparison.Ordinal)) return "/Forms/Wizard?step=calendar";
        if (sourceKey.StartsWith(WizardStepTaskKeys.ProfilePrefix,        StringComparison.Ordinal)) return "/Forms/Wizard?step=profile";
        if (sourceKey.StartsWith(WizardStepTaskKeys.AcceptPrefix,         StringComparison.Ordinal)) return "/Forms/Wizard?step=accept";
        if (sourceKey.StartsWith(WizardStepTaskKeys.AvailabilityPrefix,   StringComparison.Ordinal)) return "/Forms/Wizard?step=availability";
        // 🔒 §708.4 — THE TWO ROW-OWNING FAMILIES. No keyword guessing, by design.
        //
        // What used to live here was a ladder of `sourceKey.Contains("…")` tests over the task's
        // TITLE SLUG, mapping each guess to a form page or a `/Sponsor/CompanyDetails#section`
        // anchor. It produced a wrong destination three separate times (§674, §679, §708.4) because
        // a substring of a title is not a statement about what a task IS: "Validate booth members
        // have lead scan app + exhibitor guide" contains "member", so it landed on the booth-member
        // editor, and "Initial onboarding of sponsor" contains "onboard", so it landed on a page
        // §707.44 had already removed from the nav.
        //
        // Both families now render their own body, buttons, embedded editors and upload control on
        // their own row (§684 / §688.12), so the row is the correct and complete destination. The
        // caller that has the task id (LinkForTask, above) anchors to the exact row; without one we
        // can still name the right PAGE, which is the part the guessing got wrong.
        if (sourceKey.StartsWith("speakerdl:", StringComparison.Ordinal)) return "/Speaker/Tasks";
        if (sourceKey.StartsWith("sponsor:",   StringComparison.Ordinal)) return "/Sponsor/Tasks";
        return null;
    }
}
