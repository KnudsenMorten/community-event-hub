using System.Globalization;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>§1386 — a hotel booking as it stood BEFORE a save, so the change can be described.</summary>
public sealed record HotelBookingSnapshot(
    bool Exists, bool NeedsRoom, DateOnly? CheckIn, DateOnly? CheckOut, string? ShareWith)
{
    public static readonly HotelBookingSnapshot None = new(false, false, null, null, null);

    public static HotelBookingSnapshot Of(HotelBooking? b) => b is null
        ? None
        : new(true, b.NeedsRoom, b.CheckInDate, b.CheckOutDate, b.RoomShareWith);
}

/// <summary>
/// §1386 — placement and reservation-state rules for hotel bookings, in ONE place so the form, the
/// organizer pages and the import cannot disagree.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Auto-placement:</b> someone who needs a room and is in no hotel is placed in the
///   edition's PRIMARY hotel (lowest <see cref="Hotel.Priority"/>, then lowest id). Operator
///   2026-11-08: <i>"We must already now assign the prio 1 hotel to people who have reserved a
///   hotel"</i>. Secondary hotels are filled by an organizer, never automatically.</item>
///   <item><b>Cancellation:</b> a PLACED person who no longer needs a room becomes
///   <see cref="HotelConfirmationState.Cancelled"/> and STAYS placed, so the hotel's next
///   spreadsheet tells the hotel. An unplaced person simply has no reservation to cancel.</item>
///   <item><b>Reinstatement:</b> a Cancelled person who needs a room again is Reserved again; the old
///   confirmation / cancellation numbers belonged to the cancelled room and are cleared (they stay
///   readable in the change log).</item>
/// </list>
/// 🔒 <b>Nothing here sends mail.</b> Placement is visible to the participant in the hub; the hotel
/// learns through the spreadsheet an organizer forwards (§1386: "dont send any hotel emails to anyone
/// until approved").
/// </remarks>
public sealed class HotelPlacementService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly HotelChangeLogService _log;

    public HotelPlacementService(CommunityHubDbContext db, TimeProvider clock, HotelChangeLogService log)
    {
        _db = db;
        _clock = clock;
        _log = log;
    }

    /// <summary>The hotel new reservations go to, or null when the edition has no hotel.</summary>
    public Task<int?> PrimaryHotelIdAsync(int eventId, CancellationToken ct = default) =>
        _db.Hotels
            .Where(h => h.EventId == eventId)
            .OrderBy(h => h.Priority).ThenBy(h => h.Id)
            .Select(h => (int?)h.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Called by the hotel form AFTER the participant's booking was saved. Applies the state rules,
    /// auto-places, writes the change-log line(s) and saves.
    /// </summary>
    public async Task OnParticipantSavedAsync(
        int eventId, int participantId, HotelBookingSnapshot before, string? actorEmail,
        CancellationToken ct = default)
    {
        var booking = await _db.HotelBookings.FirstOrDefaultAsync(
            b => b.EventId == eventId && b.ParticipantId == participantId, ct);
        var p = await _db.Participants.FirstOrDefaultAsync(
            x => x.Id == participantId && x.EventId == eventId, ct);
        if (booking is null || p is null) return;

        var now = _clock.GetUtcNow();
        var actor = string.IsNullOrWhiteSpace(actorEmail) ? p.Email : actorEmail;

        // ---- auto-placement -----------------------------------------------------------------
        if (booking.NeedsRoom && p.HotelId is null && p.IsActive && !p.IsTestUser
            && p.Role != ParticipantRole.Sponsor)
        {
            var primary = await PrimaryHotelIdAsync(eventId, ct);
            if (primary is int hid)
            {
                p.HotelId = hid;
                var name = await HotelNameAsync(hid, ct);
                _log.Add(eventId, HotelChangeSource.System, "system", HotelChangeKinds.Placed,
                    $"Placed in {name} (priority hotel) — {Stay(booking.CheckInDate, booking.CheckOutDate)}",
                    hid, p.Id, p.FullName, affectsGuestList: true);
            }
        }

        // §1386 — placed FIRST, so every line below is logged against the hotel the guest is now at
        // (a line with no hotel would be mailed separately as "unplaced").

        // ---- state transitions --------------------------------------------------------------
        if (!booking.NeedsRoom && before.NeedsRoom && p.HotelId is not null
            && booking.ConfirmationState != HotelConfirmationState.Cancelled)
        {
            booking.ConfirmationState = HotelConfirmationState.Cancelled;
            booking.CancelledAt = now;
            booking.CancelledHotelId = p.HotelId;
            _log.Add(eventId, HotelChangeSource.Participant, actor, HotelChangeKinds.BookingCancelled,
                $"No longer needs a room — reservation {Stay(before.CheckIn, before.CheckOut)} is now Cancelled",
                p.HotelId, p.Id, p.FullName, affectsGuestList: true);
        }
        else if (booking.NeedsRoom && booking.ConfirmationState == HotelConfirmationState.Cancelled)
        {
            var old = string.Join(", ", new[]
            {
                string.IsNullOrWhiteSpace(booking.ConfirmationNumber) ? null : $"confirmation {booking.ConfirmationNumber}",
                string.IsNullOrWhiteSpace(booking.CancellationNumber) ? null : $"cancellation {booking.CancellationNumber}",
            }.Where(s => s is not null));
            booking.ConfirmationState = HotelConfirmationState.NotConfirmed;
            booking.CancelledAt = null;
            booking.CancelledHotelId = null;
            booking.ConfirmationNumber = null;
            booking.CancellationNumber = null;
            booking.ConfirmedAt = null;
            p.HotelConfirmationNumber = null;
            _log.Add(eventId, HotelChangeSource.Participant, actor, HotelChangeKinds.BookingReinstated,
                $"Needs a room again {Stay(booking.CheckInDate, booking.CheckOutDate)} — Reserved"
                + (old.Length > 0 ? $" (previous {old} cleared)" : ""),
                p.HotelId, p.Id, p.FullName, affectsGuestList: p.HotelId is not null);
        }
        else if (!before.Exists || (!before.NeedsRoom && booking.NeedsRoom))
        {
            if (booking.NeedsRoom)
            {
                _log.Add(eventId, HotelChangeSource.Participant, actor, HotelChangeKinds.BookingCreated,
                    $"Reserved {Stay(booking.CheckInDate, booking.CheckOutDate)}"
                    + Share(booking.RoomShareWith),
                    p.HotelId, p.Id, p.FullName, affectsGuestList: p.HotelId is not null);
            }
        }
        else if (booking.NeedsRoom && (before.CheckIn != booking.CheckInDate
                                       || before.CheckOut != booking.CheckOutDate
                                       || !string.Equals((before.ShareWith ?? "").Trim(),
                                           (booking.RoomShareWith ?? "").Trim(), StringComparison.Ordinal)))
        {
            _log.Add(eventId, HotelChangeSource.Participant, actor, HotelChangeKinds.BookingChanged,
                $"Changed {Stay(before.CheckIn, before.CheckOut)}{Share(before.ShareWith)} → "
                + $"{Stay(booking.CheckInDate, booking.CheckOutDate)}{Share(booking.RoomShareWith)}",
                p.HotelId, p.Id, p.FullName, affectsGuestList: p.HotelId is not null);
        }


        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Place every ACTIVE person who needs a room (and is not cancelled) but is in no hotel into the
    /// primary hotel. Returns how many were placed. Sends nothing.
    /// </summary>
    public async Task<int> PlaceUnassignedInPrimaryAsync(int eventId, string? actorEmail, CancellationToken ct = default)
    {
        var primary = await PrimaryHotelIdAsync(eventId, ct);
        if (primary is not int hid) return 0;
        var hotelName = await HotelNameAsync(hid, ct);

        var rows = await _db.HotelBookings
            .Where(b => b.EventId == eventId && b.NeedsRoom
                        && b.ConfirmationState != HotelConfirmationState.Cancelled
                        && b.Participant.HotelId == null
                        && b.Participant.IsActive && !b.Participant.IsTestUser
                        && b.Participant.Role != ParticipantRole.Sponsor)
            .Select(b => new { b.ParticipantId, b.CheckInDate, b.CheckOutDate })
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;

        var ids = rows.Select(r => r.ParticipantId).ToList();
        var people = await _db.Participants.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        foreach (var p in people)
        {
            var r = rows.First(x => x.ParticipantId == p.Id);
            p.HotelId = hid;
            _log.Add(eventId, HotelChangeSource.Organizer, actorEmail, HotelChangeKinds.Placed,
                $"Placed in {hotelName} (bulk: everyone reserved and unassigned) — {Stay(r.CheckInDate, r.CheckOutDate)}",
                hid, p.Id, p.FullName, affectsGuestList: true);
        }
        await _db.SaveChangesAsync(ct);
        return people.Count;
    }

    /// <summary>
    /// Log an organizer placement change (the caller has already set <c>HotelId</c>). Staged only —
    /// the caller saves.
    /// </summary>
    public async Task LogOrganizerPlacementAsync(
        int eventId, Participant p, int? fromHotelId, int? toHotelId, string? actorEmail, CancellationToken ct = default)
    {
        if (fromHotelId == toHotelId) return;
        var from = fromHotelId is int f ? await HotelNameAsync(f, ct) : "no hotel";
        var to = toHotelId is int t ? await HotelNameAsync(t, ct) : "no hotel";
        var needs = await _db.HotelBookings.AnyAsync(
            b => b.EventId == eventId && b.ParticipantId == p.Id && b.NeedsRoom, ct);

        // The OLD hotel has to hear the guest left, the new one that they arrive: one line each.
        _log.Add(eventId, HotelChangeSource.Organizer, actorEmail, HotelChangeKinds.Placed,
            $"Moved from {from} to {to}", toHotelId ?? fromHotelId, p.Id, p.FullName, affectsGuestList: needs);
        if (fromHotelId is not null && toHotelId is not null)
        {
            _log.Add(eventId, HotelChangeSource.Organizer, actorEmail, HotelChangeKinds.Placed,
                $"Moved from {from} to {to}", fromHotelId, p.Id, p.FullName, affectsGuestList: needs);
        }
    }

    /// <summary>
    /// An organizer sets a reservation state by hand (e.g. cancels a room on someone's behalf).
    /// Returns false when there is no booking.
    /// </summary>
    public async Task<bool> SetStateAsync(
        int eventId, int participantId, HotelConfirmationState state, string? actorEmail, CancellationToken ct = default)
    {
        var booking = await _db.HotelBookings.FirstOrDefaultAsync(
            b => b.EventId == eventId && b.ParticipantId == participantId, ct);
        var p = await _db.Participants.FirstOrDefaultAsync(x => x.Id == participantId && x.EventId == eventId, ct);
        if (booking is null || p is null) return false;
        if (booking.ConfirmationState == state) return true;

        var from = HotelReservationStateText.Of(booking.ConfirmationState);
        booking.ConfirmationState = state;
        booking.UpdatedAt = _clock.GetUtcNow();
        if (state == HotelConfirmationState.Cancelled) { booking.CancelledAt = _clock.GetUtcNow(); booking.CancelledHotelId = p.HotelId; }
        else booking.CancelledAt = null;

        _log.Add(eventId, HotelChangeSource.Organizer, actorEmail, HotelChangeKinds.StateChanged,
            $"Reservation state {from} → {HotelReservationStateText.Of(state)}",
            p.HotelId, p.Id, p.FullName, affectsGuestList: p.HotelId is not null);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<string> HotelNameAsync(int hotelId, CancellationToken ct) =>
        await _db.Hotels.Where(h => h.Id == hotelId).Select(h => h.Name).FirstOrDefaultAsync(ct)
        ?? $"hotel #{hotelId}";

    /// <summary>"8 Feb → 10 Feb" (the dates the hotel will see).</summary>
    public static string Stay(DateOnly? inDate, DateOnly? outDate) =>
        inDate is null || outDate is null
            ? "(no dates)"
            : $"{inDate.Value.ToString("d MMM", CultureInfo.InvariantCulture)} → "
              + outDate.Value.ToString("d MMM", CultureInfo.InvariantCulture);

    private static string Share(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : $", sharing with {s.Trim()}";
}
