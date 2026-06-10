using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>Outcome of one company's contact sync.</summary>
public sealed record SponsorContactSyncResult(
    int CompanyId,
    int UsersConsidered,
    int ParticipantsCreated,
    int ParticipantsUpdated,
    int ParticipantsSkipped);

/// <summary>
/// Mirrors a Company Manager company's linked users into the hub's
/// <see cref="Participant"/> table so sponsor contacts can PIN-log-in
/// and see their company's tasks at /Sponsor/Index. Runs as part of
/// every sponsor-order pull (one company at a time, deduped by id).
///
/// Safety rule: only INSERT new rows or UPDATE rows that are already
/// Role=Sponsor. NEVER overwrite an Organizer / Speaker / Volunteer /
/// Video / Camera / Attendee row even if the email collides with a
/// sponsor's contact -- a hub staff member listed inside a sponsor's
/// Company Manager record should not silently lose their staff role.
/// Such collisions are logged as Skipped.
/// </summary>
public sealed class SponsorContactSyncService
{
    private readonly CommunityHubDbContext _db;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<SponsorContactSyncService> _log;

    public SponsorContactSyncService(
        CommunityHubDbContext db,
        CompanyManagerClient cm,
        CompanyManagerOptions options,
        TimeProvider clock,
        ILogger<SponsorContactSyncService> log)
    {
        _db = db;
        _cm = cm;
        _options = options;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// Sync one company's users into Participants for the given edition.
    /// Idempotent: re-running for the same (event, company) makes the
    /// state converge without duplicate rows or surprise role changes.
    /// </summary>
    /// <summary>
    /// Fetch one company's Company-Manager-side record (mainly for the
    /// legal name -- WooCommerce orders often have empty billing.company,
    /// but Company Manager always has the canonical company name).
    /// Returns null if Company Manager is disabled or the call fails.
    /// </summary>
    public async Task<CompanyManagerCompany?> LookupCompanyAsync(
        int companyId, CancellationToken ct = default)
    {
        if (!_options.Enabled) return null;
        return await _cm.GetCompanyAsync(companyId, ct);
    }

    public async Task<SponsorContactSyncResult> SyncCompanyAsync(
        int eventId, int companyId, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation(
                "SponsorContactSync: CompanyManager disabled by config (company {Co}).",
                companyId);
            return new SponsorContactSyncResult(companyId, 0, 0, 0, 0);
        }

        var users = await _cm.GetCompanyUsersAsync(companyId, ct);
        if (users.Count == 0)
        {
            _log.LogInformation(
                "SponsorContactSync: no users linked to company {Co}.",
                companyId);
            return new SponsorContactSyncResult(companyId, 0, 0, 0, 0);
        }

        var companyIdStr = companyId.ToString();
        var now = _clock.GetUtcNow();
        var created = 0; var updated = 0; var skipped = 0;

        foreach (var u in users)
        {
            var existing = await _db.Participants
                .FirstOrDefaultAsync(
                    p => p.EventId == eventId && p.Email == u.Email, ct);

            if (existing is null)
            {
                _db.Participants.Add(new Participant
                {
                    EventId = eventId,
                    Email = u.Email,
                    FullName = ChooseName(u),
                    Role = ParticipantRole.Sponsor,
                    SponsorCompanyId = companyIdStr,
                    IsActive = true,
                    CreatedAt = now,
                });
                created++;
                continue;
            }

            // Existing row -- only safe to update if it's already a sponsor
            // OR has no role assignment that would be clobbered.
            if (existing.Role != ParticipantRole.Sponsor)
            {
                _log.LogWarning(
                    "SponsorContactSync: skipping {Email} -- already exists with Role={Role} (would clobber non-sponsor role).",
                    u.Email, existing.Role);
                skipped++;
                continue;
            }

            var changed = false;
            if (existing.SponsorCompanyId != companyIdStr)
            {
                existing.SponsorCompanyId = companyIdStr;
                changed = true;
            }
            var preferredName = ChooseName(u);
            if (!string.Equals(existing.FullName, preferredName, StringComparison.Ordinal))
            {
                existing.FullName = preferredName;
                changed = true;
            }
            if (!existing.IsActive)
            {
                existing.IsActive = true;
                changed = true;
            }
            if (changed) updated++;
        }

        if (created > 0 || updated > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        _log.LogInformation(
            "SponsorContactSync: company {Co} -- {N} users; created={C} updated={U} skipped={S}.",
            companyId, users.Count, created, updated, skipped);

        return new SponsorContactSyncResult(
            CompanyId: companyId,
            UsersConsidered: users.Count,
            ParticipantsCreated: created,
            ParticipantsUpdated: updated,
            ParticipantsSkipped: skipped);
    }

    /// <summary>Prefer full_name when populated; fall back to display_name.</summary>
    private static string ChooseName(CompanyManagerUser u) =>
        string.IsNullOrWhiteSpace(u.FullName) ? u.DisplayName : u.FullName;
}
