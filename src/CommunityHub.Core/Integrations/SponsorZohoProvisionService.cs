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
    private readonly SponsorZohoSyncService _sync;
    private readonly ILogger<SponsorZohoProvisionService> _log;

    public SponsorZohoProvisionService(
        ZohoClient zoho, CommunityHubDbContext db, ZohoOptions options,
        CompanyManagerClient cm, CompanyManagerOptions cmOptions,
        IBackstageExhibitorApi exhibitorApi,
        EventEditionConfigLoader cfg, EventConfigOptions cfgOptions,
        SponsorZohoSyncService sync,
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
        _sync = sync;
        _log = log;
    }

    public sealed record ProvisionResult(
        bool Enabled, int SponsorsCreated, int SponsorsLinked,
        int ExhibitorsCreated, int ExhibitorsRequested, int ExhibitorsLinked, int Skipped, List<string> Notes);

    public async Task<ProvisionResult> ProvisionAsync(int eventId, CancellationToken ct = default)
    {
        var notes = new List<string>();
        if (!_options.Enabled) return new(false, 0, 0, 0, 0, 0, 0, notes);

        var infos = await _db.SponsorInfos.Where(s => s.EventId == eventId).ToListAsync(ct);
        if (infos.Count == 0) return new(true, 0, 0, 0, 0, 0, 0, notes);

        string? token;
        try { token = await _zoho.GetAccessTokenAsync(ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Provision: token request threw."); token = null; }
        if (string.IsNullOrWhiteSpace(token))
            return new(true, 0, 0, 0, 0, 0, 0, new List<string> { "Could not authenticate to Zoho Backstage." });

        // Live indexes (fetched once): existing sponsors/exhibitors to LINK, and the
        // sponsorship-type name→id map to set on a created sponsor.
        var existingSponsors = await _zoho.GetSponsorsAsync(token!, ct);
        var existingExhibitors = await _zoho.GetExhibitorsAsync(token!, ct);
        var liveTypes = await _zoho.GetSponsorshipTypesAsync(token!, ct);
        // Booth label → booth id (fetched ONCE): the exhibitor's booth field is booth_id, so
        // the per-company assign-to-existing loop below must resolve its label to an id. Small
        // finite set; fetch once and pass into AssignExhibitorBoothAsync to avoid refetching.
        var boothMap = await _zoho.GetBoothsAsync(token!, ct);
        // Pinned category name→id (Zoho's /sponsorship_types 400s on this account) +
        // pinned booth tier→id (REQUIREMENTS §41a: Zoho requires exhibitor_category_id).
        var cfg = _cfg.Load(_cfgOptions.EventConfigPath);
        var pinnedTypes = cfg.ZohoSponsorCategoryIds ?? new Dictionary<string, string>();
        var boothCatIds = cfg.ZohoBoothCategoryIds ?? new Dictionary<string, string>();

        int created = 0, linked = 0, exCreated = 0, exRequested = 0, exLinked = 0, skipped = 0;

        foreach (var info in infos)
        {
            // Was this company ALREADY linked to Zoho when this run started? If so, the
            // create/link block below won't touch it — but its blank-in-Zoho social/web
            // fields still need the §41b fill-blank reconcile (today that only ran at
            // create-time / on a sponsor save / via the SponsorAdmin "Migrate+Resync"
            // button, so existing linked exhibitors never got their LinkedIn/Twitter
            // pushed). We reconcile those below via SponsorZohoSyncService after the
            // create/link block. Captured here BEFORE any id is assigned this run.
            bool wasAlreadyLinked =
                !string.IsNullOrWhiteSpace(info.ZohoSponsorId)
                || !string.IsNullOrWhiteSpace(info.ZohoExhibitorId);

            // Resolve the public company name (the key Zoho matches on).
            string name = info.SponsorCompanyId;
            if (_cmOptions.Enabled && int.TryParse(info.SponsorCompanyId, out var cid))
            {
                CompanyManagerCompany? company = null;
                try { company = await _cm.GetCompanyAsync(cid, ct); }
                catch { /* fall back to id */ }
                if (company is not null)
                {
                    name = !string.IsNullOrWhiteSpace(company.PublicName) ? company.PublicName : company.Name;

                    // FILL-BLANK social/web reconcile (REQUIREMENTS §41b), before the Zoho create
                    // uses info.WebsiteUrl etc. NEVER overwrites a non-blank value.
                    // CEH ← webshop:
                    if (string.IsNullOrWhiteSpace(info.WebsiteUrl) && !string.IsNullOrWhiteSpace(company.WebsiteUrl))
                        info.WebsiteUrl = company.WebsiteUrl.Trim();
                    if (string.IsNullOrWhiteSpace(info.LinkedInUrl) && !string.IsNullOrWhiteSpace(company.LinkedInUrl))
                        info.LinkedInUrl = company.LinkedInUrl.Trim();
                    if (string.IsNullOrWhiteSpace(info.TwitterUrl) && !string.IsNullOrWhiteSpace(company.TwitterUrl))
                        info.TwitterUrl = company.TwitterUrl.Trim();

                    // webshop ← CEH: push a CEH value to a blank webshop field (only filled keys).
                    var push = new Dictionary<string, object?>();
                    if (!string.IsNullOrWhiteSpace(info.WebsiteUrl) && string.IsNullOrWhiteSpace(company.WebsiteUrl))
                        push["web_address"] = info.WebsiteUrl;
                    if (!string.IsNullOrWhiteSpace(info.LinkedInUrl) && string.IsNullOrWhiteSpace(company.LinkedInUrl))
                        push["linkedin_url"] = info.LinkedInUrl;
                    if (!string.IsNullOrWhiteSpace(info.TwitterUrl) && string.IsNullOrWhiteSpace(company.TwitterUrl))
                        push["twitter_url"] = info.TwitterUrl;
                    if (push.Count > 0)
                    {
                        try { await _cm.UpdateCompanyAsync(cid, push, ct); }
                        catch (Exception ex) { _log.LogWarning(ex, "Provision: webshop social push failed for {Co}.", info.SponsorCompanyId); }
                    }
                }

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

            // Self-heal STALE ids: if a cached Zoho id no longer exists in Zoho (the record
            // was deleted there, e.g. an operator wiped sponsors to reset), clear it so this
            // run RE-creates and re-stores the new id. The webshop order is the source of
            // truth for "who is a sponsor/exhibitor"; Zoho is reconstructed to match. No
            // manual scripts — the engine reconciles itself on its next run.
            if (!string.IsNullOrWhiteSpace(info.ZohoSponsorId)
                && !existingSponsors.Any(s => string.Equals(s.Id, info.ZohoSponsorId, StringComparison.Ordinal)))
            {
                info.ZohoSponsorId = null;
                notes.Add($"{name}: cached Zoho sponsor id was stale (not in Zoho) — re-creating.");
            }
            if (!string.IsNullOrWhiteSpace(info.ZohoExhibitorId)
                && !existingExhibitors.Any(e => string.Equals(e.Id, info.ZohoExhibitorId, StringComparison.Ordinal)))
            {
                info.ZohoExhibitorId = null;
                notes.Add($"{name}: cached Zoho exhibitor id was stale (not in Zoho) — re-creating.");
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
                            // The contact email is set ONCE at create — stamp it so the sync
                            // never re-sends an unchanged email (Zoho 3× email-update cap, §41a).
                            if (!string.IsNullOrWhiteSpace(info.EventCoordinatorEmail))
                                info.ZohoContactEmail = NullIf(info.EventCoordinatorEmail);
                            created++;
                            notes.Add($"{name}: sponsor created in Zoho.");
                        }
                    }
                }
            }

            // --- EXHIBITOR (booth companies only): link existing, else CREATE directly ---
            // Operator 2026-06-25: a sponsor that has a booth MUST also exist as an exhibitor
            // (the old script created sponsor → also exhibitor). We now create the real
            // exhibitor record directly; the approval-request seam is only a fallback when the
            // direct create isn't available (e.g. the exhibitor.CREATE scope/endpoint is off).
            if (info.HasBooth && string.IsNullOrWhiteSpace(info.ZohoExhibitorId))
            {
                var exMatch = existingExhibitors.FirstOrDefault(e => NameEq(e.CompanyName, name));
                if (exMatch is not null)
                {
                    info.ZohoExhibitorId = exMatch.Id;
                    exLinked++;
                }
                else
                {
                    var boothCategoryId = ResolveBoothCategoryId(info.Tier, boothCatIds);
                    ZohoClient.ZohoCreateResult exResult;
                    try
                    {
                        exResult = await _zoho.CreateExhibitorAsync(
                            token!, name, info.WebsiteUrl, info.CompanyDescription, boothCategoryId,
                            info.EventCoordinatorFirstName, info.EventCoordinatorLastName, info.EventCoordinatorEmail,
                            boothLabel: info.BoothLabel, ct: ct);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Provision: direct exhibitor create threw for {Co}.", info.SponsorCompanyId);
                        exResult = new ZohoClient.ZohoCreateResult(null, ex.Message);
                    }

                    if (exResult.Ok)
                    {
                        info.ZohoExhibitorId = exResult.Id;
                        // The contact email is set ONCE at create — stamp it so the sync never
                        // re-sends an unchanged email (Zoho 3× email-update cap, §41a).
                        if (!string.IsNullOrWhiteSpace(info.EventCoordinatorEmail))
                            info.ZohoContactEmail = NullIf(info.EventCoordinatorEmail);
                        exCreated++;
                        notes.Add($"{name}: exhibitor created in Zoho.");
                    }
                    else
                    {
                        // Surface the ACTUAL Zoho error in the note → drift alert email
                        // (operator: "all error emails must include error message").
                        _log.LogWarning(
                            "Provision: exhibitor NOT created for {Co} (tier {Tier}): {Error}",
                            info.SponsorCompanyId, info.Tier, exResult.Error);
                        notes.Add($"{name}: exhibitor create FAILED — {exResult.Error}");
                        skipped++;
                    }
                }
            }

            // FIX UP the booth slot on an EXISTING/linked exhibitor: exhibitors created
            // before booth_label was sent show "No booth selected" in Zoho. Assign the
            // parsed booth (e.g. "E-26") with a minimal PUT (no other field touched, never
            // the email). Idempotent (booth has no Zoho update-limit).
            if (info.HasBooth && !string.IsNullOrWhiteSpace(info.ZohoExhibitorId)
                && !string.IsNullOrWhiteSpace(info.BoothLabel))
            {
                try
                {
                    if (await _zoho.AssignExhibitorBoothAsync(token!, info.ZohoExhibitorId!, info.BoothLabel!, ct, boothMap))
                        notes.Add($"{name}: booth {info.BoothLabel} assigned in Zoho.");
                }
                catch (Exception ex) { _log.LogWarning(ex, "Provision: assign booth failed for {Co}.", info.SponsorCompanyId); }
            }

            // §41b FILL-BLANK for ALREADY-LINKED companies: a company that was linked to
            // Zoho before this run (its create/link block was a no-op) still needs its
            // blank-in-Zoho social/web fields pushed. Delegate to the SAME Zoho←CEH
            // blank-only reconcile the sponsor save / Migrate+Resync uses — SyncAsync
            // pushes website/description/linkedin/twitter ONLY where Zoho is blank and
            // sends the contact email ONLY when it changed vs ZohoContactEmail (§41a),
            // so this can't overwrite or double-send. Reuses the single run token (no
            // token rate-limit) and is fail-soft per company. Skip companies the engine
            // created/linked THIS run: a just-created record already carries those fields,
            // so a reconcile would just be redundant Zoho GETs.
            if (wasAlreadyLinked
                && (!string.IsNullOrWhiteSpace(info.ZohoSponsorId)
                    || !string.IsNullOrWhiteSpace(info.ZohoExhibitorId)))
            {
                try
                {
                    var sr = await _sync.SyncAsync(eventId, info.SponsorCompanyId, name, ct, accessToken: token);
                    if (sr.SponsorSynced || sr.ExhibitorSynced)
                        notes.Add($"{name}: blank-only social/web reconciled to Zoho.");
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Provision: linked-company social reconcile failed for {Co}.", info.SponsorCompanyId);
                }
            }

            await _db.SaveChangesAsync(ct);
        }

        return new ProvisionResult(true, created, linked, exCreated, exRequested, exLinked, skipped, notes);
    }

    /// <summary>
    /// Resolve a company's Zoho sponsorship-type id. Prefers the PINNED config map
    /// (name → id, since Zoho's live endpoint 400s on this account); falls back to
    /// the live types by name. Candidate names: "{Package} sponsors" (e.g. "Diamond
    /// sponsors"), then the bare package/tier keyword.
    /// </summary>
    /// <summary>
    /// Known ELDK27 Zoho booth/exhibitor category ids by tier (REQUIREMENTS §41a) — a
    /// FALLBACK so exhibitor creation works even if the `zohoBoothCategoryIds` config map
    /// isn't loaded. The config map (if present) takes precedence. Zoho requires
    /// `exhibitor_category_id` on create; these were read live from `…/exhibitor_categories`.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> DefaultBoothCategoryIds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["platinum"] = "14880000003487212",
            ["diamond"]  = "14880000003487213",
            ["gold"]     = "14880000003487214",
            ["feature"]  = "14880000003487215",
        };

    /// <summary>Resolve the Zoho booth category id for a tier: pinned config first, then the
    /// known-id fallback. Null for <see cref="BoothTier.None"/> or an unmapped tier.</summary>
    private static string? ResolveBoothCategoryId(BoothTier tier, IReadOnlyDictionary<string, string> pinned)
    {
        if (tier == BoothTier.None) return null;
        var key = tier.ToString().ToLowerInvariant();
        if (pinned.TryGetValue(key, out var id) && !string.IsNullOrWhiteSpace(id)) return id;
        return DefaultBoothCategoryIds.TryGetValue(key, out var fb) ? fb : null;
    }

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
