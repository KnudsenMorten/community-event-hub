using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>The outcome of trying to reserve an item.</summary>
public sealed record HoldResult(SwagCatalogHold? Hold, string? Error)
{
    public bool Ok => Hold is not null;
}

/// <summary>
/// §1165b — organizer reservations of catalogue items: take one, release one, and let them lapse.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-01: <i>"it must be possible to reserve and order so they become
/// unavailable"</i>, and — asked who may reserve — <b>organizers only, with an expiry and a
/// notice</b>.</para>
///
/// <para>🔑 <b>A hold is the promise the webshop cannot express.</b> Buying takes an item off the
/// market on its own; a hold covers the gap between <i>"we'll take the power banks"</i> at a meeting
/// and the order actually arriving. Without it that promise lives in somebody's memory, and the item
/// gets sold twice.</para>
///
/// <para>🔒 <b>Organizer-only is what makes it safe.</b> One writer means no race, nobody can strand
/// an item by abandoning a checkout, and the awkward question of what to do with a sponsor's
/// half-finished hold never arises.</para>
/// </remarks>
public sealed class SwagCatalogHoldService
{
    private readonly CommunityHubDbContext _db;
    private readonly ILogger<SwagCatalogHoldService> _log;

    public SwagCatalogHoldService(CommunityHubDbContext db, ILogger<SwagCatalogHoldService> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>Reserve an item for a company until <paramref name="expiresAt"/>.</summary>
    /// <remarks>
    /// ⚠️ The unique index is the real guard, not the check below. Two organizers reserving the same
    /// item in the same minute both pass a "is it free?" read; only the database can settle it. The
    /// check exists to give a readable answer in the common case, and the catch to give one in the
    /// rare case.
    /// </remarks>
    public async Task<HoldResult> HoldAsync(
        int eventId, long productId, string productName,
        string sponsorCompanyId, string companyName,
        DateTimeOffset expiresAt, string byEmail, string? note,
        DateTimeOffset now, CancellationToken ct = default)
    {
        if (productId <= 0) return new(null, "No catalogue item was given.");
        if (string.IsNullOrWhiteSpace(sponsorCompanyId)) return new(null, "No sponsor company was given.");
        if (expiresAt <= now) return new(null, "A hold must expire in the future.");

        var existing = await _db.SwagCatalogHolds
            .Where(h => h.EventId == eventId && h.ProductId == productId && h.ReleasedAt == null)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            return new(null,
                $"'{existing.ProductName}' is already reserved for {existing.CompanyName} until "
                + $"{existing.ExpiresAt:yyyy-MM-dd}. Release that hold first if it should move.");
        }

        var hold = new SwagCatalogHold
        {
            EventId = eventId,
            ProductId = productId,
            ProductName = productName,
            SponsorCompanyId = sponsorCompanyId,
            CompanyName = companyName,
            ExpiresAt = expiresAt,
            CreatedAt = now,
            CreatedByEmail = byEmail,
            Note = note,
        };

        _db.SwagCatalogHolds.Add(hold);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // The other organizer won the race. Their hold stands; this one never existed.
            _db.Entry(hold).State = EntityState.Detached;
            _log.LogWarning(ex, "§1165b: concurrent hold on product {Product}.", productId);
            return new(null,
                "Somebody reserved that item a moment ago. Reload the page to see who has it.");
        }

        _log.LogInformation(
            "§1165b: '{Product}' reserved for {Company} until {Until} by {By}.",
            productName, companyName, expiresAt, byEmail);

        return new(hold, null);
    }

    /// <summary>End a hold. The row stays; only its ending is recorded.</summary>
    public async Task<bool> ReleaseAsync(
        int eventId, int holdId, string byEmail, string reason,
        DateTimeOffset now, CancellationToken ct = default)
    {
        var hold = await _db.SwagCatalogHolds
            .FirstOrDefaultAsync(h => h.EventId == eventId && h.Id == holdId && h.ReleasedAt == null, ct);
        if (hold is null) return false;

        hold.ReleasedAt = now;
        hold.ReleasedByEmail = byEmail;
        hold.ReleasedReason = reason;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("§1165b: hold on '{Product}' released — {Reason}.", hold.ProductName, reason);
        return true;
    }

    /// <summary>Every live hold for the edition, newest first.</summary>
    public async Task<IReadOnlyList<SwagCatalogHold>> LiveHoldsAsync(
        int eventId, DateTimeOffset now, CancellationToken ct = default)
    {
        var open = await _db.SwagCatalogHolds
            .Where(h => h.EventId == eventId && h.ReleasedAt == null)
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync(ct);

        // Filtered in memory through IsLiveAt so the rule lives in ONE place. A second copy of
        // "released is null AND expires in the future" in a LINQ predicate is how this view and the
        // sweep would come to disagree about which items are free.
        return open.Where(h => h.IsLiveAt(now)).ToList();
    }

    /// <summary>
    /// Release every hold that has run out, and report which ones lapsed.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>This is not housekeeping — it is what makes the expiry real.</b> The "one live hold per
    /// item" unique index filters on <c>ReleasedAt</c>, because an index cannot compare against
    /// "now". So a hold that is merely PAST its expiry still occupies the item: without this sweep
    /// the item would be blocked for ever, which is the precise failure the expiry was added to
    /// prevent.
    /// </remarks>
    public async Task<IReadOnlyList<SwagCatalogHold>> ReleaseExpiredAsync(
        int eventId, DateTimeOffset now, CancellationToken ct = default)
    {
        var expired = await _db.SwagCatalogHolds
            .Where(h => h.EventId == eventId && h.ReleasedAt == null && h.ExpiresAt <= now)
            .ToListAsync(ct);

        foreach (var hold in expired)
        {
            hold.ReleasedAt = now;
            hold.ReleasedByEmail = "system";
            hold.ReleasedReason = "The hold ran out and was released automatically.";
        }

        if (expired.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("§1165b: {Count} expired hold(s) released.", expired.Count);
        }

        return expired;
    }

    /// <summary>
    /// Holds that lapse within <paramref name="within"/> and have not been warned about yet.
    /// </summary>
    /// <remarks>
    /// 🔒 Stamped once. A notice that repeats every sweep tick is a notice he stops reading, which
    /// is the §302 shape — and this one is worth reading, because a lapsing hold is a promise about
    /// to be broken quietly.
    /// </remarks>
    public async Task<IReadOnlyList<SwagCatalogHold>> DueForExpiryNoticeAsync(
        int eventId, TimeSpan within, DateTimeOffset now, CancellationToken ct = default)
    {
        var cutoff = now + within;
        return await _db.SwagCatalogHolds
            .Where(h => h.EventId == eventId
                        && h.ReleasedAt == null
                        && h.ExpiryNoticeSentAt == null
                        && h.ExpiresAt > now
                        && h.ExpiresAt <= cutoff)
            .OrderBy(h => h.ExpiresAt)
            .ToListAsync(ct);
    }

    /// <summary>Record that the lapse notice went out, so it goes out once.</summary>
    public async Task MarkExpiryNoticeSentAsync(
        IEnumerable<SwagCatalogHold> holds, DateTimeOffset now, CancellationToken ct = default)
    {
        var any = false;
        foreach (var h in holds)
        {
            h.ExpiryNoticeSentAt = now;
            any = true;
        }
        if (any) await _db.SaveChangesAsync(ct);
    }
}
