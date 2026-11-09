using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>§1386 — one guest's reservation as the organizer editor shows it.</summary>
public sealed class HotelReservationView
{
    public int ParticipantId { get; init; }
    public string FullName { get; init; } = "";
    public string Email { get; init; } = "";
    public ParticipantRole Role { get; init; }
    public bool HasBooking { get; init; }
    public bool NeedsRoom { get; init; }
    public DateOnly? CheckIn { get; init; }
    public DateOnly? CheckOut { get; init; }
    public int? HotelId { get; init; }
    public int? RoommateParticipantId { get; init; }
    public int? GuestsInRoom { get; init; }
    public string? ShareWith { get; init; }
    public string? ConfirmationNumber { get; init; }
    public HotelConfirmationState State { get; init; }
    public string? CancellationNumber { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<(int Id, string Name, int Priority)> Hotels { get; init; } = Array.Empty<(int, string, int)>();
    /// <summary>Who can be the roommate: active, non-test people who need a room (excluding this guest).</summary>
    public IReadOnlyList<(int Id, string Name)> RoommateChoices { get; init; } = Array.Empty<(int, string)>();
    public IReadOnlyList<HotelChangeLogEntry> History { get; init; } = Array.Empty<HotelChangeLogEntry>();
}

/// <summary>§1386 — what an organizer posts from the editor.</summary>
public sealed record HotelReservationInput(
    bool NeedsRoom, DateOnly? CheckIn, DateOnly? CheckOut, int? HotelId, int? RoommateParticipantId,
    int? GuestsInRoom, string? ConfirmationNumber, HotelConfirmationState State, string? CancellationNumber,
    string? Notes);

/// <summary>
/// §1386 — the ONE place an organizer edits a guest's hotel reservation (operator 2026-11-08: "think as an
/// organizer"): dates, needs-a-room, hotel, roommate, guests in room, confirmation number, state, cancellation
/// number, notes. Every changed field is one change-log line, so the hotel-ops mail and the hotel's next
/// spreadsheet carry it. 🔒 Sends no mail to the guest.
/// </summary>
/// <remarks>
/// The roommate link is kept SYMMETRIC: setting A↔B also sets B's roommate to A, and clears whatever A or B
/// pointed at before — a one-sided link is how two people end up each believing the other holds the room.
/// </remarks>
public sealed class HotelReservationEditor
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly HotelChangeLogService _log;

    public HotelReservationEditor(CommunityHubDbContext db, TimeProvider clock, HotelChangeLogService log)
    {
        _db = db;
        _clock = clock;
        _log = log;
    }

    public async Task<HotelReservationView?> LoadAsync(int eventId, int participantId, CancellationToken ct = default)
    {
        var p = await _db.Participants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == participantId && x.EventId == eventId, ct);
        if (p is null) return null;
        var b = await _db.HotelBookings.AsNoTracking().FirstOrDefaultAsync(x => x.EventId == eventId && x.ParticipantId == participantId, ct);
        var hotels = await _db.Hotels.AsNoTracking().Where(h => h.EventId == eventId)
            .OrderBy(h => h.Priority).ThenBy(h => h.Name)
            .Select(h => new { h.Id, h.Name, h.Priority }).ToListAsync(ct);
        var mates = await _db.HotelBookings.AsNoTracking()
            .Where(x => x.EventId == eventId && x.NeedsRoom && x.ParticipantId != participantId
                        && x.Participant.IsActive && !x.Participant.IsTestUser)
            .Select(x => new { x.ParticipantId, x.Participant.FullName })
            .OrderBy(x => x.FullName).ToListAsync(ct);
        var history = await _db.HotelChangeLog.AsNoTracking()
            .Where(e => e.EventId == eventId && e.ParticipantId == participantId)
            .OrderByDescending(e => e.At).Take(50).ToListAsync(ct);

        return new HotelReservationView
        {
            ParticipantId = p.Id, FullName = p.FullName, Email = p.Email, Role = p.Role,
            HasBooking = b is not null, NeedsRoom = b?.NeedsRoom ?? false,
            CheckIn = b?.CheckInDate, CheckOut = b?.CheckOutDate, HotelId = p.HotelId,
            RoommateParticipantId = b?.RoommateParticipantId, GuestsInRoom = b?.GuestsInRoom, ShareWith = b?.RoomShareWith,
            ConfirmationNumber = p.HotelConfirmationNumber ?? b?.ConfirmationNumber,
            State = b?.ConfirmationState ?? HotelConfirmationState.NotConfirmed,
            CancellationNumber = b?.CancellationNumber, Notes = b?.Notes,
            Hotels = hotels.Select(h => (h.Id, h.Name, h.Priority)).ToList(),
            RoommateChoices = mates.Select(m => (m.ParticipantId, m.FullName)).ToList(),
            History = history,
        };
    }

    /// <summary>Apply the organizer's edit. Returns an error message, or null on success.</summary>
    public async Task<string?> SaveAsync(int eventId, int participantId, HotelReservationInput input, string? actor, CancellationToken ct = default)
    {
        var p = await _db.Participants.FirstOrDefaultAsync(x => x.Id == participantId && x.EventId == eventId, ct);
        if (p is null) return "That participant does not exist in this edition.";
        if (input.NeedsRoom && (input.CheckIn is null || input.CheckOut is null || input.CheckIn >= input.CheckOut))
            return "A room needs an arrival date before the departure date.";
        if (input.HotelId is int hid && !await _db.Hotels.AnyAsync(h => h.Id == hid && h.EventId == eventId, ct))
            return "That hotel does not exist.";
        if (input.RoommateParticipantId == participantId) return "A guest cannot be their own roommate.";
        if (input.GuestsInRoom is int gir && (gir < 1 || gir > 6)) return "Guests in room must be between 1 and 6.";

        var b = await _db.HotelBookings.FirstOrDefaultAsync(x => x.EventId == eventId && x.ParticipantId == participantId, ct);
        var now = _clock.GetUtcNow();
        if (b is null)
        {
            b = new HotelBooking { EventId = eventId, ParticipantId = participantId, CreatedAt = now };
            _db.HotelBookings.Add(b);
        }

        var changes = new List<string>();
        void Note(string what, object? from, object? to)
        {
            if (!Equals(from, to)) changes.Add($"{what} {Show(from)} → {Show(to)}");
        }

        Note("needs a room", b.NeedsRoom ? "yes" : "no", input.NeedsRoom ? "yes" : "no");
        Note("arrival", b.CheckInDate, input.NeedsRoom ? input.CheckIn : null);
        Note("departure", b.CheckOutDate, input.NeedsRoom ? input.CheckOut : null);
        Note("guests in room", b.GuestsInRoom, input.GuestsInRoom);
        Note("notes", Blank(b.Notes), Blank(input.Notes));
        var oldHotel = p.HotelId;

        b.NeedsRoom = input.NeedsRoom;
        b.CheckInDate = input.NeedsRoom ? input.CheckIn : null;
        b.CheckOutDate = input.NeedsRoom ? input.CheckOut : null;
        b.GuestsInRoom = input.GuestsInRoom;
        b.Notes = Blank(input.Notes);

        // Hotel placement.
        if (oldHotel != input.HotelId)
        {
            var names = await _db.Hotels.Where(h => h.EventId == eventId).ToDictionaryAsync(h => h.Id, h => h.Name, ct);
            string N(int? id) => id is int i && names.TryGetValue(i, out var n) ? n : "no hotel";
            changes.Add($"hotel {N(oldHotel)} → {N(input.HotelId)}");
            if (oldHotel is int o)   // the old hotel hears the guest left
                _log.Add(eventId, HotelChangeSource.Organizer, actor, HotelChangeKinds.Placed,
                    $"Moved from {N(oldHotel)} to {N(input.HotelId)}", o, p.Id, p.FullName, affectsGuestList: true);
            p.HotelId = input.HotelId;
        }

        // State + numbers.
        var conf = Blank(input.ConfirmationNumber);
        var cancel = Blank(input.CancellationNumber);
        var state = !input.NeedsRoom && p.HotelId is not null && input.State != HotelConfirmationState.Cancelled
            ? HotelConfirmationState.Cancelled   // a placed guest who no longer needs the room = a cancellation the hotel must hear
            : input.State;
        Note("state", HotelReservationStateText.Of(b.ConfirmationState), HotelReservationStateText.Of(state));
        Note("confirmation number", Blank(p.HotelConfirmationNumber ?? b.ConfirmationNumber), conf);
        Note("cancellation number", Blank(b.CancellationNumber), cancel);
        if (state == HotelConfirmationState.Cancelled && b.ConfirmationState != HotelConfirmationState.Cancelled)
        {
            b.CancelledAt = now;
            b.CancelledHotelId = oldHotel ?? p.HotelId;
        }
        if (state != HotelConfirmationState.Cancelled) { b.CancelledAt = null; b.CancelledHotelId = null; }
        if (state == HotelConfirmationState.Confirmed && b.ConfirmationState != HotelConfirmationState.Confirmed) b.ConfirmedAt = now;
        b.ConfirmationState = state;
        b.ConfirmationNumber = conf;
        p.HotelConfirmationNumber = conf;
        b.CancellationNumber = cancel;

        // Roommate — kept symmetric.
        var newMate = input.NeedsRoom ? input.RoommateParticipantId : null;
        if (b.RoommateParticipantId != newMate)
        {
            var names = await _db.Participants.Where(x => x.EventId == eventId &&
                    (x.Id == b.RoommateParticipantId || x.Id == newMate))
                .ToDictionaryAsync(x => x.Id, x => x.FullName, ct);
            string M(int? id) => id is int i && names.TryGetValue(i, out var n) ? n : "none";
            changes.Add($"roommate {M(b.RoommateParticipantId)} → {M(newMate)}");

            // Release the old mate's back-link, and whoever the new mate pointed at.
            var involved = new[] { b.RoommateParticipantId, newMate }.Where(x => x is not null).Select(x => x!.Value).ToList();
            var others = await _db.HotelBookings.Where(x => x.EventId == eventId && involved.Contains(x.ParticipantId)).ToListAsync(ct);
            foreach (var o in others.Where(o => o.RoommateParticipantId == participantId)) o.RoommateParticipantId = null;
            if (newMate is int nm)
            {
                var mateBooking = others.FirstOrDefault(o => o.ParticipantId == nm);
                if (mateBooking is null) return "The roommate has no hotel reservation yet — give them one first.";
                if (mateBooking.RoommateParticipantId is int prev && prev != participantId)
                {
                    var prevBooking = await _db.HotelBookings.FirstOrDefaultAsync(x => x.EventId == eventId && x.ParticipantId == prev, ct);
                    if (prevBooking?.RoommateParticipantId == nm) prevBooking.RoommateParticipantId = null;
                }
                mateBooking.RoommateParticipantId = participantId;
            }
            b.RoommateParticipantId = newMate;
        }

        if (changes.Count == 0) return null;
        b.UpdatedAt = now;
        _log.Add(eventId, HotelChangeSource.Organizer, actor, HotelChangeKinds.BookingChanged,
            "Edited by an organizer: " + string.Join("; ", changes), p.HotelId ?? oldHotel, p.Id, p.FullName, affectsGuestList: true);
        await _db.SaveChangesAsync(ct);
        return null;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Show(object? v) => v switch
    {
        null => "(none)",
        DateOnly d => d.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "(none)",
    };
}
