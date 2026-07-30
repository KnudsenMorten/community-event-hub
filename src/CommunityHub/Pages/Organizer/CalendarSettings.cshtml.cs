using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer-gated setting to enable/disable calendar invitations for the edition.
/// When ON (the default), the hub's "Add Reminder" / "Email me a calendar invite"
/// actions e-mail participants a calendar INVITATION (REQUIREMENTS §193) for their
/// task due dates, sessions, etc. When the organizer turns it OFF for the edition,
/// those actions send nothing. The switch lives on the edition's
/// <see cref="Event.CalendarSyncEnabled"/> row (no new table) and defaults ON.
///
/// REQUIREMENTS §201: the per-user subscribable iCal feed and "Add to my calendar"
/// subscription were removed — there is no feed to preview or subscribe to any more.
/// </summary>
[Authorize]
public class CalendarSettingsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHubDbContext _db;

    public CalendarSettingsModel(
        ICurrentParticipantAccessor participant,
        CommunityHubDbContext db)
    {
        _participant = participant;
        _db = db;
    }

    [BindProperty]
    public bool Enabled { get; set; }

    /// <summary>
    /// REQUIREMENTS §257 — the AUTOMATIC calendar-invite push (Dinner / Hotel /
    /// Hotel-placement / Master-Class). Off by default: the confirmation e-mails carry a
    /// manual "Add to calendar" link instead of pushing an invite into the inbox.
    /// </summary>
    [BindProperty]
    public bool AutoInvitesEnabled { get; set; }

    public bool AccessDenied { get; private set; }
    public string? SavedMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var settings = await _db.Events
            .Where(e => e.Id == me.EventId)
            .Select(e => new { e.CalendarSyncEnabled, e.AutoCalendarInvitesEnabled })
            .FirstOrDefaultAsync(ct);
        Enabled = settings?.CalendarSyncEnabled ?? false;
        AutoInvitesEnabled = settings?.AutoCalendarInvitesEnabled ?? false;

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
        ev.AutoCalendarInvitesEnabled = AutoInvitesEnabled;
        await _db.SaveChangesAsync(ct);

        var manualPart = Enabled
            ? "the manual \"Email me a calendar invite\" / \"Add to calendar\" actions are ON"
            : "the manual \"Email me a calendar invite\" / \"Add to calendar\" actions are OFF";
        var autoPart = AutoInvitesEnabled
            ? "automatic invites (dinner/hotel/master-class) are ON — a calendar invite is pushed on submit"
            : "automatic invites are OFF — confirmations carry an \"Add to calendar\" link instead";
        SavedMessage = $"Saved. {char.ToUpperInvariant(manualPart[0])}{manualPart[1..]}; {autoPart}.";
        return Page();
    }
}
