using CommunityHub.Auth;
using CommunityHub.Core.Attendees;
using CommunityHub.Core.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Attendee;

/// <summary>
/// The attendee "My Event" dashboard: one mobile-first page that pulls together
/// the practical info an attendee needs (the edition's dates / venue + a live
/// countdown), their Master Class status, and a self check-in ("I'm here")
/// action for the event days. Booking itself stays in Zoho Bookings - the hub
/// deep-links out and never re-implements seat reservation.
///
/// The view-model is computed by the pure, unit-tested
/// <see cref="AttendeeDashboardBuilder"/>; this page model only loads the data
/// and renders / persists.
/// </summary>
[Authorize]
public class MyEventModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHub.Core.Participants.ParticipantChecklistBuilder _checklist;
    private readonly CommunityHub.Core.Reminders.PublicSessionsService _sessions;
    private readonly ILogger<MyEventModel> _logger;

    public MyEventModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        CommunityHub.Core.Participants.ParticipantChecklistBuilder checklist,
        CommunityHub.Core.Reminders.PublicSessionsService sessions,
        ILogger<MyEventModel> logger)
    {
        _db = db;
        _participant = participant;
        _checklist = checklist;
        _sessions = sessions;
        _logger = logger;
    }

    public MyEventDashboard Dashboard { get; private set; } = null!;

    /// <summary>
    /// The attendee's personal agenda (§20 Attendee My-event): the session(s) they
    /// are registered for (their reconciled Master Class) plus the full public agenda
    /// with their own session highlighted, each carrying the public detail / ask /
    /// evaluate deep-links. Read-only aggregation over the SAME public session
    /// projection the public /Sessions page uses — no schema change.
    /// </summary>
    public MyEventSchedule Schedule { get; private set; } = new();

    /// <summary>
    /// The unified "what's still needed" checklist (REQUIREMENTS Top-8 #7) — the
    /// SAME shared component the Hub home and Tasks page render, so My-event no
    /// longer competes as a separate landing surface with its own task view.
    /// </summary>
    public CommunityHub.Core.Participants.ParticipantChecklist Checklist { get; private set; } =
        new(System.Array.Empty<CommunityHub.Core.Participants.ChecklistRow>(),
            System.Array.Empty<CommunityHub.Core.Participants.ChecklistRow>());

    /// <summary>The Zoho Bookings deep-link (booking stays at the source - DESIGN §6).</summary>
    public string BookingUrl => "https://book.expertslive.dk";

    /// <summary>Set after a successful self check-in so the page can show a confirmation toast.</summary>
    [TempData]
    public bool JustCheckedIn { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        Dashboard = await BuildAsync(me.EventId, me.Email, ct);
        Checklist = await _checklist.BuildAsync(me.EventId, me.ParticipantId, ct);
        Schedule = await BuildScheduleAsync(me.EventId, me.Email, ct);
        return Page();
    }

    /// <summary>
    /// Self check-in. Idempotent: stamps <c>CheckedInAt</c> only if the attendee
    /// has a ticket-holding record and has not already checked in. Re-posting is
    /// a no-op. Server-side re-validates the window so a stale page can't force
    /// an out-of-window check-in.
    /// </summary>
    public async Task<IActionResult> OnPostCheckInAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var record = await _db.Attendees.FirstOrDefaultAsync(
            a => a.EventId == me.EventId && a.Email == me.Email, ct);

        if (record is not null && record.CheckedInAt is null)
        {
            // Re-build with the SAME inputs to re-check the window server-side.
            var dash = AttendeeDashboardBuilder.Build(record, await LoadInfoAsync(me.EventId, ct), DateTimeOffset.UtcNow);
            if (dash.CanCheckIn)
            {
                record.CheckedInAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                JustCheckedIn = true;
                _logger.LogInformation(
                    "Attendee self check-in: event {EventId}, participant {ParticipantId}", me.EventId, me.ParticipantId);
            }
        }

        return RedirectToPage();
    }

    private async Task<MyEventDashboard> BuildAsync(int eventId, string email, CancellationToken ct)
    {
        var record = await _db.Attendees.FirstOrDefaultAsync(
            a => a.EventId == eventId && a.Email == email, ct);
        return AttendeeDashboardBuilder.Build(record, await LoadInfoAsync(eventId, ct), DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Build the attendee's personal schedule from the SAME public session projection
    /// the public /Sessions page uses (active edition, published gate), then mark the
    /// session(s) this attendee booked (their reconciled Master Class) as "mine".
    /// Read-only; never writes; falls back to an empty schedule when there is no
    /// active edition / no published sessions.
    /// </summary>
    private async Task<MyEventSchedule> BuildScheduleAsync(int eventId, string email, CancellationToken ct)
    {
        var view = await _sessions.BuildAsync(ct: ct);
        if (view is null) return new MyEventSchedule();

        var record = await _db.Attendees.FirstOrDefaultAsync(
            a => a.EventId == eventId && a.Email == email, ct);

        return MyEventScheduleBuilder.Build(view.Sessions, record);
    }

    private async Task<EventPracticalInfo> LoadInfoAsync(int eventId, CancellationToken ct)
    {
        var ev = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new
            {
                e.DisplayName,
                e.CommunityName,
                e.VenueName,
                e.StartDate,
                e.EndDate,
                e.PreDayDate
            })
            .FirstAsync(ct);

        return new EventPracticalInfo(
            ev.DisplayName, ev.CommunityName, ev.VenueName,
            ev.StartDate, ev.EndDate, ev.PreDayDate);
    }
}
