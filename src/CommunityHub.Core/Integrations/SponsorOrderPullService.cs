using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Tasks.Definitions;
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
    string? SkipReason,
    /// <summary>
    /// §682 — tasks refused because their RENDERED title/description exceeded the column.
    /// Defaulted so the early-exit paths above (disabled / no event / missing config) keep
    /// their positional construction. Non-zero means config needs shortening: the run
    /// completed, but a sponsor is missing a task body it should have had.
    /// </summary>
    int TasksSkippedTooLong = 0);

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
    private readonly DocLibrary.IDocLibraryPathResolver? _paths;
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
        ILogger<SponsorOrderPullService> log,
        // 🔴 §784.14 — the DocLibrary registry, so per-sponsor upload folders are created under the
        // SAME root every other reader uses. Optional (last, defaulted) so existing constructions
        // and tests keep compiling; null ⇒ the legacy config root, which is what shipped before.
        DocLibrary.IDocLibraryPathResolver? paths = null)
    {
        _paths = paths;
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
            // §783.6 — EndDate too: a POST-event deadline anchors on the edition's LAST day.
            .Select(e => new { e.Id, e.StartDate, e.EndDate })
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
        // §682 — tasks refused by the length guard below. Counted and returned so a
        // silently-shrinking task list shows up in the run result instead of only in a log line.
        var tasksSkippedTooLong = 0;

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

        // §757 — every "unmatched product" marker this run actually re-detected. An OPEN queue item
        // whose marker is NOT in here is orphaned: either a classification rule now covers it, or
        // the order is gone. Collected during the pass so the sweep below judges on THIS run's
        // evidence rather than on the age of a timestamp.
        var seenUnmatched = new HashSet<string>(StringComparer.Ordinal);

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
            // §299 b9: did ANY line classify as a speaking-session product? Stamped onto
            // SponsorInfo.HasSponsorSession below so the §292 session wizard step appears.
            var companyHasSession = false;
            // §299 b10: lines no classification rule matched — surfaced to organizers.
            var unmatchedLines = new List<string>();

            foreach (var order in group)
            {
                foreach (var item in order.LineItems)
                {
                    var cls = classifier.Classify(item.CategoriesText, item.ProductName);
                    if (cls.Kind == SponsorProductKind.Session) companyHasSession = true;
                    if (cls.Unmatched && !string.IsNullOrWhiteSpace(item.ProductName))
                    {
                        unmatchedLines.Add(
                            $"'{item.ProductName}' (categories: {(string.IsNullOrWhiteSpace(item.CategoriesText) ? "none" : item.CategoriesText)})");
                    }
                    if (companyBoothNumber is null && !string.IsNullOrWhiteSpace(cls.BoothNumber))
                    {
                        companyBoothNumber = cls.BoothNumber;
                    }
                    // Fallback (matches the legacy Resolve-ProductBoothLabel): parse the booth
                    // slot from ANY product name, not only one classified Kind=Booth — some booth
                    // products carry "… Booth E-NN" in the name but match a non-Booth rule.
                    if (companyBoothNumber is null && !string.IsNullOrWhiteSpace(item.ProductName))
                    {
                        var bm = System.Text.RegularExpressions.Regex.Match(
                            item.ProductName, @"\bE-(\d{1,3})\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (bm.Success) companyBoothNumber = "E-" + bm.Groups[1].Value;
                    }
                    if (BoothTierRanking.Weight(cls.Tier) > BoothTierRanking.Weight(companyTier))
                    {
                        companyTier = cls.Tier;
                    }
                    foreach (var st in expander.Expand(
                        cls, activeEvent.StartDate, activeEvent.EndDate, firstOrderDate, today))
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

            // Log the resolved PUBLIC name (not the raw numeric id) so diagnostics read
            // as real companies (operator 2026-06-25: never surface "Company {id}").
            _log.LogInformation(
                "SponsorOrderPull: '{Company}' (id {Co}) -> tier {Tier}, booth {Booth}.",
                companyName, companyId, companyTier, companyBoothNumber ?? "(none)");

            // 🔒 §593 — PERSIST THE NAME **HERE**, THE MOMENT IT IS RESOLVED. DO NOT MOVE THIS
            // BELOW THE SHAREPOINT BLOCK.
            //
            // Until now the ONLY persistent home for the resolved name was
            // SponsorUploadLocation.CompanyName, written inside ProvisionUploadFoldersAsync. When
            // SharePoint did not run — unconfigured, a throw, or a company with no upload-folder
            // task definitions — the name was silently DISCARDED while the order still created its
            // tasks, so nothing looked broken. That is why /Organizer/Participants showed
            // "(name not synced — CM id 30)" for a company whose CM record reads
            // "System Center Dudes" (operator 2026-07-28).
            //
            // Writing it on SponsorInfo makes the company's identity independent of folder
            // provisioning, and the SharePoint folder below is derived from this SAME value — so
            // the folder name and the display name cannot diverge ("that is also the name that the
            // sharepoint integration must create").
            //
            // Only a REAL name is stored: the "Company {id}" fallback is deliberately NOT persisted,
            // or a transient CM outage would bake a placeholder in as if it were the truth.
            if (!string.IsNullOrWhiteSpace(companyName)
                && !companyName.Equals($"Company {companyId}", StringComparison.Ordinal))
            {
                var info = await _db.SponsorInfos.FirstOrDefaultAsync(
                    x => x.EventId == activeEvent.Id && x.SponsorCompanyId == companyId, ct);
                if (info is null)
                {
                    _db.SponsorInfos.Add(new Domain.SponsorInfo
                    {
                        EventId = activeEvent.Id,
                        SponsorCompanyId = companyId,
                        CompanyName = companyName,
                    });
                }
                else if (!string.Equals(info.CompanyName, companyName, StringComparison.Ordinal))
                {
                    info.CompanyName = companyName;
                }
                await _db.SaveChangesAsync(ct);
            }

            // Provision per-task SharePoint upload folders BEFORE substitution
            // so the resulting anonymous edit-link URL can be threaded into the
            // task description via {{<placeholder>}}. Each provisioned folder
            // is also persisted as a SponsorUploadLocation so the upload
            // watcher knows where to poll + who to notify. Best-effort: a
            // SharePoint outage or mis-config leaves the URL blank and the
            // substitution falls back to {{uploadPortalUrl}}.
            // Audience is computed once here: it decides BOTH which registry tasks this company
            // gets and which upload folders it needs provisioned. Deriving them from one value
            // means a task and its folder can never disagree about whether the company qualifies.
            var facts = new TaskAudienceFacts(
                ParticipantRole.Sponsor,
                BuildAudiencePredicates(companyTier, companyHasSession));
            var definitions = TaskDefinitionRegistry.Shipped.For(facts);

            // ⚰️ §822 — this used to PRE-CREATE a SharePoint folder per company per upload task,
            // mint an edit link and write a SponsorUploadLocation row. It now only seeds the
            // description placeholders: the flat upload portal is the mechanism.
            var uploadUrls = SeedUploadPlaceholders(
                // Both sources, while the two models coexist: the JSON task sets and the migrated
                // definitions. A migrated definition carries the SAME upload shape it had in JSON,
                // so this is a move rather than a redesign.
                tasksByTitleKey.Values
                    .Where(t => t.Upload is not null)
                    .Select(t => t.Upload!)
                    .Concat(definitions
                        .Where(d => d.Upload is not null)
                        .Select(d => new SponsorTaskUploadDefinition
                        {
                            Subfolder = d.Upload!.Subfolder,
                            Placeholder = d.Upload.Placeholder,
                            NotifyEmails = d.Upload.NotifyEmails.ToList(),
                            NotifySubject = d.Upload.NotifySubject,
                        })));

            var desiredKeys = new HashSet<string>(StringComparer.Ordinal);

            // ── §686.2 / §684.7 — THE MIGRATED DEFINITIONS, SEEDED FROM THE CODE REGISTRY ──
            //
            // Seeded in the SAME pass, into the SAME table, with the SAME key shape as the JSON
            // tasks below — so from every downstream consumer's point of view (the deadlines
            // surface, the overdue badges, TaskReminderBuilder, the dedup ledger) nothing has
            // changed at all. That is §684.21's regression gate: the redesign changes where a
            // task's DEFINITION and BODY come from, and must change nothing about who gets chased,
            // when, or how.
            //
            // 🔒 The row carries NO rendered Description (§684.14). The body renders on view,
            // through TaskBodyService, which is what makes a live value like the §666 TV count
            // honest — and what removes the per-company duplication of ~20 000 characters of prose
            // rewritten on every pull.
            foreach (var definition in definitions)
            {
                var renderedDefTitle = SubstitutePlaceholders(
                    definition.Title, companyTier, companyName, editionFacts, wallSpecs, uploadUrls,
                    companyBoothNumber);

                // 🔒 Keyed off the RAW, UNSUBSTITUTED title — exactly as the JSON path below is, and
                // that detail is load-bearing. The JSON path slugs `st.Title` BEFORE substitution,
                // so the attendee-bag task's "{{expectedAttendees}}" slugs to "expectedattendees"
                // rather than to "1250". Key off the rendered title instead and every such task
                // would get a NEW key the moment it migrated — a pruned old row, a fresh row, and a
                // reminder ledger that has never chased it. See SponsorTaskKeys. Because the title
                // is unchanged, the key is byte-identical: the row is UPDATED, completion state
                // survives, and no reminder re-fires.
                var defKey = SponsorTaskKeys.For(companyId, definition.Title);
                desiredKeys.Add(defKey);

                var defDue = definition.Due switch
                {
                    TaskDue.FromConfig fromConfig => expander.ResolveDeadlineRule(
                        fromConfig.RuleName, activeEvent.StartDate, activeEvent.EndDate,
                        firstOrderDate, today),
                    TaskDue.Fixed fixedDate => fixedDate.Date,
                    TaskDue.EventMinus eventMinus => activeEvent.StartDate.AddDays(-eventMinus.Days),
                    _ => null,
                };

                var existingDef = await _db.Tasks.FirstOrDefaultAsync(
                    t => t.EventId == activeEvent.Id && t.SourceKey == defKey, ct);

                if (existingDef is null)
                {
                    _db.Tasks.Add(new ParticipantTask
                    {
                        EventId = activeEvent.Id,
                        Title = renderedDefTitle,
                        // §684.14 — rendered prose lives NOWHERE. Null, not empty: an empty string
                        // would read as "this task has no body" to anything still inspecting the
                        // column, whereas null is unambiguously "ask the registry".
                        Description = null,
                        DueDate = defDue,
                        State = TaskState.Open,
                        SourceKey = defKey,
                        SponsorCompanyId = companyId,
                        IsMandatory = definition.IsMandatory,
                        FormStepKey = (definition.Completion as TaskCompletion.Form)?.StepKey,
                    });
                    created++;
                }
                else
                {
                    existingDef.Title = renderedDefTitle;
                    existingDef.DueDate = defDue;
                    existingDef.IsMandatory = definition.IsMandatory;
                    existingDef.FormStepKey = (definition.Completion as TaskCompletion.Form)?.StepKey;
                    // 🔒 CLEAR the stored prose on a row that was previously JSON-seeded. Leaving
                    // it would be the two-sources-of-truth §684.16 forbids: the page would render
                    // the new body while anything still reading the column served the old one.
                    existingDef.Description = null;
                }
            }

            foreach (var st in tasksByTitleKey.Values)
            {
                var sourceKey = SponsorTaskKeys.For(companyId, st.Title);
                desiredKeys.Add(sourceKey);

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

                // §682 — a body longer than the column used to kill the WHOLE run: EF threw on
                // SaveChangesAsync and the sponsor ORDER sync aborted with it, so one paragraph in
                // sponsor.<edition>.json took out an entire integration. Now an over-long task
                // fails only ITSELF and the pull completes.
                //
                // Measured AFTER substitution on purpose: a body that fits in config can still
                // overflow once a long SharePoint upload URL or company name is spliced in, so the
                // config length is not the number that matters — the rendered length is.
                //
                // 🔒 Deliberately NOT truncated. These bodies carry shipping addresses, prices and
                // box-marking strings; a body cut mid-sentence would ship half an address and look
                // authoritative doing it. Refuse loudly, keep the last good row, let a human fix
                // the config. (Same reasoning as §555: "cannot tell" must never render as fact.)
                if (!TaskFieldGuard.Fits(renderedTitle, renderedDesc, out var tooLongReason))
                {
                    tasksSkippedTooLong++;
                    _log.LogError(
                        "SponsorOrderPullService: task '{Title}' for company {CompanyId} was SKIPPED — {Reason}. "
                        + "The rest of the pull continued. Shorten it in sponsor.<edition>.json; it is never "
                        + "auto-truncated.",
                        Truncate(renderedTitle, 80), companyId, tooLongReason);

                    // NOTE: sourceKey is ALREADY in desiredKeys (added above, before this guard).
                    // That is load-bearing — dropping it here would make the orphan prune below
                    // DELETE the sponsor's existing task, turning "too long to update" into
                    // "silently disappeared".
                    continue;
                }

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
                        // §648 — the wizard step that completes this task, so it can offer a real
                        // form instead of an e-mail address.
                        FormStepKey = st.Form,
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
                    // §648 — same reasoning: a task that GAINS a form in config must gain it on the
                    // rows that already exist, or the sponsors who need it most (the ones already
                    // onboarded) keep being told to send an e-mail.
                    existing.FormStepKey = st.Form;
                }
            }

            // PRUNE orphans: delete this company's pull-managed tasks (SourceKey
            // "sponsor:{companyId}:…") that the CURRENT config no longer produces —
            // e.g. a task whose title was renamed (the slug, hence the SourceKey,
            // changes, leaving the old row orphaned). Guard: only prune when this
            // run produced a non-empty desired set, so a transient empty config can
            // never wipe a company's tasks. Scoped to this company's sponsor tasks.
            if (desiredKeys.Count > 0)
            {
                var keyPrefix = $"sponsor:{companyId}:";
                var orphans = await _db.Tasks
                    .Where(t => t.EventId == activeEvent.Id
                                && t.SponsorCompanyId == companyId
                                && t.SourceKey != null
                                && t.SourceKey.StartsWith(keyPrefix)
                                && !desiredKeys.Contains(t.SourceKey))
                    .ToListAsync(ct);
                if (orphans.Count > 0)
                {
                    _db.Tasks.RemoveRange(orphans);
                    _log.LogInformation(
                        "SponsorOrderPullService: pruned {N} orphaned task(s) for {Co} (renamed/removed in config).",
                        orphans.Count, companyName);
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
            // §299 b9: a speaking-session purchase must reach SponsorInfo.HasSponsorSession
            // even when the company has NO booth tier, so the row is upserted for either.
            if (companyTier != BoothTier.None || companyHasSession)
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
                        BoothLabel = companyBoothNumber,
                        HasSponsorSession = companyHasSession,
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
                    // Fill the booth slot from the order (e.g. "E-26") when present + changed.
                    if (!string.IsNullOrWhiteSpace(companyBoothNumber) && info.BoothLabel != companyBoothNumber)
                    {
                        info.BoothLabel = companyBoothNumber;
                        changed = true;
                    }
                    if (companyPackage > info.SponsorPackage)
                    {
                        info.SponsorPackage = companyPackage;
                        changed = true;
                    }
                    // §299 b9: RAISE-ONLY like the tier — the pull sets it, never clears it
                    // (an organizer's manual set survives; refunds go via the action queue).
                    if (companyHasSession && !info.HasSponsorSession)
                    {
                        info.HasSponsorSession = true;
                        changed = true;
                    }
                    if (changed) info.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            // §299 b10: surface order lines NO classification rule matched — one action item
            // per (company, product), deduped by summary marker like the refund alerts.
            if (unmatchedLines.Count > 0)
            {
                var knownUnmatched = await _db.OrganizerActionItems
                    .Where(a => a.EventId == activeEvent.Id
                                && a.Type == Reminders.OrganizerActionItemService.TypeWebshopCategoryUnrecognized)
                    .Select(a => a.Summary)
                    .ToListAsync(ct);
                foreach (var line in unmatchedLines.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var marker = $"{companyName}: {line}";
                    // §757 — recorded BEFORE the dedup skip: an item that already exists is still
                    // being re-detected, and must not be swept away as orphaned.
                    seenUnmatched.Add(marker);
                    if (knownUnmatched.Any(s => s.StartsWith(marker, StringComparison.Ordinal))) continue;
                    _db.OrganizerActionItems.Add(new OrganizerActionItem
                    {
                        EventId = activeEvent.Id,
                        Type = Reminders.OrganizerActionItemService.TypeWebshopCategoryUnrecognized,
                        Summary = $"{marker} — no product-classification rule matched, so NO tasks "
                                  + "were generated for it. Add/adjust a rule in the sponsor config "
                                  + "(productClassification.rules) if this product should drive tasks, "
                                  + "a tier or a session step.",
                    });
                    _log.LogWarning(
                        "SponsorOrderPull: UNRECOGNIZED category for '{Company}': {Line}",
                        companyName, line);
                }
            }

            // --- Auto-close sponsor tasks whose deliverable is already on file ----
            // When the hub already holds the data a task asks for, mark the matching
            // OPEN sponsor task Done so a sponsor who did the work via Company Details
            // doesn't keep seeing a stale "to-do". DIRECTION-GUARDED: this only ever
            // flips Open -> Done and NEVER reopens, so a task a sponsor manually
            // reopened (or an organizer closed) is left exactly as they set it
            // (respecting manual reopen). Idempotent: re-running only touches rows
            // that still need it; CompletedAt is stamped only when still null. The
            // SourceKey slugs below are the Slug() of the JSON task titles
            // ("Register booth members", "Upload sponsor wall design in vector
            // format", "Initial onboarding of sponsor"). Signals are hub data only:
            //   • booth members  -> >=1 active (non-tombstoned) SponsorBoothMember
            //   • wall upload     -> >=1 file seen by the watcher in the SPONSORWALL folder
            //   • company overview-> a saved SponsorInfo.CompanyDescription
            var boothMembersOnFile = await _db.SponsorBoothMembers.AnyAsync(
                m => m.EventId == activeEvent.Id
                     && m.SponsorCompanyId == companyId
                     && m.DeletedAt == null, ct);
            // 🔒 §784.14 — BOTH sources, because the folder one is being retired.
            //
            // The watcher-seen file in `SPONSORWALL` was the ONLY evidence here, and
            // `/Sponsors/Sponsor Upload` is now retired ("that tree is retired and shouldn't be
            // pre-created at all"). Once those folders are gone this check would silently answer NO
            // forever, and the wall task would become impossible to auto-complete — with no error
            // anywhere, which is the §335 shape: "the only symptom is that nothing happens".
            //
            // Since §783.4 the sponsor uploads through the WIZARD, which writes a `wall` audit row
            // against Sponsors/Logo/… — so the audit is the durable evidence and the folder is the
            // legacy one. `SponsorDeliverablesService` already ORs the two; this call site did not.
            var wallUploadOnFile = await _db.SponsorUploadFiles.AnyAsync(
                f => f.Location.EventId == activeEvent.Id
                     && f.Location.SponsorCompanyId == companyId
                     && f.Location.FolderKey == "SPONSORWALL", ct)
                || await _db.SponsorUploadAudits.AnyAsync(
                    a => a.EventId == activeEvent.Id
                         && a.SponsorCompanyId == companyId
                         && a.Kind == "wall", ct);
            var overviewOnFile = await _db.SponsorInfos.AnyAsync(
                s => s.EventId == activeEvent.Id
                     && s.SponsorCompanyId == companyId
                     && s.CompanyDescription != null
                     && s.CompanyDescription != "", ct);

            var keysToClose = new HashSet<string>(StringComparer.Ordinal);
            if (boothMembersOnFile) keysToClose.Add($"sponsor:{companyId}:register-booth-members");
            if (wallUploadOnFile)   keysToClose.Add($"sponsor:{companyId}:upload-sponsor-wall-design-in-vector-format");
            if (overviewOnFile)     keysToClose.Add($"sponsor:{companyId}:initial-onboarding-of-sponsor");

            if (keysToClose.Count > 0)
            {
                var toClose = await _db.Tasks
                    .Where(t => t.EventId == activeEvent.Id
                                && t.State == TaskState.Open
                                && t.SourceKey != null
                                && keysToClose.Contains(t.SourceKey))
                    .ToListAsync(ct);
                foreach (var t in toClose)
                {
                    t.State = TaskState.Done;
                    t.CompletedAt ??= DateTimeOffset.UtcNow;
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

        // 🗑 §757 — THE REFUNDED/CANCELLED ACTION ITEM IS GONE. Do not reinstate it.
        //
        // Operator 2026-08-01, on the 7 of these sitting in his action queue: *"cancel/refund must
        // not be approved. this is handled inside zoho billing and must not stop anything."*
        //
        // §253 G8d raised one queue item per refunded order "so a human decides the rollback". That
        // decision is not CEH's to ask for: a refund or cancellation is settled in ZOHO BILLING,
        // which is the system of record for money. Surfacing it here asked him to re-approve
        // something already decided elsewhere — and it sat in a queue whose entire premise is
        // "everything that needs a human decision", so it made that queue lie.
        //
        // 🔑 The test is NOT "is this a real event that never self-heals?" — a refund is exactly
        // that, and it still does not belong here. The test is "is there a decision left for CEH to
        // make?". There is not.
        //
        // ⚠️ Nothing else about refund handling changed: the pull still reads status=completed only,
        // so a refunded order simply stops being mirrored. If a rollback of entitlements is ever
        // wanted it must be a deliberate feature, not a nag item.

        // §757 — CLOSE THE ORPHANS OURSELVES. Operator 2026-08-01: *"you must fix these and close
        // them if they are orphaned - dont ask me to fix them."*
        //
        // 🔒 Guarded on the pull having actually returned orders. A Woo outage returns an empty
        // list, and sweeping on that evidence would resolve every open item at once — the same
        // "an empty read is indistinguishable from a real empty" trap as §301b and §754.3.
        if (orders.Count > 0)
        {
            try
            {
                var (closed, stillOpen) = await AutoResolveObsoleteActionItemsAsync(
                    activeEvent.Id, seenUnmatched, ct);
                // 🔒 Logged on EVERY run, including zero. Logging only when something closed made
                // "the queue is already drained" and "the sweep never ran" produce identical
                // silence — which is exactly the ambiguity I then could not resolve from the logs
                // when the operator asked whether it had drained. Silence is not evidence.
                _log.LogInformation(
                    "SponsorOrderPull: action-queue sweep closed {Closed}, {Open} still open.",
                    closed, stillOpen);
            }
            catch (Exception ex)
            {
                // Housekeeping must never break the pull it rides on.
                _log.LogWarning(ex, "SponsorOrderPull: obsolete action-item sweep failed; ignored.");
            }
        }

        _log.LogInformation(
            "SponsorOrderPullService: {Orders} orders, {Created} new tasks, {Companies} companies synced ({CC} contacts created, {CU} updated), {TooLong} tasks skipped as too long.",
            orders.Count, created, companiesSynced, contactsCreated, contactsUpdated, tasksSkippedTooLong);

        return new SponsorOrderPullResult(
            OrdersFetched: orders.Count,
            TasksCreated: created,
            ContactSyncCompanies: companiesSynced,
            ContactsCreated: contactsCreated,
            ContactsUpdated: contactsUpdated,
            RanToCompletion: true,
            SkipReason: null,
            TasksSkippedTooLong: tasksSkippedTooLong);
    }

    /// <summary>
    /// §682 — shorten a value for a LOG line only (never for anything persisted). Keeps an
    /// over-long task title from flooding the log entry that reports it.
    /// </summary>
    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    /// <summary>
    /// <summary>
    /// §757 — resolve action-queue items that are no longer true, so the queue drains itself.
    /// Returns how many were closed.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-01: <i>"you must fix these and close them if they are orphaned - dont
    /// ask me to fix them."</i> He had 22 open items, 15 of which had been answered by a config
    /// change two hours after they were raised, and 7 which should never have been raised at all.
    /// Telling him to tick them off was the wrong answer twice over — it is work he should not do,
    /// and it would have happened again the next time a category was renamed.</para>
    ///
    /// <para>🔑 <b>The rule is re-detection, not age.</b> An unmatched-product item survives only
    /// while the pull keeps re-detecting that exact product as unmatched. Add a classification rule
    /// and it stops being detected; the order goes away and it stops being detected. Either way the
    /// item closes on the next run, with a note saying why — so the queue can never again accumulate
    /// a tail of things that were fixed elsewhere.</para>
    ///
    /// <para>🔒 <b>Resolved, never deleted.</b> The row keeps its history and its note, and
    /// <c>ReopenAsync</c> still works — this is the queue draining, not the evidence disappearing.
    /// </para>
    /// </remarks>
    /// <returns>How many were closed, and how many remain open after the sweep.</returns>
    private async Task<(int Closed, int StillOpen)> AutoResolveObsoleteActionItemsAsync(
        int eventId, HashSet<string> seenUnmatched, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var closed = 0;

        var open = await _db.OrganizerActionItems
            .Where(a => a.EventId == eventId && a.ResolvedAt == null)
            .ToListAsync(ct);

        foreach (var item in open)
        {
            string? why = null;

            // The refund family is retired outright (see the note at the call site): it is settled
            // in Zoho billing and CEH has no decision to ask for.
            if (item.Type == Reminders.OrganizerActionItemService.TypeSponsorOrderRefunded)
            {
                why = "Closed automatically (§757): refunds and cancellations are handled in Zoho "
                      + "billing, so CEH no longer raises them for approval.";
            }
            // An unmatched-product item the pull did NOT re-detect this run: a classification rule
            // now covers the product, or the order is gone.
            else if (item.Type == Reminders.OrganizerActionItemService.TypeWebshopCategoryUnrecognized
                     && !seenUnmatched.Any(m => item.Summary.StartsWith(m, StringComparison.Ordinal)))
            {
                why = "Closed automatically (§757): this product is no longer unmatched — a "
                      + "classification rule now covers it, or the order is gone. The pull "
                      + "re-checked and did not raise it again.";
            }

            if (why is null) continue;

            item.ResolvedAt = now;
            item.ResolvedNotes = why;
            closed++;
        }

        if (closed > 0) await _db.SaveChangesAsync(ct);
        return (closed, open.Count - closed);
    }

    /// §253 G8d — surface WooCommerce order REFUNDS/CANCELLATIONS to the organizer
    /// action queue. One <see cref="OrganizerActionItem"/> per Woo order id, ever:
    /// the marker "Woo order {id}" in the Summary is the dedup key, so a resolved
    /// item stays resolved and later runs never nag about it again. Nothing is
    /// rolled back automatically (tiers are raise-only; §56 forbids Zoho deletes) —
    /// the item tells the organizer what to review.
    /// </summary>
    private async Task RaiseRefundedOrderAlertsAsync(int eventId, CancellationToken ct)
    {
        var refunded = await _woo.GetOrdersAsync("cancelled,refunded", ct, enrichCategories: false);
        if (refunded.Count == 0) return;

        var knownSummaries = await _db.OrganizerActionItems
            .Where(a => a.EventId == eventId
                        && a.Type == Reminders.OrganizerActionItemService.TypeSponsorOrderRefunded)
            .Select(a => a.Summary)
            .ToListAsync(ct);

        var raised = 0;
        foreach (var order in refunded)
        {
            var marker = $"Woo order {order.OrderId}";
            if (knownSummaries.Any(s => s.StartsWith(marker + " ", StringComparison.Ordinal)))
                continue;

            var company = string.IsNullOrWhiteSpace(order.BillingCompany)
                ? (order.CompanyId is null ? "unknown company" : $"company {order.CompanyId}")
                : order.BillingCompany;
            var items = order.LineItems.Count == 0
                ? "no line items"
                : string.Join(", ", order.LineItems.Select(li => li.ProductName).Take(5));

            _db.OrganizerActionItems.Add(new OrganizerActionItem
            {
                EventId = eventId,
                Type = Reminders.OrganizerActionItemService.TypeSponsorOrderRefunded,
                Summary = $"{marker} ({order.Status}) — {company}: {items}. "
                          + "Review whether package/tier, booth, company tasks or the public "
                          + "logo must be rolled back (tiers are raise-only; Zoho is never "
                          + "deleted — consider the Withdraw-company action if the sponsorship ends).",
            });
            raised++;
        }

        if (raised > 0)
        {
            await _db.SaveChangesAsync(ct);
            _log.LogWarning(
                "SponsorOrderPullService: {N} refunded/cancelled Woo order(s) surfaced to the organizer action queue.",
                raised);
        }
    }

    /// <summary>
    /// ⚰️ §822 — THE PER-COMPANY UPLOAD FOLDERS ARE RETIRED. This now only SEEDS the task
    /// description placeholders so they fall back to <c>{{uploadPortalUrl}}</c>. It creates nothing.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-04, pointing at <c>SponsorUploadRoot</c> on the DocLibrary settings
    /// page: <i>"retire this legacy"</i>. It had already been decided in substance on 2026-08-03 —
    /// <i>"we should NOT create any folder - that was the old method"</i>, <i>"sponsor data like logo
    /// is in flat folder structure"</i> — and §816/§817.3(2) left this half standing pending his
    /// call.</para>
    ///
    /// <para>🔑 <b>This is the third and last part of the old model to go.</b> §816 removed the
    /// welcome guard that WAITED for a folder; §819 retired the job that WATCHED for the folder; this
    /// removes the code that CREATED it. Leaving any one of them behind is what made the other two
    /// look healthy while a real sponsor went unwelcomed — [[ceh-two-switch-trap]] three times over on
    /// one mechanism.</para>
    ///
    /// <para>🔒 <b>The placeholder seeding STAYS, and it is not vestigial.</b> Every upload task
    /// description carries a <c>{{logoFolderUrl}}</c>-style marker. Seeding it blank is what makes
    /// <see cref="SubstitutePlaceholders"/> fall back to the generic upload portal; deleting the seed
    /// would render a literal <c>{{logoFolderUrl}}</c> to a sponsor. The flat portal IS the upload
    /// mechanism now, so the fallback is no longer a fallback — it is the path.</para>
    ///
    /// <para>⚠️ Existing <c>SponsorUploadLocation</c> rows are LEFT ALONE. No new ones are written,
    /// but three surfaces still read the company NAME off them as a legacy fallback, and deleting
    /// live rows to tidy up a retirement would break a read that has nothing to do with folders.</para>
    /// </remarks>
    private Dictionary<string, string> SeedUploadPlaceholders(
        IEnumerable<SponsorTaskUploadDefinition> uploads)
    {
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var def in uploads)
        {
            if (!string.IsNullOrWhiteSpace(def.Placeholder))
            {
                urls[def.Placeholder] = string.Empty;
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
    private static string Slug(string title) => SponsorTaskKeys.Slug(title);

    /// <summary>
    /// §684.15 — translate what this pull learned about a company into the CLOSED audience
    /// predicate set the registry evaluates.
    /// </summary>
    /// <remarks>
    /// 🔒 §456 was a real incident: an exhibitor-speaker saw every SPONSOR task. Audience must be
    /// explicit and testable, never a side effect of which seeder ran — so the facts are computed
    /// HERE, once, from the company's classified orders, and handed to the registry as data.
    /// Today the same idea is spread across <c>DeadlineAllowedByEntitlement</c>, tier lists in JSON
    /// and <c>if</c> statements in several seeders.
    /// </remarks>
    private static IReadOnlyCollection<TaskAudiencePredicate> BuildAudiencePredicates(
        BoothTier tier, bool hasSession)
    {
        var predicates = new List<TaskAudiencePredicate>();
        if (tier != BoothTier.None) predicates.Add(TaskAudiencePredicate.HasBooth);
        if (hasSession) predicates.Add(TaskAudiencePredicate.HasSponsorSession);
        return predicates;
    }
}
