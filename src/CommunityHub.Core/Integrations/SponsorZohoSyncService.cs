using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// Pushes a sponsor company's Company-Details fields to Zoho Backstage on
/// "Save &amp; Sync to Zoho". Every paying company is a Zoho SPONSOR; companies
/// that bought booth products are ALSO a Zoho EXHIBITOR — so a company can carry
/// two Zoho ids.
///
/// IDs over names: the Zoho sponsor/exhibitor id is resolved ONCE by matching the
/// company name, then cached on <see cref="Domain.SponsorInfo.ZohoSponsorId"/> /
/// <c>ZohoExhibitorId</c>; subsequent syncs target by id (names change, ids don't).
/// All writes are UTF-8 (JsonContent) so Danish æøå survive. Fail-soft: the caller
/// always saves to SQL first; this only reports what synced.
/// </summary>
public sealed class SponsorZohoSyncService
{
    private readonly ZohoClient _zoho;
    private readonly CommunityHubDbContext _db;
    private readonly ZohoOptions _options;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _cmOptions;
    private readonly ILogger<SponsorZohoSyncService> _log;

    public SponsorZohoSyncService(
        ZohoClient zoho, CommunityHubDbContext db, ZohoOptions options,
        CompanyManagerClient cm, CompanyManagerOptions cmOptions,
        ILogger<SponsorZohoSyncService> log)
    {
        _zoho = zoho;
        _db = db;
        _options = options;
        _cm = cm;
        _cmOptions = cmOptions;
        _log = log;
    }

    public sealed record SyncResult(
        bool Enabled, bool SponsorSynced, bool ExhibitorSynced, bool IsExhibitor, string? Error);

    public async Task<SyncResult> SyncAsync(
        int eventId, string companyId, string companyName, CancellationToken ct = default,
        string? accessToken = null)
    {
        if (!_options.Enabled)
            return new(false, false, false, false, null);

        var info = await _db.SponsorInfos.FirstOrDefaultAsync(
            s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);
        if (info is null)
            return new(true, false, false, false, "Nothing to sync yet.");

        // Reuse a caller-supplied token (bulk re-sync fetches ONE token for the whole
        // run to avoid the Zoho token-endpoint rate limit); otherwise fetch our own.
        var token = accessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            try { token = await _zoho.GetAccessTokenAsync(ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Zoho sync: token request threw."); token = null; }
        }
        if (string.IsNullOrWhiteSpace(token))
            return new(true, false, false, info.HasBooth, "Could not authenticate to Zoho Backstage.");

        var changedIds = false;
        var sponsorSynced = false;
        var exhibitorSynced = false;

        try
        {
            // --- Sponsor record (all paying companies) ---
            if (string.IsNullOrWhiteSpace(info.ZohoSponsorId))
            {
                var sponsors = await _zoho.GetSponsorsAsync(token!, ct);
                var match = sponsors.FirstOrDefault(s => NameEq(s.CompanyName, companyName));
                if (match is not null) { info.ZohoSponsorId = match.Id; changedIds = true; }
            }
            if (!string.IsNullOrWhiteSpace(info.ZohoSponsorId))
            {
                sponsorSynced = await _zoho.UpdateSponsorAsync(
                    token!, info.ZohoSponsorId!,
                    description: info.CompanyDescription,
                    websiteUrl: info.WebsiteUrl,
                    companyName: companyName, ct,
                    contactFirstName: info.EventCoordinatorFirstName,
                    contactLastName: info.EventCoordinatorLastName,
                    contactEmail: info.EventCoordinatorEmail);
            }

            // --- Exhibitor record (booth companies only) ---
            if (info.HasBooth)
            {
                if (string.IsNullOrWhiteSpace(info.ZohoExhibitorId))
                {
                    var exhibitors = await _zoho.GetExhibitorsAsync(token!, ct);
                    var match = exhibitors.FirstOrDefault(e => NameEq(e.CompanyName, companyName));
                    if (match is not null) { info.ZohoExhibitorId = match.Id; changedIds = true; }
                }
                if (!string.IsNullOrWhiteSpace(info.ZohoExhibitorId))
                {
                    exhibitorSynced = await _zoho.UpdateExhibitorAsync(
                        token!, info.ZohoExhibitorId!,
                        companyOverview: info.CompanyDescription,
                        companyShortDescription: info.CompanyDescriptionShort,
                        ct,
                        companyName: companyName,
                        contactFirstName: info.EventCoordinatorFirstName,
                        contactLastName: info.EventCoordinatorLastName,
                        websiteUrl: info.WebsiteUrl,
                        linkedInUrl: info.LinkedInUrl,
                        twitterUrl: info.TwitterUrl,
                        contactEmail: info.EventCoordinatorEmail,
                        contactMobile: info.EventCoordinatorPhone);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Zoho sync failed for company {Co}.", companyId);
            if (changedIds) { try { await _db.SaveChangesAsync(ct); } catch { /* best-effort */ } }
            return new(true, sponsorSynced, exhibitorSynced, info.HasBooth, "Zoho sync hit an error — your details are saved; please try Sync again.");
        }

        if (changedIds) await _db.SaveChangesAsync(ct);

        string? error = null;
        if (string.IsNullOrWhiteSpace(info.ZohoSponsorId))
            error = "Couldn't find a matching sponsor in Zoho Backstage by company name — align the name in Backstage and re-sync.";
        else if (info.HasBooth && string.IsNullOrWhiteSpace(info.ZohoExhibitorId))
            error = "Synced your sponsor record, but couldn't find a matching exhibitor in Zoho by company name.";

        return new(true, sponsorSynced, exhibitorSynced, info.HasBooth, error);
    }

    public sealed record BoothSyncResult(
        bool Enabled, bool IsExhibitor, int AddedToZoho, int PulledFromZoho, string? Error);

    /// <summary>
    /// Reconcile this exhibitor's booth members with Zoho Backstage by EMAIL
    /// (there is no update/delete-member API, so this is add-only both ways):
    /// members present in Zoho but not in CEH are pulled INTO CEH; members in CEH
    /// but not in Zoho are created in Zoho (create-bulk). Matched members are
    /// flagged synced. Removals must be done in Zoho directly.
    /// </summary>
    public async Task<BoothSyncResult> SyncBoothMembersAsync(
        int eventId, string companyId, string companyName, CancellationToken ct = default)
    {
        if (!_options.Enabled) return new(false, false, 0, 0, null);

        var info = await _db.SponsorInfos.FirstOrDefaultAsync(
            s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);
        if (info is null) return new(true, false, 0, 0, "Nothing to sync yet.");
        if (!info.HasBooth) return new(true, false, 0, 0, "Booth members are for exhibitors only.");

        string? token;
        try { token = await _zoho.GetAccessTokenAsync(ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Booth sync: token request threw."); token = null; }
        if (string.IsNullOrWhiteSpace(token))
            return new(true, true, 0, 0, "Could not authenticate to Zoho Backstage.");

        // Resolve + cache the exhibitor id.
        if (string.IsNullOrWhiteSpace(info.ZohoExhibitorId))
        {
            try
            {
                var exhibitors = await _zoho.GetExhibitorsAsync(token!, ct);
                var match = exhibitors.FirstOrDefault(e => NameEq(e.CompanyName, companyName));
                if (match is not null) { info.ZohoExhibitorId = match.Id; await _db.SaveChangesAsync(ct); }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Booth sync: exhibitor lookup failed for {Co}.", companyId); }
        }
        if (string.IsNullOrWhiteSpace(info.ZohoExhibitorId))
            return new(true, true, 0, 0, "Couldn't find a matching exhibitor in Zoho by company name.");

        try
        {
            var zoho = await _zoho.GetBoothMembersAsync(token!, info.ZohoExhibitorId!, ct);
            var zohoEmails = zoho.Select(z => z.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var ceh = await _db.SponsorBoothMembers
                .Where(m => m.EventId == eventId && m.SponsorCompanyId == companyId)
                .ToListAsync(ct);
            var cehEmails = ceh.Select(c => c.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Zoho-only → pull into CEH.
            var pulled = 0;
            foreach (var z in zoho)
            {
                if (cehEmails.Contains(z.Email)) continue;
                _db.SponsorBoothMembers.Add(new SponsorBoothMember
                {
                    EventId = eventId,
                    SponsorCompanyId = companyId,
                    FirstName = z.FirstName,
                    LastName = z.LastName,
                    Email = z.Email,
                    Role = z.Role.Contains("admin", StringComparison.OrdinalIgnoreCase)
                        ? BoothMemberRole.Admin : BoothMemberRole.Staff,
                    SyncedToZoho = true,
                });
                cehEmails.Add(z.Email);
                pulled++;
            }

            // CEH-only → create in Zoho.
            var toCreate = ceh.Where(c => !zohoEmails.Contains(c.Email)).ToList();
            var added = 0;
            if (toCreate.Count > 0)
            {
                var ok = await _zoho.CreateBoothMembersAsync(
                    token!, info.ZohoExhibitorId!,
                    toCreate.Select(c => (
                        c.FirstName, c.LastName, c.Email,
                        c.Role == BoothMemberRole.Admin ? "ADMIN" : "staff",
                        (string?)companyName)).ToList(),
                    ct);
                if (!ok)
                {
                    if (pulled > 0) await _db.SaveChangesAsync(ct);
                    return new(true, true, 0, pulled, "Couldn't create booth members in Zoho — please try Sync again.");
                }
                foreach (var c in toCreate) c.SyncedToZoho = true;
                added = toCreate.Count;
            }

            // Flag matched members as synced.
            foreach (var c in ceh) if (zohoEmails.Contains(c.Email)) c.SyncedToZoho = true;

            await _db.SaveChangesAsync(ct);
            return new(true, true, added, pulled, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Booth sync failed for company {Co}.", companyId);
            return new(true, true, 0, 0, "Booth member sync hit an error — please try again.");
        }
    }

    public sealed record BulkResult(
        int Companies, int CoordinatorsFilled, int SponsorsSynced, int ExhibitorsSynced,
        int Failed, List<string> Notes);

    /// <summary>
    /// One-time migration + full re-sync (operator 2026-06-24): for every sponsor
    /// company in the event, fill the Event Coordinator from the webshop default
    /// coordinator IF empty (CEH owns it once set — never overwrites a filled one),
    /// then push every record to Zoho Backstage (fields + UTF-8-correct contact),
    /// which fixes the mojibake the legacy PowerShell sync produced. Re-runnable.
    /// </summary>
    public async Task<BulkResult> MigrateCoordinatorsAndResyncAsync(
        int eventId, CancellationToken ct = default)
    {
        var infos = await _db.SponsorInfos
            .Where(s => s.EventId == eventId)
            .ToListAsync(ct);

        // Fetch ONE Zoho token for the whole run (avoids per-company token rate-limit).
        string? token = null;
        if (_options.Enabled)
        {
            try { token = await _zoho.GetAccessTokenAsync(ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Bulk re-sync: token request threw."); }
        }

        int filled = 0, sponsorsSynced = 0, exhibitorsSynced = 0, failed = 0;
        var notes = new List<string>();

        foreach (var info in infos)
        {
            string name = info.SponsorCompanyId;
            CompanyManagerCompany? company = null;
            if (_cmOptions.Enabled && int.TryParse(info.SponsorCompanyId, out var cid))
            {
                try { company = await _cm.GetCompanyAsync(cid, ct); } catch { /* fail-soft */ }
                if (company is not null)
                    name = !string.IsNullOrWhiteSpace(company.PublicName) ? company.PublicName : company.Name;

                // Fill coordinator only when empty (CEH owns it once set).
                if (CoordinatorEmpty(info))
                {
                    try
                    {
                        var coord = await _cm.GetDefaultCoordinatorAsync(cid, ct);
                        if (coord is not null)
                        {
                            info.EventCoordinatorFirstName = NullIf(coord.FirstName);
                            info.EventCoordinatorLastName = NullIf(coord.LastName);
                            info.EventCoordinatorEmail = NullIf(coord.Email);
                            info.EventCoordinatorPhone = NullIf(coord.Phone);
                            info.EventCoordinatorCompanyName = NullIf(coord.CompanyName);
                            await _db.SaveChangesAsync(ct);
                            filled++;
                        }
                    }
                    catch (Exception ex) { _log.LogWarning(ex, "Coordinator migrate failed for {Co}.", info.SponsorCompanyId); }
                }
            }

            var r = await SyncAsync(eventId, info.SponsorCompanyId, name, ct, accessToken: token);
            if (r.SponsorSynced) sponsorsSynced++;
            if (r.ExhibitorSynced) exhibitorsSynced++;
            if (r.Error is not null) { failed++; notes.Add($"{name}: {r.Error}"); }
        }

        return new BulkResult(infos.Count, filled, sponsorsSynced, exhibitorsSynced, failed, notes);
    }

    private static bool CoordinatorEmpty(Domain.SponsorInfo i) =>
        string.IsNullOrWhiteSpace(i.EventCoordinatorFirstName)
        && string.IsNullOrWhiteSpace(i.EventCoordinatorLastName)
        && string.IsNullOrWhiteSpace(i.EventCoordinatorEmail)
        && string.IsNullOrWhiteSpace(i.EventCoordinatorPhone);

    private static string? NullIf(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool NameEq(string a, string b) =>
        string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
}
