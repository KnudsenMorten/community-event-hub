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
/// Fully DB-backed since v1.2.6: keys + token versions persist, the leads
/// grid / counters / notification prefs read and write
/// DbSet&lt;SponsorLead&gt; / DbSet&lt;SponsorLeadNotificationPref&gt;,
/// per-lead actions (Reply / Processed / Interest / Ignore / Junk) are
/// soft-status changes (nothing hard-deletes), and Sync now fires the
/// real (config-gated) Zoho CRM pull.
/// </summary>
[Authorize]
public class LeadsModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ISponsorApiKeyService _keys;
    private readonly IDeterministicSponsorTokenService _detTokens;
    private readonly SponsorLeadSyncService _sync;
    private readonly CommunityHub.Core.Email.IEmailSender _emailSender;
    private readonly TimeProvider _clock;

    public LeadsModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        ISponsorApiKeyService keys,
        IDeterministicSponsorTokenService detTokens,
        SponsorLeadSyncService sync,
        CommunityHub.Core.Email.IEmailSender emailSender,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _keys = keys;
        _detTokens = detTokens;
        _sync = sync;
        _emailSender = emailSender;
        _clock = clock;
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

    public List<SponsorLead> Leads { get; private set; } = new();
    public Dictionary<string, SponsorLeadNotificationPref> NotifyPrefs { get; private set; }
        = new(StringComparer.OrdinalIgnoreCase);

    [BindProperty(SupportsGet = true)] public bool ShowHidden { get; set; }
    [BindProperty(SupportsGet = true)] public string? SponsorFilter { get; set; }

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

        // Pipeline counters (live).
        var weekAgo = _clock.GetUtcNow().AddDays(-7);
        var leadsQ = _db.SponsorLeads.Where(l => l.EventId == me.EventId);
        TotalLeads  = await leadsQ.CountAsync(ct);
        LeadsLast7d = await leadsQ.CountAsync(l => l.CapturedAt >= weekAgo, ct);
        LastZohoSyncAt = TotalLeads == 0
            ? null
            : await leadsQ.MaxAsync(l => (DateTimeOffset?)l.LastSyncedAt, ct);

        // Notification prefs per sponsor company (absent row = defaults).
        NotifyPrefs = (await _db.SponsorLeadNotificationPrefs
            .Where(p => p.EventId == me.EventId)
            .ToListAsync(ct))
            .ToDictionary(p => p.SponsorCompanyId, StringComparer.OrdinalIgnoreCase);

        // Leads grid: Ignore/Junk hidden by default (rows preserved).
        var gridQ = _db.SponsorLeads.Where(l => l.EventId == me.EventId);
        if (!ShowHidden)
        {
            gridQ = gridQ.Where(l => l.Status != SponsorLeadStatus.Ignore
                                     && l.Status != SponsorLeadStatus.Junk);
        }
        if (!string.IsNullOrWhiteSpace(SponsorFilter))
        {
            gridQ = gridQ.Where(l => l.SponsorCompanyId == SponsorFilter);
        }
        Leads = await gridQ
            .OrderByDescending(l => l.CapturedAt)
            .Take(200)
            .ToListAsync(ct);

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

    /// <summary>Persist per-sponsor digest preferences (upsert by pair).</summary>
    public async Task<IActionResult> OnPostSaveNotifyPrefsAsync(
        string sponsorCompanyId, bool enabled, string cadence, string recipients, bool skipJunk,
        CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) return Forbid();

        var pref = await _db.SponsorLeadNotificationPrefs.FirstOrDefaultAsync(
            p => p.EventId == me.EventId && p.SponsorCompanyId == sponsorCompanyId, ct);
        if (pref is null)
        {
            pref = new SponsorLeadNotificationPref
            {
                EventId = me.EventId,
                SponsorCompanyId = sponsorCompanyId,
            };
            _db.SponsorLeadNotificationPrefs.Add(pref);
        }
        pref.Enabled = enabled;
        pref.Cadence = string.Equals(cadence, "realtime", StringComparison.OrdinalIgnoreCase)
            ? SponsorLeadNotifyCadence.RealTime
            : SponsorLeadNotifyCadence.Daily;
        pref.Recipients = (recipients ?? string.Empty).Trim();
        pref.SkipJunk = skipJunk;
        await _db.SaveChangesAsync(ct);

        TempData["Notice"] = $"Notification prefs saved for '{sponsorCompanyId}' "
            + $"(enabled={pref.Enabled}, cadence={pref.Cadence}, skipJunk={pref.SkipJunk}).";
        return RedirectToPage();
    }

    /// <summary>Per-lead status changes. NOTHING ever hard-deletes — rows are
    /// preserved so operator overrides keep training the screen.</summary>
    public Task<IActionResult> OnPostMarkLeadProcessedAsync(int leadId, CancellationToken ct)
        => SetLeadStatusAsync(leadId, SponsorLeadStatus.Processed, null, ct);
    public Task<IActionResult> OnPostMarkLeadInterestAsync(int leadId, CancellationToken ct)
        => SetLeadStatusAsync(leadId, SponsorLeadStatus.Interest, null, ct);
    public Task<IActionResult> OnPostMarkLeadIgnoreAsync(int leadId, CancellationToken ct)
        => SetLeadStatusAsync(leadId, SponsorLeadStatus.Ignore, "Operator chose not to pursue.", ct);
    public Task<IActionResult> OnPostMarkLeadJunkAsync(int leadId, CancellationToken ct)
        => SetLeadStatusAsync(leadId, SponsorLeadStatus.Junk, "Marked junk by operator.", ct);

    private async Task<IActionResult> SetLeadStatusAsync(
        int leadId, SponsorLeadStatus status, string? note, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) return Forbid();

        var lead = await _db.SponsorLeads.FirstOrDefaultAsync(
            l => l.Id == leadId && l.EventId == me.EventId, ct);
        if (lead is null)
        {
            TempData["Notice"] = $"Lead #{leadId} not found.";
        }
        else
        {
            lead.Status = status;
            lead.StatusNote = note;
            lead.StatusChangedAt = _clock.GetUtcNow();
            lead.StatusChangedByEmail = me.Email;
            await _db.SaveChangesAsync(ct);
            TempData["Notice"] = $"Lead '{lead.FullName}' set to {status}.";
        }
        return RedirectToPage(new { ShowHidden, SponsorFilter });
    }

    /// <summary>Send a reply to the lead's email + record the audit on the row.</summary>
    public async Task<IActionResult> OnPostReplyLeadAsync(
        int leadId, string subject, string body, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) return Forbid();

        var lead = await _db.SponsorLeads.FirstOrDefaultAsync(
            l => l.Id == leadId && l.EventId == me.EventId, ct);
        if (lead is null || string.IsNullOrWhiteSpace(lead.Email))
        {
            TempData["Notice"] = lead is null
                ? $"Lead #{leadId} not found."
                : $"Lead '{lead.FullName}' has no email address to reply to.";
            return RedirectToPage(new { ShowHidden, SponsorFilter });
        }
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
        {
            TempData["Notice"] = "Reply needs both a subject and a message.";
            return RedirectToPage(new { ShowHidden, SponsorFilter });
        }

        // Plain text -> encoded paragraphs (same convention as Broadcast).
        var paragraphs = System.Text.RegularExpressions.Regex
            .Split(body.Replace("\r\n", "\n").Trim(), "\n{2,}")
            .Select(p => "<p style=\"margin:0 0 16px;\">"
                + System.Net.WebUtility.HtmlEncode(p).Replace("\n", "<br>") + "</p>");
        var html = string.Concat(paragraphs);

        try
        {
            await _emailSender.SendAsync(lead.Email, subject.Trim(), html, ct);
            lead.LastReplyAt = _clock.GetUtcNow();
            lead.LastReplyByEmail = me.Email;
            if (lead.Status == SponsorLeadStatus.Open)
            {
                lead.Status = SponsorLeadStatus.Processed;
                lead.StatusChangedAt = lead.LastReplyAt;
                lead.StatusChangedByEmail = me.Email;
                lead.StatusNote = "Replied from the hub.";
            }
            await _db.SaveChangesAsync(ct);
            TempData["Notice"] = $"Reply sent to {lead.Email}.";
        }
        catch (Exception ex)
        {
            TempData["Notice"] = $"Reply to {lead.Email} FAILED: {ex.Message}";
        }
        return RedirectToPage(new { ShowHidden, SponsorFilter });
    }

    /// <summary>Fire the Zoho CRM pull on demand (config-gated).</summary>
    public async Task<IActionResult> OnPostSyncNowAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) return Forbid();

        var result = await _sync.SyncAsync(me.EventId, ct);
        TempData["Notice"] = result.Message;
        return RedirectToPage();
    }
}
