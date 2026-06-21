using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// One hotel's room-block occupancy line: the reserved block size against the
/// number of people the organizers have placed there, so a hotel can be seen at a
/// glance as under / at / over its block.
/// </summary>
/// <param name="HotelId">The hotel this line is for.</param>
/// <param name="HotelName">Display name.</param>
/// <param name="RoomBlockSize">
/// The reserved block size, or null when no block has been recorded yet (the view
/// then shows "not set" instead of a misleading remaining figure).
/// </param>
/// <param name="Assigned">People currently placed in this hotel.</param>
/// <param name="AssignedNeedingRoom">
/// Of the placed people, how many actually indicated they need a room
/// (<see cref="HotelBooking.NeedsRoom"/>) — the count that genuinely consumes a
/// reserved room. People placed who do not need a room (e.g. a local commuter who
/// was grouped for logistics) do not count against the block.
/// </param>
/// <param name="Confirmed">Placed people who have a per-person confirmation number.</param>
public sealed record HotelBlockLine(
    int HotelId,
    string HotelName,
    int? RoomBlockSize,
    int Assigned,
    int AssignedNeedingRoom,
    int Confirmed)
{
    /// <summary>True when this hotel has a recorded block size to compare against.</summary>
    public bool HasBlock => RoomBlockSize.HasValue;

    /// <summary>
    /// Rooms still free in the block (block − people-needing-a-room), floored at 0;
    /// null when no block is recorded. Uses the needs-a-room count, not the raw
    /// placement count, since only a room-needer consumes a reserved room.
    /// </summary>
    public int? Remaining =>
        RoomBlockSize is int b ? Math.Max(0, b - AssignedNeedingRoom) : null;

    /// <summary>
    /// How many room-needers exceed the block (people − block), floored at 0; null
    /// when no block is recorded. &gt; 0 means the block is oversubscribed.
    /// </summary>
    public int? Over =>
        RoomBlockSize is int b ? Math.Max(0, AssignedNeedingRoom - b) : null;

    /// <summary>True when the block is recorded AND oversubscribed (needs &gt; block).</summary>
    public bool IsOverBlock => Over is > 0;

    /// <summary>
    /// Block utilisation 0–100+ (% of the block consumed by room-needers); null
    /// when no block is recorded, and 0 for an explicit empty (size-0) block so a
    /// reference hotel never shows a divide-by-zero. Can exceed 100 when over-block.
    /// </summary>
    public int? Percent =>
        RoomBlockSize is int b
            ? (b == 0 ? 0 : (int)Math.Round(100.0 * AssignedNeedingRoom / b))
            : null;
}

/// <summary>
/// The edition-wide room-block occupancy snapshot: a per-hotel breakdown plus the
/// roll-up totals an organizer needs to know whether the room blocks across all
/// hotels cover the people who need a room.
/// </summary>
public sealed class HotelBlockSnapshot
{
    /// <summary>Per-hotel lines, alphabetical by hotel name.</summary>
    public List<HotelBlockLine> Hotels { get; set; } = new();

    /// <summary>Total reserved rooms across every hotel that HAS a recorded block.</summary>
    public int TotalBlock { get; set; }

    /// <summary>Total people placed across all hotels.</summary>
    public int TotalAssigned { get; set; }

    /// <summary>Total placed people who indicated they need a room.</summary>
    public int TotalAssignedNeedingRoom { get; set; }

    /// <summary>
    /// People who indicated they need a room but are NOT yet placed in any hotel —
    /// the organizer's outstanding placement work.
    /// </summary>
    public int UnassignedNeedingRoom { get; set; }

    /// <summary>Hotels whose block is oversubscribed (room-needers &gt; block).</summary>
    public int HotelsOverBlock => Hotels.Count(h => h.IsOverBlock);

    /// <summary>Hotels with no recorded block size yet.</summary>
    public int HotelsWithoutBlock => Hotels.Count(h => !h.HasBlock);

    /// <summary>
    /// Rooms still free across all blocks (sum of each hotel's remaining); only
    /// hotels with a recorded block contribute.
    /// </summary>
    public int TotalRemaining => Hotels.Sum(h => h.Remaining ?? 0);

    /// <summary>
    /// True when every hotel with a block is within it AND nobody who needs a room
    /// is left unplaced — i.e. the room-block plan currently holds. (Hotels with no
    /// recorded block are reported separately via <see cref="HotelsWithoutBlock"/>
    /// and do not by themselves make the plan "not OK".)
    /// </summary>
    public bool PlanLooksOk => HotelsOverBlock == 0 && UnassignedNeedingRoom == 0;
}

/// <summary>
/// The room-block occupancy authority (REQUIREMENTS §3 hotels — "manage the room
/// block per hotel" / §20 Organizer Logistics). Read-only aggregation over the
/// existing <see cref="Hotel"/> placements + <see cref="HotelBooking"/> room-need
/// preferences: it never writes (recording the block size is
/// <see cref="HotelManagementService"/>'s job). Edition-scoped; pure +
/// unit-testable (constructor-injected EF context, no clock, no I/O). The hotel
/// list query is SQL-translatable; the room-need set is read once and joined in
/// memory (the same shape <see cref="HotelManagementService.GroupByHotelAsync"/>
/// already uses).
/// </summary>
public sealed class HotelRoomBlockService
{
    private readonly CommunityHubDbContext _db;

    public HotelRoomBlockService(CommunityHubDbContext db) => _db = db;

    /// <summary>
    /// Build the edition's room-block occupancy snapshot. Every hotel appears (an
    /// empty hotel shows 0 placed). People who need a room are counted from their
    /// <see cref="HotelBooking.NeedsRoom"/> preference; a placement without a room
    /// need still appears in <see cref="HotelBlockLine.Assigned"/> but does not
    /// consume the block.
    /// </summary>
    public async Task<HotelBlockSnapshot> BuildAsync(int eventId, CancellationToken ct = default)
    {
        var hotels = await _db.Hotels
            .Where(h => h.EventId == eventId)
            .OrderBy(h => h.Name)
            .Select(h => new { h.Id, h.Name, h.RoomBlockSize })
            .ToListAsync(ct);

        // Per-participant room-need preference (no booking row = not needing a room).
        var needs = await _db.HotelBookings
            .Where(hb => hb.EventId == eventId)
            .Select(hb => new { hb.ParticipantId, hb.NeedsRoom })
            .ToDictionaryAsync(x => x.ParticipantId, x => x.NeedsRoom, ct);

        var placements = await _db.Participants
            .Where(p => p.EventId == eventId)
            .Select(p => new { p.Id, p.HotelId, p.HotelConfirmationNumber })
            .ToListAsync(ct);

        bool NeedsRoom(int participantId) => needs.TryGetValue(participantId, out var n) && n;

        var snap = new HotelBlockSnapshot();

        foreach (var h in hotels)
        {
            var here = placements.Where(p => p.HotelId == h.Id).ToList();
            var assigned = here.Count;
            var needing = here.Count(p => NeedsRoom(p.Id));
            var confirmed = here.Count(p => !string.IsNullOrWhiteSpace(p.HotelConfirmationNumber));

            snap.Hotels.Add(new HotelBlockLine(
                h.Id, h.Name, h.RoomBlockSize, assigned, needing, confirmed));

            if (h.RoomBlockSize is int b) snap.TotalBlock += b;
            snap.TotalAssigned += assigned;
            snap.TotalAssignedNeedingRoom += needing;
        }

        snap.UnassignedNeedingRoom = placements
            .Count(p => p.HotelId is null && NeedsRoom(p.Id));

        return snap;
    }
}
