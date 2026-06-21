using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer-gated setting to enable/disable calendar sync for the edition
/// (REQUIREMENTS §5). Calendar sync is the per-user subscribable iCal feed
/// (<c>GET /cal/{token}.ics</c>) plus the .ics invite attached to activation
/// emails. When the organizer turns it OFF for the edition:
///  - the feed returns 404 for every participant of the edition;
///  - the hub's "Add to my calendar" card is hidden;
///  - no .ics invite is attached to activation emails.
/// The switch lives on the edition's <see cref="Event.CalendarSyncEnabled"/> row
/// (no new table) and defaults ON.
/// </summary>
[Authorize]
public class CalendarSettingsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHubDbContext _db;
    private readonly ParticipantCalendarBuilder _calendar;

    public CalendarSettingsModel(
        ICurrentParticipantAccessor participant,
        CommunityHubDbContext db,
        ParticipantCalendarBuilder calendar)
    {
        _participant = participant;
        _db = db;
        _calendar = calendar;
    }

    [BindProperty]
    public bool Enabled { get; set; }

    public bool AccessDenied { get; private set; }
    public string? SavedMessage { get; private set; }

    /// <summary>
    /// Read-only preview of the organizer's OWN calendar feed — the same items a
    /// calendar client would see when it subscribes to the per-user .ics. Lets the
    /// organizer confirm what the feed contains before sharing the subscribe URL.
    /// </summary>
    public IReadOnlyList<ParticipantCalendarBuilder.CalendarPreviewRow> FeedPreview { get; private set; }
        = System.Array.Empty<ParticipantCalendarBuilder.CalendarPreviewRow>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Enabled = await _db.Events
            .Where(e => e.Id == me.EventId)
            .Select(e => e.CalendarSyncEnabled)
            .FirstOrDefaultAsync(ct);

        FeedPreview = await _calendar.BuildPreviewAsync(me.ParticipantId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == me.EventId, ct);
        if (ev is null) return NotFound();

        ev.CalendarSyncEnabled = Enabled;
        await _db.SaveChangesAsync(ct);

        SavedMessage = Enabled
            ? "Calendar sync is ON — participants can subscribe to their calendar feed and get .ics invites."
            : "Calendar sync is OFF — the calendar feed is disabled and no .ics invites are sent for this edition.";
        return Page();
    }
}
