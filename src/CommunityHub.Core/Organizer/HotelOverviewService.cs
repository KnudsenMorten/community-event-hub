using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// §1386 — one night of the hotel overview (operator 2026-11-08: <i>"summary + detailed view per day
/// with Allocated (current number …), Reserved/Confirmed, Extra Forecast, Result: Gap/Too Many"</i>).
/// </summary>
/// <param name="PrimaryAllocated">Rooms held at priority-1 hotels.</param>
/// <param name="BufferAllocated">Rooms held at priority-2+ hotels (used only if needed).</param>
/// <param name="Reserved">Reservations not yet confirmed by the hotel (placed or not).</param>
/// <param name="Confirmed">Reservations the hotel has confirmed.</param>
/// <param name="Unplaced">Of the reserved, how many are in no hotel yet.</param>
/// <param name="ForecastExtra">Organizer-entered extra rooms expected this night.</param>
public sealed record HotelNightLine(
    DateOnly Night, int PrimaryAllocated, int BufferAllocated,
    int Reserved, int Confirmed, int Unplaced, int ForecastExtra, string? ForecastNote)
{
    public int Allocated => PrimaryAllocated + BufferAllocated;

    /// <summary>Rooms we expect to need: reserved + confirmed + forecast.</summary>
    public int Needed => Reserved + Confirmed + ForecastExtra;

    /// <summary>Allocated − needed. Negative = GAP (find rooms), positive = TOO MANY (release).</summary>
    public int Result => Allocated - Needed;

    /// <summary>The same against priority-1 rooms only — negative means the buffer is in use.</summary>
    public int PrimaryResult => PrimaryAllocated - Needed;

    public bool IsGap => Result < 0;
    public bool IsTooMany => Result > 0;
}

/// <summary>§1386 — one hotel's summary line.</summary>
public sealed record HotelSummaryLine(
    int HotelId, string Name, int Priority, string? AgreementNumber, string? BookingReference,
    int RoomNightsAllocated, int Guests, int Reserved, int Confirmed, int Cancelled, int RoomNightsBooked);

/// <summary>§1386 — someone who may still ask for a room: hotel-relevant, has not answered the form.</summary>
public sealed record HotelPotentialRequester(int ParticipantId, string FullName, string Email, ParticipantRole Role);

/// <summary>§1386 — one guest sleeping in a room on a given night.</summary>
public sealed record HotelNightGuest(
    int ParticipantId, string FullName, string Email, ParticipantRole Role, string? HotelName,
    DateOnly? CheckIn, DateOnly? CheckOut, HotelConfirmationState State, string? ConfirmationNumber, string? ShareWith);

/// <summary>§1386 — the whole overview.</summary>
public sealed class HotelOverview
{
    public IReadOnlyList<HotelNightLine> Nights { get; init; } = Array.Empty<HotelNightLine>();
    public IReadOnlyList<HotelSummaryLine> Hotels { get; init; } = Array.Empty<HotelSummaryLine>();
    public IReadOnlyList<HotelPotentialRequester> NotAnswered { get; init; } = Array.Empty<HotelPotentialRequester>();

    /// <summary>People who answered "no room needed".</summary>
    public int AnsweredNoRoom { get; init; }

    public int TotalAllocated => Nights.Sum(n => n.Allocated);
    public int TotalNeeded => Nights.Sum(n => n.Needed);
    public int GapNights => Nights.Count(n => n.IsGap);
    public int TooManyNights => Nights.Count(n => n.IsTooMany);
    public int TotalReservedPeople { get; init; }
    public int TotalConfirmedPeople { get; init; }
    public int TotalCancelledPeople { get; init; }
    public int UnplacedPeople { get; init; }
}

/// <summary>
/// §1386 — the hotel overview: per-night supply vs demand + forecast, a per-hotel summary, and the
/// "not answered yet" forecast list. Read-only.
/// </summary>
/// <remarks>
/// Demand follows the §326bs rules (<see cref="HotelAllotmentService"/>): ACTIVE, non-test people,
/// one room per person, nights <c>[check-in, check-out)</c>. Cancelled reservations are not demand.
/// </remarks>
public sealed class HotelOverviewService
{
    private readonly CommunityHubDbContext _db;

    public HotelOverviewService(CommunityHubDbContext db) => _db = db;

    public async Task<HotelOverview> BuildAsync(int eventId, bool includeNotAnswered = true, CancellationToken ct = default)
    {
        var hotels = await _db.Hotels.AsNoTracking()
            .Where(h => h.EventId == eventId)
            .OrderBy(h => h.Priority).ThenBy(h => h.Name)
            .Select(h => new { h.Id, h.Name, h.Priority, h.AgreementNumber, h.BookingReference })
            .ToListAsync(ct);
        var prio = hotels.ToDictionary(h => h.Id, h => h.Priority);
        var primaryPrio = hotels.Count == 0 ? 1 : hotels.Min(h => h.Priority);

        var allotments = await _db.HotelAllotments.AsNoTracking()
            .Where(a => a.EventId == eventId)
            .Select(a => new { a.HotelId, a.Night, a.RoomsAllotted })
            .ToListAsync(ct);

        var bookings = await _db.HotelBookings.AsNoTracking()
            .Where(b => b.EventId == eventId && b.Participant.IsActive && !b.Participant.IsTestUser)
            .Select(b => new
            {
                b.ParticipantId, b.NeedsRoom, b.CheckInDate, b.CheckOutDate, b.ConfirmationState, b.RoommateParticipantId,
                b.Participant.HotelId,
            })
            .ToListAsync(ct);

        var forecast = await _db.HotelForecastExtras.AsNoTracking()
            .Where(f => f.EventId == eventId)
            .ToDictionaryAsync(f => f.Night, f => (f.ExtraRooms, f.Note), ct);

        var live = bookings
            .Where(b => b.NeedsRoom && b.ConfirmationState != HotelConfirmationState.Cancelled
                        && b.CheckInDate is not null && b.CheckOutDate is not null
                        && b.CheckInDate < b.CheckOutDate)
            .ToList();

        var nights = new SortedSet<DateOnly>(allotments.Select(a => a.Night));
        foreach (var b in live)
            for (var n = b.CheckInDate!.Value; n < b.CheckOutDate!.Value; n = n.AddDays(1)) nights.Add(n);
        foreach (var n in forecast.Keys) nights.Add(n);

        var lines = new List<HotelNightLine>();
        foreach (var n in nights)
        {
            var sleeping = live.Where(b => b.CheckInDate!.Value <= n && n < b.CheckOutDate!.Value).ToList();
            var primary = allotments.Where(a => a.Night == n && prio.TryGetValue(a.HotelId, out var p) && p == primaryPrio)
                .Sum(a => a.RoomsAllotted);
            var buffer = allotments.Where(a => a.Night == n && prio.TryGetValue(a.HotelId, out var p) && p != primaryPrio)
                .Sum(a => a.RoomsAllotted);
            forecast.TryGetValue(n, out var fc);
            var rooms = HotelRoomMath.Count(sleeping.Select(b => new HotelSleeper(
                b.ParticipantId, b.RoommateParticipantId, b.HotelId, b.ConfirmationState == HotelConfirmationState.Confirmed)));
            lines.Add(new HotelNightLine(
                n, primary, buffer,
                // §1386 — ROOMS, not people (HotelRoomMath): roommates at the same hotel are one room.
                Reserved: rooms.Rooms - rooms.ConfirmedRooms,
                Confirmed: rooms.ConfirmedRooms,
                Unplaced: sleeping.Count(b => b.HotelId is null),
                ForecastExtra: fc.ExtraRooms, ForecastNote: fc.Note));
        }

        var summaries = hotels.Select(h =>
        {
            var here = bookings.Where(b => b.HotelId == h.Id && (b.NeedsRoom || b.ConfirmationState == HotelConfirmationState.Cancelled)).ToList();
            var liveHere = live.Where(b => b.HotelId == h.Id).ToList();
            return new HotelSummaryLine(
                h.Id, h.Name, h.Priority, h.AgreementNumber, h.BookingReference,
                RoomNightsAllocated: allotments.Where(a => a.HotelId == h.Id).Sum(a => a.RoomsAllotted),
                Guests: liveHere.Count,
                Reserved: liveHere.Count(b => b.ConfirmationState == HotelConfirmationState.NotConfirmed),
                Confirmed: liveHere.Count(b => b.ConfirmationState == HotelConfirmationState.Confirmed),
                Cancelled: here.Count(b => b.ConfirmationState == HotelConfirmationState.Cancelled),
                // §1386 — room-nights: rooms per night (roommates share), summed over the nights.
                RoomNightsBooked: nights.Sum(n => HotelRoomMath.Count(liveHere
                    .Where(b => b.CheckInDate!.Value <= n && n < b.CheckOutDate!.Value)
                    .Select(b => new HotelSleeper(b.ParticipantId, b.RoommateParticipantId, b.HotelId, false))).Rooms));
        }).ToList();

        return new HotelOverview
        {
            Nights = lines,
            Hotels = summaries,
            NotAnswered = includeNotAnswered
                ? await NotAnsweredAsync(eventId, bookings.Select(b => b.ParticipantId).ToHashSet(), ct)
                : Array.Empty<HotelPotentialRequester>(),
            AnsweredNoRoom = bookings.Count(b => !b.NeedsRoom && b.ConfirmationState != HotelConfirmationState.Cancelled),
            TotalReservedPeople = live.Count(b => b.ConfirmationState == HotelConfirmationState.NotConfirmed),
            TotalConfirmedPeople = live.Count(b => b.ConfirmationState == HotelConfirmationState.Confirmed),
            TotalCancelledPeople = bookings.Count(b => b.ConfirmationState == HotelConfirmationState.Cancelled),
            UnplacedPeople = live.Count(b => b.HotelId is null),
        };
    }
    /// <summary>
    /// §1386 — the forecast pool: everyone who has NOT answered the hotel form (operator 2026-11-08: <i>"a way
    /// to see any people that havent responded yet or said no for hotel … all roles except sponsors +
    /// attendees"</i>). Every role but Sponsor and Attendee, active, non-test — every speaker, whatever their
    /// hotel entitlement, because a speaker who has not answered is exactly who the forecast is about.
    /// </summary>
    private async Task<IReadOnlyList<HotelPotentialRequester>> NotAnsweredAsync(
        int eventId, HashSet<int> answered, CancellationToken ct)
    {
        var people = await CrewAsync(eventId, ct);
        return people.Where(p => !answered.Contains(p.ParticipantId))
            .OrderBy(r => r.Role).ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>§1386 — the crew roles the hotel concerns: every role except Sponsor and Attendee; active, non-test.</summary>
    private async Task<List<HotelPotentialRequester>> CrewAsync(int eventId, CancellationToken ct) =>
        await CrewQuery(eventId)
            .Select(p => new HotelPotentialRequester(p.Id, p.FullName, p.Email, p.Role))
            .ToListAsync(ct);

    /// <summary>
    /// §1386 — THE one definition of who the hotel overview concerns: active, non-test, every role except
    /// Sponsor and Attendee, and NOT a sponsor-category speaker. Operator 2026-11-08: <i>"you are including
    /// sponsor speaker category in the hotel form, they must not be included"</i> · <i>"use the speaker
    /// category to differ"</i> — the sponsor pays their speaker's room, so they are not ours to forecast.
    /// Only an explicit <see cref="SpeakerCategory.Sponsor"/> excludes (an uncategorised speaker stays in).
    /// </summary>
    private IQueryable<Participant> CrewQuery(int eventId) =>
        _db.Participants.AsNoTracking()
            .Where(p => p.EventId == eventId && p.IsActive && !p.IsTestUser
                        && p.Role != ParticipantRole.Sponsor && p.Role != ParticipantRole.Attendee
                        && !(p.Role == ParticipantRole.Speaker && _db.SpeakerProfiles.Any(sp =>
                                 sp.EventId == eventId && sp.ParticipantId == p.Id
                                 && sp.Category == SpeakerCategory.Sponsor)));

    /// <summary>
    /// §1386 — the people who answered "no room needed" (same roles as <see cref="NotAnsweredAsync"/>), so an
    /// organizer can go through them too: some will change their mind.
    /// </summary>
    public async Task<IReadOnlyList<HotelPotentialRequester>> SaidNoAsync(int eventId, CancellationToken ct = default)
    {
        var no = await _db.HotelBookings.AsNoTracking()
            .Where(b => b.EventId == eventId && !b.NeedsRoom)
            .Select(b => b.ParticipantId)
            .ToListAsync(ct);
        var set = no.ToHashSet();
        return (await CrewAsync(eventId, ct)).Where(p => set.Contains(p.ParticipantId))
            .OrderBy(r => r.Role).ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// §1386 — who sleeps in a hotel room on one night (operator 2026-11-08: <i>"click on the date to see who
    /// and get list"</i>). Same demand rule as the per-night numbers: active, non-test, needs a room, not
    /// cancelled, <c>check-in &lt;= night &lt; check-out</c> — so the list length always equals the night's
    /// reserved + confirmed count.
    /// </summary>
    public async Task<IReadOnlyList<HotelNightGuest>> GuestsOnNightAsync(int eventId, DateOnly night, CancellationToken ct = default)
    {
        var rows = await _db.HotelBookings.AsNoTracking()
            .Where(b => b.EventId == eventId && b.NeedsRoom
                        && b.ConfirmationState != HotelConfirmationState.Cancelled
                        && b.Participant.IsActive && !b.Participant.IsTestUser
                        && b.CheckInDate != null && b.CheckOutDate != null
                        && b.CheckInDate <= night && b.CheckOutDate > night)
            .Select(b => new HotelNightGuest(
                b.ParticipantId, b.Participant.FullName, b.Participant.Email, b.Participant.Role,
                b.Participant.Hotel != null ? b.Participant.Hotel.Name : null,
                b.CheckInDate, b.CheckOutDate, b.ConfirmationState,
                b.Participant.HotelConfirmationNumber ?? b.ConfirmationNumber,
                b.RoomShareWith))
            .ToListAsync(ct);
        return rows.OrderBy(r => r.HotelName ?? "~").ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// §1386 — the guest grid: everyone the hotel concerns (every role but Sponsor and Attendee; active, non-test),
    /// whether or not they answered, with one flag per night. Filtered by <paramref name="f"/>; the night columns are
    /// always the edition's full set so a filtered view still lines up with the per-night table.
    /// </summary>
    public async Task<HotelGuestGrid> GuestGridAsync(int eventId, HotelGridFilter f, CancellationToken ct = default)
    {
        var people = await CrewQuery(eventId)
            .Select(p => new { p.Id, p.FullName, p.Email, p.Role, p.HotelId, HotelName = p.Hotel != null ? p.Hotel.Name : null, p.HotelConfirmationNumber })
            .ToListAsync(ct);
        var bookings = await _db.HotelBookings.AsNoTracking()
            .Where(b => b.EventId == eventId)
            .Select(b => new { b.ParticipantId, b.NeedsRoom, b.CheckInDate, b.CheckOutDate, b.ConfirmationState,
                               b.ConfirmationNumber, b.RoommateParticipantId, b.GuestsInRoom, b.RoomShareWith })
            .ToDictionaryAsync(b => b.ParticipantId, ct);
        var names = people.ToDictionary(p => p.Id, p => p.FullName);

        var all = people.Select(p =>
        {
            bookings.TryGetValue(p.Id, out var b);
            var answer = b is null ? HotelAnswer.NotAnswered
                : b.ConfirmationState == HotelConfirmationState.Cancelled ? HotelAnswer.Cancelled
                : b.NeedsRoom ? HotelAnswer.Room : HotelAnswer.NoRoom;
            var nights = new HashSet<DateOnly>();
            if (answer == HotelAnswer.Room && b!.CheckInDate is DateOnly ci && b.CheckOutDate is DateOnly co)
                for (var n = ci; n < co; n = n.AddDays(1)) nights.Add(n);
            string? mate = b?.RoommateParticipantId is int m && names.TryGetValue(m, out var mn) ? mn : null;
            return new HotelGuestGridRow(p.Id, p.FullName, p.Email, p.Role, answer, p.HotelId, p.HotelName,
                b?.ConfirmationState, b?.CheckInDate, b?.CheckOutDate,
                p.HotelConfirmationNumber ?? b?.ConfirmationNumber, mate,
                answer == HotelAnswer.Room ? HotelBooking.EffectiveGuestsInRoom(b!.GuestsInRoom, mate is null ? b.RoomShareWith : "x") : null,
                b?.RoomShareWith, nights);
        }).ToList();

        // Night columns: contracted ∪ requested — the same set the per-night table shows.
        var cols = new SortedSet<DateOnly>(await _db.HotelAllotments.AsNoTracking().Where(a => a.EventId == eventId)
            .Select(a => a.Night).Distinct().ToListAsync(ct));
        foreach (var r in all) cols.UnionWith(r.Nights);

        IEnumerable<HotelGuestGridRow> q = all;
        if (f.Answer is HotelAnswer a) q = q.Where(r => r.Answer == a);
        if (f.Role is ParticipantRole role) q = q.Where(r => r.Role == role);
        if (f.HotelId is int hid) q = hid == 0 ? q.Where(r => r.HotelId is null && r.Answer == HotelAnswer.Room) : q.Where(r => r.HotelId == hid);
        if (f.State is HotelConfirmationState st) q = q.Where(r => r.Answer == HotelAnswer.Room && r.State == st);
        if (f.Night is DateOnly night) q = q.Where(r => r.Nights.Contains(night));
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var s = f.Search.Trim();
            q = q.Where(r => r.FullName.Contains(s, StringComparison.OrdinalIgnoreCase) || r.Email.Contains(s, StringComparison.OrdinalIgnoreCase));
        }
        q = f.Sort switch
        {
            "role" => q.OrderBy(r => r.Role).ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase),
            "arrival" => q.OrderBy(r => r.CheckIn ?? DateOnly.MaxValue).ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase),
            "hotel" => q.OrderBy(r => r.HotelName ?? "~").ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase),
            "answer" => q.OrderBy(r => r.Answer).ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase),
            _ => q.OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase),
        };
        return new HotelGuestGrid(cols.ToList(), q.ToList(), all.Count);
    }

    // ---- §1386 — problems, arrivals, readiness -------------------------------------------------

    /// <summary>
    /// §1386 — reservations an organizer should look at before telling the hotel anything or reducing rooms.
    /// Each line names the guest and the fix. Active, non-test people only.
    /// </summary>
    public async Task<IReadOnlyList<HotelProblem>> ProblemsAsync(int eventId, CancellationToken ct = default)
    {
        var hotels = await _db.Hotels.AsNoTracking().Where(h => h.EventId == eventId)
            .Select(h => new { h.Id, h.Name, h.Priority }).ToListAsync(ct);
        var primaryPrio = hotels.Count == 0 ? 1 : hotels.Min(h => h.Priority);
        var contracted = (await _db.HotelAllotments.AsNoTracking().Where(a => a.EventId == eventId)
                .Select(a => new { a.HotelId, a.Night }).ToListAsync(ct))
            .Select(a => (a.HotelId, a.Night)).ToHashSet();
        var rows = await _db.HotelBookings.AsNoTracking()
            .Where(b => b.EventId == eventId
                        && CrewQuery(eventId).Any(p => p.Id == b.ParticipantId))
            .Select(b => new
            {
                b.ParticipantId, b.Participant.FullName, b.NeedsRoom, b.CheckInDate, b.CheckOutDate,
                b.ConfirmationState, b.RoommateParticipantId, b.GuestsInRoom, b.RoomShareWith, b.Participant.HotelId,
            })
            .ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.ParticipantId);
        string HotelName(int? id) => hotels.FirstOrDefault(h => h.Id == id)?.Name ?? "no hotel";

        var problems = new List<HotelProblem>();
        foreach (var r in rows)
        {
            var live = r.NeedsRoom && r.ConfirmationState != HotelConfirmationState.Cancelled;
            if (live && (r.CheckInDate is null || r.CheckOutDate is null || r.CheckInDate >= r.CheckOutDate))
            {
                problems.Add(new(r.ParticipantId, r.FullName, "Needs a room but the dates are missing or the departure is not after the arrival."));
                continue;
            }
            if (live && r.HotelId is null)
                problems.Add(new(r.ParticipantId, r.FullName, "Reserved a room but is in no hotel."));
            if (live && r.HotelId is int hid)
            {
                var outside = new List<DateOnly>();
                for (var n = r.CheckInDate!.Value; n < r.CheckOutDate!.Value; n = n.AddDays(1))
                    if (!contracted.Contains((hid, n))) outside.Add(n);
                if (outside.Count > 0)
                    problems.Add(new(r.ParticipantId, r.FullName,
                        $"Night(s) {string.Join(", ", outside.Select(d => d.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture)))} are not in {HotelName(hid)}'s allocation."));
                if (hotels.FirstOrDefault(h => h.Id == hid)?.Priority > primaryPrio)
                    problems.Add(new(r.ParticipantId, r.FullName, $"Placed in the buffer hotel {HotelName(hid)} — only if the main hotel is full."));
            }
            if (!r.NeedsRoom && r.HotelId is not null && r.ConfirmationState != HotelConfirmationState.Cancelled)
                problems.Add(new(r.ParticipantId, r.FullName, $"Answered \"no room\" but is still placed in {HotelName(r.HotelId)}."));
            if (live && r.RoommateParticipantId is int mate)
            {
                if (!byId.TryGetValue(mate, out var m) || !m.NeedsRoom || m.ConfirmationState == HotelConfirmationState.Cancelled)
                    problems.Add(new(r.ParticipantId, r.FullName, "Their roommate no longer has a room reservation."));
                else if (m.HotelId != r.HotelId)
                    problems.Add(new(r.ParticipantId, r.FullName, $"Roommate {m.FullName} is in {HotelName(m.HotelId)}, not {HotelName(r.HotelId)}."));
                else if (m.RoommateParticipantId is int back && back != r.ParticipantId)
                    problems.Add(new(r.ParticipantId, r.FullName, $"Roommate {m.FullName} is linked to someone else."));
            }
            if (live && r.RoommateParticipantId is null && r.GuestsInRoom is null && !string.IsNullOrWhiteSpace(r.RoomShareWith))
                problems.Add(new(r.ParticipantId, r.FullName,
                    $"Wrote \"sharing with {r.RoomShareWith!.Trim()}\" — link the roommate (if they are a participant) or set guests in room."));
        }
        return problems.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>§1386 — who arrives and who leaves on each day (live reservations, all hotels).</summary>
    public async Task<IReadOnlyList<HotelDayMovement>> ArrivalsAsync(int eventId, CancellationToken ct = default)
    {
        var rows = await _db.HotelBookings.AsNoTracking()
            .Where(b => b.EventId == eventId && b.NeedsRoom && b.ConfirmationState != HotelConfirmationState.Cancelled
                        && b.Participant.IsActive && !b.Participant.IsTestUser
                        && b.CheckInDate != null && b.CheckOutDate != null)
            .Select(b => new { b.ParticipantId, b.Participant.FullName, b.CheckInDate, b.CheckOutDate })
            .ToListAsync(ct);
        var days = rows.Select(r => r.CheckInDate!.Value).Concat(rows.Select(r => r.CheckOutDate!.Value)).Distinct().OrderBy(d => d);
        return days.Select(d => new HotelDayMovement(d,
                rows.Where(r => r.CheckInDate == d).Select(r => (r.ParticipantId, r.FullName)).OrderBy(x => x.FullName).ToList(),
                rows.Where(r => r.CheckOutDate == d).Select(r => (r.ParticipantId, r.FullName)).OrderBy(x => x.FullName).ToList()))
            .ToList();
    }

    /// <summary>
    /// §1386 — "before we reduce the allotment we need to verify everyone has filled out": green only when
    /// everyone answered, nobody who reserved is without a hotel, and no problem is open.
    /// </summary>
    public async Task<HotelReadiness> ReadinessAsync(int eventId, CancellationToken ct = default)
    {
        var ov = await BuildAsync(eventId, includeNotAnswered: true, ct);
        var problems = await ProblemsAsync(eventId, ct);
        return new HotelReadiness(ov.NotAnswered.Count, ov.UnplacedPeople, problems.Count,
            ov.NotAnswered.GroupBy(p => p.Role).ToDictionary(g => g.Key, g => g.Count()));
    }

    /// <summary>Set (or clear with 0/null) the forecast extra for one night. Returns true when it changed.</summary>
    public async Task<bool> SetForecastAsync(
        int eventId, DateOnly night, int? extraRooms, string? note, string? actor,
        HotelChangeLogService log, TimeProvider clock, CancellationToken ct = default)
    {
        var row = await _db.HotelForecastExtras.FirstOrDefaultAsync(f => f.EventId == eventId && f.Night == night, ct);
        var value = Math.Max(0, extraRooms ?? 0);
        var cleanNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        var before = row?.ExtraRooms ?? 0;

        if (value == 0 && cleanNote is null)
        {
            if (row is null) return false;
            _db.HotelForecastExtras.Remove(row);
        }
        else if (row is null)
        {
            _db.HotelForecastExtras.Add(new HotelForecastExtra
            {
                EventId = eventId, Night = night, ExtraRooms = value, Note = cleanNote,
                UpdatedAt = clock.GetUtcNow(), UpdatedByEmail = actor,
            });
        }
        else
        {
            if (row.ExtraRooms == value && row.Note == cleanNote) return false;
            row.ExtraRooms = value;
            row.Note = cleanNote;
            row.UpdatedAt = clock.GetUtcNow();
            row.UpdatedByEmail = actor;
        }

        log.Add(eventId, HotelChangeSource.Organizer, actor, HotelChangeKinds.Forecast,
            $"Forecast {night.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture)}: +{before} → +{value}" + (cleanNote is null ? "" : $" ({cleanNote})"), notify: false);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>§1386 — one thing to fix on a reservation.</summary>
public sealed record HotelProblem(int? ParticipantId, string Name, string Problem);

/// <summary>§1386 — arrivals and departures on one day.</summary>
public sealed record HotelDayMovement(DateOnly Day,
    IReadOnlyList<(int ParticipantId, string FullName)> Arrivals,
    IReadOnlyList<(int ParticipantId, string FullName)> Departures);

/// <summary>§1386 — may we reduce the allocation? Only when everything is filled out.</summary>
public sealed record HotelReadiness(int NotAnswered, int Unplaced, int Problems, IReadOnlyDictionary<ParticipantRole, int> NotAnsweredByRole)
{
    public bool Ready => NotAnswered == 0 && Unplaced == 0 && Problems == 0;
}

/// <summary>§1386 — what a crew member answered on the hotel form.</summary>
public enum HotelAnswer { Room = 0, NoRoom = 1, NotAnswered = 2, Cancelled = 3 }

public static class HotelAnswerText
{
    public static string Of(HotelAnswer a) => a switch
    {
        HotelAnswer.Room => "Room",
        HotelAnswer.NoRoom => "No room",
        HotelAnswer.Cancelled => "Cancelled",
        _ => "Not answered",
    };
}

/// <summary>§1386 — the guest-grid filter (all optional). HotelId 0 = reserved but in no hotel.</summary>
public sealed record HotelGridFilter(
    HotelAnswer? Answer = null, ParticipantRole? Role = null, int? HotelId = null,
    HotelConfirmationState? State = null, DateOnly? Night = null, string? Search = null, string? Sort = null);

/// <summary>§1386 — one person in the guest grid.</summary>
public sealed record HotelGuestGridRow(
    int ParticipantId, string FullName, string Email, ParticipantRole Role, HotelAnswer Answer,
    int? HotelId, string? HotelName, HotelConfirmationState? State, DateOnly? CheckIn, DateOnly? CheckOut,
    string? ConfirmationNumber, string? RoommateName, int? GuestsInRoom, string? ShareWith,
    IReadOnlySet<DateOnly> Nights);

/// <summary>§1386 — the grid: night columns, the filtered rows, and the unfiltered total.</summary>
public sealed record HotelGuestGrid(IReadOnlyList<DateOnly> NightColumns, IReadOnlyList<HotelGuestGridRow> Rows, int Total);
