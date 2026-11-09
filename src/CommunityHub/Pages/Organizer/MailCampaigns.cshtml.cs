using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1381 — MAIL CAMPAIGNS: create a mailing to a chosen audience, preview it, test it on yourself, approve the recipient
/// list, schedule it, and read its opens/clicks. The §1080 backend existed with no page; this is that page.
/// </summary>
/// <remarks>
/// <para>🔑 <b>The operator's rollout per mail is the mail's OWN ring</b> (Email templates page): ring 0 (him) → 1
/// (organizers) → 2 (design partners) → 3 (broad). Previous attendees are ring 3 at the transport (campaign mail only),
/// so a campaign to them delivers nothing until that mail's ring is Broad. This page shows the current ring and links
/// to where it is raised, rather than growing a second ring control that could disagree with the first.</para>
/// <para>🔒 Sending needs THREE things, all visible here: the recipient list approved (dry-run acknowledgement), the
/// <c>mail-campaigns</c> switch ON (default off), and the mail's ring wide enough for the audience.</para>
/// </remarks>
[Authorize]
public class MailCampaignsModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly MailCampaignService _campaigns;
    private readonly ExternalRecipientImportService _import;
    private readonly EmailTemplateRingService _rings;
    private readonly FeatureGateService _gate;
    private readonly BrevoStatsClient _stats;
    private readonly TimeProvider _clock;

    public MailCampaignsModel(CommunityHubDbContext db, ICurrentParticipantAccessor participant, MailCampaignService campaigns,
        ExternalRecipientImportService import, EmailTemplateRingService rings, FeatureGateService gate,
        BrevoStatsClient stats, TimeProvider? clock = null)
    {
        _db = db; _participant = participant; _campaigns = campaigns; _import = import;
        _rings = rings; _gate = gate; _stats = stats; _clock = clock ?? TimeProvider.System;
    }

    public static readonly TimeZoneInfo Danish = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Romance Standard Time" : "Europe/Copenhagen");

    public bool AccessDenied { get; private set; }
    [TempData] public string? Message { get; set; }
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }

    public List<MailCampaign> Campaigns { get; private set; } = new();
    public MailCampaign? Current { get; private set; }
    public MailCampaignPreview? Preview { get; private set; }
    public MailCampaignGate? GateResult { get; private set; }
    public (string Subject, string Html)? Rendered { get; private set; }
    public Ring MailRing { get; private set; }
    public bool ReleasedToEveryone { get; private set; }
    public bool SwitchOn { get; private set; }
    public BrevoStatsResult? Stats { get; private set; }
    public int ImportedCount { get; private set; }
    public List<(string Edition, int Count)> ImportedByEdition { get; private set; } = new();
    public int SuppressedCount { get; private set; }

    /// <summary>§1381 — per campaign: recipients skipped because they left the audience after the start (bought a ticket).</summary>
    public Dictionary<int, int> LeftAudienceCounts { get; private set; } = new();

    // ---- create / edit form ----
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? TemplateKey { get; set; }
    [BindProperty] public MailAudience Audience { get; set; } = MailAudience.PreviousAttendeesNotThisEdition;
    [BindProperty] public DateTime? ScheduledLocal { get; set; }

    public static IReadOnlyList<string> TemplateChoices => EmailTemplateCatalog.CampaignTemplateKeys;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!me.Role.HasOrganizerAccess()) { AccessDenied = true; return Page(); }
        await LoadAsync(me.EventId, me.Email, me.FullName, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, string myEmail, string? myName, CancellationToken ct)
    {
        Campaigns = await _db.MailCampaigns.Where(c => c.EventId == eventId)
            .OrderBy(c => c.ScheduledFor ?? DateTimeOffset.MaxValue).ThenBy(c => c.Id).ToListAsync(ct);
        SwitchOn = await _gate.IsFeatureEnabledAsync(MailCampaignFeature.Key, eventId, ct);
        ImportedByEdition = (await _db.ExternalRecipients.Where(r => r.EventId == eventId)
                .GroupBy(r => r.SourceEdition).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
            .Select(x => (x.Key ?? "(no edition)", x.N)).OrderBy(x => x.Item1).ToList();
        ImportedCount = ImportedByEdition.Sum(x => x.Count);
        SuppressedCount = await _db.MailSuppressions.CountAsync(s => s.EventId == eventId, ct);
        var ids = Campaigns.Select(c => c.Id).ToList();
        LeftAudienceCounts = await _db.MailCampaignRecipients
            .Where(r => ids.Contains(r.MailCampaignId) && r.State == MailCampaignRecipientState.LeftAudience)
            .GroupBy(r => r.MailCampaignId).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N, ct);

        Current = Id is int id ? Campaigns.FirstOrDefault(c => c.Id == id) : null;
        if (Current is null) return;

        Preview = await _campaigns.PreviewAsync(Current, 8, ct);
        GateResult = _campaigns.Gate(Current);
        MailRing = await _rings.GetEffectiveRingAsync(eventId, Current.TemplateKey, ct);
        ReleasedToEveryone = await _campaigns.ReleasedToEveryoneAsync(Current, ct);
        Rendered = await _campaigns.RenderAsync(Current, myEmail, myName, ct);
        if (Current.StartedAt is { } started)
        {
            var from = DateOnly.FromDateTime(started.UtcDateTime);
            Stats = await _stats.ForTagAsync(MailCampaignService.TagFor(Current), from, DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime), ct);
        }
    }

    private CurrentParticipant? Organizer() =>
        _participant.Current is { } me && OrganizerAuth.IsRealOrganizer(me) ? me : null;

    private async Task<MailCampaign?> FindAsync(int eventId, CancellationToken ct) =>
        Id is int id ? await _db.MailCampaigns.FirstOrDefaultAsync(c => c.Id == id && c.EventId == eventId, ct) : null;

    private DateTimeOffset? ToUtc(DateTime? local) =>
        local is { } l ? new DateTimeOffset(DateTime.SpecifyKind(l, DateTimeKind.Unspecified), Danish.GetUtcOffset(l)).ToUniversalTime() : null;

    [CommunityHub.Audit.Audit("Created a mail campaign", TargetType = "MailCampaign")]
    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        if (Organizer() is not { } me) return Forbid();
        if (string.IsNullOrWhiteSpace(Name) || TemplateKey is null || !TemplateChoices.Contains(TemplateKey))
        { Message = "Give the campaign a name and pick one of the campaign mails."; return RedirectToPage(); }

        var c = new MailCampaign
        {
            EventId = me.EventId, Name = Name.Trim(), TemplateKey = TemplateKey, Audience = Audience,
            ScheduledFor = ToUtc(ScheduledLocal), State = MailCampaignState.Draft,
            // 🔒 §1080b — required for (and only for) the audience that reaches outside the hub.
            IncludeUnsubscribeLink = MailAudienceResolver.ReachesOutsideTheHub(Audience),
            CreatedAt = _clock.GetUtcNow(), CreatedByEmail = me.Email,
        };
        _db.MailCampaigns.Add(c);
        await _db.SaveChangesAsync(ct);
        Message = "Campaign created as a draft. Check the preview, send a test to yourself, then approve the recipient list.";
        return RedirectToPage(new { id = c.Id });
    }

    [CommunityHub.Audit.Audit("Approved a mail campaign's recipient list", TargetType = "MailCampaign")]
    public async Task<IActionResult> OnPostApproveAsync(CancellationToken ct)
    {
        if (Organizer() is not { } me) return Forbid();
        if (await FindAsync(me.EventId, ct) is not { } c) return NotFound();
        var preview = await _campaigns.PreviewAsync(c, 0, ct);
        await _campaigns.AcknowledgeAsync(c, me.Email, preview.Total - preview.Suppressed, ct);
        Message = $"Recipient list approved: {preview.Total - preview.Suppressed} recipient(s).";
        return RedirectToPage(new { id = c.Id });
    }

    [CommunityHub.Audit.Audit("Scheduled a mail campaign", TargetType = "MailCampaign")]
    public async Task<IActionResult> OnPostScheduleAsync(CancellationToken ct)
    {
        if (Organizer() is not { } me) return Forbid();
        if (await FindAsync(me.EventId, ct) is not { } c) return NotFound();
        var when = ToUtc(ScheduledLocal) ?? c.ScheduledFor;
        if (when is null) { Message = "Pick a date and time first."; return RedirectToPage(new { id = c.Id }); }
        if (c.State is MailCampaignState.Sending or MailCampaignState.Sent)
        { Message = "This campaign has already started — it cannot be rescheduled."; return RedirectToPage(new { id = c.Id }); }
        var gate = _campaigns.Gate(c);
        if (!gate.MaySend) { Message = "Not scheduled: " + gate.Reason; return RedirectToPage(new { id = c.Id }); }
        c.ScheduledFor = when; c.State = MailCampaignState.Scheduled; c.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        Message = $"Scheduled for {TimeZoneInfo.ConvertTime(when.Value, Danish):ddd d MMM yyyy HH:mm} (Danish time).";
        return RedirectToPage(new { id = c.Id });
    }

    [CommunityHub.Audit.Audit("Unscheduled a mail campaign", TargetType = "MailCampaign")]
    public async Task<IActionResult> OnPostUnscheduleAsync(CancellationToken ct)
    {
        if (Organizer() is not { } me) return Forbid();
        if (await FindAsync(me.EventId, ct) is not { } c) return NotFound();
        if (c.State == MailCampaignState.Scheduled) { c.State = MailCampaignState.Draft; c.UpdatedAt = _clock.GetUtcNow(); await _db.SaveChangesAsync(ct); }
        Message = "Back to draft — nothing will be sent.";
        return RedirectToPage(new { id = c.Id });
    }

    [CommunityHub.Audit.Audit("Sent a mail campaign test to self", TargetType = "MailCampaign")]
    public async Task<IActionResult> OnPostTestAsync(CancellationToken ct)
    {
        if (Organizer() is not { } me) return Forbid();
        if (await FindAsync(me.EventId, ct) is not { } c) return NotFound();
        await _campaigns.SendTestAsync(c, me.Email, me.FullName, ct);
        Message = $"Test sent to {me.Email} (subject starts with [TEST]).";
        return RedirectToPage(new { id = c.Id });
    }

    // ---- §1388 — the ring-by-ring release plan ---------------------------------------------------------------------
    [BindProperty] public DateTime? PlanPreviewLocal { get; set; }
    [BindProperty] public DateTime? PlanWaveLocal { get; set; }
    [BindProperty] public DateTime? PlanEveryoneLocal { get; set; }
    [BindProperty] public int PlanWavePercent { get; set; } = 5;

    /// <summary>The default plan times: the next day at 08:00 / 09:00 / 10:00 Danish time (or the plan already set).</summary>
    public (DateTime Preview, DateTime Wave, DateTime Everyone) PlanDefaults(MailCampaign c)
    {
        if (c.PlanPreviewAt is { } a && c.PlanWaveAt is { } b && c.PlanEveryoneAt is { } e)
            return (TimeZoneInfo.ConvertTime(a, Danish).DateTime, TimeZoneInfo.ConvertTime(b, Danish).DateTime, TimeZoneInfo.ConvertTime(e, Danish).DateTime);
        var day = TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), Danish).Date.AddDays(1);
        return (day.AddHours(8), day.AddHours(9), day.AddHours(10));
    }

    /// <summary>§1388 — set the plan: ring 1 → ring 2 → everyone at the chosen times.</summary>
    [CommunityHub.Audit.Audit("Set a mail campaign release plan", TargetType = "MailCampaign")]
    public async Task<IActionResult> OnPostSetPlanAsync(CancellationToken ct)
    {
        if (Organizer() is not { } me) return Forbid();
        if (await FindAsync(me.EventId, ct) is not { } c) return NotFound();
        if (ToUtc(PlanPreviewLocal) is not { } r1 || ToUtc(PlanWaveLocal) is not { } r2 || ToUtc(PlanEveryoneLocal) is not { } all)
        { Message = "Fill in all three times."; return RedirectToPage(new { id = c.Id }); }
        var why = await _campaigns.SetPlanAsync(c, r1, r2, all, me.Email, ct, PlanWavePercent);
        Message = why is null
            ? $"Release plan set: organizers at {DanishText(r1)}, the first {PlanWavePercent}% of the audience at {DanishText(r2)}, everyone else from {DanishText(all)} (Danish time). info@ gets a line at each step; Stop halts it."
            : "Plan not set: " + why;
        return RedirectToPage(new { id = c.Id });
    }

    /// <summary>§1388 — Stop: no further step runs; a campaign not yet sending goes back to draft.</summary>
    [CommunityHub.Audit.Audit("Stopped a mail campaign release plan", TargetType = "MailCampaign")]
    public async Task<IActionResult> OnPostStopPlanAsync(CancellationToken ct)
    {
        if (Organizer() is not { } me) return Forbid();
        if (await FindAsync(me.EventId, ct) is not { } c) return NotFound();
        await _campaigns.StopPlanAsync(c, me.Email, ct);
        Message = "Release plan STOPPED — nothing more is sent and the campaign is back to draft. Mails already sent stay sent.";
        return RedirectToPage(new { id = c.Id });
    }

    /// <summary>A UTC time as Danish wall-clock text.</summary>
    public static string DanishText(DateTimeOffset? at) =>
        at is { } v ? TimeZoneInfo.ConvertTime(v, Danish).ToString("ddd d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "—";

    /// <summary>§1381 — steps 2–4: the review send to rings 0..N (never Broad — that is the campaign itself).</summary>
    [CommunityHub.Audit.Audit("Sent a mail campaign review to a ring", TargetType = "MailCampaign")]
    public async Task<IActionResult> OnPostReviewAsync(int ring, CancellationToken ct)
    {
        if (Organizer() is not { } me) return Forbid();
        if (await FindAsync(me.EventId, ct) is not { } c) return NotFound();
        if (ring is < 0 or > 2) { Message = "Review sends are ring 0, 1 or 2. Broad is the campaign itself."; return RedirectToPage(new { id = c.Id }); }
        var (asked, delivered) = await _campaigns.SendReviewAsync(c, (Ring)ring, ct);
        Message = delivered == asked
            ? $"Preview sent to {delivered} person(s) in rings 0–{ring}."
            : $"Preview: {delivered} of {asked} person(s) in rings 0–{ring} received it — the others are held back because this mail's ring is still below {ring}. Raise it on the Email templates page.";
        return RedirectToPage(new { id = c.Id });
    }

    [CommunityHub.Audit.Audit("Imported previous attendees", TargetType = "ExternalRecipient")]
    public async Task<IActionResult> OnPostImportAsync(IFormFile? file, string? edition, CancellationToken ct)
    {
        if (Organizer() is not { } me) return Forbid();
        if (file is null || file.Length == 0 || !file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        { Message = "Choose an .xlsx file."; return RedirectToPage(); }
        var label = string.IsNullOrWhiteSpace(edition) ? Path.GetFileNameWithoutExtension(file.FileName) : edition.Trim();
        await using var s = file.OpenReadStream();
        var r = await _import.ImportAsync(me.EventId, s, file.FileName, me.Email, label, ct);
        Message = $"{label}: {r.Added} added, {r.Updated} updated, {r.Skipped} skipped, {r.OptedOut} opted out (suppressed), {r.Cancelled} cancelled."
                  + (r.Problems.Count > 0 ? " " + string.Join(" ", r.Problems.Take(5)) : "");
        return RedirectToPage();
    }
}
