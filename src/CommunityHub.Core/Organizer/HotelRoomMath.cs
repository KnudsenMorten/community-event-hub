namespace CommunityHub.Core.Organizer;

/// <summary>§1386 — one person sleeping somewhere on a night, as the room counter needs them.</summary>
public readonly record struct HotelSleeper(int ParticipantId, int? RoommateParticipantId, int? HotelId, bool Confirmed);

/// <summary>
/// §1386 — ROOMS, not people (operator 2026-11-08: two volunteers sharing are one room). The ONE rule every
/// per-night number uses, so the board, the overview, the warning mail and the hotel sheet cannot disagree.
/// </summary>
/// <remarks>
/// Two sleepers share a room when either names the other as roommate AND both sleep at the same hotel that
/// night. A roommate who is not there that night (arrives later, cancelled, other hotel) does not shrink the
/// count — the guest who is there still needs a room. A shared room counts as Confirmed only when every
/// person in it is confirmed.
/// </remarks>
public static class HotelRoomMath
{
    /// <summary>The room key: the lower participant id of a sharing pair, else the person's own id.</summary>
    public static int RoomKey(HotelSleeper s, IReadOnlyDictionary<int, HotelSleeper> present)
    {
        if (s.RoommateParticipantId is int mate && mate != s.ParticipantId
            && present.TryGetValue(mate, out var other) && other.HotelId == s.HotelId)
            return Math.Min(s.ParticipantId, mate);

        // The other direction: someone present names THIS person as their roommate.
        foreach (var o in present.Values)
            if (o.RoommateParticipantId == s.ParticipantId && o.ParticipantId != s.ParticipantId && o.HotelId == s.HotelId)
                return Math.Min(s.ParticipantId, o.ParticipantId);

        return s.ParticipantId;
    }

    /// <summary>(rooms, confirmed rooms) for the sleepers of one night.</summary>
    public static (int Rooms, int ConfirmedRooms) Count(IEnumerable<HotelSleeper> sleepers)
    {
        var present = new Dictionary<int, HotelSleeper>();
        foreach (var s in sleepers) present[s.ParticipantId] = s;

        var rooms = present.Values
            .GroupBy(s => RoomKey(s, present))
            .ToList();
        return (rooms.Count, rooms.Count(g => g.All(s => s.Confirmed)));
    }
}
