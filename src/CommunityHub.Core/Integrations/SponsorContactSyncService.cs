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

    // §895 — e-conomic is the master for WHO the contacts are. Optional so an unconfigured ERP
    // cannot stop the CM mirror running; without it the service behaves exactly as before.
    private readonly Erp.EconomicContactAdminService? _erpContacts;

    public SponsorContactSyncService(
        CommunityHubDbContext db,
        CompanyManagerClient cm,
        CompanyManagerOptions options,
        TimeProvider clock,
        ILogger<SponsorContactSyncService> log,
        Erp.EconomicContactAdminService? erpContacts = null)
    {
        _db = db;
        _cm = cm;
        _options = options;
        _clock = clock;
        _log = log;
        _erpContacts = erpContacts;
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

    /// <summary>
    /// §895 — bring CEH's sponsor contacts in line with <b>e-conomic</b>, keyed on the e-conomic
    /// contact number so a renamed e-mail UPDATES the existing participant instead of duplicating it.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Three-step match, in this order:</b> by <c>ErpContactNumber</c> (the identity);
    /// then, once, by e-mail — which ADOPTS a row created before this column existed and stamps the
    /// id on it; then create. After the adoption pass a contact is keyed by id for ever.</para>
    ///
    /// <para>🔒 <b>Deactivate, never delete</b> (§502). A participant carrying an ERP contact number
    /// that e-conomic no longer lists is switched off, keeping their history and any links.</para>
    ///
    /// <para>⚠️ <b>An EMPTY contact list changes nothing.</b> An empty read is evidence the source
    /// cannot be trusted, never evidence that everyone left — the same safety stop as
    /// <c>ErpWebshopContactSyncService</c>, which exists because one such pass could otherwise
    /// deactivate a company's entire staff.</para>
    /// </remarks>
    private async Task ReconcileFromErpAsync(
        int eventId, int companyId, string? erpCustomerNumber, CancellationToken ct)
    {
        if (_erpContacts is null) return;
        if (!int.TryParse((erpCustomerNumber ?? string.Empty).Trim(), out var erpNo) || erpNo <= 0) return;

        IReadOnlyList<Erp.EconomicContactAdminService.ContactView> erp;
        try { erp = await _erpContacts.ListContactsAsync(erpNo, ct); }
        catch (Exception ex)
        {
            // A read we could not perform is not a roster that emptied.
            _log.LogWarning(ex, "SponsorContactSync: could not read e-conomic contacts for {ErpNo}.", erpNo);
            return;
        }

        if (erp.Count == 0) return;   // ⚠️ see remarks — never treated as "everyone left".

        var companyIdStr = companyId.ToString();
        var now = _clock.GetUtcNow();
        var seen = new HashSet<int>();

        foreach (var c in erp)
        {
            var email = (c.Email ?? string.Empty).Trim().ToLowerInvariant();
            if (email.Length == 0) continue;
            seen.Add(c.ContactNumber);

            var row = await _db.Participants
                .FirstOrDefaultAsync(p => p.EventId == eventId && p.ErpContactNumber == c.ContactNumber, ct);

            // Adoption: an existing row that predates this column, found once by e-mail.
            row ??= await _db.Participants
                .FirstOrDefaultAsync(p => p.EventId == eventId && p.Email == email, ct);

            if (row is null)
            {
                _db.Participants.Add(new Participant
                {
                    EventId = eventId,
                    Email = email,
                    FullName = string.IsNullOrWhiteSpace(c.Name) ? email : c.Name.Trim(),
                    Role = ParticipantRole.Sponsor,
                    SponsorCompanyId = companyIdStr,
                    ErpContactNumber = c.ContactNumber,
                    IsSigner = c.IsSigner,
                    IsEventCoordinator = c.IsEventCoordinator,
                    IsActive = true,
                    LifecycleState = ParticipantLifecycleState.Active,
                    CreatedAt = now,
                });
                continue;
            }

            // 🔒 Never clobber a non-sponsor role — the same guard the CM pass applies.
            if (row.Role != ParticipantRole.Sponsor) continue;

            // 🔑 THE RENAME, IN PLACE. Same CEH id, new address — tasks, links and history survive.
            row.ErpContactNumber = c.ContactNumber;
            if (!string.Equals(row.Email, email, StringComparison.Ordinal)) row.Email = email;
            if (!string.IsNullOrWhiteSpace(c.Name)) row.FullName = c.Name.Trim();
            row.SponsorCompanyId = companyIdStr;
            row.IsSigner = c.IsSigner;
            row.IsEventCoordinator = c.IsEventCoordinator;
        }

        // §502 — gone from e-conomic ⇒ switched off here. Only rows we have already keyed by id,
        // so a never-adopted legacy row is never deactivated by a rule it was not part of.
        var stale = await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.SponsorCompanyId == companyIdStr
                        && p.Role == ParticipantRole.Sponsor
                        && p.IsActive
                        && p.ErpContactNumber != null)
            .ToListAsync(ct);

        foreach (var p in stale.Where(p => !seen.Contains(p.ErpContactNumber!.Value)))
        {
            p.IsActive = false;
            _log.LogInformation(
                "SponsorContactSync: {Email} deactivated — e-conomic contact {No} no longer exists.",
                p.Email, p.ErpContactNumber);
        }

        await _db.SaveChangesAsync(ct);
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

        // 🔑 §895 — ERP FIRST. e-conomic is the master (§482), so a contact's identity and its
        // current e-mail come from there — keyed on the e-conomic contact number, which survives a
        // rename. The Company Manager pass below then only fills in the webshop seat.
        //
        // 🔴 Why this had to change: CEH matched on E-MAIL, mirrored from CM. When the operator
        // renamed three addresses in e-conomic, CM could not update a user's e-mail, so the users
        // were deleted and recreated with new ids — and BOTH keys CEH could match on changed at
        // once. The next sync would have created three duplicates and left the originals active.
        // The e-conomic contact number was the only thing that survived.
        await ReconcileFromErpAsync(eventId, companyId, company?.ErpCustomerNumber, ct);

        var companyIdStr = companyId.ToString();
        var now = _clock.GetUtcNow();
        var created = 0; var updated = 0; var skipped = 0;

        foreach (var u in users)
        {
            // Resolve this user's CM-default role flags (signer / coordinator).
            var isSigner = signerUserId != 0 && u.UserId == signerUserId;
            var isCoordinator = coordinatorUserId != 0 && u.UserId == coordinatorUserId;

            // NORMALIZE at write (§253 G17): CM returns whatever casing the shop
            // user typed. Storing it raw fed the Sessionize import's in-memory
            // email dedup a mixed-case key it could miss (near-duplicate row).
            // The SQL CI collation already matches the lookup either way; the
            // lowercased STORED value is what keeps every in-memory consumer safe.
            var email = (u.Email ?? string.Empty).Trim().ToLowerInvariant();
            if (email.Length == 0)
            {
                _log.LogWarning(
                    "SponsorContactSync: company {Co} user {UserId} has no email — skipped.",
                    companyId, u.UserId);
                skipped++;
                continue;
            }

            var existing = await _db.Participants
                .FirstOrDefaultAsync(
                    p => p.EventId == eventId && p.Email == email, ct);

            if (existing is null)
            {
                _db.Participants.Add(new Participant
                {
                    EventId = eventId,
                    Email = email,
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
                    // Active by default (operator 2026-06-23): without this the
                    // LifecycleState defaulted to Inactive, so a synced sponsor read
                    // as "Inactive" (IsActive=true AND LifecycleState==Active is the
                    // active rule) and was hidden by the active-only filter.
                    LifecycleState = ParticipantLifecycleState.Active,
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
            // Heal legacy rows stored with raw CM casing (§253 G17) — same
            // identity under the CI collation, normalized for in-memory consumers.
            if (!string.Equals(existing.Email, email, StringComparison.Ordinal))
            {
                existing.Email = email;
                changed = true;
            }
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
            // Re-activate a contact that went inactive for sync-side reasons — but
            // NEVER one an ORGANIZER deactivated (§253 G8): the tombstone
            // DeactivatedByOrganizerAt marks an explicit organizer decision that
            // this 15-min pull must not silently undo. A manual organizer
            // re-activation clears the tombstone and hands the contact back to
            // the sync.
            if (!existing.IsActive && existing.DeactivatedByOrganizerAt is null)
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
