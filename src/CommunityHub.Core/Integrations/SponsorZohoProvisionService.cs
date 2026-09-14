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

    /// <param name="Skipped">
    /// Records the engine WANTED to create and could not — a real failure, or a refusal that needs a
    /// human (an ambiguous name, a heading with no pinned id). These raise the drift alert.
    /// </param>
    /// <param name="Declined">
    /// §1164 — records the engine deliberately did NOT create because they are not earned. ⚠️ Kept
    /// SEPARATE from <paramref name="Skipped"/> on purpose: a correct decision counted as a failure
    /// mails him every fifteen minutes about the system working, which is how an operator learns to
    /// ignore the alert that will one day matter.
    /// </param>
    public sealed record ProvisionResult(
        bool Enabled, int SponsorsCreated, int SponsorsLinked,
        int ExhibitorsCreated, int ExhibitorsRequested, int ExhibitorsLinked, int Skipped, List<string> Notes,
        int Declined = 0, List<string>? DeclinedNotes = null);

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

        // §1157 — companies that signed up for the attendee app game. Operator 2026-08-31:
        // "competition sponsor are the app game sponsors which exist somewhere. that is sponsors
        // that signs up for that". It is a hub sign-up, NOT a webshop category, so it cannot come
        // from the product mapper and is unioned in per company below.
        var appGameCompanyIds = (await _db.AppGameParticipations
                .Where(a => a.EventId == eventId)
                .Select(a => a.SponsorCompanyId)
                .ToListAsync(ct))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 🔴 §1159 — WHICH ZOHO RECORD IS ALREADY SOMEONE ELSE'S.
        //
        // Operator 2026-08-31 collapsed four separate webshop companies to ONE public name, which is
        // correct for the sponsor wall. Matching a company to its Zoho record BY NAME then has four
        // companies matching the same record. CEH's own stored ids are keyed by company id and are
        // the real identity; these maps make that authority available to the name lookups below, so
        // a record another company already holds can never be handed out twice.
        //
        // ⚠️ Built from allInfos, not infos: a record held by a company that is out of scope this
        // run (test/withdrawn, §1035) is still taken.
        var sponsorIdOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        var exhibitorIdOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var i in allInfos)
        {
            foreach (var zid in SponsorZohoLinks.AllSponsorIds(i)) sponsorIdOwner[zid] = i.SponsorCompanyId;
            if (!string.IsNullOrWhiteSpace(i.ZohoExhibitorId)) exhibitorIdOwner[i.ZohoExhibitorId!] = i.SponsorCompanyId;
        }

        // Ids taken by a company OTHER than this one. Recomputed per company because "claimed by
        // someone else" is relative to who is asking.
        IReadOnlySet<string> ClaimedByOthers(Dictionary<string, string> owners, string me) =>
            owners.Where(kv => !string.Equals(kv.Value, me, StringComparison.Ordinal))
                  .Select(kv => kv.Key)
                  .ToHashSet(StringComparer.Ordinal);

        int created = 0, linked = 0, exCreated = 0, exRequested = 0, exLinked = 0, skipped = 0;
        // §1164 — deliberate non-creations, kept apart from failures so they never raise an alert.
        var declined = 0;
        var declinedNotes = new List<string>();
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
                    //
                    // 🔴 §1125 — the WEBSITE is the exception: it OVERWRITES, because the webshop
                    // owns it (§1081). ⚠️ This path matters as much as the sync one — the Zoho
                    // create below reads info.WebsiteUrl, so a stale value here is published to the
                    // public event site, not merely shown in CEH.
                    // §1126 — all THREE, via the shared rule, so this path cannot drift from the
                    // sync path again (that drift IS §1125).
                    WebshopOwnedFields.ApplyAll(
                        info, company.WebsiteUrl, company.LinkedInUrl, company.TwitterUrl);

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

            // §1157 — the Zoho headings this company's purchases entitle it to, resolved BEFORE
            // the first create so the first record lands under the right one too. The order pull
            // maps them from the product categories; the app game is a hub sign-up and is unioned
            // in here (operator: "competition sponsor are the app game sponsors").
            var wantedCategories = SponsorZohoLinks.ReadCategories(info).ToList();
            if (appGameCompanyIds.Contains(info.SponsorCompanyId)
                && !wantedCategories.Contains(CompetitionCategory, StringComparer.OrdinalIgnoreCase))
            {
                wantedCategories.Add(CompetitionCategory);
            }

            // --- SPONSOR: link existing, else create ---
            if (string.IsNullOrWhiteSpace(info.ZohoSponsorId))
            {
                // §1159 — by name, but never one that is already another company's, and never a
                // guess when several share the name.
                var match = ZohoRecordMatch.ByName(
                    existingSponsors.Select(s => (s.Id, s.CompanyName)),
                    name,
                    ClaimedByOthers(sponsorIdOwner, info.SponsorCompanyId));

                if (match.RefusalReason is not null)
                {
                    // 🔒 Nothing linked AND nothing created. Creating here would add a fifth record
                    // beside four that already share the name, which is worse than the ambiguity.
                    skipped++;
                    notes.Add($"{name}: {match.RefusalReason}");
                }
                else if (match.Id is not null)
                {
                    info.ZohoSponsorId = match.Id;
                    sponsorIdOwner[match.Id] = info.SponsorCompanyId;
                    linked++;
                }
                else
                {
                    // §1157 — the PURCHASE decides the heading. ResolveSponsorshipTypeId derives
                    // it from SponsorPackage, which derives from the booth tier, so it answered
                    // "Silver sponsors" for every company that bought no booth regardless of what
                    // they paid for. It stays as the fallback for a purchase that maps to no
                    // heading (a Feature exhibitor has none of its own).
                    var firstCategory = wantedCategories.FirstOrDefault();
                    var typeId = firstCategory is null ? null : LookupPinnedType(pinnedTypes, firstCategory);
                    if (string.IsNullOrWhiteSpace(typeId))
                    {
                        firstCategory = null;
                        typeId = ResolveSponsorshipTypeId(info, pinnedTypes, liveTypes);
                    }
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
                            // §1159 — claim it immediately, so a later company sharing this public
                            // name cannot link the record we just created for THIS one.
                            if (!string.IsNullOrEmpty(newId)) sponsorIdOwner[newId] = info.SponsorCompanyId;
                            // §1157 — record WHICH heading it was created under, so the per-category
                            // pass below does not create a second record for the same one.
                            if (!string.IsNullOrEmpty(newId) && firstCategory is not null)
                                SponsorZohoLinks.Write(info, new[] { new SponsorZohoLink(firstCategory, typeId!, newId) });
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

            // --- §1157: ONE ZOHO SPONSOR RECORD PER PURCHASED CATEGORY ---
            //
            // Operator 2026-08-31: "a sponsor that buys 3 products that fits into 3 categories must
            // be created 3 times and linked to each category". The block above answers "does this
            // company exist in Zoho at all"; this one brings it up to the FULL set of headings its
            // purchases entitle it to. Keeping the two separate means a company with no mapped
            // category (a Feature exhibitor has no heading of its own) behaves exactly as before.
            //
            // 🔑 The defect being fixed: the heading used to be derived from SponsorPackage, which
            // is derived from the BOOTH TIER — so every company that bought no booth was filed
            // under "Silver sponsors" whatever they actually paid for.
            if (wantedCategories.Count > 0 && existingSponsors.Count > 0)
            {
                // Adopt first, create second: a record the company ALREADY holds under a wanted
                // heading must be linked, never duplicated. This is also what gives a pre-§1157
                // record (stored with no category) its heading, so it stops looking missing.
                var adopted = AdoptExistingCategoryRecords(
                    info, name, existingSponsors, pinnedTypes,
                    ClaimedByOthers(sponsorIdOwner, info.SponsorCompanyId), sponsorIdOwner);
                if (adopted > 0) linked += adopted;

                // ⚠️ §1140b GUARD — unreadable ≠ different. If Zoho's sponsor list does not carry
                // the sponsorship category at all, we cannot tell which heading an existing record
                // sits under, so we cannot tell whether one is missing. Creating on that basis
                // would add a duplicate record for every company on EVERY run, and CEH has no
                // delete path into Zoho (§56). Report it and create nothing.
                var categoriesReadable = existingSponsors.Any(s => !string.IsNullOrWhiteSpace(s.SponsorshipTypeId));

                foreach (var category in wantedCategories)
                {
                    if (SponsorZohoLinks.HasCategory(info, category)) continue;

                    var catTypeId = LookupPinnedType(pinnedTypes, category);
                    if (string.IsNullOrWhiteSpace(catTypeId))
                    {
                        skipped++;
                        notes.Add($"{name}: Zoho category '{category}' has no pinned id in event config — record not created.");
                        continue;
                    }

                    if (!categoriesReadable)
                    {
                        skipped++;
                        notes.Add(
                            $"{name}: needs a Zoho record under '{category}', but Zoho's sponsor list does not "
                            + "expose which category the existing records sit under — not created (creating blind "
                            + "would duplicate a record on every run). Add it by hand in Backstage.");
                        continue;
                    }

                    var extraId = await _zoho.CreateSponsorAsync(
                        token!, name, info.WebsiteUrl, info.CompanyDescription, catTypeId!,
                        info.EventCoordinatorFirstName, info.EventCoordinatorLastName, info.EventCoordinatorEmail, ct);
                    if (extraId is null)
                    {
                        skipped++;
                        notes.Add($"{name}: Zoho sponsor create failed for category '{category}'.");
                        continue;
                    }

                    created++;
                    notes.Add($"{name}: sponsor created in Zoho under '{category}'.");
                    zohoWrites.Add($"Created sponsor '{name}' under '{category}'"
                        + (string.IsNullOrEmpty(extraId) ? string.Empty : $" (Zoho id {extraId})"));

                    // ⚠️ An EMPTY id means "created, id unknown" — Zoho did not return one. The
                    // link is NOT recorded in that case: a link with no id is not a link, and the
                    // next run adopts the record by name + category instead.
                    if (!string.IsNullOrEmpty(extraId))
                    {
                        var links = SponsorZohoLinks.Read(info).ToList();
                        links.Add(new SponsorZohoLink(category, catTypeId!, extraId));
                        SponsorZohoLinks.Write(info, links);
                        sponsorIdOwner[extraId] = info.SponsorCompanyId;   // §1159
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
                // §1159 — same rule as the sponsor record above.
                var exMatch = ZohoRecordMatch.ByName(
                    existingExhibitors.Select(e => (e.Id, e.CompanyName)),
                    name,
                    ClaimedByOthers(exhibitorIdOwner, info.SponsorCompanyId));

                if (exMatch.RefusalReason is not null)
                {
                    skipped++;
                    notes.Add($"{name} (exhibitor): {exMatch.RefusalReason}");
                }
                else if (exMatch.Id is not null)
                {
                    info.ZohoExhibitorId = exMatch.Id;
                    exhibitorIdOwner[exMatch.Id] = info.SponsorCompanyId;
                    exLinked++;
                }
                else if (!info.HasCurrentBoothOrder)
                {
                    // 🔴 §1163 — DO NOT RE-CREATE A RECORD THE ORDERS NO LONGER EARN.
                    //
                    // Operator 2026-08-31: *"i have cancelled the order"* · *"but the sponsor still
                    // have 1 order - but it is not exhibitor anymore"* · *"so i bet it is
                    // isexhibitor = 1 which must be 0"*. He is right about the mechanism: IsExhibitor
                    // is raise-only, so HasBooth stays true after a cancellation and this block saw a
                    // booth company with no exhibitor record.
                    //
                    // ⚠️ He had ALREADY deleted the Zoho record by hand. Without this guard the
                    // provisioner re-created it on every pull — he would have been deleting the same
                    // record every fifteen minutes with nothing explaining why it came back. The
                    // create was only failing because of a malformed payload; fixing that alone
                    // would have turned a visible error into a silent fight.
                    //
                    // 🔒 Reported, not silent, and §1161 raises the matching action item + mail.
                    // §1164 — DECLINED, not skipped. Nothing is wrong and there is nothing to do.
                    declined++;
                    declinedNotes.Add($"{name}: no exhibitor record and no booth product in their "
                        + "current orders — correctly not created.");
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
                        // §1159 — claim it now; companies sharing a public name are processed in
                        // the same loop.
                        if (!string.IsNullOrWhiteSpace(exResult.Id))
                            exhibitorIdOwner[exResult.Id!] = info.SponsorCompanyId;
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

        // §792 — and ONE batched HAND-ENTRY mail for the whole run, separate from the writes above
        // because it says a different thing: these particular values could not be written, please
        // type them in.
        // ⚠️ One mail per RUN, not per company — 13 sponsors after a stamp flush would otherwise be
        // 13 separate mails, which is not the "complete list" he asked for.
        //
        // 🔴 §1087 — THIS INTRO IS THE SECOND COPY, AND IT WAS STILL LYING AFTER THE FIRST WAS FIXED.
        // The identical sentence lives in `SponsorZohoSyncService`; correcting only that one left
        // THIS path — the provision run — still telling him "Zoho Backstage ignores API updates for
        // these fields", which stopped being true on 2026-08-16. A duplicated string is a duplicated
        // claim, and a claim can rot on one copy while the other is repaired.
        // ⇒ Both now name the two HONEST reasons a line can appear here: Backstage has no API for
        // booth videos/collateral (§792.7), and CEH deliberately never PUTs contact details because
        // Zoho caps those at three updates (§791.5).
        if (_zohoChanges is not null && manualLines.Count > 0)
        {
            await _zohoChanges.NotifyAsync(
                "Sponsors / exhibitors", manualLines, ct,
                actionable: true, actionUrl: null, actionText: null,
                intro: "These are the values Backstage has <strong>no API for</strong> (booth videos "
                       + "and collateral) or that CEH deliberately never writes (contact details — "
                       + "Zoho caps those at three updates). Copy each one into the matching field "
                       + "in Backstage so Zoho matches CEH.",
                manualOnly: true);

            // 🔒 §792.5 — stamp AFTER the mail, and only the companies that were in it. Both bulk
            // paths must do this: whichever one runs first would otherwise re-send the same list on
            // every pass, and the other would then never see anything left to report.
            await _sync.StampManualReportAsync(eventId, reportedCompanyIds, ct);
        }

        return new ProvisionResult(
            true, created, linked, exCreated, exRequested, exLinked, skipped, notes,
            declined, declinedNotes);
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

    /// <summary>
    /// §1157 — the Zoho heading for the attendee app game. Operator 2026-08-31: <i>"competition
    /// sponsor are the app game sponsors which exist somewhere. that is sponsors that signs up for
    /// that"</i> — a hub sign-up, so it has no webshop category to map from.
    /// </summary>
    private const string CompetitionCategory = "Competition sponsor";

    /// <summary>Case-insensitive lookup into the pinned category name → id map.</summary>
    /// <remarks>
    /// The map is hand-maintained in event config, so its casing is whatever was typed into it; an
    /// ordinal miss here would look exactly like "this category does not exist" and quietly stop a
    /// record being created.
    /// </remarks>
    private static string? LookupPinnedType(IReadOnlyDictionary<string, string> pinned, string category)
    {
        foreach (var kv in pinned)
            if (string.Equals(kv.Key.Trim(), category.Trim(), StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(kv.Value))
                return kv.Value;
        return null;
    }

    /// <summary>
    /// §1157 — link the Zoho sponsor records this company ALREADY has, before anything is created.
    /// Returns how many links were newly recorded.
    /// </summary>
    /// <remarks>
    /// <para>Does two jobs, both of which have to happen before the create loop runs:</para>
    /// <list type="number">
    /// <item>Gives a PRE-§1157 link its heading. Such a link was stored with an empty category, so
    /// every wanted category looks missing and the create loop would duplicate the record it
    /// already has.</item>
    /// <item>Adopts a record an organizer made by hand in Backstage. CEH did not create it, so
    /// nothing in CEH points at it — and without adopting it, the next run creates a second one.</item>
    /// </list>
    ///
    /// <para>⚠️ Only records whose category Zoho actually reports are adopted. A record whose
    /// category is unreadable is left alone rather than guessed into a heading — filing a sponsor
    /// publicly under the wrong heading is the very complaint this section exists to answer.</para>
    /// </remarks>
    private static int AdoptExistingCategoryRecords(
        SponsorInfo info,
        string name,
        IReadOnlyList<ZohoClient.BackstageSponsor> existingSponsors,
        IReadOnlyDictionary<string, string> pinned,
        IReadOnlySet<string> claimedByOthers,
        Dictionary<string, string> sponsorIdOwner)
    {
        // Zoho type id → the pinned category NAME, so a record's heading can be named.
        var nameByTypeId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in pinned)
            if (!string.IsNullOrWhiteSpace(kv.Value)) nameByTypeId[kv.Value] = kv.Key;

        var links = SponsorZohoLinks.Read(info).ToList();
        var byId = existingSponsors
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var adopted = 0;
        var changed = false;

        // 1) name the headings of the links we already hold.
        for (var i = 0; i < links.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(links[i].CategoryName)) continue;
            if (!byId.TryGetValue(links[i].ZohoSponsorId, out var live)) continue;
            if (string.IsNullOrWhiteSpace(live.SponsorshipTypeId)) continue;
            if (!nameByTypeId.TryGetValue(live.SponsorshipTypeId!, out var catName)) continue;

            links[i] = links[i] with { CategoryName = catName, CategoryId = live.SponsorshipTypeId! };
            changed = true;
        }

        // 2) adopt same-name records in Zoho that we hold no link for.
        var known = links.Select(l => l.ZohoSponsorId).ToHashSet(StringComparer.Ordinal);
        foreach (var live in existingSponsors)
        {
            if (known.Contains(live.Id)) continue;
            // 🔒 §1159 — a record another CEH company already holds is NOT this company's, however
            // exactly the names agree. Four companies now share one public name by design.
            if (claimedByOthers.Contains(live.Id)) continue;
            if (!NameEq(live.CompanyName, name)) continue;
            if (string.IsNullOrWhiteSpace(live.SponsorshipTypeId)) continue;
            if (!nameByTypeId.TryGetValue(live.SponsorshipTypeId!, out var catName)) continue;
            // Never two links under one heading — that is a duplicate in Zoho, and adopting both
            // would hide it rather than leave it visible for a human to clean up.
            if (links.Any(l => string.Equals(l.CategoryName, catName, StringComparison.OrdinalIgnoreCase))) continue;

            links.Add(new SponsorZohoLink(catName, live.SponsorshipTypeId!, live.Id));
            known.Add(live.Id);
            sponsorIdOwner[live.Id] = info.SponsorCompanyId;
            adopted++;
            changed = true;
        }

        if (changed) SponsorZohoLinks.Write(info, links);
        return adopted;
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
