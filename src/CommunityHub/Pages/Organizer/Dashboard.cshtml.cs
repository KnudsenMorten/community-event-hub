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

    // --- Pending volunteer applications (Role=Volunteer, IsActive=false) --
    // Fed by the public anonymous /volunteer/signup page. Each row here is
    // an applicant awaiting organizer review. Approving flips IsActive=true
    // (the person can then PIN-log-in + use the volunteer-availability form).
    // Rejecting deletes the row outright -- nothing in the system depends
    // on a rejected applicant since they were never an active Participant.
    public List<PendingVolunteer> PendingVolunteers { get; private set; } = new();
    public string? PendingActionMessage { get; private set; }
    public record PendingVolunteer(int Id, string Name, string Email, string? Phone, DateTimeOffset SubmittedAt);

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

    // --- Surveys (ELDK27 Topics) ----------------------------------------
    // Aggregate counts only -- the full breakdown lives at the public
    // dashboard at /survey/eldk27-topics/results so organizers + speakers
    // can share one link.
    public int SurveyTotalResponses { get; private set; }
    public DateTimeOffset? SurveyLatestAt { get; private set; }
    public string? SurveyTopTrackName { get; private set; }
    public int SurveyTopTrackCount { get; private set; }
    public const string SurveySlug = "eldk27-topics";

    // --- Sponsor leads + group photos + app game (v1.2.8) -----------------
    public int LeadsTotal { get; private set; }
    public int LeadsLast7d { get; private set; }
    public int LeadsOpen { get; private set; }
    public int PhotoRegs { get; private set; }
    public int PhotoUnscheduled { get; private set; }
    public int AppGameSponsors { get; private set; }
    public int AppGameUnconfirmed { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Report = await _reporting.BuildAsync(me.EventId, ct);
        await LoadSpeakerDeadlineGraphicsAsync(me.EventId, ct);
        await LoadTravelAndLunchGraphicsAsync(me.EventId, ct);
        await LoadPendingVolunteersAsync(me.EventId, ct);
        await LoadSurveyStatsAsync(ct);
        await LoadPipelineStatsAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadPipelineStatsAsync(int eventId, CancellationToken ct)
    {
        var weekAgo = _clock.GetUtcNow().AddDays(-7);
        var leads = _db.SponsorLeads.Where(l => l.EventId == eventId);
        LeadsTotal  = await leads.CountAsync(ct);
        LeadsLast7d = await leads.CountAsync(l => l.CapturedAt >= weekAgo, ct);
        LeadsOpen   = await leads.CountAsync(l => l.Status == SponsorLeadStatus.Open, ct);

        var photos = _db.GroupPhotoRegistrations.Where(g => g.EventId == eventId);
        PhotoRegs        = await photos.CountAsync(ct);
        PhotoUnscheduled = await photos.CountAsync(g => g.ScheduledAtUtc == null, ct);

        var game = _db.AppGameParticipations.Where(a => a.EventId == eventId);
        AppGameSponsors    = await game.CountAsync(ct);
        AppGameUnconfirmed = await game.CountAsync(a => !a.GiftConfirmed, ct);
    }

    private async Task LoadSurveyStatsAsync(CancellationToken ct)
    {
        // Survey responses are NOT scoped to EventId -- the survey is
        // its own anonymous artefact (the slug is the event tie-in).
        // Track-popularity peek so the card shows "top track so far".
        var responses = await _db.SurveyResponses
            .Where(r => r.SurveySlug == SurveySlug)
            .Select(r => new { r.SubmittedAt, r.SelectedTrackId })
            .ToListAsync(ct);
        SurveyTotalResponses = responses.Count;
        SurveyLatestAt = responses.Count == 0 ? null : responses.Max(r => r.SubmittedAt);
        var top = responses
            .GroupBy(r => r.SelectedTrackId, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (top is not null)
        {
            SurveyTopTrackName = top.Key;
            SurveyTopTrackCount = top.Count();
        }
    }

    /// <summary>
    /// Approve a volunteer applicant: flip IsActive=true so they can
    /// PIN-log-in and access the volunteer-availability form. Re-renders
    /// the dashboard so the row disappears from the pending list.
    /// </summary>
    public async Task<IActionResult> OnPostApproveVolunteerAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var applicant = await _db.Participants.FirstOrDefaultAsync(
            p => p.Id == id && p.EventId == me.EventId
                 && p.Role == ParticipantRole.Volunteer && !p.IsActive, ct);
        if (applicant is not null)
        {
            applicant.IsActive = true;
            await _db.SaveChangesAsync(ct);
            PendingActionMessage = $"Approved {applicant.FullName} ({applicant.Email}). They can now sign in.";
        }
        else
        {
            PendingActionMessage = "That applicant was not found (or was already processed).";
        }
        return RedirectToPage(new { msg = PendingActionMessage });
    }

    /// <summary>
    /// Reject a volunteer applicant: hard-delete the inactive row. Safe
    /// because the row never had AssignedTasks / LoginPins / etc. (those
    /// are only created after the person actually engages with the hub).
    /// </summary>
    public async Task<IActionResult> OnPostRejectVolunteerAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var applicant = await _db.Participants.FirstOrDefaultAsync(
            p => p.Id == id && p.EventId == me.EventId
                 && p.Role == ParticipantRole.Volunteer && !p.IsActive, ct);
        if (applicant is not null)
        {
            _db.Participants.Remove(applicant);
            await _db.SaveChangesAsync(ct);
            PendingActionMessage = $"Declined {applicant.FullName} ({applicant.Email}). Row removed.";
        }
        else
        {
            PendingActionMessage = "That applicant was not found (or was already processed).";
        }
        return RedirectToPage(new { msg = PendingActionMessage });
    }

    [BindProperty(SupportsGet = true)]
    public string? Msg { get; set; }

    private async Task LoadPendingVolunteersAsync(int eventId, CancellationToken ct)
    {
        PendingVolunteers = await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.Role == ParticipantRole.Volunteer
                        && !p.IsActive)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new PendingVolunteer(p.Id, p.FullName, p.Email, p.Phone, p.CreatedAt))
            .ToListAsync(ct);
        if (!string.IsNullOrEmpty(Msg))
        {
            PendingActionMessage = Msg;
        }
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
