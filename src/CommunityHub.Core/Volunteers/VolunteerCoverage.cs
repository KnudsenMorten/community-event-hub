using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Volunteers;

/// <summary>One hour of one day: how many people the tasks need, how many are on them, how many said they are here.</summary>
public sealed record CoverageHour(TimeOnly Start, int Needed, int Assigned, int Available)
{
    /// <summary>People needed at this hour beyond everyone who said they are here — recruiting is the only fix.</summary>
    public int ShortOfPeople => Math.Max(0, Needed - Available);

    /// <summary>Places still open at this hour (needed − assigned or queued).</summary>
    public int Open => Math.Max(0, Needed - Assigned);

    public string Label => $"{Start:HH\\:mm}–{Start.AddHours(1):HH\\:mm}";
}

/// <summary>One event day in the coverage overview.</summary>
/// <param name="NeededSlots">Sum of "people needed" over the day's volunteer tasks.</param>
/// <param name="FilledSlots">Places filled (published or queued), never more than needed per task.</param>
/// <param name="NeededHours">Person-hours the day's timed tasks need.</param>
/// <param name="PlannableHours">Person-hours volunteers can give that day under the §1380 rule — full-day
/// workers all their stated hours, everyone else 50%.</param>
public sealed record CoverageDay(
    DateOnly Day,
    int NeededSlots,
    int FilledSlots,
    double NeededHours,
    double PlannableHours,
    int VolunteersPresent,
    int FullDayWorkersPresent,
    IReadOnlyList<CoverageHour> Hours)
{
    public int OpenSlots => Math.Max(0, NeededSlots - FilledSlots);
    public double GapHours => Math.Max(0d, NeededHours - PlannableHours);

    /// <summary>
    /// The person-hour gap expressed as people: how many more volunteers giving half a day
    /// (<see cref="VolunteerPlanner.UnstatedDayHours"/> × <see cref="VolunteerPlanner.SessionAttendeeShare"/> = 5 h)
    /// would close it. An estimate, and labelled as one on the page.
    /// </summary>
    public int ExtraVolunteersAtHalfDay =>
        GapHours <= 0 ? 0 : (int)Math.Ceiling(GapHours / (VolunteerPlanner.UnstatedDayHours * VolunteerPlanner.SessionAttendeeShare));

    /// <summary>The hour with the biggest head-count shortage, or null when no hour is short.</summary>
    public CoverageHour? Peak => Hours.Where(h => h.ShortOfPeople > 0)
        .OrderByDescending(h => h.ShortOfPeople).ThenBy(h => h.Start).FirstOrDefault();
}

/// <summary>The whole overview.</summary>
public sealed record CoverageReport(IReadOnlyList<CoverageDay> Days, int UndatedTasks, int UndatedSlots, int UndatedOpen)
{
    public int NeededSlots => Days.Sum(d => d.NeededSlots) + UndatedSlots;
    public int OpenSlots => Days.Sum(d => d.OpenSlots) + UndatedOpen;
    public int ExtraVolunteers => Days.Count == 0 ? 0 : Days.Max(d => Math.Max(d.ExtraVolunteersAtHalfDay, d.Peak?.ShortOfPeople ?? 0));
}

/// <summary>A volunteer task as the overview sees it.</summary>
public sealed record CoverageTask(int Id, int Needed, int Assigned, DateOnly? Day, TimeWindow? Window);

/// <summary>A volunteer's answer for one day.</summary>
public sealed record CoverageAnswer(int ParticipantId, DateOnly Day, DayWindow Answer, bool FullDayWorker);

/// <summary>
/// §1380 — "how many volunteers are we short, and when?" (Leth 2026-11-01: <i>"jeg har en fornemmelse af vi
/// mangler volunteers, men jeg ved ikke hvor mange og jeg ved ikke hvornår"</i>). PURE — no DB, no clock — and
/// it never runs or seeds the planner: the only way to see this before was to run auto-assign.
/// </summary>
/// <remarks>
/// Two measures, because they answer different questions:
/// <list type="bullet">
/// <item><b>Head-count per hour</b> — people needed at that hour vs people who said they are here. A shortage
/// here can only be fixed by recruiting (or moving the task).</item>
/// <item><b>Person-hours per day</b> — what the tasks need vs what volunteers can give under the 50% rule. A gap
/// here with no hourly shortage means the people exist but are capped: mark more full-day workers.</item>
/// </list>
/// </remarks>
public static class VolunteerCoverage
{
    public static CoverageReport Compute(IEnumerable<CoverageTask> tasks, IEnumerable<CoverageAnswer> answers)
    {
        var taskList = tasks.ToList();
        var answersByDay = answers.Where(a => a.Answer.Present).GroupBy(a => a.Day).ToDictionary(g => g.Key, g => g.ToList());

        var undated = taskList.Where(t => t.Day is null).ToList();
        var days = new List<CoverageDay>();

        foreach (var dayGroup in taskList.Where(t => t.Day is not null).GroupBy(t => t.Day!.Value).OrderBy(g => g.Key))
        {
            var day = dayGroup.Key;
            var dayTasks = dayGroup.ToList();
            var present = answersByDay.TryGetValue(day, out var list) ? list : new List<CoverageAnswer>();

            var neededHours = dayTasks.Where(t => t.Window is not null).Sum(t => t.Needed * t.Window!.Value.Hours);
            var plannable = present.Sum(a => VolunteerPlanner.PlannableHours(a.Answer, a.FullDayWorker));

            var timed = dayTasks.Where(t => t.Window is not null).ToList();
            var hours = new List<CoverageHour>();
            if (timed.Count > 0)
            {
                var first = timed.Min(t => t.Window!.Value.Start.Hour);
                var last = timed.Max(t => t.Window!.Value.End.Minute > 0 ? t.Window!.Value.End.Hour + 1 : t.Window!.Value.End.Hour);
                for (var h = first; h < last && h < 24; h++)
                {
                    var slot = new TimeWindow(new TimeOnly(h, 0), h == 23 ? new TimeOnly(23, 59) : new TimeOnly(h + 1, 0));
                    var mid = new TimeOnly(h, 30);
                    var during = timed.Where(t => t.Window!.Value.Overlaps(slot)).ToList();
                    var needed = during.Sum(t => t.Needed);
                    if (needed == 0) continue;
                    var assigned = during.Sum(t => Math.Min(t.Assigned, t.Needed));
                    var available = present.Count(a => a.Answer.Window is not { } w || (w.Start <= mid && mid < w.End));
                    hours.Add(new CoverageHour(slot.Start, needed, assigned, available));
                }
            }

            days.Add(new CoverageDay(
                day,
                dayTasks.Sum(t => t.Needed),
                dayTasks.Sum(t => Math.Min(t.Assigned, t.Needed)),
                neededHours,
                plannable,
                present.Select(a => a.ParticipantId).Distinct().Count(),
                present.Where(a => a.FullDayWorker).Select(a => a.ParticipantId).Distinct().Count(),
                hours));
        }

        return new CoverageReport(days, undated.Count, undated.Sum(t => t.Needed), undated.Sum(t => Math.Max(0, t.Needed - t.Assigned)));
    }
}

/// <summary>§1380 — loads the edition's volunteer tasks, fills and answers, and runs <see cref="VolunteerCoverage"/>.</summary>
public sealed class VolunteerCoverageService
{
    private readonly CommunityHubDbContext _db;
    public VolunteerCoverageService(CommunityHubDbContext db) => _db = db;

    public async Task<CoverageReport> LoadAsync(int eventId, CancellationToken ct = default)
    {
        var taskRows = await _db.VolunteerTasks.Where(t => t.EventId == eventId).ToListAsync(ct);
        // Only work the VOLUNTEER queue handles — tracked-only and organizer-team tasks are not volunteer demand.
        taskRows = taskRows.Where(t => ResponsibleTeamRouter.Route(t.ResponsibleTeam) == AllocationQueueKind.Volunteer).ToList();

        // Filled = effective published assignments (active, not declined) + queued volunteer drafts — the same
        // budget the planner uses, so the overview and an auto-assign run never disagree about what is open.
        var published = await _db.VolunteerTaskAssignments
            .Where(a => a.EventId == eventId && a.Participant.IsActive && a.DecisionStatus != ShiftDecisionStatus.Declined)
            .GroupBy(a => a.TaskId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var queued = await _db.TaskAllocationDrafts
            .Where(d => d.EventId == eventId && d.TargetRole == ParticipantRole.Volunteer)
            .GroupBy(d => d.TaskId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var filled = published.Concat(queued).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Sum(x => x.N));

        var tasks = taskRows.Select(t => new CoverageTask(
            t.Id, t.ResourcesNeeded, filled.GetValueOrDefault(t.Id), t.DueDate, VolunteerTaskWindow.WindowOf(t)));

        var activeVolunteers = await _db.Participants
            .Where(p => p.EventId == eventId && p.Role == ParticipantRole.Volunteer && p.IsActive && !p.IsTestUser)
            .Select(p => p.Id).ToListAsync(ct);
        var workers = (await _db.VolunteerPlanningRoster.Where(r => r.EventId == eventId)
            .Select(r => r.ParticipantId).ToListAsync(ct)).ToHashSet();
        var rows = await _db.VolunteerDayAvailabilities
            .Where(v => v.EventId == eventId && activeVolunteers.Contains(v.ParticipantId))
            .Select(v => new { v.ParticipantId, v.Day, v.Level, v.Note }).ToListAsync(ct);

        // Several rows for one day should not happen; if they do, the most generous answer wins (as in the planner).
        var answers = rows
            .GroupBy(r => (r.ParticipantId, r.Day))
            .Select(g =>
            {
                var best = g.Select(r => VolunteerDayOptions.WindowFor(r.Day, r.Level, r.Note))
                    .OrderByDescending(x => x.Present).ThenByDescending(x => x.Window is null).First();
                return new CoverageAnswer(g.Key.ParticipantId, g.Key.Day, best, workers.Contains(g.Key.ParticipantId));
            })
            .ToList();

        return VolunteerCoverage.Compute(tasks, answers);
    }
}
