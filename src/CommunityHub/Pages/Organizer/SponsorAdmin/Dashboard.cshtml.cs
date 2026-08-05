using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CommunityHub.Pages.Organizer.SponsorAdmin;

/// <summary>
/// Per-sponsor-company status dashboard. One row per sponsor company
/// (Participant.SponsorCompanyId), counting:
///   - tasks done / total / overdue (from <see cref="ParticipantTask"/>)
///   - leads total / last 7 days (zero until the Zoho pipeline lands)
///   - last Zoho sync timestamp (null until the pipeline lands)
///
/// Sorted by overdue tasks first so the operator sees the at-risk
/// sponsors immediately on open. The "overdue" arithmetic uses today's
/// date in UTC to match the existing Sponsors.cshtml computation.
/// </summary>
[Authorize]
public class DashboardModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;
    private readonly ZohoOptions _zoho;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _cmOptions;
    private readonly CommunityHub.Core.Settings.FeatureGateService _gate;
    private readonly ILogger<DashboardModel> _logger;

    public DashboardModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock,
        ZohoOptions zoho,
        CompanyManagerClient cm,
        CompanyManagerOptions cmOptions,
        CommunityHub.Core.Settings.FeatureGateService gate,
        ILogger<DashboardModel> logger)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
        _zoho = zoho;
        _cm = cm;
        _cmOptions = cmOptions;
        _gate = gate;
        _logger = logger;
    }

    public bool AccessDenied { get; private set; }

    [TempData] public string? ActionMessage { get; set; }

    /// <summary>
    /// One-time (re-runnable) maintenance: fill each sponsor's Event Coordinator
    /// from the webshop default coordinator where empty, then re-sync every sponsor
    /// record to Zoho Backstage (fixes the legacy UTF-8 mojibake + pushes contacts).
    /// </summary>
    /// <summary>
    /// §496c — pull sponsor CONTACTS from Company Manager into the hub, on demand.
    ///
    /// <para><b>Why this is needed.</b> §493b syncs immediately when a sponsor edits contacts on
    /// the hub's Company Details page — but a contact added DIRECTLY in Company Manager (or in the
    /// ERP, then pushed to CM) raises no event the hub can see. Until the next scheduled
    /// <c>SponsorOrderPullService</c> run, the person exists in CM and is invisible in CEH, with no
    /// way to hurry it along. The operator hit exactly that: <i>"i see the new contact in CM module
    /// - mh@2linkit.net - fix so i also see in ceh"</i>.</para>
    ///
    /// <para>Idempotent — it is the same per-company sync the scheduled pull runs, so pressing it
    /// twice changes nothing the second time.</para>
    /// </summary>
    public async Task<IActionResult> OnPostSyncContactsAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var db = HttpContext.RequestServices.GetRequiredService<CommunityHub.Core.Data.CommunityHubDbContext>();
        var sync = HttpContext.RequestServices.GetRequiredService<CommunityHub.Core.Integrations.SponsorContactSyncService>();

        // Every sponsor company the hub knows about for this edition.
        var companyIds = await db.Participants
            .Where(p => p.EventId == me.EventId
                        && p.Role == CommunityHub.Core.Domain.ParticipantRole.Sponsor
                        && p.SponsorCompanyId != null)
            .Select(p => p.SponsorCompanyId!)
            .Distinct()
            .ToListAsync(ct);

        int companies = 0, created = 0, updated = 0, failed = 0;
        foreach (var idStr in companyIds)
        {
            if (!int.TryParse(idStr, out var cmId)) continue;
            try
            {
                // One company's Company Manager hiccup must not abandon the rest — the same
                // per-company tolerance the scheduled pull uses.
                var r = await sync.SyncCompanyAsync(me.EventId, cmId, ct);
                companies++;
                created += r.ParticipantsCreated;
                updated += r.ParticipantsUpdated;
            }
            catch { failed++; }
        }

        ActionMessage = $"Contact sync: {companies} company(ies) checked, {created} contact(s) created, "
                        + $"{updated} updated"
                        + (failed > 0 ? $", {failed} company(ies) could not be reached." : ".");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostMigrateResyncAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        // GATE (§16/§234): the manual "do it now" resync honours the SAME per-edition
        // 'backstage-sync' kill-switch as the scheduled Backstage/Zoho sync, so GUI
        // state == actual behaviour. Disabled ⇒ no-op with a clear notice.
        if (!await _gate.IsFeatureEnabledAsync("backstage-sync", me.EventId, ct))
        {
            ActionMessage = "The Backstage / Zoho exhibitor sync feature is turned off for this event. "
                + "Enable it in Settings to run the migrate + re-sync.";
            return RedirectToPage();
        }

        var svc = HttpContext.RequestServices.GetRequiredService<SponsorZohoSyncService>();
        var r = await svc.MigrateCoordinatorsAndResyncAsync(me.EventId, ct);
        ActionMessage =
            $"Coordinators filled from webshop: {r.CoordinatorsFilled}. " +
            $"Synced to Zoho: {r.SponsorsSynced} sponsor + {r.ExhibitorsSynced} exhibitor record(s) " +
            $"across {r.Companies} compan{(r.Companies == 1 ? "y" : "ies")}." +
            (r.Failed > 0 ? $" {r.Failed} need attention: {string.Join("; ", r.Notes.Take(8))}" : " No issues.");
        return RedirectToPage();
    }

    /// <summary>
    /// Stage 4b: CREATE or LINK each sponsor company's Zoho Backstage sponsor +
    /// exhibitor records from webshop data (the manual equivalent of the scheduled
    /// provisioning the order-pull job runs). Re-runnable; existing/linked companies
    /// are left as-is.
    /// </summary>
    public async Task<IActionResult> OnPostProvisionAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        // GATE (§16/§234): same per-edition 'sponsor-zoho-provision' kill-switch the
        // scheduled WooCommercePullJob provisioning leg checks.
        if (!await _gate.IsFeatureEnabledAsync("sponsor-zoho-provision", me.EventId, ct))
        {
            ActionMessage = "The Zoho sponsor/exhibitor provisioning feature is turned off for this event. "
                + "Enable it in Settings to run the provision.";
            return RedirectToPage();
        }

        var svc = HttpContext.RequestServices.GetRequiredService<SponsorZohoProvisionService>();
        var r = await svc.ProvisionAsync(me.EventId, ct);
        ActionMessage = !r.Enabled
            ? "Zoho Backstage is not enabled for this environment."
            : $"Zoho provision: {r.SponsorsCreated} sponsor(s) created, {r.SponsorsLinked} linked, " +
              $"{r.ExhibitorsRequested} exhibitor request(s), {r.ExhibitorsLinked} exhibitor(s) linked, {r.Skipped} skipped." +
              (r.Notes.Count > 0 ? " " + string.Join("; ", r.Notes.Take(8)) : "");
        return RedirectToPage();
    }

    /// <summary>
    /// ERP→webshop reconcile: create missing Company Manager users from the group-1
    /// e-conomic contacts, set each company's default signer + event coordinator from
    /// the contact roles, and email an alert for any contacts missing a Role:1/Role:2.
    /// </summary>
    public async Task<IActionResult> OnPostReconcileErpAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        // GATE (§16/§234): same per-edition 'erp-webshop-reconcile' kill-switch the
        // scheduled ErpSyncCustomerContactJob checks.
        if (!await _gate.IsFeatureEnabledAsync("erp-webshop-reconcile", me.EventId, ct))
        {
            ActionMessage = "The ERP-to-webshop contact reconcile feature is turned off for this event. "
                + "Enable it in Settings to run the reconcile.";
            return RedirectToPage();
        }

        var svc = HttpContext.RequestServices.GetRequiredService<CommunityHub.Core.Integrations.Erp.ErpWebshopContactSyncService>();
        // §482b: the organizer's manual reconcile stays a FULL sweep across all sponsor customers.
        var r = await svc.SyncAsync(ct: ct);
        ActionMessage = !r.Enabled
            ? "Backend / Company Manager is not configured for this environment."
            : $"ERP→webshop reconcile: {r.Customers} sponsor customers, {r.UsersCreated} webshop user(s) created, " +
              $"{r.DefaultsSet} default(s) set, {r.Alerts} item(s) need attention" +
              (r.Alerts > 0 ? $" (alert emailed to {CommunityHub.Core.Integrations.Erp.ErpWebshopContactSyncService.AlertEmail}): {string.Join("; ", r.AlertNotes.Take(10))}" : ".");
        return RedirectToPage();
    }

    /// <summary>
    /// True only when the leads pipeline is genuinely unconfigured: the Zoho CRM pull is off AND no
    /// SponsorLead rows exist for the event.
    /// </summary>
    /// <remarks>
    /// 🗑 §784.3 — the BANNER this drove is gone. It read "Zoho pipeline not yet configured … until
    /// the Zoho sync job is wired up (Zoho OAuth + DB migration + storage SAS)", which the operator
    /// flagged as *"seems wrong"*: Zoho has synced sponsors for months, and the sentence described a
    /// commissioning state the system left long ago. Its only true subject was the LEAD counters,
    /// which §784.6 removed because the feature is switched off in Settings.
    ///
    /// The FLAG is kept — it is still an accurate computation and the Leads page may want it — but
    /// nothing on this page renders it. Do not resurrect the banner without rewriting its text.
    /// </remarks>
    public bool ZohoPipelinePending { get; private set; }

    public record SponsorRow(
        string CompanyId,
        string CompanyName,
        int Contacts,
        int TasksTotal,
        int TasksDone,
        int TasksOverdue,
        int LeadsTotal,
        int LeadsLast7d,
        DateTimeOffset? LastZohoSync);

    public List<SponsorRow> Rows { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

        // Pull sponsor participants once and group locally so we get a
        // per-company contact count cheaply.
        var sponsorParticipants = await _db.Participants
            .Where(p => p.EventId == me.EventId && p.Role == ParticipantRole.Sponsor)
            .Select(p => new { p.Id, p.SponsorCompanyId })
            .ToListAsync(ct);

        // Pull sponsor tasks for the event. Same predicate as
        // Sponsors.cshtml.cs Download handler so the dashboard counts
        // and the existing exports always agree.
        var sponsorTasks = await _db.Tasks
            .Where(t => t.EventId == me.EventId
                        && (t.SponsorCompanyId != null
                            || (t.SourceKey != null && t.SourceKey.StartsWith("sponsor:"))))
            .Select(t => new { t.SponsorCompanyId, t.State, t.DueDate })
            .ToListAsync(ct);

        // Group sponsor tasks by company, tally the four states.
        var taskAgg = sponsorTasks
            .GroupBy(t => t.SponsorCompanyId ?? "(no company id)")
            .Select(g => new
            {
                CompanyId    = g.Key,
                TasksTotal   = g.Count(),
                TasksDone    = g.Count(t => t.State == TaskState.Done),
                TasksOverdue = g.Count(t => t.State != TaskState.Done && t.DueDate is not null && t.DueDate.Value < today),
            })
            .ToDictionary(x => x.CompanyId, x => x);

        // Include companies that have contacts but zero tasks too --
        // they still show on the dashboard with empty counters so we
        // can spot a company that simply has no work assigned yet.
        var contactsByCompany = sponsorParticipants
            .Where(p => !string.IsNullOrEmpty(p.SponsorCompanyId))
            .GroupBy(p => p.SponsorCompanyId!)
            .ToDictionary(g => g.Key, g => g.Count());

        // Leads pipeline columns (live since v1.2.8; were zero-stubs while
        // DbSet<SponsorLead> didn't exist).
        var weekAgo = _clock.GetUtcNow().AddDays(-7);
        var leadAgg = (await _db.SponsorLeads
            .Where(l => l.EventId == me.EventId)
            .Select(l => new { l.SponsorCompanyId, l.CapturedAt, l.LastSyncedAt })
            .ToListAsync(ct))
            .GroupBy(l => l.SponsorCompanyId)
            .ToDictionary(g => g.Key, g => new
            {
                Total  = g.Count(),
                Last7d = g.Count(l => l.CapturedAt >= weekAgo),
                LastSync = (DateTimeOffset?)g.Max(l => l.LastSyncedAt),
            });

        // Banner state: "pending" only when the CRM pull is off AND no leads
        // have ever landed for this event. Either condition clears it.
        ZohoPipelinePending = !(_zoho.CrmEnabled || leadAgg.Count > 0);

        var allCompanyIds = new HashSet<string>(taskAgg.Keys);
        foreach (var cid in contactsByCompany.Keys) allCompanyIds.Add(cid);
        foreach (var cid in leadAgg.Keys) allCompanyIds.Add(cid);

        // Resolve each company's display NAME (don't show the raw id) from CEH SQL — the copy
        // the CM → CEH sync captured — in one query. Falls back to "Company {id}" for a company
        // the sync has not captured yet (§443).
        var names = await ResolveCompanyNamesAsync(me.EventId, allCompanyIds, ct);

        Rows = allCompanyIds
            .Select(cid =>
            {
                taskAgg.TryGetValue(cid, out var ta);
                contactsByCompany.TryGetValue(cid, out var contactCount);
                leadAgg.TryGetValue(cid, out var la);
                return new SponsorRow(
                    CompanyId:    cid,
                    CompanyName:  names.TryGetValue(cid, out var nm) ? nm : $"Company {cid}",
                    Contacts:     contactCount,
                    TasksTotal:   ta?.TasksTotal   ?? 0,
                    TasksDone:    ta?.TasksDone    ?? 0,
                    TasksOverdue: ta?.TasksOverdue ?? 0,
                    LeadsTotal:   la?.Total  ?? 0,
                    LeadsLast7d:  la?.Last7d ?? 0,
                    LastZohoSync: la?.LastSync);
            })
            .OrderByDescending(r => r.TasksOverdue)
            .ThenBy(r => r.CompanyName)
            .ToList();

        return Page();
    }

    /// <summary>
    /// Map each sponsor company id to its display name — from CEH SQL, in ONE query.
    ///
    /// <para>§443 (operator 2026-07-27): this used to call Company Manager once per company,
    /// sequentially, while rendering, which made this page take ~7.9 s warm on PROD. The name is
    /// already synced into CEH by <c>SponsorOrderPullService</c> (through the same
    /// <see cref="SponsorCompanyName"/> chain), so the admin interface reads the local copy and
    /// never reaches the WordPress plugin on the request path.</para>
    /// </summary>
    private Task<Dictionary<string, string>> ResolveCompanyNamesAsync(
        int eventId, IEnumerable<string> companyIds, CancellationToken ct) =>
        SponsorCompanyNameService.ResolveFromLocalAsync(_db, eventId, companyIds, ct);
}
