using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>One role's participation line: how many people hold the role and
/// how many of those are currently active.</summary>
public sealed record RoleCount(string Role, int Total, int Active)
{
    public int Inactive => Total - Active;
}

/// <summary>Task completion for one grouping (a category, a role, or "all"):
/// how many of the tasks in that group are Done.</summary>
public sealed record CompletionRate(string Label, int Done, int Total)
{
    /// <summary>Percent done (0 when there is nothing in the group).</summary>
    public int Percent => Total == 0 ? 0 : (int)Math.Round(100.0 * Done / Total);
}

/// <summary>How many speakers have cleared one milestone deadline (a task that
/// the same milestone produced for every speaker, grouped by its title).</summary>
public sealed record MilestoneProgress(string Milestone, int Done, int Total, int Overdue)
{
    public int Percent => Total == 0 ? 0 : (int)Math.Round(100.0 * Done / Total);
}

/// <summary>Volunteer-structure coverage for one category: of its
/// <see cref="VolunteerTask"/> rows, how many have at least one volunteer
/// assigned vs. how many are still open (unassigned).</summary>
public sealed record VolunteerCoverage(string Category, int Assigned, int Open)
{
    public int Total => Assigned + Open;
    public int Percent => Total == 0 ? 0 : (int)Math.Round(100.0 * Assigned / Total);
}

/// <summary>The cross-role organizer overview snapshot for one edition. Every
/// number is an aggregate computed live from existing entities — there is no
/// per-overview table and nothing is persisted.</summary>
public sealed class OrganizerOverview
{
    public string EventDisplayName { get; set; } = string.Empty;

    // --- Participation (counts by role) ------------------------------------
    public int TotalPeople { get; set; }
    public int ActivePeople { get; set; }
    public List<RoleCount> RolesBreakdown { get; set; } = new();

    // --- Task completion ---------------------------------------------------
    public CompletionRate TaskOverall { get; set; } = new("All tasks", 0, 0);
    public List<CompletionRate> TasksByRole { get; set; } = new();
    public List<CompletionRate> TasksByCategory { get; set; } = new();

    // --- Speaker milestone-deadline progress -------------------------------
    public List<MilestoneProgress> SpeakerMilestones { get; set; } = new();

    // --- Volunteer-structure coverage --------------------------------------
    public int VolunteerTasksTotal { get; set; }
    public int VolunteerTasksAssigned { get; set; }
    public int VolunteerTasksOpen { get; set; }
    public List<VolunteerCoverage> VolunteerCoverageByCategory { get; set; } = new();

    // --- Sponsor totals ----------------------------------------------------
    public int SponsorTaskTotal { get; set; }
    public int SponsorTaskDone { get; set; }
    public int SponsorLeadTotal { get; set; }
    public int SponsorLeadOpen { get; set; }

    // --- Attendees ---------------------------------------------------------
    public int AttendeeTotal { get; set; }

    // --- "Needs attention" tiles ------------------------------------------
    public int OverdueTasks { get; set; }
    public int UnassignedVolunteerTasks { get; set; }
    public int OpenHelpRequests { get; set; }
    public int PendingVolunteerApplications { get; set; }
}

/// <summary>
/// Builds the organizer cross-role <see cref="OrganizerOverview"/>
/// (REQUIREMENTS §11). It is a <b>read-only aggregation</b> over the entities
/// that already exist for an edition — participants, tasks, the volunteer work
/// structure, sponsor leads and attendees. It computes nothing into a new table
/// and never writes; calling <see cref="BuildAsync"/> twice yields the same
/// snapshot of the data.
///
/// Distinct from <see cref="CommunityHub.Core.Reporting.ReportingService"/>
/// (form-completion + shift coverage on the operational dashboard) and from the
/// Action Queue: this surface answers "where does the whole event stand, across
/// every role?" at a glance — participation, task completion per role/category,
/// speaker milestone progress, volunteer coverage, sponsor totals, attendee
/// check-in, and the open items that need an organizer's attention.
/// </summary>
public sealed class OrganizerOverviewService
{
    // Sponsor pipeline tasks are keyed "sponsor:{companyId}:{slug}"
    // (matches ReportingService; the legacy "woo:" prefix is no longer emitted).
    private const string SponsorTaskPrefix = "sponsor:";

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public OrganizerOverviewService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<OrganizerOverview> BuildAsync(int eventId, CancellationToken ct = default)
    {
        var overview = new OrganizerOverview
        {
            EventDisplayName = await _db.Events
                .Where(e => e.Id == eventId)
                .Select(e => e.DisplayName)
                .FirstOrDefaultAsync(ct) ?? string.Empty,
        };

        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

        await PopulateParticipationAsync(overview, eventId, ct);
        await PopulateTasksAsync(overview, eventId, today, ct);
        await PopulateSpeakerMilestonesAsync(overview, eventId, today, ct);
        await PopulateVolunteerCoverageAsync(overview, eventId, ct);
        await PopulateSponsorsAsync(overview, eventId, ct);
        await PopulateAttendeesAsync(overview, eventId, ct);

        return overview;
    }

    private async Task PopulateParticipationAsync(
        OrganizerOverview o, int eventId, CancellationToken ct)
    {
        var people = await _db.Participants
            .Where(p => p.EventId == eventId)
            .Select(p => new { p.Role, p.IsActive })
            .ToListAsync(ct);

        o.TotalPeople = people.Count;
        o.ActivePeople = people.Count(p => p.IsActive);
        o.RolesBreakdown = people
            .GroupBy(p => p.Role)
            .Select(g => new RoleCount(g.Key.ToString(), g.Count(), g.Count(x => x.IsActive)))
            .OrderBy(rc => rc.Role)
            .ToList();

        // A pending volunteer application = a Volunteer row that is not yet
        // active (created by the public anonymous /volunteer/signup page).
        o.PendingVolunteerApplications = people.Count(
            p => p.Role == ParticipantRole.Volunteer && !p.IsActive);
    }

    private async Task PopulateTasksAsync(
        OrganizerOverview o, int eventId, DateOnly today, CancellationToken ct)
    {
        // Pull the per-participant task list once with the assignee role so the
        // by-role completion split needs no extra query.
        var tasks = await _db.Tasks
            .Where(t => t.EventId == eventId)
            .Select(t => new
            {
                t.State,
                t.DueDate,
                t.SourceKey,
                AssigneeRole = t.AssignedParticipant != null
                    ? (ParticipantRole?)t.AssignedParticipant.Role
                    : null,
            })
            .ToListAsync(ct);

        o.TaskOverall = new CompletionRate(
            "All tasks", tasks.Count(t => t.State == TaskState.Done), tasks.Count);

        o.OverdueTasks = tasks.Count(t =>
            t.State != TaskState.Done && t.DueDate != null && t.DueDate.Value < today);

        // Completion per assignee role (unassigned tasks land under "Unassigned").
        o.TasksByRole = tasks
            .GroupBy(t => t.AssigneeRole.HasValue
                ? t.AssigneeRole.Value.ToString()
                : "Unassigned")
            .Select(g => new CompletionRate(
                g.Key, g.Count(x => x.State == TaskState.Done), g.Count()))
            .OrderByDescending(c => c.Total)
            .ThenBy(c => c.Label)
            .ToList();

        // "Category" for the personal/sponsor task list = sponsor vs. general.
        // (The volunteer work tree has its own category coverage below.)
        var sponsorTasks = tasks
            .Where(t => t.SourceKey != null && t.SourceKey.StartsWith(SponsorTaskPrefix))
            .ToList();
        var generalTasks = tasks
            .Where(t => t.SourceKey == null || !t.SourceKey.StartsWith(SponsorTaskPrefix))
            .ToList();

        o.TasksByCategory = new List<CompletionRate>();
        if (generalTasks.Count > 0)
        {
            o.TasksByCategory.Add(new CompletionRate(
                "General", generalTasks.Count(t => t.State == TaskState.Done), generalTasks.Count));
        }
        if (sponsorTasks.Count > 0)
        {
            o.TasksByCategory.Add(new CompletionRate(
                "Sponsor", sponsorTasks.Count(t => t.State == TaskState.Done), sponsorTasks.Count));
        }
    }

    private async Task PopulateSpeakerMilestonesAsync(
        OrganizerOverview o, int eventId, DateOnly today, CancellationToken ct)
    {
        // A speaker milestone deadline = a task assigned to a (masterclass)
        // speaker. The same milestone produces one task per speaker, so the task
        // Title is the milestone; grouping by it shows how many speakers cleared
        // each deadline.
        var speakerTasks = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.AssignedParticipant != null
                        && t.AssignedParticipant.Role == ParticipantRole.Speaker)
            .Select(t => new { t.Title, t.State, t.DueDate })
            .ToListAsync(ct);

        o.SpeakerMilestones = speakerTasks
            .GroupBy(t => t.Title)
            .Select(g => new MilestoneProgress(
                g.Key,
                g.Count(x => x.State == TaskState.Done),
                g.Count(),
                g.Count(x => x.State != TaskState.Done
                             && x.DueDate != null && x.DueDate.Value < today)))
            .OrderByDescending(m => m.Overdue)
            .ThenBy(m => m.Percent)
            .ThenBy(m => m.Milestone)
            .ToList();
    }

    private async Task PopulateVolunteerCoverageAsync(
        OrganizerOverview o, int eventId, CancellationToken ct)
    {
        // Volunteer-structure coverage: of the VolunteerTask rows for the
        // edition, how many have at least one volunteer assigned vs. still open.
        // "Cancelled" tasks are excluded from coverage (no longer needed).
        var tasks = await _db.VolunteerTasks
            .Where(t => t.EventId == eventId && t.Status != VolunteerTaskStatus.Cancelled)
            .Select(t => new
            {
                t.Id,
                Category = t.Subcategory.Category.Name,
                AssignmentCount = t.Assignments.Count,
            })
            .ToListAsync(ct);

        o.VolunteerTasksTotal = tasks.Count;
        o.VolunteerTasksAssigned = tasks.Count(t => t.AssignmentCount > 0);
        o.VolunteerTasksOpen = tasks.Count(t => t.AssignmentCount == 0);
        o.UnassignedVolunteerTasks = o.VolunteerTasksOpen;

        o.VolunteerCoverageByCategory = tasks
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Category) ? "(uncategorized)" : t.Category)
            .Select(g => new VolunteerCoverage(
                g.Key,
                g.Count(x => x.AssignmentCount > 0),
                g.Count(x => x.AssignmentCount == 0)))
            .OrderByDescending(v => v.Open)
            .ThenBy(v => v.Category)
            .ToList();

        o.OpenHelpRequests = await _db.VolunteerHelpRequests
            .CountAsync(h => h.EventId == eventId
                             && h.Status == VolunteerHelpStatus.Open, ct);
    }

    private async Task PopulateSponsorsAsync(
        OrganizerOverview o, int eventId, CancellationToken ct)
    {
        var sponsorTasks = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.SourceKey != null && t.SourceKey.StartsWith(SponsorTaskPrefix))
            .Select(t => t.State)
            .ToListAsync(ct);
        o.SponsorTaskTotal = sponsorTasks.Count;
        o.SponsorTaskDone = sponsorTasks.Count(s => s == TaskState.Done);

        o.SponsorLeadTotal = await _db.SponsorLeads
            .CountAsync(l => l.EventId == eventId, ct);
        o.SponsorLeadOpen = await _db.SponsorLeads
            .CountAsync(l => l.EventId == eventId && l.Status == SponsorLeadStatus.Open, ct);
    }

    private async Task PopulateAttendeesAsync(
        OrganizerOverview o, int eventId, CancellationToken ct)
    {
        o.AttendeeTotal = await _db.Attendees.CountAsync(a => a.EventId == eventId, ct);
    }
}
