using CommunityHub.Core.Data;
using CommunityHub.Core.Domain.Evaluation;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §1350 — the room mapper: <b>device → physical room → schedule rooms → sessions</b>.
/// </summary>
/// <remarks>
/// <para>🔒 The ONE place that answers "which schedule rooms does this unit collect for". Ingest
/// attribution, the kiosk's current session and countdown, and the sync backfill all ask here, so the
/// screen can never name one session while the vote lands on another. The press time then picks the
/// session (<see cref="EvaluationAttribution.Resolve"/>) — the pre-day and the main days use different
/// schedule rooms for the same space, and never at the same time.</para>
///
/// <para>A unit with a physical room uses that room's placements only. A unit without one falls back to
/// its §1349 direct links (<see cref="EvaluationDevice.LinkedRoomIds"/>), so nothing linked before the
/// mapper existed stops counting.</para>
/// </remarks>
public static class EvaluationRoomMap
{
    /// <summary>The schedule rooms this unit collects for.</summary>
    public static async Task<IReadOnlyList<int>> RoomIdsAsync(
        CommunityHubDbContext db, EvaluationDevice device, CancellationToken ct = default)
    {
        if (device.PhysicalRoomId is int physicalId)
        {
            return await db.EvaluationRoomPlacements
                .Where(p => p.PhysicalRoomId == physicalId)
                .Select(p => p.EvaluationRoomId)
                .ToListAsync(ct);
        }
        return device.LinkedRoomIds();
    }

    /// <summary>Every unit of the event → its schedule rooms, for the batch backfill.</summary>
    public static async Task<Dictionary<string, List<int>>> RoomIdsBySerialAsync(
        CommunityHubDbContext db, int eventId, CancellationToken ct = default)
    {
        var devices = await db.EvaluationDevices
            .Where(d => d.EventId == eventId
                        && (d.PhysicalRoomId != null || d.RoomId != null || d.MasterClassRoomId != null))
            .Select(d => new { d.SerialNumber, d.PhysicalRoomId, d.RoomId, d.MasterClassRoomId })
            .ToListAsync(ct);
        var physicalIds = devices.Where(d => d.PhysicalRoomId != null).Select(d => d.PhysicalRoomId!.Value).Distinct().ToList();
        var placements = (await db.EvaluationRoomPlacements
                .Where(p => physicalIds.Contains(p.PhysicalRoomId))
                .Select(p => new { p.PhysicalRoomId, p.EvaluationRoomId })
                .ToListAsync(ct))
            .GroupBy(p => p.PhysicalRoomId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.EvaluationRoomId).ToList());

        return devices.ToDictionary(
            d => d.SerialNumber,
            d => d.PhysicalRoomId is int pid
                ? (placements.TryGetValue(pid, out var rooms) ? rooms : new List<int>())
                : new[] { d.RoomId, d.MasterClassRoomId }.OfType<int>().Distinct().ToList(),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The name the kiosk shows: the physical room, else the §1349 main-day room, else its pre-day room.
    /// </summary>
    public static async Task<string?> RoomNameAsync(
        CommunityHubDbContext db, EvaluationDevice device, CancellationToken ct = default)
    {
        if (device.PhysicalRoomId is int physicalId)
            return await db.EvaluationPhysicalRooms.Where(r => r.Id == physicalId).Select(r => r.Name).FirstOrDefaultAsync(ct);
        return (device.RoomId ?? device.MasterClassRoomId) is int roomId
            ? await db.EvaluationRooms.Where(r => r.Id == roomId).Select(r => r.Name).FirstOrDefaultAsync(ct)
            : null;
    }

    // ---- what an organizer must see -----------------------------------------------------------

    /// <summary>One problem with the mapping, in words an organizer can act on.</summary>
    public sealed record Warning(string Text);

    /// <summary>
    /// The mapping's gaps: a schedule room with sessions that no physical room holds (its sessions get
    /// no tablet feedback), two schedule rooms in one physical room whose sessions overlap (a press in
    /// the overlap is ambiguous), and a physical room with no active tablet.
    /// </summary>
    public static async Task<IReadOnlyList<Warning>> WarningsAsync(
        CommunityHubDbContext db, int eventId, CancellationToken ct = default)
    {
        var warnings = new List<Warning>();
        var rooms = await db.EvaluationRooms.Where(r => r.EventId == eventId).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var physical = await db.EvaluationPhysicalRooms
            .Where(p => p.EventId == eventId)
            .Include(p => p.Placements)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
        var sessions = await db.EvaluationSessions
            .Where(s => s.EventId == eventId && s.RoomId != null)
            .Select(s => new { s.Id, s.Title, RoomId = s.RoomId!.Value, s.ScheduledStart, s.ScheduledEnd })
            .ToListAsync(ct);

        var placed = physical.SelectMany(p => p.Placements).Select(p => p.EvaluationRoomId).ToHashSet();
        foreach (var g in sessions.GroupBy(s => s.RoomId).Where(g => !placed.Contains(g.Key)).OrderBy(g => rooms.GetValueOrDefault(g.Key)))
        {
            warnings.Add(new Warning(
                $"'{rooms.GetValueOrDefault(g.Key)}' has {g.Count()} session(s) but is in no physical room — "
                + "no tablet collects feedback for them."));
        }

        foreach (var p in physical)
        {
            var roomIds = p.Placements.Select(x => x.EvaluationRoomId).ToHashSet();
            var ordered = sessions.Where(s => roomIds.Contains(s.RoomId)).OrderBy(s => s.ScheduledStart).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                for (var j = i + 1; j < ordered.Count && ordered[j].ScheduledStart < ordered[i].ScheduledEnd; j++)
                {
                    // Same schedule room: the sync already reports it. Here only the mapping's own doing.
                    if (ordered[i].RoomId == ordered[j].RoomId) continue;
                    if (!EvaluationAttribution.Overlaps(ordered[i].ScheduledStart, ordered[i].ScheduledEnd,
                                                        ordered[j].ScheduledStart, ordered[j].ScheduledEnd)) continue;
                    warnings.Add(new Warning(
                        $"'{p.Name}': '{ordered[i].Title}' ({rooms.GetValueOrDefault(ordered[i].RoomId)}) and "
                        + $"'{ordered[j].Title}' ({rooms.GetValueOrDefault(ordered[j].RoomId)}) overlap — a press "
                        + "in the overlap cannot be attributed unambiguously."));
                }
            }
        }

        var withTablet = await db.EvaluationDevices
            .Where(d => d.EventId == eventId && d.IsActive && d.PhysicalRoomId != null)
            .Select(d => d.PhysicalRoomId!.Value)
            .Distinct()
            .ToListAsync(ct);
        foreach (var p in physical.Where(p => !withTablet.Contains(p.Id) && p.Placements.Any(x => sessions.Any(s => s.RoomId == x.EvaluationRoomId))))
            warnings.Add(new Warning($"'{p.Name}' has sessions but no active tablet."));

        return warnings;
    }
}
