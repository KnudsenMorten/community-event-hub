using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// Organizer-only BULK delete over a selected set of HOTELS in one edition
/// (REQUIREMENTS §20 "Full CRUD + bulk everywhere"). The Hotels grid already
/// deletes one row at a time via <see cref="HotelManagementService.DeleteHotelAsync"/>;
/// this is the multi-select counterpart for clearing several hotels at once
/// (a mis-imported / duplicated room block is the common place an organizer needs it).
///
/// It applies EXACTLY the same safe semantics as the single-row delete, batch-wide:
///   - EVERY operation is scoped to the caller's <c>eventId</c>; a hotel in another
///     edition is never found, never touched.
///   - Any participants placed in a deleted hotel are first UN-ASSIGNED
///     (<c>Participant.HotelId</c> → null) so a row is never removed while a foreign
///     key still points at it — nobody loses their participant record, they just
///     lose the (now-gone) hotel placement, exactly like the single-row delete.
///   - One <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> per call — the
///     whole batch commits together or not at all.
///
/// Unlike sessions, a hotel carries no attendee-supplied data to protect, so nothing
/// is "blocked"; the result still reports how many people were un-assigned so the
/// confirm flow / banner can be honest about the side effect. No schema change — it
/// operates on the same entities + the same <c>Participant.HotelId</c> link the
/// single-row service does.
/// </summary>
public sealed class HotelBulkOperationService
{
    private readonly CommunityHubDbContext _db;

    public HotelBulkOperationService(CommunityHubDbContext db)
    {
        _db = db;
    }

    /// <summary>Outcome of a bulk hotel-delete call.</summary>
    /// <param name="Matched">Distinct ids that resolved to a hotel in this event.</param>
    /// <param name="Deleted">How many hotels were physically removed.</param>
    /// <param name="Unassigned">
    /// How many participants were un-assigned (their now-deleted hotel placement
    /// cleared) as a side effect — so the caller can say so honestly.
    /// </param>
    public sealed record BulkResult(int Matched, int Deleted, int Unassigned)
    {
        /// <summary>How many requested ids did NOT resolve in this event (ignored).</summary>
        public int Skipped(int requested) => Math.Max(0, requested - Matched);
    }

    /// <summary>
    /// Delete every selected hotel in the edition. Participants placed in any of
    /// them are un-assigned first (HotelId → null), then the hotels are removed.
    /// One transaction. Ids that do not resolve in this edition are ignored (counted
    /// via <see cref="BulkResult.Skipped"/>).
    /// </summary>
    public async Task<BulkResult> DeleteAsync(
        int eventId, IEnumerable<int> hotelIds, CancellationToken ct = default)
    {
        var ids = Normalize(hotelIds);
        if (ids.Count == 0) return new BulkResult(0, 0, 0);

        var targets = await _db.Hotels
            .Where(h => h.EventId == eventId && ids.Contains(h.Id))
            .ToListAsync(ct);
        if (targets.Count == 0) return new BulkResult(0, 0, 0);

        var targetIds = targets.Select(h => h.Id).ToList();

        // Un-assign everyone placed in any of the doomed hotels in one set-based query
        // (rather than per-hotel N+1) so no FK dangles when the rows are removed.
        var placed = await _db.Participants
            .Where(p => p.EventId == eventId && p.HotelId != null && targetIds.Contains(p.HotelId!.Value))
            .ToListAsync(ct);
        foreach (var p in placed) p.HotelId = null;

        _db.Hotels.RemoveRange(targets);
        await _db.SaveChangesAsync(ct);

        return new BulkResult(
            Matched: targets.Count, Deleted: targets.Count, Unassigned: placed.Count);
    }

    // Distinct + drop non-positive ids so a stray "0"/duplicate from a posted form
    // never widens the match set.
    private static List<int> Normalize(IEnumerable<int> ids) =>
        ids.Where(id => id > 0).Distinct().ToList();
}
