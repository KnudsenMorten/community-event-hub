using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

// ---------------------------------------------------------------------------
// §326bs — per-night allotment vs demand. Read-only projections; the only
// mutations are the explicit Set* methods an organizer drives.
// ---------------------------------------------------------------------------

/// <summary>One hotel × one night: what is contracted, what is needed, and the gap.</summary>
public sealed record AllotmentCell(DateOnly Night, int? Allotted, int Demand)
{
    /// <summary>True when this night has no contracted number at all (no row).
    /// Distinct from a contracted 0 — "not part of the deal" vs "zero rooms held".</summary>
    public bool NotContracted => Allotted is null;

    /// <summary>Contracted minus needed. Negative = SHORT (find more rooms);
    /// positive = SLACK (release candidates); 0 = exact. Null when not contracted.</summary>
    public int? Variance => Allotted is int a ? a - Demand : null;

    public bool IsShort => Variance is int v && v < 0;
    public bool HasSlack => Variance is int v && v > 0;
}

/// <summary>One hotel's row in the allotment matrix, plus its release deadlines.</summary>
public sealed record AllotmentRow(
    int? HotelId,
    string HotelName,
    IReadOnlyList<AllotmentCell> Cells,
    IReadOnlyList<HotelCutoff> Cutoffs)
{
    /// <summary>True for the synthetic "Not assigned yet" row — real demand from people
    /// who need a room but have not been placed in a hotel. It has no allotment to edit;
    /// it exists so unplaced demand is never invisible when totals are read.</summary>
    public bool IsUnassigned => HotelId is null;

    public int TotalDemand => Cells.Sum(c => c.Demand);
    public int TotalAllotted => Cells.Sum(c => c.Allotted ?? 0);
    public int ShortNights => Cells.Count(c => c.IsShort);
}

/// <summary>The whole matrix: the nights (columns) and one row per hotel.</summary>
public sealed class AllotmentBoard
{
    public IReadOnlyList<DateOnly> Nights { get; init; } = Array.Empty<DateOnly>();
    public IReadOnlyList<AllotmentRow> Rows { get; init; } = Array.Empty<AllotmentRow>();

    /// <summary>Contracted total across every hotel for one night.</summary>
    public int AllottedOn(DateOnly night) => Rows
        .Where(r => !r.IsUnassigned)
        .Sum(r => r.Cells.FirstOrDefault(c => c.Night == night)?.Allotted ?? 0);

    /// <summary>Rooms needed across every hotel INCLUDING unplaced people for one night.</summary>
    public int DemandOn(DateOnly night) => Rows
        .Sum(r => r.Cells.FirstOrDefault(c => c.Night == night)?.Demand ?? 0);

    /// <summary>Whole-event variance for one night (contracted − needed).</summary>
    public int VarianceOn(DateOnly night) => AllottedOn(night) - DemandOn(night);

    /// <summary>Nights where the whole event is short — the "ask for more rooms" list.</summary>
    public IReadOnlyList<DateOnly> ShortNights =>
        Nights.Where(n => VarianceOn(n) < 0).ToList();

    /// <summary>Nights with spare contracted rooms — the "release before cut-off" list.</summary>
    public IReadOnlyList<DateOnly> SlackNights =>
        Nights.Where(n => VarianceOn(n) > 0).ToList();

    public bool IsEmpty => Nights.Count == 0 || Rows.Count == 0;
}

/// <summary>
/// §326bs — compares CONTRACTED rooms per night (<see cref="HotelAllotment"/>)
/// against the rooms actually needed, so an organizer can answer the only two
/// questions a hotel contract really poses:
/// <list type="bullet">
///   <item>where are we SHORT (ask this hotel for more, or place people elsewhere)?</item>
///   <item>where do we have SLACK (release it before the <see cref="HotelCutoff"/>
///         so we stop paying for empty rooms)?</item>
/// </list>
///
/// <para><b>Demand.</b> A participant needs a room on night N when they have a
/// <see cref="HotelBooking"/> with <c>NeedsRoom</c> and
/// <c>CheckInDate &lt;= N &lt; CheckOutDate</c> — check-out day is NOT a night slept.
/// Demand counts ROOMS, one per person: room-sharing is recorded as free text
/// (<see cref="HotelBooking.RoomShareWith"/>) and cannot be paired reliably, so
/// counting one room each is the safe direction to be wrong in — an over-estimate
/// leaves spare rooms, an under-estimate leaves someone with nowhere to sleep.</para>
///
/// <para><b>ACTIVE people only</b> (§253 G2 / §326bh): a drop-out's surviving booking
/// must never inflate what we contract and pay for. This is the same gate every other
/// logistics total uses.</para>
///
/// <para><b>Unplaced demand is shown, not hidden.</b> Someone who needs a room but has
/// no <see cref="Participant.HotelId"/> yet appears in a synthetic "Not assigned yet"
/// row. Dropping them would make the totals look balanced while people still had no
/// bed — the exact failure this board exists to prevent.</para>
/// </summary>
public sealed class HotelAllotmentService
{
    /// <summary>Label for the synthetic row holding demand from unplaced people.</summary>
    public const string UnassignedRowName = "Not assigned yet";

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public HotelAllotmentService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Build the board. The night columns span every night that is either contracted
    /// or requested, so the grid always shows the whole picture — including a booking
    /// that falls OUTSIDE the contracted window, which is precisely the case an
    /// organizer must notice.
    /// </summary>
    public async Task<AllotmentBoard> BuildAsync(int eventId, CancellationToken ct = default)
    {
        var hotels = await _db.Hotels
            .Where(h => h.EventId == eventId)
            .OrderBy(h => h.Name)
            .Select(h => new { h.Id, h.Name })
            .ToListAsync(ct);

        var allotments = await _db.HotelAllotments
            .Where(a => a.EventId == eventId)
            .Select(a => new { a.HotelId, a.Night, a.RoomsAllotted })
            .ToListAsync(ct);

        // ACTIVE people only, with their placement. A booking whose dates are missing
        // or inverted contributes no nights (it cannot be counted honestly).
        var bookings = await _db.HotelBookings
            .Where(b => b.EventId == eventId
                        && b.NeedsRoom
                        && b.Participant.IsActive && !b.Participant.IsTestUser
                        && b.CheckInDate != null
                        && b.CheckOutDate != null)
            .Select(b => new
            {
                b.CheckInDate,
                b.CheckOutDate,
                HotelId = b.Participant.HotelId,
            })
            .ToListAsync(ct);

        var stays = bookings
            .Where(b => b.CheckInDate!.Value < b.CheckOutDate!.Value)
            .ToList();

        // Columns: every contracted night ∪ every requested night.
        var nights = new SortedSet<DateOnly>(allotments.Select(a => a.Night));
        foreach (var s in stays)
        {
            for (var n = s.CheckInDate!.Value; n < s.CheckOutDate!.Value; n = n.AddDays(1))
                nights.Add(n);
        }
        var nightList = nights.ToList();
        if (nightList.Count == 0)
        {
            return new AllotmentBoard();   // nothing contracted and nothing requested
        }

        var cutoffs = await _db.HotelCutoffs
            .Where(c => c.EventId == eventId)
            .OrderBy(c => c.CutoffDate)
            .ToListAsync(ct);

        int DemandFor(int? hotelId, DateOnly night) => stays.Count(
            s => s.HotelId == hotelId
                 && s.CheckInDate!.Value <= night && night < s.CheckOutDate!.Value);

        var rows = new List<AllotmentRow>();
        foreach (var h in hotels)
        {
            var byNight = allotments
                .Where(a => a.HotelId == h.Id)
                .ToDictionary(a => a.Night, a => a.RoomsAllotted);

            rows.Add(new AllotmentRow(
                h.Id,
                h.Name,
                nightList.Select(n => new AllotmentCell(
                    n,
                    byNight.TryGetValue(n, out var alloc) ? alloc : null,
                    DemandFor(h.Id, n))).ToList(),
                cutoffs.Where(c => c.HotelId == h.Id).ToList()));
        }

        // Unplaced demand — only when it exists, so the board stays clean once
        // everyone has been given a hotel.
        var unassigned = nightList.Select(n => new AllotmentCell(n, null, DemandFor(null, n))).ToList();
        if (unassigned.Any(c => c.Demand > 0))
        {
            rows.Add(new AllotmentRow(
                null, UnassignedRowName, unassigned, Array.Empty<HotelCutoff>()));
        }

        return new AllotmentBoard { Nights = nightList, Rows = rows };
    }

    /// <summary>
    /// Upsert one hotel's allotment for a set of nights. A null count DELETES the row
    /// (back to "not contracted") rather than storing 0 — the two mean different things.
    /// Negative input is clamped to 0, matching the §326ba rule that a nonsense number
    /// is a typo, never a hidden "unlimited".
    /// </summary>
    public async Task<int> SetAllotmentsAsync(
        int eventId, int hotelId, IReadOnlyDictionary<DateOnly, int?> byNight,
        CancellationToken ct = default)
    {
        var hotelExists = await _db.Hotels
            .AnyAsync(h => h.Id == hotelId && h.EventId == eventId, ct);
        if (!hotelExists) return 0;

        var nights = byNight.Keys.ToList();
        var existing = await _db.HotelAllotments
            .Where(a => a.EventId == eventId && a.HotelId == hotelId && nights.Contains(a.Night))
            .ToListAsync(ct);

        var now = _clock.GetUtcNow();
        var changed = 0;

        foreach (var (night, value) in byNight)
        {
            var row = existing.FirstOrDefault(a => a.Night == night);

            if (value is null)
            {
                if (row is not null) { _db.HotelAllotments.Remove(row); changed++; }
                continue;
            }

            var rooms = Math.Max(0, value.Value);
            if (row is null)
            {
                _db.HotelAllotments.Add(new HotelAllotment
                {
                    EventId = eventId, HotelId = hotelId, Night = night,
                    RoomsAllotted = rooms, CreatedAt = now, UpdatedAt = now,
                });
                changed++;
            }
            else if (row.RoomsAllotted != rooms)
            {
                row.RoomsAllotted = rooms;
                row.UpdatedAt = now;
                changed++;
            }
        }

        if (changed > 0) await _db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>
    /// Fill a contiguous night range with the SAME count in one go — the fast path for
    /// entering a contract that holds a flat number across a block of nights. Existing
    /// nights in the range are overwritten.
    /// </summary>
    public Task<int> SetRangeAsync(
        int eventId, int hotelId, DateOnly fromNight, DateOnly toNight, int rooms,
        CancellationToken ct = default)
    {
        if (toNight < fromNight) (fromNight, toNight) = (toNight, fromNight);

        var map = new Dictionary<DateOnly, int?>();
        for (var n = fromNight; n <= toNight; n = n.AddDays(1)) map[n] = rooms;
        return SetAllotmentsAsync(eventId, hotelId, map, ct);
    }

    // ----- Cut-offs ---------------------------------------------------------

    public Task<List<HotelCutoff>> ListCutoffsAsync(int eventId, CancellationToken ct = default) =>
        _db.HotelCutoffs
            .Where(c => c.EventId == eventId)
            .OrderBy(c => c.CutoffDate).ThenBy(c => c.Id)
            .ToListAsync(ct);

    /// <summary>Add or update a release deadline (keyed on hotel + date).</summary>
    public async Task<bool> SaveCutoffAsync(
        int eventId, int hotelId, DateOnly cutoffDate, string label,
        int? releasePercent, string? notes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        var hotelExists = await _db.Hotels
            .AnyAsync(h => h.Id == hotelId && h.EventId == eventId, ct);
        if (!hotelExists) return false;

        var pct = releasePercent is int p ? Math.Clamp(p, 0, 100) : (int?)null;
        var now = _clock.GetUtcNow();

        var row = await _db.HotelCutoffs.FirstOrDefaultAsync(
            c => c.EventId == eventId && c.HotelId == hotelId && c.CutoffDate == cutoffDate, ct);

        if (row is null)
        {
            _db.HotelCutoffs.Add(new HotelCutoff
            {
                EventId = eventId, HotelId = hotelId, CutoffDate = cutoffDate,
                Label = label.Trim(), ReleasePercent = pct, Notes = Blank(notes),
                CreatedAt = now, UpdatedAt = now,
            });
        }
        else
        {
            row.Label = label.Trim();
            row.ReleasePercent = pct;
            row.Notes = Blank(notes);
            row.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteCutoffAsync(int eventId, int cutoffId, CancellationToken ct = default)
    {
        var row = await _db.HotelCutoffs.FirstOrDefaultAsync(
            c => c.Id == cutoffId && c.EventId == eventId, ct);
        if (row is null) return false;

        _db.HotelCutoffs.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
