using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Forms;

[Authorize]
public class LunchModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;

    public LunchModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
    }

    /// <summary>Roles eligible to declare lunch attendance. Sponsors + Attendees excluded.</summary>
    public static readonly ParticipantRole[] EligibleRoles =
    {
        ParticipantRole.Volunteer,
        ParticipantRole.Speaker,
        ParticipantRole.Organizer,
        ParticipantRole.Media,
        ParticipantRole.EventPartner,
    };

    /// <summary>
    /// Crew/organizer roles that are on site for the SETUP days (Sun + Mon, before the
    /// pre-day): volunteers, organizers and media (video/camera). They see all three
    /// lunch days. Speakers + master-class speakers are NOT on site for setup; their
    /// form only offers the Pre-day / Master Class lunch (Tue, StartDate).
    /// </summary>
    public static bool ShowSetupDayFor(ParticipantRole role) =>
        role is ParticipantRole.Volunteer
             or ParticipantRole.Organizer
             or ParticipantRole.Media
             or ParticipantRole.EventPartner;

    /// <summary>
    /// FEATURE B: lunch is gated by ENTITLEMENT
    /// (<see cref="OrderItem.LunchPreDay"/> OR <see cref="OrderItem.LunchMainDay"/>).
    /// A sponsor-self-funded speaker IS entitled (LunchMainDay) and so still sees
    /// the form. A non-speaker role keeps its historical access even if its
    /// entitlement changed, so access is never silently removed; speakers are gated
    /// purely by entitlement.
    /// </summary>
    private async Task<bool> IsEligibleAsync(CurrentParticipant me, CancellationToken ct)
    {
        var entitled = await FormEntitlementGate.IsEntitledToAnyAsync(
            _db, me.EventId, me.ParticipantId, ct,
            OrderItem.LunchPreDay, OrderItem.LunchMainDay);
        var historicalNonSpeaker =
            me.Role != ParticipantRole.Speaker && EligibleRoles.Contains(me.Role);
        return entitled || historicalNonSpeaker;
    }

    /// <summary>SourceKey prefix used for the "complete the lunch form" task.</summary>
    public const string LunchTaskKey = "lunch-form";

    [BindProperty] public bool LunchEarlySetupDay { get; set; }
    [BindProperty] public bool LunchSetupDay { get; set; }
    [BindProperty] public bool LunchPreDay { get; set; }
    [BindProperty] public string? Notes { get; set; }

    public string FullName { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public ParticipantRole Role { get; private set; }
    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }

    public string EarlySetupDayLabel { get; private set; } = "Setup day (Sun)";
    public string SetupDayLabel { get; private set; } = "Setup day (Mon)";
    public string PreDayLabel { get; private set; } = "Pre-day (Master Class)";
    public string MainDayLabel { get; private set; } = "main day";
    public bool ShowSetupDay { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        FullName = me.FullName;
        Email = me.Email;
        Role = me.Role;
        if (!await IsEligibleAsync(me, ct))
        {
            AccessDenied = true;
            return Page();
        }

        await ResolveDayLabelsAsync(me.EventId, ct);
        ShowSetupDay = ShowSetupDayFor(me.Role);

        // Make sure the "complete the lunch form" task exists on first visit
        // so it shows up under My tasks even before the speaker fills it in.
        await EnsureLunchTaskExistsAsync(me.EventId, me.ParticipantId, ct);

        var existing = await _db.LunchSignups.FirstOrDefaultAsync(
            l => l.EventId == me.EventId && l.ParticipantId == me.ParticipantId, ct);
        if (existing is not null)
        {
            LunchEarlySetupDay = existing.LunchEarlySetupDay;
            LunchSetupDay = existing.LunchSetupDay;
            LunchPreDay = existing.LunchPreDay;
            Notes = existing.Notes;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        FullName = me.FullName;
        Email = me.Email;
        Role = me.Role;
        if (!await IsEligibleAsync(me, ct))
        {
            AccessDenied = true;
            return Page();
        }

        await ResolveDayLabelsAsync(me.EventId, ct);
        ShowSetupDay = ShowSetupDayFor(me.Role);

        var signup = await _db.LunchSignups.FirstOrDefaultAsync(
            l => l.EventId == me.EventId && l.ParticipantId == me.ParticipantId, ct);

        if (signup is null)
        {
            signup = new LunchSignup
            {
                EventId = me.EventId,
                ParticipantId = me.ParticipantId,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.LunchSignups.Add(signup);
        }
        else
        {
            signup.UpdatedAt = _clock.GetUtcNow();
        }

        // Speakers can't sign up for the setup days -- their form doesn't ask, so
        // ignore any value that came through (defensive against tampering).
        signup.LunchEarlySetupDay = ShowSetupDay && LunchEarlySetupDay;
        signup.LunchSetupDay = ShowSetupDay && LunchSetupDay;
        signup.LunchPreDay = LunchPreDay;
        signup.Notes = Notes;

        await _db.SaveChangesAsync(ct);

        // Saving the form is what marks the lunch task Done.
        await MarkLunchTaskDoneAsync(me.EventId, me.ParticipantId, ct);

        Message = "Your lunch preferences have been saved.";
        return Page();
    }

    private async Task EnsureLunchTaskExistsAsync(
        int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = $"{LunchTaskKey}:{participantId}";
        var exists = await _db.Tasks.AnyAsync(
            t => t.EventId == eventId
                 && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey, ct);
        if (exists) return;

        var due = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => (DateOnly?)e.StartDate.AddDays(-21))
            .FirstOrDefaultAsync(ct);

        _db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId,
            AssignedParticipantId = participantId,
            Title = "Complete the Lunch logistics form",
            Description = "Tell us which lunches you'll join (Pre-day / Master Class -- " +
                          "plus Setup day if you're a volunteer or organizer). " +
                          "Saving the form marks this task Done.",
            DueDate = due,
            State = TaskState.Open,
            SourceKey = sourceKey,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task MarkLunchTaskDoneAsync(
        int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = $"{LunchTaskKey}:{participantId}";
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId
                 && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey, ct);
        if (task is null || task.State == TaskState.Done) return;

        task.State = TaskState.Done;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Resolve display labels for Setup-day and Pre-day from the Event row.
    /// Setup-day = the day BEFORE Pre-day (or two days before StartDate when
    /// PreDayDate is null).
    /// </summary>
    private async Task ResolveDayLabelsAsync(int eventId, CancellationToken ct)
    {
        var evt = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.StartDate, e.EndDate })
            .FirstOrDefaultAsync(ct);
        if (evt is null) return;

        // The conference StartDate IS the pre-day / Master Class day; the two days
        // before it are setup days (crew + organizers + media). The EndDate is the
        // main day -- its lunch is booked for everyone, so it's a note, not a choice.
        var preDay        = evt.StartDate;
        var setupDay      = evt.StartDate.AddDays(-1);
        var earlySetupDay = evt.StartDate.AddDays(-2);

        EarlySetupDayLabel = $"Setup day ({earlySetupDay:dddd, MMM d yyyy})";
        SetupDayLabel      = $"Setup day ({setupDay:dddd, MMM d yyyy})";
        PreDayLabel        = $"Pre-day / Master Class ({preDay:dddd, MMM d yyyy})";
        MainDayLabel       = $"{evt.EndDate:dddd, MMM d yyyy}";
    }
}
