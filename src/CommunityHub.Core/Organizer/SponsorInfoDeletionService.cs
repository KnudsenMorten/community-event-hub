using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// The single server-side authority for DELETING a sponsor company's self-service
/// facts row — the <see cref="Domain.SponsorInfo"/> (logos, descriptions, website,
/// tier) keyed by (EventId, SponsorCompanyId) — from an edition (REQUIREMENTS §22
/// "Sponsor contacts / facts CRUD gap"). The Sponsors back-office previously had
/// NO way to remove a stale or duplicate company-facts row (e.g. a booth order
/// processed under a wrong/changed company id leaving an orphaned card on the
/// public sponsors page).
///
/// Sponsor CONTACTS are <see cref="Domain.Participant"/> rows already covered by
/// the Participants grid's per-row + bulk delete; this is the complementary gap —
/// the one-row-per-company facts record that drives the PUBLIC sponsors page.
///
/// It mirrors the established delete-safely pattern
/// (<see cref="SessionDeletionService"/>):
///   - The safe default is to REFUSE a delete while the company is LIVE — i.e. it
///     still has any active sponsor contact in this edition. Deleting the facts of
///     a live sponsor would silently drop their logo/description from the public
///     page. The organizer is told to deactivate/remove the company's contacts
///     first (then the facts row is safe to remove).
///   - A facts row for a company with NO active contacts (a genuinely stale /
///     orphaned record) is removed.
///
/// Invariants (all enforced HERE, not in the page):
///   - EVERY operation is scoped to the caller's <c>eventId</c>; a facts row in
///     another edition is never found, never touched.
///   - Lookup is by the row's primary key id (the grid passes the SponsorInfo id);
///     a missing id is reported <see cref="DeletionStatus.NotFound"/>.
///   - One <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>.
///
/// No schema change — operates only on existing entities.
/// </summary>
public sealed class SponsorInfoDeletionService
{
    private readonly CommunityHubDbContext _db;

    public SponsorInfoDeletionService(CommunityHubDbContext db)
    {
        _db = db;
    }

    /// <summary>The outcome of a sponsor-facts delete call.</summary>
    public enum DeletionStatus
    {
        /// <summary>No facts row with that id exists in this edition.</summary>
        NotFound,
        /// <summary>Deleted: the orphaned/stale company-facts row was removed.</summary>
        Deleted,
        /// <summary>
        /// Refused because the company is still live (it has active sponsor
        /// contacts). The facts row is left untouched; handle the contacts first.
        /// </summary>
        Blocked,
    }

    /// <summary>Result of a sponsor-facts delete call.</summary>
    /// <param name="Status">What happened.</param>
    /// <param name="SponsorInfoId">The facts-row id acted on (0 when not found).</param>
    /// <param name="SponsorCompanyId">The company id, for the confirmation message (null when not found).</param>
    /// <param name="ActiveContactCount">
    /// When <see cref="DeletionStatus.Blocked"/>, how many active sponsor contacts
    /// the company still has (so the message can say "still has N active contact(s)").
    /// 0 otherwise.
    /// </param>
    public sealed record DeletionResult(
        DeletionStatus Status, int SponsorInfoId, string? SponsorCompanyId, int ActiveContactCount)
    {
        public bool Found => Status != DeletionStatus.NotFound;
    }

    /// <summary>
    /// Count active sponsor contacts for a company in this edition — what makes a
    /// facts delete unsafe. 0 = the facts row is orphaned and safe to remove. Lets
    /// the UI offer the delete (or show the "live company" note).
    /// </summary>
    public Task<int> GetActiveContactCountAsync(
        int eventId, string sponsorCompanyId, CancellationToken ct = default)
        => _db.Participants.CountAsync(
            p => p.EventId == eventId
                 && p.Role == Domain.ParticipantRole.Sponsor
                 && p.IsActive
                 && p.SponsorCompanyId == sponsorCompanyId, ct);

    /// <summary>
    /// Delete a sponsor company-facts row WHEN SAFE: a row whose company still has
    /// active contacts is refused (<see cref="DeletionStatus.Blocked"/>); an
    /// orphaned/stale row is removed.
    /// </summary>
    public async Task<DeletionResult> DeleteAsync(
        int eventId, int sponsorInfoId, CancellationToken ct = default)
    {
        var info = await _db.SponsorInfos
            .FirstOrDefaultAsync(s => s.Id == sponsorInfoId && s.EventId == eventId, ct);
        if (info is null)
        {
            return new DeletionResult(
                DeletionStatus.NotFound, 0 < sponsorInfoId ? sponsorInfoId : 0, null, 0);
        }

        var activeContacts = await GetActiveContactCountAsync(eventId, info.SponsorCompanyId, ct);
        if (activeContacts > 0)
        {
            return new DeletionResult(
                DeletionStatus.Blocked, info.Id, info.SponsorCompanyId, activeContacts);
        }

        _db.SponsorInfos.Remove(info);
        await _db.SaveChangesAsync(ct);
        return new DeletionResult(DeletionStatus.Deleted, info.Id, info.SponsorCompanyId, 0);
    }
}
