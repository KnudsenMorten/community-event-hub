using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>One participant placed in (or assignable to) a hotel.</summary>
public sealed record HotelOccupant(
    int ParticipantId,
    string FullName,
    string Email,
    ParticipantRole Role,
    int? HotelId,
    string? HotelConfirmationNumber,
    bool NeedsRoom);

/// <summary>
/// A hotel plus the participants placed in it — the "group everyone by hotel"
/// view so organizers can manage the room block per hotel. <see cref="Hotel"/>
/// is null for the synthetic "Not assigned" group.
/// </summary>
public sealed record HotelGroup(Hotel? Hotel, IReadOnlyList<HotelOccupant> Occupants)
{
    public int Count => Occupants.Count;
    public int Confirmed => Occupants.Count(o => !string.IsNullOrWhiteSpace(o.HotelConfirmationNumber));
}

/// <summary>
/// The single server-side authority for multi-hotel management (REQUIREMENTS §3
/// hotels). Organizers CRUD the edition's <see cref="Hotel"/> rows, assign each
/// <see cref="Participant"/> to one, set the per-person
/// <see cref="Participant.HotelConfirmationNumber"/>, and view everyone grouped
/// by hotel. Everything is edition-scoped; nothing here writes outbound mail
/// (the hotel email enrichment is the email system's job).
/// </summary>
public sealed class HotelManagementService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public HotelManagementService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    // ----- Hotel CRUD -------------------------------------------------------

    public async Task<IReadOnlyList<Hotel>> ListHotelsAsync(int eventId, CancellationToken ct = default) =>
        await _db.Hotels
            .Where(h => h.EventId == eventId)
            .OrderBy(h => h.Name)
            .ToListAsync(ct);

    public async Task<Hotel?> GetHotelAsync(int eventId, int hotelId, CancellationToken ct = default) =>
        await _db.Hotels.FirstOrDefaultAsync(h => h.EventId == eventId && h.Id == hotelId, ct);

    /// <summary>Create a hotel. Throws <see cref="ArgumentException"/> on a blank name.</summary>
    public async Task<Hotel> CreateHotelAsync(
        int eventId, string name, string? address, string? contactEmail, string? notes,
        int? roomBlockSize = null, CancellationToken ct = default)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) throw new ArgumentException("Hotel name is required.", nameof(name));

        var hotel = new Hotel
        {
            EventId = eventId,
            Name = name,
            Address = Blank(address),
            ContactEmail = Blank(contactEmail),
            Notes = Blank(notes),
            RoomBlockSize = NormalizeBlockSize(roomBlockSize),
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.Hotels.Add(hotel);
        await _db.SaveChangesAsync(ct);
        return hotel;
    }

    /// <summary>Update a hotel in place. Returns false if it does not exist in the edition.</summary>
    public async Task<bool> UpdateHotelAsync(
        int eventId, int hotelId, string name, string? address, string? contactEmail, string? notes,
        int? roomBlockSize = null, CancellationToken ct = default)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) throw new ArgumentException("Hotel name is required.", nameof(name));

        var hotel = await GetHotelAsync(eventId, hotelId, ct);
        if (hotel is null) return false;

        hotel.Name = name;
        hotel.Address = Blank(address);
        hotel.ContactEmail = Blank(contactEmail);
        hotel.Notes = Blank(notes);
        hotel.RoomBlockSize = NormalizeBlockSize(roomBlockSize);
        hotel.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Normalise a room-block size posted from the UI: a null clears the block
    /// (occupancy view shows "not set"); a negative number is clamped to 0 so a
    /// stray "-1" never recorded a nonsense block. 0 is a legitimate value (a
    /// hotel kept for reference with no rooms held).
    /// </summary>
    private static int? NormalizeBlockSize(int? size) =>
        size is null ? null : Math.Max(0, size.Value);

    /// <summary>
    /// Delete a hotel. Any participants placed in it are first un-assigned
    /// (HotelId → null) so the row can be removed without orphaning a FK.
    /// </summary>
    public async Task<bool> DeleteHotelAsync(int eventId, int hotelId, CancellationToken ct = default)
    {
        var hotel = await GetHotelAsync(eventId, hotelId, ct);
        if (hotel is null) return false;

        var placed = await _db.Participants
            .Where(p => p.EventId == eventId && p.HotelId == hotelId)
            .ToListAsync(ct);
        foreach (var p in placed) p.HotelId = null;

        _db.Hotels.Remove(hotel);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ----- Assignment + confirmation ---------------------------------------

    /// <summary>
    /// Place a participant in a hotel (or clear the placement when
    /// <paramref name="hotelId"/> is null). Validates that the participant and
    /// the hotel both belong to the edition. Returns false if either is missing.
    /// </summary>
    public async Task<bool> AssignParticipantAsync(
        int eventId, int participantId, int? hotelId, CancellationToken ct = default)
    {
        var p = await _db.Participants
            .FirstOrDefaultAsync(x => x.Id == participantId && x.EventId == eventId, ct);
        if (p is null) return false;

        if (hotelId is not null)
        {
            var exists = await _db.Hotels
                .AnyAsync(h => h.Id == hotelId.Value && h.EventId == eventId, ct);
            if (!exists) return false;
        }

        p.HotelId = hotelId;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Set (or clear) a participant's per-person hotel confirmation number.</summary>
    public async Task<bool> SetConfirmationNumberAsync(
        int eventId, int participantId, string? confirmationNumber, CancellationToken ct = default)
    {
        var p = await _db.Participants
            .FirstOrDefaultAsync(x => x.Id == participantId && x.EventId == eventId, ct);
        if (p is null) return false;

        p.HotelConfirmationNumber = Blank(confirmationNumber);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ----- Group-by-hotel view ---------------------------------------------

    /// <summary>
    /// Everyone in the edition grouped by their assigned hotel — one
    /// <see cref="HotelGroup"/> per hotel (alphabetical), then a final
    /// "Not assigned" group (<see cref="HotelGroup.Hotel"/> == null) for the
    /// unplaced. Empty hotels still appear so an organizer sees a hotel with 0
    /// people. Only people who indicated they need a room are listed by default
    /// is NOT applied here — the view shows all so an organizer can place anyone;
    /// the <see cref="HotelOccupant.NeedsRoom"/> flag surfaces the preference.
    /// </summary>
    public async Task<IReadOnlyList<HotelGroup>> GroupByHotelAsync(
        int eventId, CancellationToken ct = default)
    {
        var hotels = await ListHotelsAsync(eventId, ct);

        // Per-participant room-need preference (left join: no booking = unknown/false).
        var needs = await _db.HotelBookings
            .Where(hb => hb.EventId == eventId)
            .Select(hb => new { hb.ParticipantId, hb.NeedsRoom })
            .ToDictionaryAsync(x => x.ParticipantId, x => x.NeedsRoom, ct);

        var people = await _db.Participants
            .Where(p => p.EventId == eventId)
            .Select(p => new
            {
                p.Id, p.FullName, p.Email, p.Role, p.HotelId, p.HotelConfirmationNumber,
            })
            .ToListAsync(ct);

        var occupants = people
            .Select(p => new HotelOccupant(
                p.Id, p.FullName, p.Email, p.Role, p.HotelId, p.HotelConfirmationNumber,
                needs.TryGetValue(p.Id, out var n) && n))
            .ToList();

        var byHotel = occupants
            .Where(o => o.HotelId is not null)
            .ToLookup(o => o.HotelId!.Value);

        var groups = hotels
            .Select(h => new HotelGroup(
                h,
                byHotel[h.Id].OrderBy(o => o.FullName, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();

        var unassigned = occupants
            .Where(o => o.HotelId is null)
            .OrderBy(o => o.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unassigned.Count > 0)
        {
            groups.Add(new HotelGroup(null, unassigned));
        }

        return groups;
    }

    private static string? Blank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
