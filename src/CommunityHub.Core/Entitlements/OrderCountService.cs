using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Entitlements;

/// <summary>
/// One participant entitled to a given <see cref="OrderItem"/>, projected for
/// the per-item entitled list.
/// </summary>
/// <param name="ParticipantId">The entitled participant's id.</param>
/// <param name="FullName">Their name.</param>
/// <param name="Email">Their email (identity).</param>
/// <param name="Role">Their primary role.</param>
public sealed record EntitledParticipant(
    int ParticipantId,
    string FullName,
    string Email,
    ParticipantRole Role);

/// <summary>
/// Counts how many DISTINCT physical people are entitled to each
/// <see cref="OrderItem"/> in an edition, using the EFFECTIVE entitlements
/// (<see cref="OrderEntitlements.Effective"/>, i.e. AFTER overrides).
///
/// <para>
/// Dedup once per person: a participant whose
/// <see cref="Participant.SamePersonAsId"/> is set is a DUPLICATE of another row
/// and is NOT counted on its own — the primary it points to represents them, so
/// each physical person contributes at most one of each item.
/// </para>
///
/// Loads the edition's rows then computes in memory (the entitlement rules are
/// pure CLR logic, not translatable to SQL) — correctness over cleverness.
/// </summary>
public sealed class OrderCountService
{
    private readonly CommunityHubDbContext _db;

    public OrderCountService(CommunityHubDbContext db) => _db = db;

    /// <summary>
    /// Per-<see cref="OrderItem"/> count of distinct entitled people in the
    /// edition (duplicates excluded). Every item appears in the result, including
    /// items nobody is entitled to (count 0).
    /// </summary>
    public async Task<Dictionary<OrderItem, int>> CountsAsync(
        int eventId, CancellationToken ct = default)
    {
        var byItem = await EntitledByItemAsync(eventId, ct);
        return Enum.GetValues<OrderItem>()
            .ToDictionary(
                item => item,
                item => byItem.TryGetValue(item, out var list) ? list.Count : 0);
    }

    /// <summary>
    /// The list of distinct entitled people PER <see cref="OrderItem"/>
    /// (duplicates excluded). Every item key is present, with an empty list when
    /// nobody is entitled.
    /// </summary>
    public async Task<Dictionary<OrderItem, List<EntitledParticipant>>> EntitledByItemAsync(
        int eventId, CancellationToken ct = default)
    {
        // Only PRIMARY rows count toward tallies; a row pointing at another via
        // SamePersonAsId is a duplicate represented by its primary.
        var participants = await _db.Participants
            .Where(p => p.EventId == eventId && p.SamePersonAsId == null)
            .ToListAsync(ct);

        var speakerByParticipant = await _db.SpeakerProfiles
            .Where(s => s.EventId == eventId)
            .ToDictionaryAsync(s => s.ParticipantId, s => s, ct);

        var overridesByParticipant = (await _db.ParticipantOrderOverrides
                .Where(o => o.EventId == eventId)
                .ToListAsync(ct))
            .GroupBy(o => o.ParticipantId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = Enum.GetValues<OrderItem>()
            .ToDictionary(item => item, _ => new List<EntitledParticipant>());

        foreach (var p in participants)
        {
            speakerByParticipant.TryGetValue(p.Id, out var speaker);
            var overrides = overridesByParticipant.TryGetValue(p.Id, out var ov)
                ? ov
                : (IEnumerable<ParticipantOrderOverride>)Array.Empty<ParticipantOrderOverride>();

            var items = OrderEntitlements.Effective(p, speaker, overrides);
            if (items.Count == 0) continue;

            var entitled = new EntitledParticipant(p.Id, p.FullName, p.Email, p.Role);
            foreach (var item in items)
            {
                result[item].Add(entitled);
            }
        }

        return result;
    }
}
