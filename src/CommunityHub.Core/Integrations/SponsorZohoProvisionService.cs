using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// INITIAL provisioning of Zoho Backstage sponsor / exhibitor records from webshop
/// data — the C# replacement for the legacy Sync-Webshop-Sponsors-to-Zoho-Backstage
/// PowerShell script (Stage 4b). Runs after the order pull:
///   1. webshop orders → CEH (SponsorOrderPullService, separate).
///   2. for each CEH sponsor company WITHOUT a Zoho id: LINK it to an existing Zoho
///      record by company name, else CREATE it (sponsor always; exhibitor request
///      when the company has a booth), seeding from CEH data + the webshop default
///      coordinator. CEH owns the data once a company exists — this never overwrites
///      an already-linked company (only fills a missing Zoho id).
///   3. ongoing edits + field sync are sponsor-driven (SponsorZohoSyncService).
/// Idempotent + re-runnable; one Zoho token per run.
/// </summary>
public sealed class SponsorZohoProvisionService
{
    private readonly ZohoClient _zoho;
    private readonly CommunityHubDbContext _db;
    private readonly ZohoOptions _options;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _cmOptions;
    private readonly IBackstageExhibitorApi _exhibitorApi;
    private readonly EventEditionConfigLoader _cfg;
    private readonly EventConfigOptions _cfgOptions;
    private readonly ILogger<SponsorZohoProvisionService> _log;

    public SponsorZohoProvisionService(
        ZohoClient zoho, CommunityHubDbContext db, ZohoOptions options,
        CompanyManagerClient cm, CompanyManagerOptions cmOptions,
        IBackstageExhibitorApi exhibitorApi,
        EventEditionConfigLoader cfg, EventConfigOptions cfgOptions,
        ILogger<SponsorZohoProvisionService> log)
    {
        _zoho = zoho;
        _db = db;
        _options = options;
        _cm = cm;
        _cmOptions = cmOptions;
        _exhibitorApi = exhibitorApi;
        _cfg = cfg;
        _cfgOptions = cfgOptions;
        _log = log;
    }

    public sealed record ProvisionResult(
        bool Enabled, int SponsorsCreated, int SponsorsLinked,
        int ExhibitorsRequested, int ExhibitorsLinked, int Skipped, List<string> Notes);

    public async Task<ProvisionResult> ProvisionAsync(int eventId, CancellationToken ct = default)
    {
        var notes = new List<string>();
        if (!_options.Enabled) return new(false, 0, 0, 0, 0, 0, notes);

        var infos = await _db.SponsorInfos.Where(s => s.EventId == eventId).ToListAsync(ct);
        if (infos.Count == 0) return new(true, 0, 0, 0, 0, 0, notes);

        string? token;
        try { token = await _zoho.GetAccessTokenAsync(ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Provision: token request threw."); token = null; }
        if (string.IsNullOrWhiteSpace(token))
            return new(true, 0, 0, 0, 0, 0, new List<string> { "Could not authenticate to Zoho Backstage." });

        // Live indexes (fetched once): existing sponsors/exhibitors to LINK, and the
        // sponsorship-type name→id map to set on a created sponsor.
        var existingSponsors = await _zoho.GetSponsorsAsync(token!, ct);
        var existingExhibitors = await _zoho.GetExhibitorsAsync(token!, ct);
        var liveTypes = await _zoho.GetSponsorshipTypesAsync(token!, ct);
        // Pinned category name→id (Zoho's /sponsorship_types 400s on this account).
        var pinnedTypes = _cfg.Load(_cfgOptions.EventConfigPath).ZohoSponsorCategoryIds
            ?? new Dictionary<string, string>();

        int created = 0, linked = 0, exRequested = 0, exLinked = 0, skipped = 0;

        foreach (var info in infos)
        {
            // Resolve the public company name (the key Zoho matches on).
            string name = info.SponsorCompanyId;
            if (_cmOptions.Enabled && int.TryParse(info.SponsorCompanyId, out var cid))
            {
                try
                {
                    var company = await _cm.GetCompanyAsync(cid, ct);
                    if (company is not null)
                        name = !string.IsNullOrWhiteSpace(company.PublicName) ? company.PublicName : company.Name;
                }
                catch { /* fall back to id */ }

                // Fill the coordinator from the webshop default ONLY where empty.
                if (CoordinatorEmpty(info))
                {
                    try
                    {
                        var coord = await _cm.GetDefaultCoordinatorAsync(cid, ct);
                        if (coord is not null)
                        {
                            info.EventCoordinatorFirstName ??= NullIf(coord.FirstName);
                            info.EventCoordinatorLastName ??= NullIf(coord.LastName);
                            info.EventCoordinatorEmail ??= NullIf(coord.Email);
                            info.EventCoordinatorPhone ??= NullIf(coord.Phone);
                            info.EventCoordinatorCompanyName ??= NullIf(coord.CompanyName);
                        }
                    }
                    catch (Exception ex) { _log.LogWarning(ex, "Provision: coordinator fetch failed for {Co}.", info.SponsorCompanyId); }
                }
            }

            // --- SPONSOR: link existing, else create ---
            if (string.IsNullOrWhiteSpace(info.ZohoSponsorId))
            {
                var match = existingSponsors.FirstOrDefault(s => NameEq(s.CompanyName, name));
                if (match is not null)
                {
                    info.ZohoSponsorId = match.Id;
                    linked++;
                }
                else
                {
                    var typeId = ResolveSponsorshipTypeId(info, pinnedTypes, liveTypes);
                    if (string.IsNullOrWhiteSpace(typeId))
                    {
                        skipped++;
                        notes.Add($"{name}: no Zoho sponsorship type matched package '{info.SponsorPackage}' — not created.");
                    }
                    else
                    {
                        var newId = await _zoho.CreateSponsorAsync(
                            token!, name, info.WebsiteUrl, info.CompanyDescription, typeId!,
                            info.EventCoordinatorFirstName, info.EventCoordinatorLastName, info.EventCoordinatorEmail, ct);
                        if (newId is null)
                        {
                            skipped++;
                            notes.Add($"{name}: Zoho sponsor create failed.");
                        }
                        else
                        {
                            info.ZohoSponsorId = string.IsNullOrEmpty(newId) ? null : newId;  // empty = created, id unknown (linked next run)
                            created++;
                        }
                    }
                }
            }

            // --- EXHIBITOR (booth companies only): link existing, else request ---
            if (info.HasBooth && string.IsNullOrWhiteSpace(info.ZohoExhibitorId))
            {
                var exMatch = existingExhibitors.FirstOrDefault(e => NameEq(e.CompanyName, name));
                if (exMatch is not null)
                {
                    info.ZohoExhibitorId = exMatch.Id;
                    exLinked++;
                }
                else if (_exhibitorApi.CanCreate)
                {
                    try
                    {
                        var rec = new ExhibitorRecord(info.SponsorCompanyId, name, info.EventCoordinatorEmail);
                        if (!await _exhibitorApi.ExistsAsync(rec, ct))
                        {
                            await _exhibitorApi.CreateAsync(rec, ct);
                            exRequested++;
                            notes.Add($"{name}: exhibitor request created in Zoho (links once approved).");
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Provision: exhibitor request failed for {Co}.", info.SponsorCompanyId);
                        notes.Add($"{name}: exhibitor request failed.");
                    }
                }
            }

            await _db.SaveChangesAsync(ct);
        }

        return new ProvisionResult(true, created, linked, exRequested, exLinked, skipped, notes);
    }

    /// <summary>
    /// Resolve a company's Zoho sponsorship-type id. Prefers the PINNED config map
    /// (name → id, since Zoho's live endpoint 400s on this account); falls back to
    /// the live types by name. Candidate names: "{Package} sponsors" (e.g. "Diamond
    /// sponsors"), then the bare package/tier keyword.
    /// </summary>
    private static string? ResolveSponsorshipTypeId(
        SponsorInfo info,
        IReadOnlyDictionary<string, string> pinned,
        IReadOnlyList<ZohoClient.BackstageSponsorshipType> live)
    {
        var categoryNames = new List<string> { $"{info.SponsorPackage} sponsors" };
        var keywords = new List<string> { info.SponsorPackage.ToString() };
        if (info.Tier != BoothTier.None) { categoryNames.Add($"{info.Tier} sponsors"); keywords.Add(info.Tier.ToString()); }

        // 1) pinned: exact category-name match (case-insensitive).
        foreach (var cn in categoryNames)
            foreach (var kv in pinned)
                if (string.Equals(kv.Key, cn, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
                    return kv.Value;

        // 2) pinned: keyword contained in a category name.
        foreach (var kw in keywords)
            foreach (var kv in pinned)
                if (kv.Key.Contains(kw, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
                    return kv.Value;

        // 3) live endpoint fallback.
        foreach (var kw in keywords)
        {
            var hit = live.FirstOrDefault(t => t.Name.Contains(kw, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit.Id;
        }
        return null;
    }

    private static bool CoordinatorEmpty(SponsorInfo i) =>
        string.IsNullOrWhiteSpace(i.EventCoordinatorFirstName)
        && string.IsNullOrWhiteSpace(i.EventCoordinatorLastName)
        && string.IsNullOrWhiteSpace(i.EventCoordinatorEmail)
        && string.IsNullOrWhiteSpace(i.EventCoordinatorPhone);

    private static string? NullIf(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool NameEq(string a, string b) =>
        string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
}
