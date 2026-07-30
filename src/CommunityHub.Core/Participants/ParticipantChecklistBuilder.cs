using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
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

        var all = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && (t.AssignedParticipantId == participantId
                            || (sponsorCompanyId != null
                                && t.SponsorCompanyId == sponsorCompanyId)))
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
                id, title, due, state, overdue, LinkForSourceKey(sourceKey), description);
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
    /// Map a <see cref="ParticipantTask.SourceKey"/> to the page that completes it,
    /// so every surface deep-links a pending task to its form. Returns null when no
    /// specific form is known (UI falls back to the generic tasks list).
    /// </summary>
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
        if (sourceKey.StartsWith("speakerdl:",                    StringComparison.Ordinal))
        {
            // A speaker-deadline task mirrors a logistics form: deep-link to the
            // form that completes it (matched by the form keyword the slugger
            // embeds, same as FormTaskReconciler). Upload-deck deadlines carry no
            // form, so they stay on the generic tasks list.
            if (sourceKey.Contains("hotel",  StringComparison.Ordinal)) return "/Forms/Wizard?step=hotel";
            if (sourceKey.Contains("dinner", StringComparison.Ordinal)) return "/Forms/Wizard?step=dinner";
            if (sourceKey.Contains("lunch",  StringComparison.Ordinal)) return "/Forms/Wizard?step=lunch";
            if (sourceKey.Contains("swag",   StringComparison.Ordinal)) return "/Forms/Wizard?step=swag";
            // §679 (operator 2026-07-29: "clicking goes to wrong place ... found for speaker,
            // pending tasks under HOME"). "travel" was missing from this keyword list, so the
            // travel-reimbursement DEADLINE task fell through to the generic /Tasks list — even
            // though the method already maps the `travel:` prefix to the form. Same defect as §674
            // on the sponsor side: the fall-through, not the mapping, was wrong.
            if (sourceKey.Contains("travel", StringComparison.Ordinal)) return "/Forms/Wizard?step=travel";
            // §314: the Help-Promote deadline deep-links to the Help Promote page.
            if (sourceKey.Contains("promote", StringComparison.Ordinal)) return "/Speaker/Graphics";
            // §322i: the preview/final upload deadlines deep-link to My Sessions — the
            // upload lives on each session card there (the standalone page is retired).
            if (sourceKey.Contains("presentation", StringComparison.Ordinal)) return "/Speaker";
            return "/Tasks";
        }
        if (sourceKey.StartsWith("sponsor:", StringComparison.Ordinal))
        {
            // §297/§298: deep-link to the RIGHT Company Details section (#anchor) so the task lands
            // on its section, not the top of the page. Matched by the keyword the task slug embeds.
            const string cd = "/Sponsor/CompanyDetails";
            if (sourceKey.Contains("logo",        StringComparison.OrdinalIgnoreCase)) return cd + "#logos";
            if (sourceKey.Contains("wall",        StringComparison.OrdinalIgnoreCase)) return cd + "#exhibitor-wall";
            if (sourceKey.Contains("material",    StringComparison.OrdinalIgnoreCase)) return cd + "#booth-materials";
            if (sourceKey.Contains("member",      StringComparison.OrdinalIgnoreCase)) return cd + "#booth-members";
            if (sourceKey.Contains("check",       StringComparison.OrdinalIgnoreCase)) return cd + "#booth-checkin";
            if (sourceKey.Contains("coordinator", StringComparison.OrdinalIgnoreCase)) return cd + "#coordinator";
            if (sourceKey.Contains("contact",     StringComparison.OrdinalIgnoreCase)) return cd + "#contacts";
            if (sourceKey.Contains("onboard",     StringComparison.OrdinalIgnoreCase)) return cd + "#company";

            // §674 (operator 2026-07-29: "when i click a pending task, it does NOT take me to the
            // tasks, but company details"). The fallback used to be Company Details itself, so EVERY
            // sponsor task whose slug matches none of the keywords above — TV rental, attendee-bag
            // swag, the app game, pre-event shipment, download leads — silently landed on a page
            // with nothing to do with it. Six of his eight pending rows were in that bucket.
            //
            // The right fallback is the sponsor TASKS page, mirroring what the speakerdl: branch
            // already does ("/Tasks"): a task we cannot deep-link belongs on the task list, not on
            // an unrelated form. A keyword match is still a deep link; only the fall-through moved.
            return "/Sponsor/Tasks";
        }
        return null;
    }
}
