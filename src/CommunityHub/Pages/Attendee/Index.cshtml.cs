using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Attendee;

/// <summary>
/// The attendee area (CONTEXT.md 9z). Shows the signed-in attendee their
/// Master Class booking status, reconciled from Zoho. Booking itself stays
/// in Zoho Bookings - the hub deep-links out, it does not re-implement it.
/// </summary>
[Authorize]
public class IndexModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;

    public IndexModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant)
    {
        _db = db;
        _participant = participant;
    }

    public Core.Domain.Attendee? Record { get; private set; }

    /// <summary>The Zoho Bookings deep-link (CONTEXT.md 9z).</summary>
    public string BookingUrl => "https://book.expertslive.dk";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        Record = await _db.Attendees.FirstOrDefaultAsync(
            a => a.EventId == me.EventId && a.Email == me.Email, ct);
        return Page();
    }
}
