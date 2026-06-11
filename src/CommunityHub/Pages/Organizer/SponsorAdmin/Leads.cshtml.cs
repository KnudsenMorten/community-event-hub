using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Sponsors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer.SponsorAdmin;

/// <summary>
/// Sponsor leads management hub. Splits responsibilities across four
/// sections (one .cshtml, four anchors):
///
///   1. Pipeline status      -- counters + last Zoho sync timestamp.
///   2. Per-sponsor API keys -- issue / revoke / show prefix + samples.
///   3. Notification prefs   -- per-sponsor digest cadence + recipient list.
///   4. Leads grid           -- the actual rows, with per-lead actions
///                              (Reply / Processed / Junk / Delete) and
///                              AI screen badges.
///
/// SCAFFOLD ON THIS COMMIT:
///   - Section 1 + 2 are functional against
///     <see cref="ISponsorApiKeyService"/> -- the in-memory stub
///     persists keys only across a single App Service warm window.
///   - Sections 3 + 4 render with placeholders; the underlying
///     <c>DbSet&lt;SponsorLead&gt;</c> and
///     <c>DbSet&lt;SponsorLeadNotificationPref&gt;</c> need an EF
///     migration before queries land. The corresponding handler stubs
///     write a TempData notice so you can see the wiring works
///     end-to-end in the browser.
/// </summary>
[Authorize]
public class LeadsModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ISponsorApiKeyService _keys;
    private readonly IDeterministicSponsorTokenService _detTokens;

    public LeadsModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        ISponsorApiKeyService keys,
        IDeterministicSponsorTokenService detTokens)
    {
        _db = db;
        _participant = participant;
        _keys = keys;
        _detTokens = detTokens;
    }

    public bool AccessDenied { get; private set; }

    /// <summary>Lit once by the issue-key handler so the page can show the rawKey BANNER (one-time-only display).</summary>
    [TempData] public string? FreshKeyForSponsor { get; set; }
    [TempData] public string? FreshKeyValue      { get; set; }

    public record SponsorKeyRow(string SponsorCompanyId, string? KeyPrefix, DateTimeOffset? IssuedAt, string? IssuedByEmail, string DeterministicToken, int TokenVersion);

    public List<SponsorKeyRow> SponsorKeys { get; private set; } = new();
    public int  TotalLeads     { get; private set; }
    public int  LeadsLast7d    { get; private set; }
    public DateTimeOffset? LastZohoSyncAt { get; private set; }

    public string BaseUrl { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        // Compose the base URL the sponsor will paste into PowerShell /
        // their browser. Includes scheme + host so the samples are
        // copy-paste-ready.
        BaseUrl = $"{Request.Scheme}://{Request.Host.Value}";

        // List every sponsor company we know about for this event,
        // along with the current (non-revoked) key's metadata (if any).
        var sponsorCompanyIds = await _db.Participants
            .Where(p => p.EventId == me.EventId
                        && p.Role == ParticipantRole.Sponsor
                        && p.SponsorCompanyId != null)
            .Select(p => p.SponsorCompanyId!)
            .Distinct()
            .OrderBy(cid => cid)
            .ToListAsync(ct);

        foreach (var cid in sponsorCompanyIds)
        {
            var row = await _keys.GetCurrentAsync(me.EventId, cid, ct);
            string detTok = "";
            int detVer = 1;
            try
            {
                detTok = await _detTokens.DeriveAsync(me.EventId, cid, ct);
                detVer = await _detTokens.GetVersionAsync(me.EventId, cid, ct);
            }
            catch
            {
                // Global secret not configured -- leave deterministic
                // columns blank so the page surfaces the misconfig
                // without crashing.
            }
            SponsorKeys.Add(new SponsorKeyRow(cid, row?.KeyPrefix, row?.IssuedAt, row?.IssuedByEmail, detTok, detVer));
        }

        // Pipeline counters: placeholder until DbSet<SponsorLead> exists.
        TotalLeads = 0;
        LeadsLast7d = 0;
        LastZohoSyncAt = null;

        return Page();
    }

    public async Task<IActionResult> OnPostIssueKeyAsync(string sponsorCompanyId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) return Forbid();

        var (raw, _) = await _keys.IssueAsync(me.EventId, sponsorCompanyId, me.Email,
            label: $"Issued {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC by {me.Email}", ct);
        FreshKeyForSponsor = sponsorCompanyId;
        FreshKeyValue = raw;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeKeyAsync(string sponsorCompanyId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) return Forbid();

        await _keys.RevokeAsync(me.EventId, sponsorCompanyId, me.Email, ct);
        TempData["Notice"] = $"Revoked API key for sponsor '{sponsorCompanyId}'.";
        return RedirectToPage();
    }

    /// <summary>
    /// Bump the sponsor's deterministic-token version. The previous
    /// derived token immediately stops validating; the next page render
    /// shows the new one. Use when a sponsor reports their token has
    /// leaked or their primary contact changed and you want to invalidate
    /// whatever value the old contact had.
    /// </summary>
    public async Task<IActionResult> OnPostBumpTokenVersionAsync(string sponsorCompanyId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) return Forbid();

        var newVersion = await _detTokens.BumpVersionAsync(me.EventId, sponsorCompanyId, me.Email, ct);
        TempData["Notice"] = $"Bumped deterministic-token version for '{sponsorCompanyId}' to v{newVersion}. Previous token is now invalid.";
        return RedirectToPage();
    }

    /// <summary>SCAFFOLD: enable / disable + cadence + recipients for a sponsor.</summary>
    public IActionResult OnPostSaveNotifyPrefs(string sponsorCompanyId, bool enabled, string cadence, string recipients, bool skipJunk)
    {
        TempData["Notice"] = $"NotifyPref scaffold: would have saved (enabled={enabled}, cadence={cadence}, recipients='{recipients}', skipJunk={skipJunk}) for sponsor '{sponsorCompanyId}'. Backend pending (DbSet<SponsorLeadNotificationPref>).";
        return RedirectToPage();
    }

    /// <summary>SCAFFOLD: per-lead status-change handlers. NOTHING ever hard-deletes -- "delete" sets Status=Ignore.</summary>
    public IActionResult OnPostMarkLeadProcessed(int leadId)
    {
        TempData["Notice"] = $"MarkProcessed scaffold: would set SponsorLead#{leadId} Status=Processed. Backend pending.";
        return RedirectToPage();
    }
    public IActionResult OnPostMarkLeadInterest(int leadId)
    {
        TempData["Notice"] = $"MarkInterest scaffold: would set SponsorLead#{leadId} Status=Interest. Backend pending.";
        return RedirectToPage();
    }
    public IActionResult OnPostMarkLeadIgnore(int leadId)
    {
        TempData["Notice"] = $"MarkIgnore scaffold: would set SponsorLead#{leadId} Status=Ignore (soft-hide, row preserved). Backend pending.";
        return RedirectToPage();
    }
    public IActionResult OnPostMarkLeadJunk(int leadId)
    {
        TempData["Notice"] = $"MarkJunk scaffold: would set SponsorLead#{leadId} Status=Junk. Backend pending.";
        return RedirectToPage();
    }
    public IActionResult OnPostReplyLead(int leadId, string subject, string body)
    {
        TempData["Notice"] = $"ReplyLead scaffold: would send '{subject}' to the lead's Email + record on SponsorLead#{leadId} LastReplyAt. Backend pending.";
        return RedirectToPage();
    }

    /// <summary>SCAFFOLD: manual Zoho sync trigger.</summary>
    public IActionResult OnPostSyncNow()
    {
        TempData["Notice"] = "SyncNow scaffold: would have fired the Zoho pull + per-sponsor export jobs. Backend pending (Zoho OAuth + DbSet<SponsorLead> migration).";
        return RedirectToPage();
    }
}
