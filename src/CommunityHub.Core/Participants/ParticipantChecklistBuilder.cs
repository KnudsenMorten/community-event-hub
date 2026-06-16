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
/// deep-links stay consistent everywhere the checklist renders. Pure read over
/// the DB; the form-auto-task backfill (which WRITES rows) stays on the Hub page
/// — this builder never mutates.
/// </summary>
public sealed class ParticipantChecklistBuilder
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public ParticipantChecklistBuilder(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<ParticipantChecklist> BuildAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
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
        if (sourceKey.StartsWith("volunteer-form:",               StringComparison.Ordinal)) return "/Forms/VolunteerWizard";
        if (sourceKey.StartsWith("speaker-form:",                 StringComparison.Ordinal)) return "/Forms/Speaker";
        if (sourceKey.StartsWith("speakerdl:",                    StringComparison.Ordinal)) return "/Tasks";
        if (sourceKey.StartsWith("sponsor:",                      StringComparison.Ordinal)) return "/Sponsor/Tasks";
        return null;
    }
}
