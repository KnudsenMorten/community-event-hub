using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages;

/// <summary>
/// The role-personalized hub landing page (CONTEXT.md section 4). Every role
/// sees a tailored set of sections: a section is shown only if it applies to
/// the participant's <see cref="ParticipantRole"/>. The page model resolves
/// the participant from the session cookie and loads their per-edition data
/// (tasks, form status) so the view can render each section's state.
/// </summary>
[Authorize]
public class IndexModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SpeakerDeadlineSeeder _speakerDeadlines;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SpeakerDeadlineSeeder speakerDeadlines,
        ILogger<IndexModel> logger)
    {
        _db = db;
        _participant = participant;
        _speakerDeadlines = speakerDeadlines;
        _logger = logger;
    }

    public CurrentParticipant Me { get; private set; } = null!;
    public string CommunityName { get; private set; } = "Community Hub";
    public string EventDisplayName { get; private set; } = string.Empty;

    // --- Section visibility (driven by role) --------------------------------
    public bool ShowHotel { get; private set; }
    public bool ShowDinner { get; private set; }
    public bool ShowVolunteerShifts { get; private set; }
    public bool ShowSpeakerDeadlines { get; private set; }
    public bool ShowSponsorPipeline { get; private set; }
    public bool ShowAttendeeArea { get; private set; }
    public bool ShowOrganizerTools { get; private set; }

    // --- Section data -------------------------------------------------------
    public int OpenTaskCount { get; private set; }
    public bool HotelSubmitted { get; private set; }
    public bool DinnerSubmitted { get; private set; }
    public bool VolunteerSubmitted { get; private set; }
    public MasterClassBookingStatus? AttendeeBookingStatus { get; private set; }

    public List<TaskRow> PendingTasks { get; private set; } = new();
    public List<TaskRow> CompletedTasks { get; private set; } = new();
    public record TaskRow(int Id, string Title, DateOnly? DueDate, TaskState State, int? DaysOverdue, string? Link);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null)
        {
            return RedirectToPage("/Login");
        }

        // First-time-after-login welcome: redirect once, then never again.
        // The /Welcome page sets WelcomeShownAt to UtcNow when the user clicks
        // OK, so subsequent /Index loads skip this branch.
        var welcomeShown = await _db.Participants
            .Where(p => p.Id == me.ParticipantId)
            .Select(p => p.WelcomeShownAt)
            .FirstOrDefaultAsync(ct);
        if (welcomeShown is null)
        {
            return RedirectToPage("/Welcome");
        }

        Me = me;

        var ev = await _db.Events
            .Where(e => e.Id == me.EventId)
            .Select(e => new { e.CommunityName, e.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (ev is not null)
        {
            CommunityName = ev.CommunityName;
            EventDisplayName = ev.DisplayName;
        }

        // Auto-seed speaker-deadline tasks on every visit by a speaker /
        // master-class speaker. Idempotent on SourceKey: existing tasks are
        // skipped, NEW deadlines added to the speaker-deadlines JSON config
        // appear on the next page load automatically -- no Functions run
        // required, no admin step.
        if (me.Role == ParticipantRole.Speaker
            || me.Role == ParticipantRole.MasterclassSpeaker)
        {
            try
            {
                await _speakerDeadlines.SeedAsync(me.EventId, ct);
            }
            catch (Exception ex)
            {
                // Don't fail the hub page if the config is missing/broken --
                // log it so the organizer can spot a deploy issue.
                _logger.LogWarning(ex,
                    "Speaker-deadline seeding failed for event {EventId}", me.EventId);
            }
        }

        ApplyRoleVisibility(me.Role);
        await LoadSectionDataAsync(me, ct);
        return Page();
    }

    /// <summary>
    /// Which sections each role sees. Defaults chosen per CONTEXT.md section 4:
    ///  - Organizer         : everything + organizer tools
    ///  - Speaker           : hotel, dinner, speaker deadlines
    ///  - MasterclassSpeaker : as Speaker (pre-day items fold into deadlines)
    ///  - Volunteer         : hotel, dinner, volunteer shifts
    ///  - Sponsor           : sponsor pipeline
    ///  - Attendee          : attendee area only
    /// Tasks are shown to every role.
    /// </summary>
    private void ApplyRoleVisibility(ParticipantRole role)
    {
        switch (role)
        {
            case ParticipantRole.Organizer:
                ShowHotel = ShowDinner = ShowVolunteerShifts = true;
                ShowSpeakerDeadlines = ShowSponsorPipeline = true;
                ShowOrganizerTools = true;
                break;
            case ParticipantRole.Speaker:
            case ParticipantRole.MasterclassSpeaker:
                ShowHotel = ShowDinner = ShowSpeakerDeadlines = true;
                break;
            case ParticipantRole.Volunteer:
                ShowHotel = ShowDinner = ShowVolunteerShifts = true;
                break;
            case ParticipantRole.Sponsor:
                ShowSponsorPipeline = true;
                break;
            case ParticipantRole.Attendee:
                ShowAttendeeArea = true;
                break;
        }
    }

    private async Task LoadSectionDataAsync(
        CurrentParticipant me, CancellationToken ct)
    {
        var allMyTasks = await _db.Tasks
            .Where(t => t.EventId == me.EventId
                        && t.AssignedParticipantId == me.ParticipantId)
            .Select(t => new { t.Id, t.Title, t.DueDate, t.State, t.SourceKey })
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        TaskRow ToRow(dynamic t)
        {
            int? overdue = (t.DueDate is not null && t.DueDate < today && t.State != TaskState.Done)
                ? today.DayNumber - ((DateOnly)t.DueDate).DayNumber
                : (int?)null;
            return new TaskRow(t.Id, t.Title, t.DueDate, t.State, overdue,
                LinkForSourceKey((string?)t.SourceKey));
        }
        PendingTasks = allMyTasks
            .Where(t => t.State != TaskState.Done)
            .OrderByDescending(t => t.DueDate is not null && t.DueDate < today)
            .ThenBy(t => t.DueDate)
            .ThenBy(t => t.Title)
            .Select(t => ToRow(t))
            .ToList();
        CompletedTasks = allMyTasks
            .Where(t => t.State == TaskState.Done)
            .OrderBy(t => t.Title)
            .Select(t => ToRow(t))
            .ToList();
        OpenTaskCount = PendingTasks.Count;

        if (ShowHotel)
        {
            HotelSubmitted = await _db.HotelBookings.AnyAsync(
                h => h.EventId == me.EventId
                     && h.ParticipantId == me.ParticipantId, ct);
        }

        if (ShowDinner)
        {
            DinnerSubmitted = await _db.DinnerSignups.AnyAsync(
                d => d.EventId == me.EventId
                     && d.ParticipantId == me.ParticipantId, ct);
        }

        if (ShowVolunteerShifts)
        {
            VolunteerSubmitted = await _db.VolunteerAvailabilities.AnyAsync(
                v => v.EventId == me.EventId
                     && v.ParticipantId == me.ParticipantId, ct);
        }

        if (ShowAttendeeArea)
        {
            AttendeeBookingStatus = await _db.Attendees
                .Where(a => a.EventId == me.EventId && a.Email == me.Email)
                .Select(a => (MasterClassBookingStatus?)a.BookingStatus)
                .FirstOrDefaultAsync(ct);
        }
    }

    /// <summary>
    /// Map a ParticipantTask.SourceKey to the page that completes it,
    /// so the hub landing can deep-link each pending task to its form.
    /// Returns null when no specific form is known; UI falls back to /Tasks.
    /// </summary>
    private static string? LinkForSourceKey(string? sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return null;
        if (sourceKey.StartsWith("lunch-form:",                   StringComparison.Ordinal)) return "/Forms/Lunch";
        if (sourceKey.StartsWith("swag-form:",                    StringComparison.Ordinal)) return "/Forms/Swag";
        if (sourceKey.StartsWith("travel:submit-ticket-invoice:", StringComparison.Ordinal)) return "/Forms/Travel";
        if (sourceKey.StartsWith("hotel-form:",                   StringComparison.Ordinal)) return "/Forms/Hotel";
        if (sourceKey.StartsWith("dinner-form:",                  StringComparison.Ordinal)) return "/Forms/Dinner";
        if (sourceKey.StartsWith("speaker-form:",                 StringComparison.Ordinal)) return "/Forms/Speaker";
        if (sourceKey.StartsWith("speakerdl:",                    StringComparison.Ordinal)) return "/Tasks";
        return null;
    }
}
