using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// One headline tile on the command-center landing: a number, a label key, and
/// where clicking it should take the organizer (a page + optional query so the
/// tile lands on the relevant <b>filtered</b> grid, not a dead end). When
/// <see cref="IsAttention"/> is true the tile warns while the count is &gt; 0 and
/// reads "all clear" at 0 — so an empty event shows an honest calm state rather
/// than a fake red badge.
/// </summary>
/// <param name="Key">Stable id for the tile (also the i18n label-key suffix).</param>
/// <param name="Count">The live aggregate this tile reports.</param>
/// <param name="LinkPage">Razor page to open on click (e.g. "/Organizer/TasksTable").</param>
/// <param name="LinkQuery">Optional query-string pairs that pre-filter the target grid.</param>
/// <param name="IsAttention">True for "needs attention" tiles (warn when &gt; 0).</param>
public sealed record CommandCenterTile(
    string Key,
    int Count,
    string LinkPage,
    IReadOnlyDictionary<string, string?>? LinkQuery = null,
    bool IsAttention = false);

/// <summary>One persona's onboarding-completion line for the command center.</summary>
public sealed record PersonaCompletion(PersonaGroup Persona, int Completed, int Total)
{
    public int Percent => Total == 0 ? 0 : (int)Math.Round(100.0 * Completed / Total);
    public int Outstanding => Total - Completed;
}

/// <summary>A headcount line (hotel / swag / lunch / dinner) for the command center.</summary>
public sealed record HeadcountLine(string Key, int Count, string LinkPage);

/// <summary>One overdue/upcoming task row in the "what needs my attention" list.</summary>
public sealed record AttentionTask(
    int TaskId, string Title, string? Assignee, DateOnly? DueDate, bool IsOverdue);

/// <summary>
/// The organizer command-center snapshot for one edition (REQUIREMENTS §20
/// Organizer — "Command-center dashboard"). A single glance answers "is the event
/// on track and what do I do next": total registrations, onboarding completion %
/// per persona, who-hasn't-done-what, hotel/swag/lunch/dinner headcounts, session
/// + sponsor status, and today's / overdue tasks.
///
/// Every number is a <b>read-only aggregate</b> over entities that already exist
/// — nothing is persisted, calling <see cref="CommandCenterService.BuildAsync"/>
/// twice yields the same snapshot. <see cref="GeneratedAtUtc"/> records when this
/// snapshot was computed so the page can show a "last updated" time.
/// </summary>
public sealed class CommandCenterSnapshot
{
    public string EventDisplayName { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; set; }

    // --- Registrations / participation ------------------------------------
    public int TotalParticipants { get; set; }
    public int ActiveParticipants { get; set; }
    public int TotalAttendees { get; set; }

    // --- Onboarding completion (overall + per persona) --------------------
    public int OnboardingOverallPercent { get; set; }
    public List<PersonaCompletion> OnboardingByPersona { get; set; } = new();

    // --- Headcounts (hotel / swag / lunch / dinner) -----------------------
    public List<HeadcountLine> Headcounts { get; set; } = new();

    // --- Session + sponsor status -----------------------------------------
    public int SessionsTotal { get; set; }
    public int SessionsScheduled { get; set; }   // has a StartsAt + Room
    public int SessionsUnscheduled => SessionsTotal - SessionsScheduled;
    public int SponsorsTotal { get; set; }
    public int SponsorTasksTotal { get; set; }
    public int SponsorTasksDone { get; set; }
    public int SponsorTasksPercent =>
        SponsorTasksTotal == 0 ? 0 : (int)Math.Round(100.0 * SponsorTasksDone / SponsorTasksTotal);

    // --- "What needs my attention" tiles + task list ----------------------
    public List<CommandCenterTile> AttentionTiles { get; set; } = new();
    public List<AttentionTask> OverdueAndTodayTasks { get; set; } = new();
    public int TasksOverdue { get; set; }
    public int TasksDueToday { get; set; }

    /// <summary>True when there is nothing for the organizer to act on right now.</summary>
    public bool AllClear =>
        AttentionTiles.Where(t => t.IsAttention).All(t => t.Count == 0)
        && OverdueAndTodayTasks.Count == 0;
}

/// <summary>
/// Builds the organizer <see cref="CommandCenterSnapshot"/> (REQUIREMENTS §20).
/// Read-only aggregation, edition-scoped, never writes. Distinct from:
///   <list type="bullet">
///   <item><see cref="OrganizerOverviewService"/> — the exhaustive cross-role
///   read-only report (§11); the command center is the <i>actionable landing</i>
///   that links each number to the grid that lets you act on it;</item>
///   <item><see cref="CommunityHub.Core.Reporting.ReportingService"/> — the
///   operational form-completion/shift dashboard.</item>
///   </list>
/// It reuses <see cref="OnboardingService"/> for persona-aware completion so the
/// percentages match the Onboarding overview exactly.
/// </summary>
public sealed class CommandCenterService
{
    private const string SponsorTaskPrefix = "sponsor:";

    private readonly CommunityHubDbContext _db;
    private readonly OnboardingService _onboarding;
    private readonly TimeProvider _clock;

    public CommandCenterService(
        CommunityHubDbContext db,
        OnboardingService onboarding,
        TimeProvider clock)
    {
        _db = db;
        _onboarding = onboarding;
        _clock = clock;
    }

    public async Task<CommandCenterSnapshot> BuildAsync(int eventId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var snap = new CommandCenterSnapshot
        {
            GeneratedAtUtc = now,
            EventDisplayName = await _db.Events
                .Where(e => e.Id == eventId)
                .Select(e => e.DisplayName)
                .FirstOrDefaultAsync(ct) ?? string.Empty,
        };

        await PopulateParticipationAsync(snap, eventId, ct);
        await PopulateOnboardingAsync(snap, eventId, ct);
        await PopulateHeadcountsAsync(snap, eventId, ct);
        await PopulateSessionsAndSponsorsAsync(snap, eventId, ct);
        await PopulateAttentionAsync(snap, eventId, today, ct);

        return snap;
    }

    private async Task PopulateParticipationAsync(
        CommandCenterSnapshot s, int eventId, CancellationToken ct)
    {
        var people = await _db.Participants
            .Where(p => p.EventId == eventId)
            .Select(p => p.IsActive)
            .ToListAsync(ct);
        s.TotalParticipants = people.Count;
        s.ActiveParticipants = people.Count(active => active);
        s.TotalAttendees = await _db.Attendees.CountAsync(a => a.EventId == eventId, ct);
    }

    private async Task PopulateOnboardingAsync(
        CommandCenterSnapshot s, int eventId, CancellationToken ct)
    {
        // Reuse the onboarding overview so the % is the same persona-aware number
        // the Onboarding page shows. One all-personas build gives the overall %;
        // its rows already carry the persona so the per-persona split needs no
        // extra DB round-trips.
        var overview = await _onboarding.BuildOverviewAsync(eventId, persona: null, ct);
        s.OnboardingOverallPercent = overview.OverallPercent;

        s.OnboardingByPersona = overview.Rows
            .GroupBy(r => r.Persona)
            .Select(g => new PersonaCompletion(g.Key, g.Count(r => r.IsComplete), g.Count()))
            .OrderByDescending(pc => pc.Outstanding)   // most outstanding first
            .ThenBy(pc => pc.Persona.ToString())
            .ToList();
    }

    private async Task PopulateHeadcountsAsync(
        CommandCenterSnapshot s, int eventId, CancellationToken ct)
    {
        var hotelRooms = await _db.HotelBookings
            .CountAsync(h => h.EventId == eventId && h.NeedsRoom, ct);
        var swag = await _db.SwagPreferences
            .CountAsync(w => w.EventId == eventId
                             && (w.WantsPolo || w.WantsJacket || w.WantsGift), ct);
        var lunch = await _db.LunchSignups
            .CountAsync(l => l.EventId == eventId && (l.LunchSetupDay || l.LunchPreDay), ct);
        // Dinner headcount = attendees (Attending) + their plus-ones.
        var dinnerRows = await _db.DinnerSignups
            .Where(d => d.EventId == eventId && d.Attending)
            .Select(d => d.PlusOneCount)
            .ToListAsync(ct);
        var dinner = dinnerRows.Count + dinnerRows.Sum();

        s.Headcounts = new List<HeadcountLine>
        {
            new("Hotel", hotelRooms, "/Organizer/HotelAssignments"),
            new("Swag", swag, "/Organizer/Swag"),
            new("Lunch", lunch, "/Organizer/Lunch"),
            new("Dinner", dinner, "/Organizer/Participants"),
        };
    }

    private async Task PopulateSessionsAndSponsorsAsync(
        CommandCenterSnapshot s, int eventId, CancellationToken ct)
    {
        var sessions = await _db.Sessions
            .Where(x => x.EventId == eventId && !x.IsServiceSession)
            .Select(x => new { x.StartsAt, x.Room })
            .ToListAsync(ct);
        s.SessionsTotal = sessions.Count;
        s.SessionsScheduled = sessions.Count(
            x => x.StartsAt != null && !string.IsNullOrWhiteSpace(x.Room));

        s.SponsorsTotal = await _db.SponsorInfos.CountAsync(x => x.EventId == eventId, ct);

        var sponsorTasks = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.SourceKey != null && t.SourceKey.StartsWith(SponsorTaskPrefix))
            .Select(t => t.State)
            .ToListAsync(ct);
        s.SponsorTasksTotal = sponsorTasks.Count;
        s.SponsorTasksDone = sponsorTasks.Count(state => state == TaskState.Done);
    }

    private async Task PopulateAttentionAsync(
        CommandCenterSnapshot s, int eventId, DateOnly today, CancellationToken ct)
    {
        var openTasks = await _db.Tasks
            .Where(t => t.EventId == eventId && t.State != TaskState.Done && t.DueDate != null)
            .Select(t => new
            {
                t.Id,
                t.Title,
                t.DueDate,
                Assignee = t.AssignedParticipant != null ? t.AssignedParticipant.FullName : null,
            })
            .ToListAsync(ct);

        s.TasksOverdue = openTasks.Count(t => t.DueDate!.Value < today);
        s.TasksDueToday = openTasks.Count(t => t.DueDate!.Value == today);

        s.OverdueAndTodayTasks = openTasks
            .Where(t => t.DueDate!.Value <= today)
            .OrderBy(t => t.DueDate!.Value)
            .ThenBy(t => t.Title)
            .Take(15)
            .Select(t => new AttentionTask(
                t.Id, t.Title, t.Assignee, t.DueDate, t.DueDate!.Value < today))
            .ToList();

        // Unassigned volunteer tasks (open coverage).
        var volTasks = await _db.VolunteerTasks
            .Where(t => t.EventId == eventId && t.Status != VolunteerTaskStatus.Cancelled)
            .Select(t => t.Assignments.Count)
            .ToListAsync(ct);
        var unassignedVol = volTasks.Count(c => c == 0);

        var openHelp = await _db.VolunteerHelpRequests
            .CountAsync(h => h.EventId == eventId && h.Status == VolunteerHelpStatus.Open, ct);

        var pendingVol = await _db.Participants
            .CountAsync(p => p.EventId == eventId
                             && p.Role == ParticipantRole.Volunteer && !p.IsActive, ct);

        var reconMismatches = await _db.Attendees
            .CountAsync(a => a.EventId == eventId && a.HasReconciliationMismatch, ct);

        var openActions = await _db.OrganizerActionItems
            .CountAsync(a => a.EventId == eventId && a.ResolvedAt == null, ct);

        var unscheduledSessions = s.SessionsUnscheduled;

        // Each attention tile carries the page + query that lands on the grid
        // already filtered to exactly the rows the number counts.
        s.AttentionTiles = new List<CommandCenterTile>
        {
            new("OverdueTasks", s.TasksOverdue, "/Organizer/TasksTable",
                new Dictionary<string, string?> { ["StateFilter"] = nameof(TaskState.Open), ["Sort"] = "due" },
                IsAttention: true),
            new("DueToday", s.TasksDueToday, "/Organizer/TasksTable",
                new Dictionary<string, string?> { ["StateFilter"] = nameof(TaskState.Open), ["Sort"] = "due" },
                IsAttention: true),
            new("UnassignedVolunteerTasks", unassignedVol, "/Organizer/VolunteerStructure",
                LinkQuery: null, IsAttention: true),
            new("OpenHelpRequests", openHelp, "/Organizer/VolunteerStructure",
                LinkQuery: null, IsAttention: true),
            new("PendingVolunteers", pendingVol, "/Organizer/Participants",
                new Dictionary<string, string?> { ["RoleFilter"] = nameof(ParticipantRole.Volunteer), ["ActiveFilter"] = "inactive" },
                IsAttention: true),
            new("ReconciliationMismatches", reconMismatches, "/Organizer/Index",
                LinkQuery: null, IsAttention: true),
            new("OpenActionItems", openActions, "/Organizer/ActionQueue",
                LinkQuery: null, IsAttention: true),
            new("UnscheduledSessions", unscheduledSessions, "/Organizer/Sessions",
                LinkQuery: null, IsAttention: true),
        };
    }
}
