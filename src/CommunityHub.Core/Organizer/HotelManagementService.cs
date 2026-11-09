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
    bool NeedsRoom,
    // §1386 — people in the room (organizer-set or the default) and whether an organizer set it.
    int GuestsInRoom = 1,
    bool GuestsInRoomSet = false);

/// <summary>
/// A participant placed in a hotel who needs a room, with their own check-in/out
/// dates — the recipient of the re-issued CONFIRMED hotel calendar invite (§46).
/// </summary>
public sealed record HotelReserver(
    int ParticipantId,
    string FullName,
    string Email,
    DateOnly CheckInDate,
    DateOnly CheckOutDate);

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

    private readonly HotelChangeLogService? _log;

    /// <param name="log">§1386 — optional so existing tests need not build it; DI injects it and every
    /// placement / per-person confirmation change is then logged.</param>
    public HotelManagementService(CommunityHubDbContext db, TimeProvider clock, HotelChangeLogService? log = null)
    {
        _db = db;
        _clock = clock;
        _log = log;
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
        int? roomBlockSize = null, string? confirmationNumber = null, CancellationToken ct = default)
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
            ConfirmationNumber = Blank(confirmationNumber),
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.Hotels.Add(hotel);
        await _db.SaveChangesAsync(ct);
        return hotel;
    }

    /// <summary>
    /// Update a hotel in place. Returns false if it does not exist in the edition.
    /// </summary>
    public async Task<bool> UpdateHotelAsync(
        int eventId, int hotelId, string name, string? address, string? contactEmail, string? notes,
        int? roomBlockSize = null, string? confirmationNumber = null, CancellationToken ct = default)
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
        hotel.ConfirmationNumber = Blank(confirmationNumber);
        hotel.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// §1386 — the contract details: priority, agreement number + label, other booking reference,
    /// hotel-ops recipients. Logged when the priority changes (it decides where new guests go).
    /// </summary>
    public async Task<bool> SetContractDetailsAsync(
        int eventId, int hotelId, int priority, string? agreementNumber, string? agreementLabel,
        string? bookingReference, string? notifyEmails, string? actor = null, CancellationToken ct = default)
    {
        var hotel = await GetHotelAsync(eventId, hotelId, ct);
        if (hotel is null) return false;

        var p = Math.Clamp(priority, 1, 9);
        if (_log is not null && hotel.Priority != p)
            _log.Add(eventId, HotelChangeSource.Organizer, actor, HotelChangeKinds.Allocation,
                $"Priority of {hotel.Name} {hotel.Priority} → {p}", hotel.Id);

        hotel.Priority = p;
        hotel.AgreementNumber = Blank(agreementNumber);
        hotel.AgreementLabel = Blank(agreementLabel);
        hotel.BookingReference = Blank(bookingReference);
        hotel.NotifyEmails = Blank(notifyEmails);
        hotel.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Set (or clear) the hotel-level booking CONFIRMATION NUMBER (REQUIREMENTS §46)
    /// and return whether the value actually CHANGED to a non-blank state — i.e. the
    /// reservation just became CONFIRMED. The caller uses that signal to decide
    /// whether to re-issue the calendar invites to everyone placed in the hotel.
    /// Returns null when the hotel does not exist in the edition.
    /// </summary>
    /// <summary>
    /// §1386 — "Mark as sent to the hotel": the organizer forwarded the hotel's current spreadsheet. The page
    /// then shows only the changes made since. Logged (not mailed).
    /// </summary>
    public async Task<bool> MarkSentToHotelAsync(int eventId, int hotelId, string? actor, CancellationToken ct = default)
    {
        var hotel = await GetHotelAsync(eventId, hotelId, ct);
        if (hotel is null) return false;
        hotel.SentToHotelAt = _clock.GetUtcNow();
        hotel.SentToHotelByEmail = actor;
        _log?.Add(eventId, HotelChangeSource.Organizer, actor, HotelChangeKinds.SentToHotel,
            $"The guest spreadsheet was sent to {hotel.Name}", hotel.Id, notify: false);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>§1386 — changes to a hotel's guest list since it was last marked as sent (newest first).</summary>
    public async Task<List<HotelChangeLogEntry>> ChangesSinceSentAsync(int eventId, int hotelId, CancellationToken ct = default)
    {
        var sent = await _db.Hotels.Where(h => h.Id == hotelId && h.EventId == eventId).Select(h => h.SentToHotelAt).FirstOrDefaultAsync(ct);
        return await _db.HotelChangeLog.AsNoTracking()
            .Where(e => e.EventId == eventId && e.HotelId == hotelId && e.AffectsGuestList && (sent == null || e.At > sent))
            .OrderByDescending(e => e.At).ToListAsync(ct);
    }

    public async Task<bool?> SetHotelConfirmationNumberAsync(
        int eventId, int hotelId, string? confirmationNumber, CancellationToken ct = default)
    {
        var hotel = await GetHotelAsync(eventId, hotelId, ct);
        if (hotel is null) return null;

        var normalized = Blank(confirmationNumber);
        var wasConfirmed = !string.IsNullOrWhiteSpace(hotel.ConfirmationNumber);
        var nowConfirmed = !string.IsNullOrWhiteSpace(normalized);
        var changed = !string.Equals(hotel.ConfirmationNumber, normalized, StringComparison.Ordinal);

        hotel.ConfirmationNumber = normalized;
        if (changed) hotel.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        // "Just became confirmed" = a non-blank number is now set and the stored
        // value changed (newly set, or replaced with a different number). Clearing
        // or re-saving the same number does not re-fire the invites.
        return nowConfirmed && changed && (!wasConfirmed || changed);
    }

    /// <summary>
    /// Everyone placed in this hotel who needs a room — the recipients of the
    /// re-issued CONFIRMED calendar invite (REQUIREMENTS §46). Pulls the
    /// participant's own check-in/out dates from their <see cref="HotelBooking"/>;
    /// people with no booking, no room need, or missing/zero-length dates are
    /// skipped (there is nothing to put in a calendar invite for them).
    /// </summary>
    public async Task<IReadOnlyList<HotelReserver>> ListReserversForInviteAsync(
        int eventId, int hotelId, CancellationToken ct = default)
    {
        // ACTIVE people only (§253 G2/H3): a deactivated participant must never
        // receive the re-issued CONFIRMED calendar invite (worst of the hotel
        // ghost surfaces — it emailed drop-outs a confirmed booking).
        var people = await _db.Participants
            .Where(p => p.EventId == eventId && p.HotelId == hotelId
                        && p.IsActive && !p.IsTestUser
                        && p.Role != ParticipantRole.Sponsor)
            .Select(p => new { p.Id, p.FullName, p.Email })
            .ToListAsync(ct);
        if (people.Count == 0) return Array.Empty<HotelReserver>();

        var ids = people.Select(p => p.Id).ToList();
        var bookings = await _db.HotelBookings
            .Where(hb => hb.EventId == eventId && ids.Contains(hb.ParticipantId) && hb.NeedsRoom)
            .Select(hb => new { hb.ParticipantId, hb.CheckInDate, hb.CheckOutDate })
            .ToListAsync(ct);

        var byId = people.ToDictionary(p => p.Id);
        var reservers = new List<HotelReserver>();
        foreach (var b in bookings)
        {
            if (b.CheckInDate is null || b.CheckOutDate is null) continue;
            if (b.CheckInDate.Value >= b.CheckOutDate.Value) continue;
            if (!byId.TryGetValue(b.ParticipantId, out var p)) continue;
            if (string.IsNullOrWhiteSpace(p.Email)) continue;
            reservers.Add(new HotelReserver(
                p.Id, p.FullName, p.Email, b.CheckInDate.Value, b.CheckOutDate.Value));
        }
        return reservers;
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
        int eventId, int participantId, int? hotelId, CancellationToken ct = default, string? actor = null)
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

        // §1386 — log the move against BOTH hotels: the old one must hear the guest left.
        if (_log is not null && p.HotelId != hotelId)
        {
            var names = await _db.Hotels.Where(h => h.EventId == eventId)
                .ToDictionaryAsync(h => h.Id, h => h.Name, ct);
            string N(int? id) => id is int i && names.TryGetValue(i, out var n) ? n : "no hotel";
            var booking = await _db.HotelBookings.FirstOrDefaultAsync(
                b => b.EventId == eventId && b.ParticipantId == p.Id, ct);
            var onSheet = booking is not null && booking.NeedsRoom;
            var summary = $"Moved from {N(p.HotelId)} to {N(hotelId)}";
            if (p.HotelId is int from)
            {
                _log.Add(eventId, HotelChangeSource.Organizer, actor, HotelChangeKinds.Placed, summary,
                    from, p.Id, p.FullName, affectsGuestList: onSheet);
                // The OLD hotel's confirmation number does not travel to the new hotel: the guest is
                // Reserved again there. The old hotel hears through its change mail ("Moved from …").
                if (onSheet && booking!.ConfirmationState != HotelConfirmationState.Cancelled && hotelId is not null)
                {
                    booking.ConfirmationNumber = null;
                    booking.ConfirmationState = HotelConfirmationState.NotConfirmed;
                    p.HotelConfirmationNumber = null;
                }
            }
            if (hotelId is int to)
                _log.Add(eventId, HotelChangeSource.Organizer, actor, HotelChangeKinds.Placed, summary,
                    to, p.Id, p.FullName, affectsGuestList: onSheet);
        }

        p.HotelId = hotelId;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Set (or clear) a participant's per-person hotel confirmation number. §1386 — a number makes
    /// the reservation Confirmed (as the spreadsheet import does); clearing it makes it Reserved.
    /// Sends no mail.
    /// </summary>
    public async Task<bool> SetConfirmationNumberAsync(
        int eventId, int participantId, string? confirmationNumber, CancellationToken ct = default, string? actor = null)
    {
        var p = await _db.Participants
            .FirstOrDefaultAsync(x => x.Id == participantId && x.EventId == eventId, ct);
        if (p is null) return false;

        var number = Blank(confirmationNumber);
        var before = p.HotelConfirmationNumber;
        p.HotelConfirmationNumber = number;

        var booking = await _db.HotelBookings.FirstOrDefaultAsync(
            b => b.EventId == eventId && b.ParticipantId == participantId, ct);
        if (booking is not null && booking.ConfirmationState != HotelConfirmationState.Cancelled)
        {
            booking.ConfirmationNumber = number;
            booking.ConfirmationState = number is null ? HotelConfirmationState.NotConfirmed : HotelConfirmationState.Confirmed;
            booking.ConfirmedAt = number is null ? null : (booking.ConfirmedAt ?? _clock.GetUtcNow());
        }
        if (_log is not null && before != number)
        {
            _log.Add(eventId, HotelChangeSource.Organizer, actor, HotelChangeKinds.StateChanged,
                $"Confirmation number {before ?? "(none)"} → {number ?? "(none)"} (entered by hand)",
                p.HotelId, p.Id, p.FullName, affectsGuestList: false);
        }
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

        // Sponsors are not part of crew logistics (hotel, dinner, swag, …) — they
        // never get a hotel room block, so they must not appear in the hotel
        // assignment lists in org admin (operator 2026-06-21). ACTIVE people only
        // (§253 G2): drop-outs must leave the rosters + "(N people, M confirmed)"
        // badges (the cascade also un-places them, this is the belt-and-braces).
        var people = await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.IsActive && !p.IsTestUser
                        && p.Role != ParticipantRole.Sponsor)
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
