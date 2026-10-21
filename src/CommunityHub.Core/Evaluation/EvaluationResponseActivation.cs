using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §1357 — set the responses collected so far INACTIVE (kept, counted nowhere), or bring them back.
/// </summary>
/// <remarks>
/// Operator 2026-10-19: *"the current session eval submissions we have already must flushed before event or
/// better: make a state inactive so it is not included. i need option to set existing data in inactive mode like a
/// button"*. Inactive is enforced by the global query filter on <c>EvaluationResponse</c>; this class is one of
/// the few places that looks past it. 🔒 Nothing is deleted — the rows stay append-only and reversible.
/// </remarks>
public static class EvaluationResponseActivation
{
    public sealed record Counts(int Active, int Inactive);

    public static async Task<Counts> CountAsync(CommunityHubDbContext db, int eventId, CancellationToken ct = default)
    {
        var all = db.EvaluationResponses.IgnoreQueryFilters().Where(r => r.EventId == eventId);
        return new Counts(
            await all.CountAsync(r => !r.IsInactive, ct),
            await all.CountAsync(r => r.IsInactive, ct));
    }

    /// <summary>Every response of the edition collected up to <paramref name="now"/> becomes inactive. Returns how many.</summary>
    public static async Task<int> SetAllInactiveAsync(CommunityHubDbContext db, int eventId, DateTimeOffset now, CancellationToken ct = default)
    {
        var rows = await db.EvaluationResponses
            .Where(r => r.EventId == eventId && r.ReceivedTimestamp <= now)
            .ToListAsync(ct);
        foreach (var r in rows) { r.IsInactive = true; r.InactivatedAt = now; }
        if (rows.Count > 0) await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    /// <summary>Every inactive response of the edition counts again. Returns how many.</summary>
    public static async Task<int> ReactivateAllAsync(CommunityHubDbContext db, int eventId, CancellationToken ct = default)
    {
        var rows = await db.EvaluationResponses.IgnoreQueryFilters()
            .Where(r => r.EventId == eventId && r.IsInactive)
            .ToListAsync(ct);
        foreach (var r in rows) { r.IsInactive = false; r.InactivatedAt = null; }
        if (rows.Count > 0) await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}
