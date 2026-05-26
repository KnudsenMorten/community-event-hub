using System.Text;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>One row of the participant grid: a person plus their hotel data.</summary>
public sealed class ParticipantGridRow
{
    public int ParticipantId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public ParticipantRole Role { get; set; }
    public bool IsActive { get; set; }

    // Hotel fields (null if the person has no HotelBooking row).
    public bool HasHotelBooking { get; set; }
    public bool NeedsRoom { get; set; }
    public DateOnly? CheckInDate { get; set; }
    public DateOnly? CheckOutDate { get; set; }
}

/// <summary>
/// Excel-like organizer grid for managing participants and their hotel data
/// in one table (CONTEXT.md - organizer data management). The columns are the
/// things that change as the event nears: active/inactive (people drop out)
/// and hotel check-in / check-out dates. One row per person, joined across the
/// Participant and HotelBooking tables. Inline edit + per-row save + CSV
/// export. Organizer-only.
///
/// Dinner and volunteer detail stay on their own pages - cramming every field
/// from four tables into one grid makes it unreadable.
/// </summary>
[Authorize]
public class DataGridModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;

    public DataGridModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
    }

    public bool AccessDenied { get; private set; }
    public List<ParticipantGridRow> Rows { get; private set; } = new();
    public string? Message { get; private set; }

    /// <summary>Role filter. Default covers speakers + volunteers.</summary>
    [BindProperty(SupportsGet = true)]
    public string RoleFilter { get; set; } = "speakers-volunteers";

    /// <summary>Status filter: "all", "active", "inactive".</summary>
    [BindProperty(SupportsGet = true)]
    public string ActiveFilter { get; set; } = "active";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer)
        {
            AccessDenied = true;
            return Page();
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Save one edited grid row - participant + hotel fields together.</summary>
    public async Task<IActionResult> OnPostSaveRowAsync(
        int participantId, bool isActive, bool needsRoom,
        DateOnly? checkInDate, DateOnly? checkOutDate, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer)
        {
            AccessDenied = true;
            return Page();
        }

        var participant = await _db.Participants.FirstOrDefaultAsync(
            p => p.Id == participantId && p.EventId == me.EventId, ct);
        if (participant is not null)
        {
            participant.IsActive = isActive;

            // Upsert the hotel booking for this person.
            var hotel = await _db.HotelBookings.FirstOrDefaultAsync(
                h => h.EventId == me.EventId
                     && h.ParticipantId == participantId, ct);
            if (hotel is null)
            {
                hotel = new HotelBooking
                {
                    EventId = me.EventId,
                    ParticipantId = participantId,
                    CreatedAt = _clock.GetUtcNow(),
                };
                _db.HotelBookings.Add(hotel);
            }
            else
            {
                hotel.UpdatedAt = _clock.GetUtcNow();
            }
            hotel.NeedsRoom = needsRoom;
            hotel.CheckInDate = checkInDate;
            hotel.CheckOutDate = checkOutDate;

            await _db.SaveChangesAsync(ct);
            Message = $"Saved {participant.FullName}.";
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Export the current filtered grid as CSV.</summary>
    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer)
        {
            return Forbid();
        }

        await LoadAsync(me.EventId, ct);

        var header = new[]
        {
            "Name", "Email", "Role", "Active",
            "Needs room", "Check-in", "Check-out",
        };
        var rows = Rows.Select(r => (IReadOnlyList<string>)new[]
        {
            r.FullName,
            r.Email,
            r.Role.ToString(),
            r.IsActive ? "Yes" : "No",
            r.HasHotelBooking ? (r.NeedsRoom ? "Yes" : "No") : string.Empty,
            r.CheckInDate?.ToString("dd/MM/yyyy") ?? string.Empty,
            r.CheckOutDate?.ToString("dd/MM/yyyy") ?? string.Empty,
        });

        var csv = CsvWriter.Write(header, rows);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", "participants.csv");
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        var query = _db.Participants.Where(p => p.EventId == eventId);

        // Role filter.
        query = RoleFilter switch
        {
            "speakers-volunteers" => query.Where(p =>
                p.Role == ParticipantRole.Speaker
                || p.Role == ParticipantRole.MasterclassSpeaker
                || p.Role == ParticipantRole.Volunteer),
            "all" => query,
            _ => Enum.TryParse<ParticipantRole>(RoleFilter, out var r)
                ? query.Where(p => p.Role == r)
                : query,
        };

        // Status filter.
        query = ActiveFilter switch
        {
            "active" => query.Where(p => p.IsActive),
            "inactive" => query.Where(p => !p.IsActive),
            _ => query,
        };

        var people = await query
            .OrderBy(p => p.Role)
            .ThenBy(p => p.FullName)
            .ToListAsync(ct);

        // Hotel bookings for those people, in one query.
        var personIds = people.Select(p => p.Id).ToList();
        var hotels = await _db.HotelBookings
            .Where(h => h.EventId == eventId
                        && personIds.Contains(h.ParticipantId))
            .ToDictionaryAsync(h => h.ParticipantId, h => h, ct);

        Rows = people.Select(p =>
        {
            hotels.TryGetValue(p.Id, out var h);
            return new ParticipantGridRow
            {
                ParticipantId = p.Id,
                FullName = p.FullName,
                Email = p.Email,
                Role = p.Role,
                IsActive = p.IsActive,
                HasHotelBooking = h is not null,
                NeedsRoom = h?.NeedsRoom ?? false,
                CheckInDate = h?.CheckInDate,
                CheckOutDate = h?.CheckOutDate,
            };
        }).ToList();
    }
}
