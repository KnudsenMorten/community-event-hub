using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// The sponsor's company-shared task list. Companion page to
/// /Sponsor (which shows the company / contacts / orders details).
/// Tasks are company-scoped per docx "Sponsors to-do" -- any contact
/// of the company may complete or reopen any task.
/// </summary>
[Authorize]
public class TasksModel : PageModel
{
    // Char limits, mirrored on the form so the textarea maxlength enforces
    // them client-side too.
    public const int MaxDesc   = 1000;
    public const int MaxShort  = 80;
    public const int MaxSocial = 600;

    // Notification recipients when a sponsor saves Upload Company information.
    private static readonly string[] CompanyInfoNotifyTo =
        new[] { "info@expertslive.dk", "mlh@expertslive.dk" };

    // Task SourceKey suffix that identifies the "Upload Company information"
    // task. Must stay in sync with the title slug -- both shown on /Sponsor/Tasks
    // and used to scope the inline form submission.
    public const string CompanyInfoSlug = "upload-company-information";

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;
    private readonly IEmailSender _email;
    private readonly EmailOptions _emailOptions;
    private readonly ILogger<TasksModel> _log;

    public TasksModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock,
        IEmailSender email,
        IOptions<EmailOptions> emailOptions,
        ILogger<TasksModel> log)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
        _email = email;
        _emailOptions = emailOptions.Value;
        _log = log;
    }

    public List<ParticipantTask> SponsorTasks { get; private set; } = new();
    public List<Participant> LinkedContacts { get; private set; } = new();
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    // Current company-info values, prefilled into the inline form.
    public string? CompanyDescriptionCurrent      { get; private set; }
    public string? CompanyDescriptionShortCurrent { get; private set; }
    public string? SocialMediaIntroCurrent        { get; private set; }

    // Bound from the inline Upload Company information form.
    [BindProperty] public string? CompanyDescription      { get; set; }
    [BindProperty] public string? CompanyDescriptionShort { get; set; }
    [BindProperty] public string? SocialMediaIntro        { get; set; }

    /// <summary>True when this sponsor has no company id set (see the view).</summary>
    public bool NoCompanyLink { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        await LoadAsync(me, ct);
        return Page();
    }

    /// <summary>Mark one of this company's sponsor tasks done, or reopen it.</summary>
    public async Task<IActionResult> OnPostToggleAsync(int taskId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (companyId is null)
        {
            NoCompanyLink = true;
            await LoadAsync(me, ct);
            return Page();
        }

        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.Id == taskId
                 && t.EventId == me.EventId
                 && t.SourceKey != null
                 && t.SourceKey.StartsWith("sponsor:")
                 && t.SponsorCompanyId == companyId,
            ct);

        if (task is not null)
        {
            if (task.State == TaskState.Done)
            {
                task.State = TaskState.Open;
                task.CompletedAt = null;
                Message = "Task reopened.";
            }
            else
            {
                task.State = TaskState.Done;
                task.CompletedAt = _clock.GetUtcNow();
                Message = "Task marked complete.";
            }
            await _db.SaveChangesAsync(ct);
        }

        await LoadAsync(me, ct);
        return Page();
    }

    private async Task LoadAsync(CurrentParticipant me, CancellationToken ct)
    {
        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (companyId is null)
        {
            NoCompanyLink = true;
            SponsorTasks = new List<ParticipantTask>();
            return;
        }

        SponsorTasks = await _db.Tasks
            .Where(t => t.EventId == me.EventId
                        && t.SourceKey != null
                        && t.SourceKey.StartsWith("sponsor:")
                        && t.SponsorCompanyId == companyId)
            .OrderBy(t => t.State)
            .ThenBy(t => t.DueDate)
            .ToListAsync(ct);

        LinkedContacts = await _db.Participants
            .Where(p => p.EventId == me.EventId
                        && p.SponsorCompanyId == companyId
                        && p.Role == ParticipantRole.Sponsor
                        && p.IsActive)
            .OrderBy(p => p.FullName)
            .ToListAsync(ct);

        // Prefill the inline Upload Company information form with the
        // company's current saved values (so the sponsor sees their last
        // input rather than blank textareas).
        var info = await _db.SponsorInfos.FirstOrDefaultAsync(
            s => s.EventId == me.EventId && s.SponsorCompanyId == companyId, ct);
        if (info is not null)
        {
            CompanyDescriptionCurrent      = info.CompanyDescription;
            CompanyDescriptionShortCurrent = info.CompanyDescriptionShort;
            SocialMediaIntroCurrent        = info.SocialMediaIntro;
            CompanyDescription      ??= info.CompanyDescription;
            CompanyDescriptionShort ??= info.CompanyDescriptionShort;
            SocialMediaIntro        ??= info.SocialMediaIntro;
        }
    }

    /// <summary>
    /// Inline submit on the "Upload Company information" task. Upserts the
    /// SponsorInfo row, marks the matching task Done, emails the configured
    /// recipients (info + mlh), and logs the intended Zoho Backstage push --
    /// the actual PATCH to the exhibitor profile endpoint is a TODO pending
    /// confirmation of the v3 update endpoint + UPDATE scope on the OAuth
    /// token (see SponsorInfo update plan).
    /// </summary>
    public async Task<IActionResult> OnPostUploadCompanyInfoAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (companyId is null)
        {
            NoCompanyLink = true;
            await LoadAsync(me, ct);
            return Page();
        }

        if ((CompanyDescription ?? "").Length > MaxDesc)
        { Error = $"Company description must be {MaxDesc} chars max."; await LoadAsync(me, ct); return Page(); }
        if ((CompanyDescriptionShort ?? "").Length > MaxShort)
        { Error = $"Short description must be {MaxShort} chars max."; await LoadAsync(me, ct); return Page(); }
        if ((SocialMediaIntro ?? "").Length > MaxSocial)
        { Error = $"Social-media intro must be {MaxSocial} chars max."; await LoadAsync(me, ct); return Page(); }

        var info = await _db.SponsorInfos.FirstOrDefaultAsync(
            s => s.EventId == me.EventId && s.SponsorCompanyId == companyId, ct);
        if (info is null)
        {
            info = new SponsorInfo
            {
                EventId          = me.EventId,
                SponsorCompanyId = companyId!,
                CreatedAt        = _clock.GetUtcNow(),
            };
            _db.SponsorInfos.Add(info);
        }
        else
        {
            info.UpdatedAt = _clock.GetUtcNow();
        }

        info.CompanyDescription      = NormaliseOrNull(CompanyDescription);
        info.CompanyDescriptionShort = NormaliseOrNull(CompanyDescriptionShort);
        info.SocialMediaIntro        = NormaliseOrNull(SocialMediaIntro);
        info.LastUpdatedByEmail      = me.Email;

        // Mark the "Upload Company information" task Done for this company.
        // Identified by SourceKey suffix so a future title tweak does not
        // silently break the auto-complete.
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == me.EventId
                 && t.SponsorCompanyId == companyId
                 && t.SourceKey != null
                 && t.SourceKey.EndsWith(":" + CompanyInfoSlug), ct);
        if (task is not null && task.State != TaskState.Done)
        {
            task.State = TaskState.Done;
            task.CompletedAt = _clock.GetUtcNow();
        }

        await _db.SaveChangesAsync(ct);

        // Notify the organizer team. One mail per recipient so the test-mode
        // redirect annotates each correctly.
        try
        {
            var companyName = await ResolveCompanyDisplayNameAsync(me.EventId, companyId!, ct);
            var subject = $"[ELDK Event Hub] Sponsor company info submitted: {companyName}";
            var body    = BuildCompanyInfoEmailBody(companyName, info, me.Email);
            foreach (var to in CompanyInfoNotifyTo)
            {
                await _email.SendAsync(to, subject, body, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Tasks: failed to notify recipients of company-info submission for {Co}.",
                companyId);
        }

        // The Zoho Backstage exhibitor profile is NOT auto-updated from here.
        // IBackstageExhibitorApi only exposes Exists/Create of an exhibitor
        // *request* -- there is no update/PATCH contract for the
        // company_overview / company_short_description fields, and the
        // Backstage v3 update endpoint + zohobackstage.exhibitor.UPDATE scope
        // are not wired. Rather than fabricate a push that silently does
        // nothing, we record an explicit "not available" warning and tell the
        // sponsor plainly that the organizer mirrors the text into Backstage.
        var isExhibitor = await _db.Tasks.AnyAsync(
            t => t.EventId == me.EventId
                 && t.SponsorCompanyId == companyId
                 && t.SourceKey != null
                 && t.SourceKey.Contains(":upload-sponsor-wall-design"), ct);
        _log.LogWarning(
            "Tasks: Zoho Backstage profile auto-push is NOT available (no update "
            + "endpoint/scope wired). Company info saved + emailed for manual "
            + "entry. company={Co}, isExhibitor={Ex}, company_overview_len={OvLen}, "
            + "company_short_description_len={ShLen}.",
            companyId, isExhibitor,
            (info.CompanyDescription ?? "").Length,
            (info.CompanyDescriptionShort ?? "").Length);

        Message = "Company information saved and sent to the organizer team. "
            + "The text is not pushed to Zoho Backstage automatically yet -- the "
            + "organizers will mirror it into your Backstage exhibitor profile.";
        await LoadAsync(me, ct);
        return Page();
    }

    private async Task<string> ResolveCompanyDisplayNameAsync(int eventId, string companyId, CancellationToken ct)
    {
        // SponsorUploadLocation.CompanyName is the display name SponsorOrderPullService
        // already resolved through the canonical Company Manager chain (public name ->
        // legal name -> billing) and persisted per (event, company). Reuse that rather
        // than re-resolving here. Fall back to the raw id only when nothing resolved.
        var resolved = await _db.SponsorUploadLocations
            .Where(l => l.EventId == eventId && l.SponsorCompanyId == companyId
                        && l.CompanyName != string.Empty)
            .OrderBy(l => l.Id)
            .Select(l => l.CompanyName)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(resolved) ? $"Company {companyId}" : resolved;
    }

    private static string BuildCompanyInfoEmailBody(string companyName, SponsorInfo info, string submitter)
    {
        var desc      = info.CompanyDescription      ?? "(empty)";
        var descShort = info.CompanyDescriptionShort ?? "(empty)";
        var social    = info.SocialMediaIntro        ?? "(empty)";
        return $@"<p>Sponsor company <b>{System.Net.WebUtility.HtmlEncode(companyName)}</b> just saved their company information in the Event Hub.</p>
<p>Submitted by: <code>{System.Net.WebUtility.HtmlEncode(submitter)}</code></p>
<h3>Company description ({(info.CompanyDescription ?? "").Length} / 1000 chars)</h3>
<pre style=""white-space:pre-wrap;font-family:inherit;border:1px solid #ddd;padding:10px;border-radius:4px;background:#fafafa;"">{System.Net.WebUtility.HtmlEncode(desc)}</pre>
<h3>Short description ({(info.CompanyDescriptionShort ?? "").Length} / 80 chars)</h3>
<pre style=""white-space:pre-wrap;font-family:inherit;border:1px solid #ddd;padding:10px;border-radius:4px;background:#fafafa;"">{System.Net.WebUtility.HtmlEncode(descShort)}</pre>
<h3>Social-media intro ({(info.SocialMediaIntro ?? "").Length} / 600 chars)</h3>
<pre style=""white-space:pre-wrap;font-family:inherit;border:1px solid #ddd;padding:10px;border-radius:4px;background:#fafafa;"">{System.Net.WebUtility.HtmlEncode(social)}</pre>
<p>The hub will mirror these into the Zoho Backstage exhibitor profile once the API push is wired up. Until then, copy-paste them into Backstage manually.</p>";
    }

    private static string? NormaliseOrNull(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task<string?> GetCompanyIdAsync(int participantId, CancellationToken ct) =>
        await _db.Participants
            .Where(p => p.Id == participantId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Generate an .ics calendar reminder for one sponsor task. Sponsors
    /// download + open in Outlook / Apple Calendar / Google Calendar /
    /// whatever they use. The reminder fires at 09:00 local on the due date
    /// (no time-of-day on tasks, so 09:00 is the practical default) and runs
    /// until 09:30. ALARM trigger is -P1D so the sponsor's calendar reminds
    /// them one day before.
    /// </summary>
    public async Task<IActionResult> OnGetIcsAsync(int taskId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (companyId is null) return NotFound();

        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.Id == taskId
                 && t.EventId == me.EventId
                 && t.SourceKey != null
                 && t.SourceKey.StartsWith("sponsor:")
                 && t.SponsorCompanyId == companyId, ct);
        if (task is null) return NotFound();

        var due = task.DueDate ?? DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(7));
        var start = due.ToDateTime(new TimeOnly(9, 0));
        var end   = due.ToDateTime(new TimeOnly(9, 30));

        var uid = $"sponsor-task-{task.Id}@eventhub.expertslive.dk";
        var now = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var startFloat = start.ToString("yyyyMMdd'T'HHmmss");
        var endFloat   = end.ToString("yyyyMMdd'T'HHmmss");
        var summary = $"[ELDK27] {EscapeIcs(task.Title)}";
        var desc    = EscapeIcs(task.Description ?? string.Empty);

        var ics = new StringBuilder();
        ics.AppendLine("BEGIN:VCALENDAR");
        ics.AppendLine("VERSION:2.0");
        ics.AppendLine("PRODID:-//Experts Live Denmark//Event Hub//EN");
        ics.AppendLine("CALSCALE:GREGORIAN");
        ics.AppendLine("METHOD:PUBLISH");
        ics.AppendLine("BEGIN:VEVENT");
        ics.AppendLine($"UID:{uid}");
        ics.AppendLine($"DTSTAMP:{now}");
        ics.AppendLine($"DTSTART;TZID=Europe/Copenhagen:{startFloat}");
        ics.AppendLine($"DTEND;TZID=Europe/Copenhagen:{endFloat}");
        ics.AppendLine($"SUMMARY:{summary}");
        ics.AppendLine($"DESCRIPTION:{desc}");
        ics.AppendLine("BEGIN:VALARM");
        ics.AppendLine("TRIGGER:-P1D");
        ics.AppendLine("ACTION:DISPLAY");
        ics.AppendLine($"DESCRIPTION:Reminder: {summary}");
        ics.AppendLine("END:VALARM");
        ics.AppendLine("END:VEVENT");
        ics.AppendLine("END:VCALENDAR");

        var bytes = Encoding.UTF8.GetBytes(ics.ToString());
        var safeName = string.Concat(task.Title.Where(char.IsLetterOrDigit));
        if (safeName.Length > 50) safeName = safeName[..50];
        return File(bytes, "text/calendar; charset=utf-8", $"eldk27-{safeName}.ics");
    }

    private static string EscapeIcs(string s) =>
        s.Replace("\\", "\\\\")
         .Replace(",",  "\\,")
         .Replace(";",  "\\;")
         .Replace("\r\n", "\\n")
         .Replace("\n", "\\n");
}
