using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

[Authorize]
public class DashboardModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ReportingService _reporting;
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public DashboardModel(
        ICurrentParticipantAccessor participant,
        ReportingService reporting,
        CommunityHubDbContext db,
        TimeProvider clock)
    {
        _participant = participant;
        _reporting = reporting;
        _db = db;
        _clock = clock;
    }

    public bool AccessDenied { get; private set; }
    public DashboardReport? Report { get; private set; }

    // --- Speaker-deadline graphics ----------------------------------------
    public List<DeadlineStat> SpeakerDeadlines { get; private set; } = new();
    public List<OverdueSpeaker> TopOverdueSpeakers { get; private set; } = new();
    public int SpeakerTasksTotal { get; private set; }
    public int SpeakerTasksDone { get; private set; }
    public int SpeakerTasksOverdue { get; private set; }

    public record DeadlineStat(string Title, int Total, int Done, int Overdue, int PercentDone);
    public record OverdueSpeaker(string Name, string Email, int OverdueCount);

    // --- Travel + lunch graphics ------------------------------------------
    public int TravelClaiming { get; private set; }
    public int TravelPaid { get; private set; }
    public decimal TravelClaimedEur { get; private set; }
    public decimal TravelOutstandingEur { get; private set; }
    public int LunchSetupDayCount { get; private set; }
    public int LunchPreDayCount { get; private set; }
    public string LunchSetupDayLabel { get; private set; } = "Setup day";
    public string LunchPreDayLabel { get; private set; } = "Pre-day";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Report = await _reporting.BuildAsync(me.EventId, ct);
        await LoadSpeakerDeadlineGraphicsAsync(me.EventId, ct);
        await LoadTravelAndLunchGraphicsAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadTravelAndLunchGraphicsAsync(int eventId, CancellationToken ct)
    {
        var travel = await _db.TravelReimbursements
            .Where(t => t.EventId == eventId)
            .Select(t => new { t.RequestReimbursement, t.ClaimAmountEur, t.IsPaid })
            .ToListAsync(ct);
        var claiming = travel.Where(t => t.RequestReimbursement).ToList();
        TravelClaiming       = claiming.Count;
        TravelPaid           = claiming.Count(t => t.IsPaid);
        TravelClaimedEur     = claiming.Sum(t => t.ClaimAmountEur ?? 0);
        TravelOutstandingEur = TravelClaimedEur
            - claiming.Where(t => t.IsPaid).Sum(t => t.ClaimAmountEur ?? 0);

        var evt = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.StartDate, e.PreDayDate })
            .FirstOrDefaultAsync(ct);
        if (evt is not null)
        {
            var preDay   = evt.PreDayDate ?? evt.StartDate.AddDays(-1);
            var setupDay = preDay.AddDays(-1);
            LunchSetupDayLabel = $"Setup day ({setupDay:dddd, MMM d})";
            LunchPreDayLabel   = $"Pre-day ({preDay:dddd, MMM d})";
        }
        var lunch = await _db.LunchSignups
            .Where(l => l.EventId == eventId)
            .Select(l => new { l.LunchSetupDay, l.LunchPreDay })
            .ToListAsync(ct);
        LunchSetupDayCount = lunch.Count(l => l.LunchSetupDay);
        LunchPreDayCount   = lunch.Count(l => l.LunchPreDay);
    }

    private async Task LoadSpeakerDeadlineGraphicsAsync(int eventId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

        var tasks = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.AssignedParticipantId != null
                        && (t.AssignedParticipant!.Role == ParticipantRole.Speaker
                            || t.AssignedParticipant!.Role == ParticipantRole.MasterclassSpeaker))
            .Select(t => new
            {
                t.Title,
                t.DueDate,
                t.State,
                AssigneeName = t.AssignedParticipant!.FullName,
                AssigneeEmail = t.AssignedParticipant!.Email,
            })
            .ToListAsync(ct);

        SpeakerTasksTotal   = tasks.Count;
        SpeakerTasksDone    = tasks.Count(t => t.State == TaskState.Done);
        SpeakerTasksOverdue = tasks.Count(t => t.State != TaskState.Done
                                                 && t.DueDate is not null
                                                 && t.DueDate < today);

        SpeakerDeadlines = tasks
            .GroupBy(t => t.Title)
            .Select(g =>
            {
                var total = g.Count();
                var done = g.Count(x => x.State == TaskState.Done);
                var overdue = g.Count(x => x.State != TaskState.Done
                                            && x.DueDate is not null
                                            && x.DueDate < today);
                var pct = total == 0 ? 0 : (int)Math.Round(100.0 * done / total);
                return new DeadlineStat(g.Key, total, done, overdue, pct);
            })
            .OrderByDescending(s => s.Overdue)
            .ThenBy(s => s.PercentDone)
            .ThenBy(s => s.Title)
            .ToList();

        TopOverdueSpeakers = tasks
            .Where(t => t.State != TaskState.Done
                        && t.DueDate is not null
                        && t.DueDate < today)
            .GroupBy(t => new { t.AssigneeName, t.AssigneeEmail })
            .Select(g => new OverdueSpeaker(g.Key.AssigneeName, g.Key.AssigneeEmail, g.Count()))
            .OrderByDescending(s => s.OverdueCount)
            .ThenBy(s => s.Name)
            .Take(10)
            .ToList();
    }
}
