using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Sponsors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// The sponsor's company-shared task list. Companion page to
/// /Sponsor (which shows the company / contacts / orders details).
/// Tasks are company-scoped per docx "Sponsors to-do" -- any contact
/// of the company may complete or reopen any task.
///
/// Company INFO collection (description / short / social) moved out of this page
/// to the dedicated /Sponsor/CompanyDetails page (operator 2026-06-24); this page
/// is now purely list + toggle + per-task .ics.
/// </summary>
[Authorize]
public class TasksModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;
    private readonly SponsorDeliverablesService _deliverables;

    public TasksModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock,
        SponsorDeliverablesService deliverables)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
        _deliverables = deliverables;
    }

    public List<ParticipantTask> SponsorTasks { get; private set; } = new();
    public List<Participant> LinkedContacts { get; private set; } = new();
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    /// <summary>True when this sponsor has no company id set (see the view).</summary>
    public bool NoCompanyLink { get; private set; }

    /// <summary>
    /// §135 (operator 2026-06-27): the company's deliverables rollup (X of N done, % + the
    /// still-missing/overdue items with deep links), surfaced at the TOP of My Tasks now that
    /// the standalone /Sponsor/Deliverables nav item is removed (mirrors the speaker §138
    /// readiness move). A pure read-only AGGREGATE of existing data via
    /// <see cref="SponsorDeliverablesService"/>; null when there is no company link or the
    /// rollup could not be built (the view then omits the card).
    /// </summary>
    public SponsorDeliverables? Deliverables { get; private set; }

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

        // §135: build the deliverables rollup for the top of the page. Read-only AGGREGATE;
        // tolerate a build hiccup so the task list still renders (the view omits the card
        // when Deliverables is null).
        try
        {
            var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
            Deliverables = await _deliverables.BuildForCompanyAsync(me.EventId, companyId, today, ct: ct);
        }
        catch { Deliverables = null; }

        LinkedContacts = await _db.Participants
            .Where(p => p.EventId == me.EventId
                        && p.SponsorCompanyId == companyId
                        && p.Role == ParticipantRole.Sponsor
                        && p.IsActive)
            .OrderBy(p => p.FullName)
            .ToListAsync(ct);
    }

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
        // Strip the **bold**/__underline__/[label](url) markup to plain text so the
        // markers don't leak literally into the calendar entry.
        var desc    = EscapeIcs(CommunityHub.Core.Email.TaskMarkup.ToPlainText(task.Description));

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
        // Serve INLINE (no filename ⇒ no attachment Content-Disposition) so the
        // browser hands the .ics to the OS calendar app to OPEN (easy "Save & Close")
        // rather than silently downloading it.
        return File(bytes, "text/calendar; charset=utf-8");
    }

    private static string EscapeIcs(string s) =>
        s.Replace("\\", "\\\\")
         .Replace(",",  "\\,")
         .Replace(";",  "\\;")
         .Replace("\r\n", "\\n")
         .Replace("\n", "\\n");
}
