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
    private readonly CommunityHub.Core.Settings.FeatureGateService _gate;

    // 🗑 §1085 — TimeProvider / ZohoOptions / CompanyManagerClient / CompanyManagerOptions / ILogger
    // went with the status table: the clock dated the overdue tallies, the Zoho options drove the
    // lead rollup, and the Company Manager pair resolved company NAMES for rows this page no longer
    // renders. What is left is four maintenance actions and the count they quote.
    public DashboardModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        CommunityHub.Core.Settings.FeatureGateService gate)
    {
        _db = db;
        _participant = participant;
        _gate = gate;
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
        // §1088 — "skipped" is stated as its own outcome and never as "need attention". A test or
        // withdrawn company is excluded ON PURPOSE; reporting it as needing attention taught the
        // organizer that this button always ends in a complaint.
        ActionMessage =
            $"Coordinators filled from webshop: {r.CoordinatorsFilled}. " +
            $"Synced to Zoho: {r.SponsorsSynced} sponsor + {r.ExhibitorsSynced} exhibitor record(s) " +
            $"across {r.Companies} compan{(r.Companies == 1 ? "y" : "ies")}." +
            (r.Skipped > 0
                ? $" {r.Skipped} skipped on purpose (test data or withdrawn)."
                : string.Empty) +
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
    /// §1173 — carry a sponsor's brand profile to the company that succeeds it.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-03, on a supplier that re-registered under a new VAT number:
    /// <i>"as i need new contract with correct vat number"</i> · <i>"i have done this before"</i>.</para>
    ///
    /// <para>🔑 The contract belongs to the legal entity; the logo and description belong to the
    /// organisation, which has not changed. Without this the sponsor is asked for everything twice.</para>
    ///
    /// <para>🔒 Preview first, and fill-blank only — nothing already set on the new company is
    /// overwritten, and Zoho ids, ERP links and order-derived state are never copied at all.</para>
    /// </remarks>
    public async Task<IActionResult> OnPostCarryForwardAsync(
        string fromCompanyId, string toCompanyId, bool apply, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var svc = HttpContext.RequestServices
            .GetRequiredService<CommunityHub.Core.Integrations.SponsorProfileCarryForward>();
        var r = await svc.RunAsync(me.EventId, fromCompanyId, toCompanyId, dryRun: !apply, ct);

        if (!r.Ok)
        {
            ActionMessage = r.Error;
            return RedirectToPage();
        }

        if (r.Copied.Count == 0)
        {
            ActionMessage = "Nothing to carry forward — the new company already has everything the "
                + "old one holds."
                + (r.Skipped.Count > 0 ? $" Already set: {string.Join("; ", r.Skipped)}." : string.Empty);
            return RedirectToPage();
        }

        ActionMessage = (apply
                ? $"Carried {r.Copied.Count} field(s) forward"
                : $"PREVIEW — {r.Copied.Count} field(s) would be carried forward, nothing written")
            + $": {string.Join("; ", r.Copied)}."
            + (r.Skipped.Count > 0 ? $" Left alone: {string.Join("; ", r.Skipped)}." : string.Empty);

        return RedirectToPage();
    }

    /// <summary>
    /// §1158 — the ONE-TIME sweep that takes the legal form (A/S, ApS, GmbH, LLC, K/S, AG …) off
    /// every sponsor's PUBLIC name in the webshop. Preview by default; <c>apply=true</c> writes.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-31: <i>"i would like a one-time run through all sponsors in the
    /// webshop and adjust the public name and remove these legal company types acronyms"</i>.</para>
    ///
    /// <para>🔒 <b>Two presses, on purpose.</b> He also said <i>"i am worried to automate this"</i>
    /// and <i>"as a sponsor can change the field at any time"</i>. Preview shows exactly which names
    /// change before any of them do — this rewrites names in a live webshop that sponsors see, and
    /// the recurring reconcile deliberately does NOT do this (it fills only an empty field).</para>
    /// </remarks>
    public async Task<IActionResult> OnPostNormalizePublicNamesAsync(bool apply, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        // GATE (§16/§234): the same kill-switch as the reconcile that owns this field.
        if (!await _gate.IsFeatureEnabledAsync("erp-webshop-reconcile", me.EventId, ct))
        {
            ActionMessage = "The ERP-to-webshop reconcile feature is turned off for this event. "
                + "Enable it in Settings to run the public-name sweep.";
            return RedirectToPage();
        }

        var svc = HttpContext.RequestServices.GetRequiredService<SponsorPublicNameNormalizer>();
        var r = await svc.RunAsync(me.EventId, apply, ct);

        if (!r.Enabled)
        {
            ActionMessage = "Company Manager is not configured for this environment.";
            return RedirectToPage();
        }

        // ⚠️ ALWAYS list what was left alone. A name whose legal form we do not RECOGNISE looks
        // exactly like a name that is already correct, so without this the result cannot tell him
        // which is which — the first run reported "10 of 17 changed" and two of the untouched seven
        // were unrecognised forms, not clean names.
        var untouched = r.Unchanged.Count == 0
            ? string.Empty
            : $" Left unchanged ({r.Unchanged.Count}): "
              + string.Join(", ", r.Unchanged.Take(30))
              + (r.Unchanged.Count > 30 ? $" … and {r.Unchanged.Count - 30} more" : string.Empty)
              + ". If one of those still carries a legal form, tell me — it means the form is not "
              + "recognised yet.";

        if (r.Changes.Count == 0)
        {
            ActionMessage = $"Public names: {r.CompaniesChecked} company(ies) checked, "
                + "none carries a legal form we recognise — nothing to change."
                + (r.Unreadable > 0 ? $" ⚠️ {r.Unreadable} could not be read and were skipped." : string.Empty)
                + untouched;
            return RedirectToPage();
        }

        var lines = r.Changes
            .Take(25)
            .Select(c => $"{c.CurrentName} → {c.SuggestedName}"
                + (c.Source == "legal" ? " (public name was empty)" : string.Empty)
                + (c.Error is not null ? $" — FAILED: {c.Error}" : string.Empty));

        var applied = r.Changes.Count(c => c.Applied);
        var failed = r.Changes.Count(c => !c.Applied && c.Error is not null);

        ActionMessage = (apply
                ? $"Public names UPDATED: {applied} of {r.Changes.Count} changed"
                  + (failed > 0 ? $", {failed} FAILED" : string.Empty)
                : $"Public names PREVIEW ({r.Changes.Count} would change — nothing written yet)")
            + $", {r.CompaniesChecked} company(ies) checked"
            + (r.Unreadable > 0 ? $", ⚠️ {r.Unreadable} unreadable and skipped" : string.Empty)
            + ": " + string.Join("; ", lines)
            + (r.Changes.Count > 25 ? $" … and {r.Changes.Count - 25} more." : string.Empty)
            + untouched;

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
    /// How many ACTIVE sponsor companies the maintenance actions will touch — the number the
    /// confirm dialogs quote ("re-sync N compan(ies)"). One cheap query; it is a count, not a board.
    /// </summary>
    /// <remarks>
    /// 🔴 §1085 — <b>THE PER-COMPANY STATUS TABLE THAT USED TO LIVE HERE IS GONE.</b> Operator
    /// 2026-08-13: <i>"Sponsor status dashboard - give me one dashboard that works … i have 2 and
    /// both i didn't trust. redesign one of them and retire the other"</i>, then
    /// <i>"in general we should consider one page for all roles with filter"</i>. Status now has ONE
    /// home for every role — <c>/Organizer/ParticipantStatus</c> — which reads the wizard services
    /// rather than counting tasks a second way. That second way is what made the two boards
    /// disagree, and a board he cannot trust is worth less than no board.
    ///
    /// <para>✅ <b>The four maintenance buttons STAY</b> (operator: <i>"i remember there where old
    /// buttoms etc that should live there"</i>) — sync contacts, migrate + re-sync, Zoho provision,
    /// ERP→webshop reconcile. This page is now exactly those, and nothing else.</para>
    ///
    /// <para>🗑 Retired with the table: the per-company task/lead aggregation, the
    /// <c>LastZohoSync</c> column, <c>HiddenRetiredCount</c> (it explained rows hidden from a table
    /// that no longer exists), and <c>ZohoPipelinePending</c> (computed from the lead rollup the
    /// table needed; §784.3 had already removed the banner it drove, so nothing rendered it).</para>
    /// </remarks>
    public int CompanyCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        // 🔒 §1071 — ACTIVE contacts only, the same rule the retired table finally learned: a
        // company whose every contact is deactivated is not a current sponsor, and the maintenance
        // actions should not claim to cover it.
        CompanyCount = await _db.Participants
            .Where(p => p.EventId == me.EventId
                        && p.Role == ParticipantRole.Sponsor
                        && p.IsActive
                        && p.SponsorCompanyId != null)
            .Select(p => p.SponsorCompanyId!)
            .Distinct()
            .CountAsync(ct);

        return Page();
    }

    // 🗑 §1085 — the company-name resolver went with the table: this page shows no company names
    // any more. The §443 rule it carried (read the LOCAL name copy, never call Company Manager on a
    // request path — that cost ~7.9 s warm on PROD) lives on in SponsorCompanyNameService, which
    // both the deliverables board and the new participant-status board call.
    //
    // ⚠️ Deliberately NOT naming the old method here: NoLiveCompanyManagerOnRenderPathTests treats
    // that identifier appearing in an organizer page as evidence the page resolves names, and would
    // fail on a comment about a method that no longer exists.
}
