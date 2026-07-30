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

            var uploadUrls = await ProvisionUploadFoldersAsync(
                activeEvent.Id, companyId, companyName,
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
                        })),
                editionFacts, ct);

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
                        fromConfig.RuleName, activeEvent.StartDate, firstOrderDate, today),
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
            var wallUploadOnFile = await _db.SponsorUploadFiles.AnyAsync(
                f => f.Location.EventId == activeEvent.Id
                     && f.Location.SponsorCompanyId == companyId
                     && f.Location.FolderKey == "SPONSORWALL", ct);
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

        // §253 G8d: refunded/cancelled orders were INVISIBLE — the pull reads
        // status=completed only, tiers/packages are raise-only and nothing ever
        // rolled a mirrored order back. Probe the refund statuses and raise ONE
        // organizer action-queue item per order (dedup by Woo order id, resolved
        // items never re-raise) so a human decides the rollback. Fail-soft: a
        // probe hiccup never breaks the pull.
        try
        {
            await RaiseRefundedOrderAlertsAsync(activeEvent.Id, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "SponsorOrderPullService: refunded-order probe failed; continuing (next run retries).");
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
        IEnumerable<SponsorTaskUploadDefinition> uploads,
        EventEditionConfig editionFacts,
        CancellationToken ct)
    {
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);

        // De-dup uploads by subfolder so the same folder is not provisioned
        // twice if two tasks reference it (rare but defensive).
        //
        // 🔒 §686.2 — this takes UPLOAD DEFINITIONS rather than tasks now, because they arrive from
        // two places during the migration: the JSON task sets and the code registry. Leaving it
        // reading only the JSON tasks is a trap worth naming — the sponsor-wall task's folder would
        // simply stop being provisioned the moment that task migrated. Nothing would look broken:
        // the task still renders, the Upload button is still there, and the file lands somewhere
        // nobody is watching.
        var uploadDefs = uploads
            .Where(u => !string.IsNullOrWhiteSpace(u.Subfolder))
            .GroupBy(u => u.Subfolder, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // SEED every upload placeholder with a blank value up-front so the
        // description NEVER renders a literal "{{logoFolderUrl}}". A blank value
        // makes SubstitutePlaceholders fall back to the generic {{uploadPortalUrl}};
        // a successful SharePoint provision below overrides it with the real
        // per-company edit link. This keeps the button working even when SharePoint
        // is disabled or provisioning fails.
        foreach (var def in uploadDefs)
        {
            if (!string.IsNullOrWhiteSpace(def.Placeholder))
            {
                urls[def.Placeholder] = string.Empty;
            }
        }

        var sp = editionFacts.SharePoint;
        if (sp is null || string.IsNullOrWhiteSpace(sp.SiteUrl) || !_sharePoint.IsConfigured)
        {
            // §326cd — this used to return in SILENCE, and the silence had a consequence
            // nobody could see: with no SharePoint, no SponsorUploadLocation is ever written,
            // and the sponsor-welcome provisioning guard then blocks EVERY Gold+ booth company
            // FOREVER (SponsorWelcomeEmailService ~:63-85). The welcome never goes out and the
            // only symptom is an absence. Say it, on every pull, at Warning.
            _log.LogWarning(
                "SponsorOrderPullService: SharePoint NOT configured for this edition "
                + "(site '{Site}', client configured={Configured}) — upload folders will not be "
                + "provisioned for {Co}. CONSEQUENCE: every Gold+ booth company stays BLOCKED "
                + "from the sponsor welcome until this is configured.",
                sp?.SiteUrl ?? "(none)", _sharePoint.IsConfigured, companyName);
            return urls;
        }

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
            // §102: a slow SharePoint call can surface a TaskCanceledException
            // (HttpClient timeout) rather than a SharePointUploadException — that
            // previously bubbled out and FAILED the whole WooCommerce pull (the
            // 13:32 engine alert). Treat any non-shutdown transient failure as a
            // per-folder skip + continue so one slow SharePoint call never aborts
            // the pull. A REAL host-shutdown cancellation (our ct) still propagates.
            catch (Exception ex) when (ex is not OperationCanceledException
                                       || !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex,
                    "SponsorOrderPullService: transient/slow SharePoint failure provisioning "
                    + "'{Path}' for {Co} (e.g. Graph HttpClient timeout); skipping this folder "
                    + "and continuing the pull.",
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
