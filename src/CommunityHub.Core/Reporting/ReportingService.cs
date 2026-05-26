using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reporting;

/// <summary>A completion stat: how many of a total have done something.</summary>
public sealed record CompletionStat(string Label, int Done, int Total)
{
    public int Percent => Total == 0 ? 0 : (int)Math.Round(100.0 * Done / Total);
}

/// <summary>Count of tasks in one state.</summary>
public sealed record TaskStateCount(string State, int Count);

/// <summary>Volunteer coverage for one shift.</summary>
public sealed record ShiftCoverage(string Shift, int VolunteerCount);

/// <summary>The full organizer dashboard snapshot for an edition.</summary>
public sealed class DashboardReport
{
    public string EventDisplayName { get; set; } = string.Empty;

    // Headline counts.
    public int TotalParticipants { get; set; }
    public int ActiveParticipants { get; set; }
    public int InactiveParticipants { get; set; }
    public Dictionary<string, int> ParticipantsByRole { get; set; } = new();

    // Form completion (per role that the form applies to).
    public CompletionStat HotelCompletion { get; set; } = new("Hotel", 0, 0);
    public CompletionStat DinnerCompletion { get; set; } = new("Dinner", 0, 0);
    public CompletionStat VolunteerCompletion { get; set; } = new("Volunteer", 0, 0);

    // Tasks.
    public List<TaskStateCount> TaskStates { get; set; } = new();
    public int OverdueTasks { get; set; }

    // Sponsors.
    public int SponsorTaskTotal { get; set; }
    public int SponsorTaskDone { get; set; }

    // Attendees.
    public int AttendeeTotal { get; set; }
    public int AttendeeMismatches { get; set; }

    // Volunteer coverage.
    public List<ShiftCoverage> ShiftCoverage { get; set; } = new();
}

/// <summary>
/// Builds the organizer dashboard report (CONTEXT.md - reporting). All numbers
/// are computed live from the database for the active edition: completion
/// rates, task status, sponsor progress, attendee reconciliation health, and
/// volunteer shift coverage. Read-only - no side effects.
/// </summary>
public sealed class ReportingService
{
    private const char ShiftDelimiter = '|';

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public ReportingService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<DashboardReport> BuildAsync(
        int eventId, CancellationToken ct = default)
    {
        var report = new DashboardReport();

        var ev = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => e.DisplayName)
            .FirstOrDefaultAsync(ct);
        report.EventDisplayName = ev ?? string.Empty;

        // --- Participants ---------------------------------------------------
        var participants = await _db.Participants
            .Where(p => p.EventId == eventId)
            .Select(p => new { p.Id, p.Role, p.IsActive })
            .ToListAsync(ct);

        report.TotalParticipants = participants.Count;
        report.ActiveParticipants = participants.Count(p => p.IsActive);
        report.InactiveParticipants = participants.Count(p => !p.IsActive);
        report.ParticipantsByRole = participants
            .GroupBy(p => p.Role.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        // Active participants per role - the denominator for form completion.
        var activeByRole = participants
            .Where(p => p.IsActive)
            .GroupBy(p => p.Role)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToHashSet());

        HashSet<int> Role(ParticipantRole r) =>
            activeByRole.TryGetValue(r, out var s) ? s : new HashSet<int>();

        // Hotel + dinner apply to speakers, MC speakers, volunteers.
        var hotelDinnerAudience = Role(ParticipantRole.Speaker)
            .Concat(Role(ParticipantRole.MasterclassSpeaker))
            .Concat(Role(ParticipantRole.Volunteer))
            .ToHashSet();
        var volunteerAudience = Role(ParticipantRole.Volunteer);

        // --- Form completion ------------------------------------------------
        var hotelSubmitters = await _db.HotelBookings
            .Where(h => h.EventId == eventId)
            .Select(h => h.ParticipantId)
            .ToListAsync(ct);
        var dinnerSubmitters = await _db.DinnerSignups
            .Where(d => d.EventId == eventId)
            .Select(d => d.ParticipantId)
            .ToListAsync(ct);
        var volunteerRows = await _db.VolunteerAvailabilities
            .Where(v => v.EventId == eventId)
            .Select(v => new { v.ParticipantId, v.SelectedShifts })
            .ToListAsync(ct);

        report.HotelCompletion = new CompletionStat(
            "Hotel",
            hotelSubmitters.Count(id => hotelDinnerAudience.Contains(id)),
            hotelDinnerAudience.Count);
        report.DinnerCompletion = new CompletionStat(
            "Dinner",
            dinnerSubmitters.Count(id => hotelDinnerAudience.Contains(id)),
            hotelDinnerAudience.Count);
        report.VolunteerCompletion = new CompletionStat(
            "Volunteer sign-up",
            volunteerRows.Count(v => volunteerAudience.Contains(v.ParticipantId)),
            volunteerAudience.Count);

        // --- Tasks ----------------------------------------------------------
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var tasks = await _db.Tasks
            .Where(t => t.EventId == eventId)
            .Select(t => new { t.State, t.DueDate, t.SourceKey })
            .ToListAsync(ct);

        report.TaskStates = tasks
            .GroupBy(t => t.State)
            .Select(g => new TaskStateCount(g.Key.ToString(), g.Count()))
            .OrderBy(x => x.State)
            .ToList();
        report.OverdueTasks = tasks.Count(t =>
            t.State != TaskState.Done
            && t.DueDate != null
            && t.DueDate.Value < today);

        // Sponsor tasks = tasks sourced from the WooCommerce pull.
        var sponsorTasks = tasks
            .Where(t => t.SourceKey != null && t.SourceKey.StartsWith("woo:"))
            .ToList();
        report.SponsorTaskTotal = sponsorTasks.Count;
        report.SponsorTaskDone = sponsorTasks.Count(t => t.State == TaskState.Done);

        // --- Attendees ------------------------------------------------------
        report.AttendeeTotal = await _db.Attendees
            .CountAsync(a => a.EventId == eventId, ct);
        report.AttendeeMismatches = await _db.Attendees
            .CountAsync(a => a.EventId == eventId
                             && a.HasReconciliationMismatch, ct);

        // --- Volunteer shift coverage --------------------------------------
        var coverage = new Dictionary<string, int>();
        foreach (var v in volunteerRows)
        {
            var shifts = (v.SelectedShifts ?? string.Empty)
                .Split(ShiftDelimiter, StringSplitOptions.RemoveEmptyEntries);
            foreach (var shift in shifts)
            {
                coverage[shift] = coverage.GetValueOrDefault(shift) + 1;
            }
        }
        report.ShiftCoverage = coverage
            .Select(kv => new ShiftCoverage(kv.Key, kv.Value))
            .OrderByDescending(x => x.VolunteerCount)
            .ToList();

        return report;
    }
}
