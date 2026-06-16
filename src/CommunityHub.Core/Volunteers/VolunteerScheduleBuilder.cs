using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Volunteers;

/// <summary>
/// One entry in a volunteer's unified "My schedule" — a single assigned
/// <see cref="VolunteerTask"/> flattened with its WHERE / WHEN and the people a
/// volunteer turns to (bucket supervisor(s) + ELDK lead). This is the row the
/// "My day" page renders, time-ordered, and the same shape the per-user .ics
/// feed emits for assigned volunteer work.
/// </summary>
public sealed record VolunteerScheduleEntry(
    int TaskId,
    string Title,
    string Bucket,
    string Subcategory,
    DateOnly? Due,
    /// <summary>Free-text start window (e.g. "Day 1, 08:00").</summary>
    string? Shift,
    /// <summary>Free-text end window (e.g. "12:00").</summary>
    string? TimeEnd,
    VolunteerTaskStatus Status,
    string? Instructions,
    string? Prerequisites,
    string? Expectations,
    /// <summary>Comma-joined supervisor name(s) — the bucket go-to people.</summary>
    string Supervisors,
    /// <summary>The bucket's ELDK lead (free text), or null.</summary>
    string? EldkLeadName,
    /// <summary>The volunteer's own self-service decision on this shift
    /// (confirm / decline / swap), so the page can show + edit it.</summary>
    ShiftDecisionStatus Decision,
    /// <summary>The note the volunteer left when declining / requesting a swap.</summary>
    string? DecisionNote);

/// <summary>
/// The whole "My schedule" for one volunteer: the time-ordered entries plus the
/// volunteer's own help requests (so replies surface inline). Pure data — the
/// page model and tests both consume this.
/// </summary>
public sealed record VolunteerSchedule(
    IReadOnlyList<VolunteerScheduleEntry> Entries,
    IReadOnlyList<VolunteerHelpRequest> MyHelp)
{
    public bool IsEmpty => Entries.Count == 0;
}

/// <summary>
/// Aggregates a volunteer's assigned tasks into ONE flat, time-ordered schedule
/// (REQUIREMENTS §20 Volunteer "My day" / §21 "Volunteer unified My-schedule").
/// Unlike the category-grouped <c>/Volunteer/MyTasks</c> view, this answers
/// "what am I doing, and when?" at a glance: entries sort by due date then by the
/// free-text shift window, then title, with undated work last.
///
/// It resolves each owning bucket's supervisor(s) (the legacy single supervisor
/// + every multi-supervisor row, de-duplicated) and ELDK lead so a volunteer can
/// see exactly who to ask for help. Pure aggregation over the existing model — no
/// schema change.
/// </summary>
public sealed class VolunteerScheduleBuilder
{
    private readonly CommunityHubDbContext _db;
    private readonly VolunteerStructureService _structure;

    public VolunteerScheduleBuilder(CommunityHubDbContext db, VolunteerStructureService structure)
    {
        _db = db;
        _structure = structure;
    }

    /// <summary>
    /// Build the unified schedule for one volunteer in one edition. Returns an
    /// empty (non-null) schedule when the volunteer has no assignments.
    /// </summary>
    public async Task<VolunteerSchedule> BuildAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var tasks = await _structure.LoadMyTasksAsync(eventId, participantId, ct);

        // This volunteer's own per-shift decision (confirm / decline / swap),
        // keyed by task — so the schedule shows what they already flagged.
        var decisionByTask = await _db.VolunteerTaskAssignments
            .Where(a => a.EventId == eventId && a.ParticipantId == participantId)
            .Select(a => new { a.TaskId, a.DecisionStatus, a.DecisionNote })
            .ToDictionaryAsync(x => x.TaskId, x => (x.DecisionStatus, x.DecisionNote), ct);

        // Resolve the go-to people per bucket once (LoadSupervisorsAsync also folds
        // in the legacy single-supervisor column).
        var catIds = tasks.Select(t => t.Subcategory.CategoryId).Distinct().ToList();

        var eldkLeadByCat = await _db.VolunteerCategories
            .Where(c => c.EventId == eventId && catIds.Contains(c.Id))
            .Select(c => new { c.Id, c.EldkLeadName })
            .ToDictionaryAsync(x => x.Id, x => x.EldkLeadName, ct);

        var supByCat = new Dictionary<int, string>();
        foreach (var catId in catIds)
        {
            var sups = await _structure.LoadSupervisorsAsync(eventId, catId, ct);
            var names = sups
                .Select(p => string.IsNullOrWhiteSpace(p.FullName) ? p.Email : p.FullName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            supByCat[catId] = string.Join(", ", names);
        }

        var entries = tasks
            .Select(t =>
            {
                var catId = t.Subcategory.CategoryId;
                return new VolunteerScheduleEntry(
                    t.Id,
                    t.Title,
                    t.Subcategory.Category?.Name ?? string.Empty,
                    t.Subcategory.Name,
                    t.DueDate,
                    string.IsNullOrWhiteSpace(t.Shift) ? null : t.Shift,
                    string.IsNullOrWhiteSpace(t.TimeEnd) ? null : t.TimeEnd,
                    t.Status,
                    t.Instructions,
                    t.Prerequisites,
                    t.Expectations,
                    supByCat.TryGetValue(catId, out var s) ? s : string.Empty,
                    eldkLeadByCat.TryGetValue(catId, out var lead) && !string.IsNullOrWhiteSpace(lead)
                        ? lead : null,
                    decisionByTask.TryGetValue(t.Id, out var d) ? d.DecisionStatus : ShiftDecisionStatus.None,
                    decisionByTask.TryGetValue(t.Id, out var dn) ? dn.DecisionNote : null);
            })
            // Time-ordered "my day": dated work first (earliest due), then by the
            // free-text shift window, then title; undated work sorts to the end.
            .OrderBy(e => e.Due is null)
            .ThenBy(e => e.Due ?? DateOnly.MaxValue)
            .ThenBy(e => e.Shift ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var myHelp = await _db.VolunteerHelpRequests
            .Where(h => h.EventId == eventId && h.RequestedByParticipantId == participantId)
            .Include(h => h.Task)
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync(ct);

        return new VolunteerSchedule(entries, myHelp);
    }
}
