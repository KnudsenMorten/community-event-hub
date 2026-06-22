using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>Outcome of one sponsor-order pull run.</summary>
public sealed record SponsorOrderPullResult(
    int OrdersFetched,
    int TasksCreated,
    int ContactSyncCompanies,
    int ContactsCreated,
    int ContactsUpdated,
    bool RanToCompletion,
    string? SkipReason);

/// <summary>
/// The sponsor-order pull engine. Fetches completed WooCommerce orders,
/// classifies each line item by category (or product-name fallback), expands
/// it into the JSON-defined task list (sponsor.&lt;edition&gt;.json), and
/// upserts ParticipantTask rows. Idempotent via SourceKey
/// "woo:{orderId}:{productId}:{title-slug}" - re-running never duplicates.
///
/// This is the single source of truth for sponsor pulls. Hosted by:
///   * <c>CommunityHub.Jobs.WooCommercePullJob</c> (daily 03:00 UTC timer)
///   * <c>CommunityHub.OneShot</c> CLI (on-demand local DEV run, used for
///     verifying classification + creation without a Function App deploy).
/// </summary>
public sealed class SponsorOrderPullService
{
    private readonly CommunityHubDbContext _db;
    private readonly WooCommerceClient _woo;
    private readonly SponsorConfigLoader _configLoader;
    private readonly WooCommerceOptions _options;
    private readonly SponsorConfigOptions _configOptions;
    private readonly EventEditionConfigLoader _eventConfigLoader;
    private readonly EventConfigOptions _eventConfigOptions;
    private readonly SponsorContactSyncService _contactSync;
    private readonly SharePointUploadClient _sharePoint;
    private readonly ILogger<SponsorOrderPullService> _log;

    public SponsorOrderPullService(
        CommunityHubDbContext db,
        WooCommerceClient woo,
        SponsorConfigLoader configLoader,
        WooCommerceOptions options,
        SponsorConfigOptions configOptions,
        EventEditionConfigLoader eventConfigLoader,
        EventConfigOptions eventConfigOptions,
        SponsorContactSyncService contactSync,
        SharePointUploadClient sharePoint,
        ILogger<SponsorOrderPullService> log)
    {
        _db = db;
        _woo = woo;
        _configLoader = configLoader;
        _options = options;
        _configOptions = configOptions;
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
        _contactSync = contactSync;
        _sharePoint = sharePoint;
        _log = log;
    }

    public async Task<SponsorOrderPullResult> RunAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("SponsorOrderPullService: WooCommerce disabled by config.");
            return new SponsorOrderPullResult(0, 0, 0, 0, 0, false, "WooCommerce disabled");
        }

        var activeEvent = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => new { e.Id, e.StartDate })
            .FirstOrDefaultAsync(ct);
        if (activeEvent is null)
        {
            _log.LogWarning("SponsorOrderPullService: no active event in DB.");
            return new SponsorOrderPullResult(0, 0, 0, 0, 0, false, "no active event");
        }

        SponsorTaskExpander expander;
        SponsorProductClassifier classifier;
        BoothWallSpecs? wallSpecs;
        try
        {
            var config = _configLoader.Load(_configOptions.SponsorConfigPath);
            expander = new SponsorTaskExpander(config);
            classifier = new SponsorProductClassifier(config);
            wallSpecs = config.BoothWallSpecs;
        }
        catch (FileNotFoundException ex)
        {
            _log.LogError(ex, "SponsorOrderPullService: sponsor config missing at {Path}.",
                _configOptions.SponsorConfigPath);
            return new SponsorOrderPullResult(0, 0, 0, 0, 0, false, "sponsor config missing");
        }

        // Edition facts + cross-cutting placeholders that get substituted
        // into task descriptions ({{expectedAttendees}}, {{editionCode}},
        // {{uploadPortalUrl}}, {{supportEmail}}, ...). Empty config -> blank
        // placeholders, not crash.
        var editionFacts = _eventConfigLoader.Load(_eventConfigOptions.EventConfigPath);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var orders = await _woo.GetOrdersAsync("completed", ct);
        var created = 0;

        // Group orders by sponsor company. Orders without a Company Manager
        // company id are "legacy and skipped" per integrations config -- they
        // can't be attributed to a hub-visible sponsor anyway. Within each
        // company, dedup tasks by title (NOT by order) so a sponsor placing
        // multiple orders does not accumulate duplicate "Upload logo" /
        // "Choose your booth layout" rows. SourceKey switches from
        // "woo:{orderId}:{productId}:{slug}" to "sponsor:{companyId}:{slug}"
        // for the same reason: keyed on the SPONSOR, not the order.
        var ordersByCompany = orders
            .Where(o => !string.IsNullOrWhiteSpace(o.CompanyId))
            .GroupBy(o => o.CompanyId!)
            .ToList();

        foreach (var group in ordersByCompany)
        {
            var companyId = group.Key;

            // Resolve the company display name. Priority:
            //   1. Company Manager "Public Company Name" (company_name_public)
            //      -- this is the SHORT public name the team uses in
            //      announcements + sponsor listings (e.g. "2LINKIT").
            //   2. Company Manager Legal Name (name)
            //      -- e.g. "2linkIT ApS"; used when the public name is blank.
            //   3. WooCommerce billing.company
            //      -- often empty (it was for 2LINKIT order 10726).
            //   4. "Company {id}" fallback so substitution renders something.
            // Best-effort: if Company Manager is disabled / the call fails,
            // skip straight to billing / fallback.
            string? cmPublicName = null;
            string? cmLegalName = null;
            try
            {
                if (int.TryParse(companyId, out var cidInt))
                {
                    var cm = await _contactSync.LookupCompanyAsync(cidInt, ct);
                    cmPublicName = cm?.PublicName;
                    cmLegalName  = cm?.Name;
                }
            }
            catch
            {
                // swallow -- substitution falls back below
            }

            // For contractPlus deadlines, "first order date" = the EARLIEST
            // completed-order date across all of this company's orders --
            // not whichever happens to appear first in the API response
            // (WooCommerce default ordering is date desc, so without this
            // we'd accidentally use the LATEST date).
            var firstOrderDate = group
                .Where(o => o.CreatedAt is not null)
                .Select(o => DateOnly.FromDateTime(o.CreatedAt!.Value.UtcDateTime))
                .DefaultIfEmpty(today)
                .Min();

            // Walk every line item across every order, dedup task titles
            // (case-insensitive). First occurrence wins -- the expander
            // emits allSponsors first, then booth/session/etc. so deeper
            // sets cannot accidentally clobber a baseline task.
            // Also capture the company's booth number (e.g. "E-29") from
            // the first booth product so {{boothNumber}} substitutes into
            // the shipping task instead of the placeholder text.
            var tasksByTitleKey = new Dictionary<string, SponsorTask>(
                StringComparer.OrdinalIgnoreCase);
            string? companyBoothNumber = null;
            // The company's highest booth tier across every line item it ordered,
            // stamped onto SponsorInfo.Tier below so the public sponsors page groups
            // this company under the right tier without a manual organizer set.
            var companyTier = BoothTier.None;

            foreach (var order in group)
            {
                foreach (var item in order.LineItems)
                {
                    var cls = classifier.Classify(item.CategoriesText, item.ProductName);
                    if (companyBoothNumber is null && !string.IsNullOrWhiteSpace(cls.BoothNumber))
                    {
                        companyBoothNumber = cls.BoothNumber;
                    }
                    if (BoothTierRanking.Weight(cls.Tier) > BoothTierRanking.Weight(companyTier))
                    {
                        companyTier = cls.Tier;
                    }
                    foreach (var st in expander.Expand(
                        cls, activeEvent.StartDate, firstOrderDate, today))
                    {
                        tasksByTitleKey.TryAdd(st.Title, st);
                    }
                }
            }

            var billingName = group
                .Select(o => o.BillingCompany)
                .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
            var companyName =
                (!string.IsNullOrWhiteSpace(cmPublicName) ? cmPublicName :
                 !string.IsNullOrWhiteSpace(cmLegalName)  ? cmLegalName  :
                 !string.IsNullOrWhiteSpace(billingName)  ? billingName  :
                 $"Company {companyId}");

            // Provision per-task SharePoint upload folders BEFORE substitution
            // so the resulting anonymous edit-link URL can be threaded into the
            // task description via {{<placeholder>}}. Each provisioned folder
            // is also persisted as a SponsorUploadLocation so the upload
            // watcher knows where to poll + who to notify. Best-effort: a
            // SharePoint outage or mis-config leaves the URL blank and the
            // substitution falls back to {{uploadPortalUrl}}.
            var uploadUrls = await ProvisionUploadFoldersAsync(
                activeEvent.Id, companyId, companyName,
                tasksByTitleKey.Values, editionFacts, ct);

            foreach (var st in tasksByTitleKey.Values)
            {
                var sourceKey = $"sponsor:{companyId}:{Slug(st.Title)}";

                var existing = await _db.Tasks.FirstOrDefaultAsync(
                    t => t.EventId == activeEvent.Id
                         && t.SourceKey == sourceKey, ct);

                // Title can ALSO carry placeholders ({{expectedAttendees}}
                // in the attendee-bag tasks) -- substitute on the way in.
                // Description is the per-task help text from
                // sponsor.<edition>.json with all placeholders
                // resolved (edition facts + per-tier wall spec /
                // coupon + per-company name + cross-cutting URLs /
                // emails / addresses from event.<edition>.json
                // placeholders + per-task SharePoint upload-folder
                // URLs). Per design doc "Sponsors to-do": tasks are
                // scoped to the sponsor company, not the order /
                // contact -- so NEVER embed order id, product name,
                // or contact email here.
                var renderedTitle = SubstitutePlaceholders(st.Title, st.Tier, companyName, editionFacts, wallSpecs, uploadUrls, companyBoothNumber);
                var renderedDesc  = SubstitutePlaceholders(st.Description, st.Tier, companyName, editionFacts, wallSpecs, uploadUrls, companyBoothNumber);

                if (existing is null)
                {
                    _db.Tasks.Add(new ParticipantTask
                    {
                        EventId = activeEvent.Id,
                        Title = renderedTitle,
                        Description = renderedDesc,
                        DueDate = st.DueDate,
                        State = TaskState.Open,
                        SourceKey = sourceKey,
                        SponsorCompanyId = companyId,
                        IsMandatory = st.Mandatory,
                    });
                    created++;
                }
                else
                {
                    // Re-render the title + description on every pull so a
                    // config edit (new coupon code, new spec URL, new wording)
                    // reaches existing sponsors without the org having to
                    // delete+rerun. Trade-off: a one-off in-DB editorial
                    // tweak on a sponsor-pull task gets overwritten on the
                    // next pull -- canonical source is sponsor.<edition>.json.
                    existing.Title = renderedTitle;
                    existing.Description = renderedDesc;
                }
            }

            // Stamp the company's highest booth tier onto its SponsorInfo facts row
            // so the PUBLIC sponsors page (/Sponsors) groups it under the right tier
            // without an organizer setting it by hand. Idempotent + RAISE-ONLY: the
            // pull only fills a blank tier or upgrades to a higher one, so an
            // organizer's manual correction (e.g. a comped tier bump) is never
            // silently downgraded on the next pull. A facts row is created lazily
            // here when the sponsor hasn't opened their self-service info page yet,
            // so the public listing shows the company as soon as their booth order
            // lands. None-tier orders never create a row (nothing to show yet).
            if (companyTier != BoothTier.None)
            {
                var info = await _db.SponsorInfos.FirstOrDefaultAsync(
                    s => s.EventId == activeEvent.Id
                         && s.SponsorCompanyId == companyId, ct);
                // Derive the commercial package from the company's highest booth
                // tier (Gold+ ⇒ booth/exhibitor; None ⇒ digital Silver). RAISE-ONLY
                // like the tier itself so a manual organizer bump survives a re-pull.
                var companyPackage = SponsorPackageMapper.FromBoothTier(companyTier);
                if (info is null)
                {
                    _db.SponsorInfos.Add(new SponsorInfo
                    {
                        EventId = activeEvent.Id,
                        SponsorCompanyId = companyId,
                        Tier = companyTier,
                        SponsorPackage = companyPackage,
                    });
                }
                else
                {
                    var changed = false;
                    if (BoothTierRanking.Weight(companyTier) > BoothTierRanking.Weight(info.Tier))
                    {
                        info.Tier = companyTier;
                        changed = true;
                    }
                    if (companyPackage > info.SponsorPackage)
                    {
                        info.SponsorPackage = companyPackage;
                        changed = true;
                    }
                    if (changed) info.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
        }

        // Save both new ParticipantTask rows AND any SponsorUploadLocation
        // upserts (re-runs may add/refresh locations even when every task
        // already exists from a prior pull -- guarding only on `created`
        // dropped the location writes on dispose).
        if (_db.ChangeTracker.HasChanges())
        {
            await _db.SaveChangesAsync(ct);
        }

        // Contact sync: every distinct company that appeared in this pull
        // gets its Company Manager users mirrored into Participants, so a
        // newly-onboarded sponsor's coordinators can PIN-log-in immediately
        // and see their tasks (without manual UPDATEs to Participant rows).
        var distinctCompanyIds = orders
            .Select(o => o.CompanyId)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct()
            .ToList();

        var companiesSynced = 0;
        var contactsCreated = 0;
        var contactsUpdated = 0;

        foreach (var companyIdStr in distinctCompanyIds)
        {
            if (!int.TryParse(companyIdStr, out var companyIdInt))
            {
                _log.LogWarning("SponsorOrderPullService: company id '{Id}' is not numeric, skipping contact sync.",
                    companyIdStr);
                continue;
            }
            try
            {
                var result = await _contactSync.SyncCompanyAsync(activeEvent.Id, companyIdInt, ct);
                companiesSynced++;
                contactsCreated += result.ParticipantsCreated;
                contactsUpdated += result.ParticipantsUpdated;
            }
            catch (Exception ex)
            {
                // Don't let one company's Company Manager hiccup fail the whole
                // pull -- log and move on. The next run reconverges.
                _log.LogError(ex,
                    "SponsorOrderPullService: contact sync failed for company {Co}; continuing.",
                    companyIdInt);
            }
        }

        _log.LogInformation(
            "SponsorOrderPullService: {Orders} orders, {Created} new tasks, {Companies} companies synced ({CC} contacts created, {CU} updated).",
            orders.Count, created, companiesSynced, contactsCreated, contactsUpdated);

        return new SponsorOrderPullResult(
            OrdersFetched: orders.Count,
            TasksCreated: created,
            ContactSyncCompanies: companiesSynced,
            ContactsCreated: contactsCreated,
            ContactsUpdated: contactsUpdated,
            RanToCompletion: true,
            SkipReason: null);
    }

    /// <summary>
    /// Pre-create the per-task SharePoint upload folders for one sponsor
    /// company, mint anonymous edit-link URLs, and persist a
    /// <see cref="SponsorUploadLocation"/> row per folder so the watcher
    /// knows where to poll and who to notify. Returns a dictionary keyed by
    /// the task's upload placeholder name (e.g. "logoFolderUrl") -&gt; the
    /// minted URL, used to substitute the URL into the task description.
    ///
    /// Best-effort: any SharePoint failure (mis-config, 403, network) leaves
    /// the URL absent so <see cref="SubstitutePlaceholders"/> can fall back
    /// to <c>{{uploadPortalUrl}}</c>. Existing location rows are UPSERTed so
    /// re-running the pull refreshes the link / recipients without duplicating.
    /// </summary>
    private async Task<Dictionary<string, string>> ProvisionUploadFoldersAsync(
        int eventId,
        string companyId,
        string companyName,
        IEnumerable<SponsorTask> tasks,
        EventEditionConfig editionFacts,
        CancellationToken ct)
    {
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);

        var sp = editionFacts.SharePoint;
        if (sp is null || string.IsNullOrWhiteSpace(sp.SiteUrl) || !_sharePoint.IsConfigured)
        {
            return urls;
        }

        // De-dup uploads by subfolder so the same folder is not provisioned
        // twice if two tasks reference it (rare but defensive).
        var uploadDefs = tasks
            .Where(t => t.Upload is not null && !string.IsNullOrWhiteSpace(t.Upload.Subfolder))
            .Select(t => t.Upload!)
            .GroupBy(u => u.Subfolder, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        foreach (var def in uploadDefs)
        {
            var relPath = $"{companyName}/{def.Subfolder}";
            try
            {
                var provisioned = await _sharePoint.EnsureFolderWithEditLinkAsync(
                    sp.SiteUrl, sp.DriveName, sp.RootFolderPath, relPath, ct);

                if (!string.IsNullOrWhiteSpace(def.Placeholder)
                    && !string.IsNullOrWhiteSpace(provisioned.WebUrl))
                {
                    urls[def.Placeholder] = provisioned.WebUrl;
                }

                // UPSERT the watcher's row -- keyed (EventId, CompanyId, FolderKey).
                // FolderKey uses Subfolder as the stable identity; if the config
                // renames a subfolder, the old row is orphaned (no auto-cleanup
                // today; orphans are harmless because the watcher swallows
                // 404s).
                var existing = await _db.SponsorUploadLocations
                    .FirstOrDefaultAsync(
                        l => l.EventId == eventId
                             && l.SponsorCompanyId == companyId
                             && l.FolderKey == def.Subfolder, ct);

                var notifyCsv = string.Join(",",
                    (def.NotifyEmails ?? new List<string>())
                    .Where(s => !string.IsNullOrWhiteSpace(s)));

                if (existing is null)
                {
                    _db.SponsorUploadLocations.Add(new SponsorUploadLocation
                    {
                        EventId = eventId,
                        SponsorCompanyId = companyId,
                        CompanyName = companyName,
                        FolderKey = def.Subfolder,
                        Subfolder = def.Subfolder,
                        FolderPath = provisioned.FolderPath,
                        EditLinkUrl = provisioned.WebUrl,
                        NotifyEmailsCsv = notifyCsv,
                        NotifySubject = def.NotifySubject ?? string.Empty,
                    });
                }
                else
                {
                    existing.CompanyName = companyName;
                    existing.FolderPath  = provisioned.FolderPath;
                    existing.EditLinkUrl = provisioned.WebUrl;
                    existing.NotifyEmailsCsv = notifyCsv;
                    existing.NotifySubject   = def.NotifySubject ?? string.Empty;
                }
            }
            catch (SharePointUploadException ex)
            {
                _log.LogWarning(ex,
                    "SponsorOrderPullService: failed to provision SharePoint folder '{Path}' for {Co}; "
                    + "task description will fall back to {{uploadPortalUrl}}.",
                    relPath, companyName);
            }
        }

        return urls;
    }

    /// <summary>
    /// Resolve every <c>{{key}}</c> placeholder in a task title / description.
    /// Sources, in precedence order:
    ///   0. Per-task upload URLs  -- {{logoFolderUrl}}, {{wallFolderUrl}}
    ///                              (configured per task via .upload; falls
    ///                              back to {{uploadPortalUrl}} when blank)
    ///   1. Per-company dynamic   -- {{companyName}}
    ///   2. Per-tier dynamic      -- {{wallSpecUrl}}, {{couponCode}}, {{wallSize}}
    ///   3. Per-edition facts     -- {{expectedAttendees}}, {{editionCode}}, {{editionCodeLower}}
    ///   4. Cross-cutting strings -- event.&lt;edition&gt;.json -&gt; placeholders
    ///                              ({{uploadPortalUrl}}, {{supportEmail}}, ...)
    /// All non-matching {{markers}} are left untouched so a typo is visible
    /// rather than silently erased.
    /// </summary>
    private static string SubstitutePlaceholders(
        string template,
        BoothTier tier,
        string companyName,
        EventEditionConfig facts,
        BoothWallSpecs? wallSpecs,
        IReadOnlyDictionary<string, string>? uploadUrls = null,
        string? boothNumber = null)
    {
        if (string.IsNullOrEmpty(template)) return template;
        var s = template;

        // (0) Per-task upload-folder URLs. When the URL is blank/unset
        // (SharePoint disabled or provisioning failed), fall back to the
        // generic {{uploadPortalUrl}} so the description still works.
        if (uploadUrls is { Count: > 0 })
        {
            var fallback = facts.Placeholders.TryGetValue("uploadPortalUrl", out var up)
                ? up ?? string.Empty
                : string.Empty;
            foreach (var (key, url) in uploadUrls)
            {
                s = s.Replace("{{" + key + "}}",
                    string.IsNullOrEmpty(url) ? fallback : url);
            }
        }

        // (1) Per-company
        s = s.Replace("{{companyName}}", companyName ?? string.Empty);
        s = s.Replace("{{boothNumber}}", boothNumber ?? string.Empty);

        // (2) Per-tier: pick the matching boothWallSpecs.tiers row.
        // BoothTier.ToString() yields PascalCase ("Gold") but the JSON keys
        // are lowercase ("gold"); System.Text.Json replaces the dictionary
        // instance and ignores the initializer's OrdinalIgnoreCase comparer,
        // so the lookup must be done with a lowercased key.
        // furnitureSpec is substituted FIRST so nested {{couponCode}} inside
        // it gets resolved by the {{couponCode}} pass on the same line below.
        if (tier != BoothTier.None && wallSpecs?.Tiers is { Count: > 0 } tiers)
        {
            var tierKey = tier.ToString().ToLowerInvariant();
            if (tiers.TryGetValue(tierKey, out var tspec))
            {
                s = s.Replace("{{furnitureSpec}}", tspec.FurnitureSpec ?? string.Empty);
                s = s.Replace("{{wallSpecUrl}}", tspec.SpecUrl ?? string.Empty);
                s = s.Replace("{{couponCode}}", tspec.Coupon ?? string.Empty);
                s = s.Replace("{{wallSize}}", tspec.WallSize ?? string.Empty);
            }
        }

        // (3) Per-edition facts (always resolvable -- defaults to "" / 0).
        s = s.Replace("{{expectedAttendees}}", facts.ExpectedAttendees.ToString());
        s = s.Replace("{{editionCode}}", facts.Code ?? string.Empty);
        s = s.Replace("{{editionCodeLower}}",
            (facts.Code ?? string.Empty).ToLowerInvariant());

        // (4) Cross-cutting placeholders map.
        if (facts.Placeholders is { Count: > 0 } map)
        {
            foreach (var (key, value) in map)
            {
                s = s.Replace("{{" + key + "}}", value ?? string.Empty);
            }
        }

        return s;
    }

    /// <summary>A short, stable slug of a task title for the SourceKey.</summary>
    private static string Slug(string title)
    {
        var chars = title.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ')
            .ToArray();
        var slug = new string(chars).Replace(' ', '-');
        return slug.Length > 60 ? slug[..60] : slug;
    }
}
