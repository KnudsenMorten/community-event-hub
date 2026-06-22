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
/// Media / EventPartner / Attendee row even if the email collides with a
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

        // Company-level role pointers. Company Manager exposes NO per-user roles
        // on the users endpoint (REQUIREMENTS §7c) -- only these two single-default
        // pointers on the company record. So we can only flag the two default
        // contacts (signer / event-coordinator); every other contact's role is
        // left for an organizer to set in the hub (we never guess). A contact who
        // is BOTH pointers gets BOTH flags.
        var company = await _cm.GetCompanyAsync(companyId, ct);
        var signerUserId = company?.DefaultSignerUserId ?? 0;
        var coordinatorUserId = company?.EventCoordinationDefaultContactUserId ?? 0;

        var companyIdStr = companyId.ToString();
        var now = _clock.GetUtcNow();
        var created = 0; var updated = 0; var skipped = 0;

        foreach (var u in users)
        {
            // Resolve this user's CM-default role flags (signer / coordinator).
            var isSigner = signerUserId != 0 && u.UserId == signerUserId;
            var isCoordinator = coordinatorUserId != 0 && u.UserId == coordinatorUserId;

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
                    // The unique-identifier contact link (REQUIREMENTS §7c): write
                    // the CM user_id from the /companies/{id}/users response, so the
                    // hub contact is linked to CM by id, never by name. 0 (CM omitted
                    // it) maps to null.
                    CmUserId = u.UserId != 0 ? u.UserId : null,
                    IsSigner = isSigner,
                    IsEventCoordinator = isCoordinator,
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
            // Unique-identifier contact link (REQUIREMENTS §7c): write/refresh the
            // CM user_id on update too, so a row created before this column existed
            // (or whose CM user_id changed) is backfilled by the next sync. Linked
            // by id, never by name. 0 (CM omitted it) is left as-is, never overwriting
            // a previously-captured id with null.
            var cmUserId = u.UserId != 0 ? (int?)u.UserId : null;
            if (cmUserId is not null && existing.CmUserId != cmUserId)
            {
                existing.CmUserId = cmUserId;
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
            // Role flags are SET-only from the CM default pointers, never cleared:
            // CM only knows the single signer / coordinator defaults, so clearing
            // would wipe an organizer's manually-added coordinator. A contact that
            // is the CM default signer/coordinator is flagged here; everyone else's
            // flags are left exactly as the organizer set them (REQUIREMENTS §7c).
            if (isSigner && !existing.IsSigner)
            {
                existing.IsSigner = true;
                changed = true;
            }
            if (isCoordinator && !existing.IsEventCoordinator)
            {
                existing.IsEventCoordinator = true;
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
