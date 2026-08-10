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

    // RULE (operator 2026-07-23): every CEH-made Zoho write must notify info@expertslive.dk
    // (the operator must publish/delete manually in Backstage). Optional so tests/legacy
    // constructions keep compiling; null ⇒ no notification.
    private readonly Email.ZohoChangeNotifier? _zohoChanges;

    public SponsorZohoProvisionService(
        ZohoClient zoho, CommunityHubDbContext db, ZohoOptions options,
        CompanyManagerClient cm, CompanyManagerOptions cmOptions,
        IBackstageExhibitorApi exhibitorApi,
        EventEditionConfigLoader cfg, EventConfigOptions cfgOptions,
        SponsorZohoSyncService sync,
        ILogger<SponsorZohoProvisionService> log,
        Email.ZohoChangeNotifier? zohoChanges = null)
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
        _zohoChanges = zohoChanges;
    }

    public sealed record ProvisionResult(
        bool Enabled, int SponsorsCreated, int SponsorsLinked,
        int ExhibitorsCreated, int ExhibitorsRequested, int ExhibitorsLinked, int Skipped, List<string> Notes);

    public async Task<ProvisionResult> ProvisionAsync(int eventId, CancellationToken ct = default)
    {
        var notes = new List<string>();
        if (!_options.Enabled) return new(false, 0, 0, 0, 0, 0, 0, notes);

        // 🔴 §1035 — TEST AND WITHDRAWN COMPANIES ARE NEVER CREATED OR UPDATED IN ZOHO.
        // Operator 2026-08-10: *"company 100 was a test company … so i had to offboard them as
        // sponsor so i didnt get them synced to zoho"*. Neither flag was read on this path, so the
        // offboarding did not achieve what it was for. Skipped companies are logged, not dropped
        // silently — an organizer who expects a record in Backstage must be able to find out why
        // there isn't one.
        var allInfos = await _db.SponsorInfos.Where(s => s.EventId == eventId).ToListAsync(ct);
        var infos = allInfos.Where(SponsorZohoScope.MayPushToZoho).ToList();
        foreach (var outOfScope in allInfos.Where(i => !SponsorZohoScope.MayPushToZoho(i)))
        {
            _log.LogInformation(
                "Provision: {Co} skipped — {Reason} (§1035); nothing created or updated in Zoho.",
                outOfScope.SponsorCompanyId, SponsorZohoScope.SkipReason(outOfScope));
        }
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
        // Operator 2026-07-23: collect every SUCCESSFUL Zoho write for ONE batched ops mail
        // per provision run (linking a cached id is CEH-side only — not a Zoho write).
        var zohoWrites = new List<string>();
        // §792 — hand-entry lines for the whole run, batched into ONE mail at the end.
        var manualLines = new List<string>();
        // §792.5 — WHICH companies contributed those lines, so only they are stamped as reported.
        var reportedCompanyIds = new List<string>();

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
            // §326aw AMBIGUITY GUARD (the §301b pattern, missing here). GetSponsorsAsync /
            // GetExhibitorsAsync return an EMPTY list on any auth/HTTP failure — their own
            // doc comments say "Returns empty on auth/HTTP failure". Without the count check
            // a failed read makes EVERY cached id look stale, and the create arms below then
            // POST a fresh sponsor + exhibitor record into Zoho for every company, each
            // carrying the coordinator's e-mail. CEH has no delete path for those (§56), so
            // cleanup is manual in the Backstage GUI — and this runs on the 15-minute
            // WooCommerce timer. An empty live list is never evidence that a record was
            // deleted: skip the heal and let the next run decide.
            if (existingSponsors.Count > 0
                && !string.IsNullOrWhiteSpace(info.ZohoSponsorId)
                && !existingSponsors.Any(s => string.Equals(s.Id, info.ZohoSponsorId, StringComparison.Ordinal)))
            {
                info.ZohoSponsorId = null;
                notes.Add($"{name}: cached Zoho sponsor id was stale (not in Zoho) — re-creating.");
            }
            if (existingExhibitors.Count > 0
                && !string.IsNullOrWhiteSpace(info.ZohoExhibitorId)
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
                            zohoWrites.Add($"Created sponsor '{name}'"
                                + (string.IsNullOrEmpty(newId) ? string.Empty : $" (Zoho id {newId})"));
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
                            boothLabel: info.BoothLabel,
                            // 🔴 §1033 — the social pages travel WITH the create. Never sent here
                            // before, which is why §791.3's PUT measurements say nothing about it.
                            linkedInUrl: info.LinkedInUrl, twitterUrl: info.TwitterUrl,
                            ct: ct);
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
                        zohoWrites.Add($"Created exhibitor '{name}' (Zoho id {exResult.Id})");

                        // 🔴 §1033 — READ THE SOCIAL PAGES BACK. The create now sends them (a path
                        // §791.3 never measured — all four of its calls were PUTs), but a 200 is
                        // exactly what the PUT returns while storing nothing, so the only honest
                        // report is what the record says afterwards. This line is the measurement:
                        // the first real create settles whether the create endpoint keeps it.
                        if (!string.IsNullOrWhiteSpace(info.LinkedInUrl)
                            || !string.IsNullOrWhiteSpace(info.TwitterUrl))
                        {
                            try
                            {
                                var back = await _zoho.GetExhibitorByIdAsync(token!, exResult.Id!, ct);
                                var kept = new List<string>();
                                if (!string.IsNullOrWhiteSpace(back?.LinkedInUrl)) kept.Add("LinkedIn");
                                if (!string.IsNullOrWhiteSpace(back?.TwitterUrl)) kept.Add("X/Twitter");

                                if (kept.Count > 0)
                                {
                                    zohoWrites.Add(
                                        $"Created exhibitor '{name}' — social pages STORED on create "
                                        + $"and read back: {string.Join(", ", kept)} (§1033).");
                                    _log.LogInformation(
                                        "§1033: exhibitor create KEPT company_social_pages for {Co} "
                                        + "({Kept}) — the create endpoint writes what the PUT discards.",
                                        info.SponsorCompanyId, string.Join("+", kept));
                                }
                                else
                                {
                                    // Not a failure to alert on: it is the §791.3 behaviour extending
                                    // to create, and the §792 hand-entry mail already carries the
                                    // value to him. Logged so the answer is on the record either way.
                                    _log.LogInformation(
                                        "§1033: exhibitor create did NOT keep company_social_pages for "
                                        + "{Co} — the create endpoint discards it too, like the PUT "
                                        + "(§791.3). The hand-entry mail remains the route.",
                                        info.SponsorCompanyId);
                                }
                            }
                            catch (Exception ex)
                            {
                                // A read-back that could not be made says nothing either way — and
                                // §553 forbids collapsing "could not look" into "not there".
                                _log.LogWarning(ex,
                                    "§1033: could not read {Co}'s exhibitor back after create — no "
                                    + "conclusion drawn about company_social_pages.",
                                    info.SponsorCompanyId);
                            }
                        }
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
            // the email). §302 (operator 2026-07-24, the 70-mail night): SKIP when Zoho's
            // live booth_id ALREADY equals the target — the unconditional re-PUT fired a
            // "change" mail line on EVERY 10-minute pass. An exhibitor absent from the
            // start-of-run live list was created THIS run (booth_label rode the create).
            if (info.HasBooth && !string.IsNullOrWhiteSpace(info.ZohoExhibitorId)
                && !string.IsNullOrWhiteSpace(info.BoothLabel))
            {
                try
                {
                    var targetBoothId = boothMap.TryGetValue(info.BoothLabel!.Trim(), out var tb) ? tb : null;
                    var liveEx = existingExhibitors.FirstOrDefault(
                        e => string.Equals(e.Id, info.ZohoExhibitorId, StringComparison.Ordinal));
                    var alreadySet = targetBoothId is not null
                        && (liveEx is null   // created this run, booth carried on the create
                            || string.Equals(liveEx.BoothId, targetBoothId, StringComparison.Ordinal));
                    if (!alreadySet
                        && await _zoho.AssignExhibitorBoothAsync(token!, info.ZohoExhibitorId!, info.BoothLabel!, ct, boothMap))
                    {
                        notes.Add($"{name}: booth {info.BoothLabel} assigned in Zoho.");
                        zohoWrites.Add($"Assigned booth {info.BoothLabel} to exhibitor '{name}' (Zoho GUI field: Booth)");
                    }
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
                    // notifyZohoChange: false — this run sends ONE batched change mail below.
                    // §302: SyncAsync now skips the PUT when Zoho already matches CEH, and
                    // reports the Zoho GUI fields it wrote — so a quiet pass adds NO line
                    // (the old unconditional "blank-only reconcile" line fired every 10 min).
                    var sr = await _sync.SyncAsync(eventId, info.SponsorCompanyId, name, ct,
                        accessToken: token, notifyZohoChange: false);
                    var fields = (sr.SponsorFields ?? Array.Empty<string>())
                        .Concat(sr.ExhibitorFields ?? Array.Empty<string>()).Distinct().ToList();
                    if (fields.Count > 0)
                    {
                        notes.Add($"{name}: reconciled to Zoho ({string.Join(", ", fields)}).");
                        // §791.2 — "PUSHED TO", not "Updated": the PUT returning success is not
                        // evidence Zoho kept the field (§791.3). Same wording as the bulk path.
                        zohoWrites.Add($"Pushed to sponsor/exhibitor '{name}' — Zoho GUI fields: {string.Join(", ", fields)}");
                    }

                    // 🔴 §792 — the hand-entry lines ride the SAME batched mail. Plan B writes
                    // nothing, so `fields` above is now always empty for sponsors/exhibitors; if
                    // these were not collected here the scheduled catch-up would run completely
                    // SILENT and the operator would be waiting for a mail that never comes.
                    if (sr.ManualLines is { Count: > 0 })
                    {
                        manualLines.AddRange(sr.ManualLines);
                        reportedCompanyIds.Add(info.SponsorCompanyId);
                        notes.Add($"{name}: {sr.ManualLines.Count} field(s) need entering by hand in Backstage.");
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Provision: linked-company social reconcile failed for {Co}.", info.SponsorCompanyId);
                }
            }

            await _db.SaveChangesAsync(ct);
        }

        // Operator 2026-07-23: ONE batched ops mail per provision run listing every
        // successful Zoho write (publish/delete is manual in Backstage). Never throws.
        if (_zohoChanges is not null)
            await _zohoChanges.NotifyAsync("Sponsors / exhibitors", zohoWrites, ct);

        // 🔴 §792 — and ONE batched HAND-ENTRY mail for the whole run, separate from the writes
        // above because it says the opposite thing: nothing was written, please type these in.
        // ⚠️ One mail per RUN, not per company — 13 sponsors after a stamp flush would otherwise be
        // 13 separate mails, which is not the "complete list" he asked for.
        if (_zohoChanges is not null && manualLines.Count > 0)
        {
            await _zohoChanges.NotifyAsync(
                "Sponsors / exhibitors", manualLines, ct,
                actionable: true, actionUrl: null, actionText: null,
                intro: "Zoho Backstage <strong>ignores API updates</strong> for these fields "
                       + "(measured 2026-08-04, §791.3), so CEH no longer tries. Copy each value "
                       + "below into the matching field in Backstage so Zoho matches CEH.",
                manualOnly: true);

            // 🔒 §792.5 — stamp AFTER the mail, and only the companies that were in it. Both bulk
            // paths must do this: whichever one runs first would otherwise re-send the same list on
            // every pass, and the other would then never see anything left to report.
            await _sync.StampManualReportAsync(eventId, reportedCompanyIds, ct);
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
