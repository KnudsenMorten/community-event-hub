using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Entitlements;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms;

/// <summary>
/// Gates the self-service forms (Hotel, Travel, Swag/Polo, Lunch, Appreciation
/// dinner, Award) by ENTITLEMENT rather than role alone, using
/// <see cref="OrderEntitlements"/> as the single source of truth. A sponsor
/// SELF-FUNDED speaker, for example, is entitled to Lunch + the appreciation
/// dinner but NOT to Hotel / Travel / Swag / Award — which a plain role check
/// cannot express.
///
/// <para>The entitlement rules already encode every role's defaults, so the
/// effective set is authoritative for everyone. To honour the existing-access
/// guarantee (a form must never silently lose a role's current access), each
/// per-form check is the entitlement test AND'd / OR'd with the form's own
/// historical role rule by the caller — this helper just computes the effective
/// <see cref="OrderItem"/> set for a participant.</para>
/// </summary>
public static class FormEntitlementGate
{
    /// <summary>
    /// Compute the EFFECTIVE <see cref="OrderItem"/> set for a participant:
    /// <see cref="OrderEntitlements.Base"/> (role hat + speaker hat) with the
    /// participant's <see cref="ParticipantOrderOverride"/>s applied. Loads the
    /// participant row, their <see cref="SpeakerProfile"/> (if any) and their
    /// overrides from the DB.
    /// </summary>
    public static async Task<IReadOnlySet<OrderItem>> EffectiveItemsAsync(
        CommunityHubDbContext db, int eventId, int participantId, CancellationToken ct)
    {
        var participant = await db.Participants
            .FirstOrDefaultAsync(p => p.Id == participantId && p.EventId == eventId, ct);
        if (participant is null)
        {
            return new HashSet<OrderItem>();
        }

        var speaker = await db.SpeakerProfiles
            .FirstOrDefaultAsync(sp => sp.EventId == eventId && sp.ParticipantId == participantId, ct);

        var overrides = await db.ParticipantOrderOverrides
            .Where(o => o.EventId == eventId && o.ParticipantId == participantId)
            .ToListAsync(ct);

        return OrderEntitlements.Effective(participant, speaker, overrides);
    }

    /// <summary>
    /// True when the participant is entitled to <paramref name="item"/> (effective
    /// set contains it).
    /// </summary>
    public static async Task<bool> IsEntitledAsync(
        CommunityHubDbContext db, int eventId, int participantId,
        OrderItem item, CancellationToken ct)
    {
        var items = await EffectiveItemsAsync(db, eventId, participantId, ct);
        return items.Contains(item);
    }

    /// <summary>
    /// True when the participant is entitled to ANY of <paramref name="items"/>
    /// (used by forms covering more than one item, e.g. Lunch = pre-day OR main-day,
    /// Swag = swag OR polo).
    /// </summary>
    public static async Task<bool> IsEntitledToAnyAsync(
        CommunityHubDbContext db, int eventId, int participantId,
        CancellationToken ct, params OrderItem[] items)
    {
        var set = await EffectiveItemsAsync(db, eventId, participantId, ct);
        return items.Any(set.Contains);
    }
}
