using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Sponsors;
using CommunityHub.Core.Organizer;
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
    private readonly CommunityHub.Core.Settings.FeatureGateService _gate;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _cmOptions;
    private readonly ILogger<LeadsModel> _logger;
    private readonly CommunityHub.Core.Email.IEmailContextAccessor? _emailContext;

    public LeadsModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        ISponsorApiKeyService keys,
        IDeterministicSponsorTokenService detTokens,
        SponsorLeadSyncService sync,
        CommunityHub.Core.Email.IEmailSender emailSender,
        TimeProvider clock,
        CommunityHub.Core.Settings.FeatureGateService gate,
        CompanyManagerClient cm,
        CompanyManagerOptions cmOptions,
        ILogger<LeadsModel> logger,
        // §707.7 — optional so existing constructions keep compiling; the web host registers it.
        CommunityHub.Core.Email.IEmailContextAccessor? emailContext = null)
    {
        _emailContext = emailContext;
        _db = db;
        _participant = participant;
        _keys = keys;
        _detTokens = detTokens;
        _sync = sync;
        _emailSender = emailSender;
        _clock = clock;
        _gate = gate;
        _cm = cm;
        _cmOptions = cmOptions;
        _logger = logger;
    }

    public bool AccessDenied { get; private set; }

    /// <summary>§326bq — the per-edition <c>sponsor-leads</c> switch is OFF (its shipped
    /// default). Leads live in Zoho Backstage today; CEH may take them over later, so this
    /// is a FLAG, not a deletion — the page and its pipeline return intact when it is
    /// switched on in Settings. While off, the page renders an explanation only and every
    /// mutating handler refuses.</summary>
    public bool FeatureDisabled { get; private set; }

    /// <summary>§326bq — shared guard for every mutating handler on this page: refuse
    /// while the feature is off so nothing can issue keys, bump versions, change
    /// notification preferences or triage leads for a pipeline that is not running.</summary>
    private async Task<bool> LeadsDisabledAsync(int eventId, CancellationToken ct)
    {
        if (await _gate.IsFeatureEnabledAsync("sponsor-leads", eventId, ct)) return false;
        TempData["Notice"] = "The sponsor leads pipeline is turned off for this event — "
            + "leads live in Zoho Backstage. Enable 'sponsor-leads' in Settings to use this page.";
        return true;
    }

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

    /// <summary>
    /// §234 (2026-07-07): sponsor-company DISPLAY names, so the page never shows a raw
    /// internal company id on its own. Resolved once per request through the canonical
    /// <see cref="SponsorCompanyName"/> chain (Company Manager public → legal name, with
    /// the per-company name captured on SponsorUploadLocation as a DB-local fallback).
    /// </summary>
    public Dictionary<string, string> CompanyNames { get; private set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Display name for a sponsor company id ("Company {id}" when unresolvable).</summary>
    public string CompanyDisplayName(string companyId) =>
        !string.IsNullOrWhiteSpace(companyId) && CompanyNames.TryGetValue(companyId, out var n)
            ? n
            : SponsorCompanyName.Resolve(null, null, null, companyId);

    [BindProperty(SupportsGet = true)] public bool ShowHidden { get; set; }
    [BindProperty(SupportsGet = true)] public string? SponsorFilter { get; set; }

    // --- Search / sort / paging (GET-bound; threaded through per-lead action
    //     redirects so an action returns the organizer to the same place).
    //     Matches the Participants / Speakers / Attendees grids (REQUIREMENTS §21). -
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    /// <summary>Sort column key: captured | name | company | status. Default captured (newest first).</summary>
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "captured";
    [BindProperty(SupportsGet = true)] public bool Desc { get; set; } = true;
    [BindProperty(SupportsGet = true)] public int PageNo { get; set; } = 1;

    public GridPage Paging { get; private set; }

    public bool NextDescFor(string col) => Sort == col && !Desc;
    public string SortIndicator(string col) => Sort != col ? "" : (Desc ? " ▼" : " ▲");
    public string AriaSort(string col) => Sort != col ? "none" : (Desc ? "descending" : "ascending");

    /// <summary>Route values that keep the grid's place after a per-lead action.</summary>
    public object GridRoute => new { ShowHidden, SponsorFilter, Search, Sort, Desc, PageNo };

    public string BaseUrl { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        // §326bq (operator 2026-07-25: "sponsor leads is not active and should be disabled.
        // currently it resides inside zoho backstage and maybe it will be supported in ceh").
        // Only OnPostSyncNowAsync used to honour the 'sponsor-leads' switch, so with the
        // feature OFF this page still rendered the whole machine — pipeline status,
        // per-sponsor API keys, download samples, notification prefs — which reads as a
        // live CEH lead pipeline that does not exist. The GET is now gated too: when the
        // feature is off the page says where leads actually live and loads nothing.
        // Turning the switch on in Settings restores the page unchanged.
        FeatureDisabled = !await _gate.IsFeatureEnabledAsync("sponsor-leads", me.EventId, ct);
        if (FeatureDisabled) return Page();

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
        // Free-text search over the lead's name, email + sponsor company id —
        // applied in the database so the grid never loads the whole edition.
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            gridQ = gridQ.Where(l => l.FullName.Contains(s)
                                     || (l.Email != null && l.Email.Contains(s))
                                     || l.SponsorCompanyId.Contains(s));
        }

        var matched = await gridQ.CountAsync(ct);
        Paging = GridPaging.Resolve(PageNo, GridPaging.DefaultPageSize, matched);

        // Stable, deterministic ordering (Id tiebreak) for the chosen column.
        var sorted = (Sort, Desc) switch
        {
            ("name", false)    => gridQ.OrderBy(l => l.FullName).ThenBy(l => l.Id),
            ("name", true)     => gridQ.OrderByDescending(l => l.FullName).ThenByDescending(l => l.Id),
            ("company", false) => gridQ.OrderBy(l => l.SponsorCompanyId).ThenBy(l => l.Id),
            ("company", true)  => gridQ.OrderByDescending(l => l.SponsorCompanyId).ThenByDescending(l => l.Id),
            ("status", false)  => gridQ.OrderBy(l => l.Status).ThenBy(l => l.Id),
            ("status", true)   => gridQ.OrderByDescending(l => l.Status).ThenByDescending(l => l.Id),
            ("captured", false) => gridQ.OrderBy(l => l.CapturedAt).ThenBy(l => l.Id),
            _                  => gridQ.OrderByDescending(l => l.CapturedAt).ThenByDescending(l => l.Id),
        };

        Leads = await sorted
            .Skip(Paging.Skip).Take(Paging.PageSize)
            .ToListAsync(ct);

        // §234: resolve every company id the page will render (key rows, prefs,
        // filter dropdown + the leads on this page) to a display name.
        var allCompanyIds = new HashSet<string>(sponsorCompanyIds, StringComparer.OrdinalIgnoreCase);
        foreach (var l in Leads)
            if (!string.IsNullOrWhiteSpace(l.SponsorCompanyId)) allCompanyIds.Add(l.SponsorCompanyId);
        CompanyNames = await ResolveCompanyNamesAsync(me.EventId, allCompanyIds, ct);

        return Page();
    }

    /// <summary>
    /// Map each sponsor company id to its display name: Company Manager (public →
    /// legal, the canonical <see cref="SponsorCompanyName"/> chain) when configured,
    /// with the DB-local SponsorUploadLocation company name as fallback. Resilient —
    /// a failed lookup just leaves the id out of the map so the caller falls back to
    /// "Company {id}" instead of 500-ing the page (same pattern as the sponsor
    /// dashboard).
    /// </summary>
    /// §443 (operator 2026-07-27): resolved from CEH SQL in ONE query — see
    /// <c>SponsorCompanyNameService</c>. This page already read the local
    /// <c>SponsorUploadLocation</c> name, but only as a FALLBACK behind a live Company Manager
    /// call per company. The local copy is the same value, synced by
    /// <c>SponsorOrderPullService</c> through the same chain, so it is now the only source and the
    /// admin interface never reaches the WordPress plugin on the request path.
    private Task<Dictionary<string, string>> ResolveCompanyNamesAsync(
        int eventId, IEnumerable<string> companyIds, CancellationToken ct) =>
        SponsorCompanyNameService.ResolveFromLocalAsync(_db, eventId, companyIds, ct);

    public async Task<IActionResult> OnPostIssueKeyAsync(string sponsorCompanyId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (await LeadsDisabledAsync(me.EventId, ct)) return RedirectToPage();

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
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (await LeadsDisabledAsync(me.EventId, ct)) return RedirectToPage();

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
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (await LeadsDisabledAsync(me.EventId, ct)) return RedirectToPage();

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
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

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
        if (await LeadsDisabledAsync(me.EventId, ct)) return RedirectToPage();

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
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (await LeadsDisabledAsync(me.EventId, ct)) return RedirectToPage(GridRoute);

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
        return RedirectToPage(GridRoute);
    }

    /// <summary>Send a reply to the lead's email + record the audit on the row.</summary>
    public async Task<IActionResult> OnPostReplyLeadAsync(
        int leadId, string subject, string body, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var lead = await _db.SponsorLeads.FirstOrDefaultAsync(
            l => l.Id == leadId && l.EventId == me.EventId, ct);
        if (lead is null || string.IsNullOrWhiteSpace(lead.Email))
        {
            TempData["Notice"] = lead is null
                ? $"Lead #{leadId} not found."
                : $"Lead '{lead.FullName}' has no email address to reply to.";
            return RedirectToPage(GridRoute);
        }
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
        {
            TempData["Notice"] = "Reply needs both a subject and a message.";
            return RedirectToPage(GridRoute);
        }

        // Plain text -> encoded paragraphs (same convention as Broadcast).
        var paragraphs = System.Text.RegularExpressions.Regex
            .Split(body.Replace("\r\n", "\n").Trim(), "\n{2,}")
            .Select(p => "<p style=\"margin:0 0 16px;\">"
                + System.Net.WebUtility.HtmlEncode(p).Replace("\n", "<br>") + "</p>");
        var html = string.Concat(paragraphs);

        try
        {
            // 🔒 §707.7 — RING-EXEMPT, and this one FIXES A SILENT DROP rather than merely
            // documenting intent. A sponsor LEAD is a booth visitor, not a Participant, so the
            // ring gate's unknown-recipient rule (fail-closed) was discarding this reply before it
            // ever reached the transport — the organizer saw "reply sent" and the lead received
            // nothing. It is an organizer-initiated 1:1 reply to someone who asked to be contacted;
            // it has no rollout audience and must never be ring-gated.
            using (_emailContext?.Set(new CommunityHub.Core.Email.EmailContext(
                "sponsor-lead-reply", RingExempt: true)))
            {
                await _emailSender.SendAsync(lead.Email, subject.Trim(), html, ct);
            }
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
        return RedirectToPage(GridRoute);
    }

    /// <summary>Fire the Zoho CRM pull on demand (config-gated).</summary>
    public async Task<IActionResult> OnPostSyncNowAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        // GATE (REQUIREMENTS §23): the manual CRM pull honours the SAME per-edition
        // 'sponsor-leads' switch as the scheduled SponsorLeadsJob, so GUI state ==
        // actual behaviour. Disabled ⇒ no-op with a clear "feature disabled" notice.
        if (!await _gate.IsFeatureEnabledAsync("sponsor-leads", me.EventId, ct))
        {
            TempData["Notice"] = "The sponsor leads pipeline is turned off for this event. "
                + "Enable it in Settings to pull leads.";
            return RedirectToPage();
        }

        var result = await _sync.SyncAsync(me.EventId, ct);
        TempData["Notice"] = result.Message;
        return RedirectToPage();
    }
}
