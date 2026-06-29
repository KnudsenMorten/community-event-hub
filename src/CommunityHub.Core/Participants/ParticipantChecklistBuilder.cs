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
    string? Link);

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
            .Select(t => new { t.Id, t.Title, t.DueDate, t.State, t.SourceKey })
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

        ChecklistRow Row(int id, string title, DateOnly? due, TaskState state, string? sourceKey)
        {
            int? overdue = (due is not null && due < today && state != TaskState.Done)
                ? today.DayNumber - due.Value.DayNumber
                : (int?)null;
            return new ChecklistRow(id, title, due, state, overdue, LinkForSourceKey(sourceKey));
        }

        var pending = all
            .Where(t => t.State != TaskState.Done)
            .Select(t => Row(t.Id, t.Title, t.DueDate, t.State, t.SourceKey))
            // Overdue first, then by due date, then title.
            .OrderByDescending(r => r.DaysOverdue is > 0)
            .ThenBy(r => r.DueDate ?? DateOnly.MaxValue)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var completed = all
            .Where(t => t.State == TaskState.Done)
            .Select(t => Row(t.Id, t.Title, t.DueDate, t.State, t.SourceKey))
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
        if (sourceKey.StartsWith("lunch-form:",                   StringComparison.Ordinal)) return "/Forms/Lunch";
        if (sourceKey.StartsWith("swag-form:",                    StringComparison.Ordinal)) return "/Forms/Swag";
        if (sourceKey.StartsWith("travel:submit-ticket-invoice:", StringComparison.Ordinal)) return "/Forms/Travel";
        if (sourceKey.StartsWith("hotel-form:",                   StringComparison.Ordinal)) return "/Forms/Hotel";
        if (sourceKey.StartsWith("dinner-form:",                  StringComparison.Ordinal)) return "/Forms/Dinner";
        if (sourceKey.StartsWith("volunteer-form:",               StringComparison.Ordinal)) return "/volunteer/availability";
        if (sourceKey.StartsWith("speaker-form:",                 StringComparison.Ordinal)) return "/Forms/Speaker";
        // §161: keep My-Tasks rows in step with the Get-Started cards — the manual mark-done
        // steps (Signal join §109, speaker Promote §116, Party RSVP §164) deep-link to the
        // SAME page their Get-Started card opens, so both surfaces tell one story.
        if (sourceKey.StartsWith("signal:",                       StringComparison.Ordinal)) return "/Forms/Signal";
        if (sourceKey.StartsWith("promote:",                      StringComparison.Ordinal)) return "/Speaker/Promote";
        if (sourceKey.StartsWith("party-form:",                   StringComparison.Ordinal)) return "/Party";
        if (sourceKey.StartsWith("speakerdl:",                    StringComparison.Ordinal))
        {
            // A speaker-deadline task mirrors a logistics form: deep-link to the
            // form that completes it (matched by the form keyword the slugger
            // embeds, same as FormTaskReconciler). Upload-deck deadlines carry no
            // form, so they stay on the generic tasks list.
            if (sourceKey.Contains("hotel",  StringComparison.Ordinal)) return "/Forms/Hotel";
            if (sourceKey.Contains("dinner", StringComparison.Ordinal)) return "/Forms/Dinner";
            if (sourceKey.Contains("lunch",  StringComparison.Ordinal)) return "/Forms/Lunch";
            if (sourceKey.Contains("swag",   StringComparison.Ordinal)) return "/Forms/Swag";
            return "/Tasks";
        }
        if (sourceKey.StartsWith("sponsor:",                      StringComparison.Ordinal)) return "/Sponsor/CompanyDetails";
        return null;
    }
}
