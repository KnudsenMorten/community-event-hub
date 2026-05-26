using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

[Authorize]
public class SpeakerRemindersModel : PageModel
{
    private const string ReminderType = "task-reminder";

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly IEmailSender _emailSender;
    private readonly TimeProvider _clock;

    public SpeakerRemindersModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        IEmailSender emailSender,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _emailSender = emailSender;
        _clock = clock;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }

    /// <summary>"open" (default) | "all" | "overdue"</summary>
    [BindProperty(SupportsGet = true)] public string Filter { get; set; } = "open";

    public List<Row> Rows { get; private set; } = new();
    public DateOnly Today { get; private set; }

    public record Row(
        int TaskId,
        int ParticipantId,
        string SpeakerName,
        string SpeakerEmail,
        string Role,
        string TaskTitle,
        DateOnly? DueDate,
        int? DaysOverdue,
        TaskState State,
        DateTimeOffset? LastRemindedAt,
        int RemindersSent);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadRowsAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSendAsync(int taskId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var task = await _db.Tasks
            .Where(t => t.Id == taskId && t.EventId == me.EventId)
            .Select(t => new
            {
                t.Id,
                t.Title,
                t.Description,
                t.DueDate,
                t.State,
                t.AssignedParticipantId,
            })
            .FirstOrDefaultAsync(ct);
        if (task is null || task.AssignedParticipantId is null)
        {
            Message = "Task not found or has no assignee.";
            await LoadRowsAsync(me.EventId, ct);
            return Page();
        }

        var p = await _db.Participants
            .Where(x => x.Id == task.AssignedParticipantId && x.IsActive)
            .Select(x => new { x.Id, x.Email, x.FullName })
            .FirstOrDefaultAsync(ct);
        if (p is null)
        {
            Message = "Participant not found or is inactive.";
            await LoadRowsAsync(me.EventId, ct);
            return Page();
        }

        var eventCode = await _db.Events
            .Where(e => e.Id == me.EventId)
            .Select(e => e.Code)
            .FirstOrDefaultAsync(ct) ?? "Event Hub";

        var html = BuildReminderHtml(p.FullName, task.Title, task.Description, task.DueDate, eventCode);
        var subject = $"{eventCode} Event Hub - reminder: {task.Title}";
        try
        {
            await _emailSender.SendAsync(p.Email, subject, html, ct);
            _db.SentReminders.Add(new SentReminder
            {
                EventId = me.EventId,
                RecipientEmail = p.Email,
                ReminderType = ReminderType,
                OccasionKey = $"manual:task-{task.Id}:{_clock.GetUtcNow().UtcDateTime:yyyyMMddHHmmss}",
                SentAt = _clock.GetUtcNow(),
            });
            await _db.SaveChangesAsync(ct);
            Message = $"Reminder sent to {p.Email}.";
        }
        catch (Exception ex)
        {
            Message = $"Could not send reminder to {p.Email}: {ex.Message}";
        }

        await LoadRowsAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadRowsAsync(int eventId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        Today = today;

        // Only speaker-role assignees -- the user asked specifically about speakers.
        var tasks = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.AssignedParticipantId != null
                        && (t.AssignedParticipant!.Role == ParticipantRole.Speaker
                            || t.AssignedParticipant!.Role == ParticipantRole.MasterclassSpeaker))
            .Select(t => new
            {
                TaskId = t.Id,
                t.Title,
                t.DueDate,
                t.State,
                ParticipantId = t.AssignedParticipantId!.Value,
                SpeakerName = t.AssignedParticipant!.FullName,
                SpeakerEmail = t.AssignedParticipant!.Email,
                Role = t.AssignedParticipant!.Role.ToString(),
            })
            .ToListAsync(ct);

        // Map task-reminder history (one row per send, latest first).
        var emails = tasks.Select(t => t.SpeakerEmail).Distinct().ToList();
        var reminders = await _db.SentReminders
            .Where(s => s.EventId == eventId
                        && s.ReminderType == ReminderType
                        && emails.Contains(s.RecipientEmail))
            .ToListAsync(ct);

        Rows = tasks.Select(t =>
        {
            int? daysOverdue = (t.DueDate is not null && t.DueDate < today && t.State != TaskState.Done)
                ? today.DayNumber - t.DueDate.Value.DayNumber
                : (int?)null;

            // Match reminders on (recipient + taskId-in-OccasionKey).
            var marker = $"task-{t.TaskId}";
            var hist = reminders
                .Where(r => string.Equals(r.RecipientEmail, t.SpeakerEmail, StringComparison.OrdinalIgnoreCase)
                            && r.OccasionKey.Contains(marker))
                .OrderByDescending(r => r.SentAt)
                .ToList();

            return new Row(
                t.TaskId, t.ParticipantId,
                t.SpeakerName, t.SpeakerEmail, t.Role,
                t.Title, t.DueDate, daysOverdue, t.State,
                hist.FirstOrDefault()?.SentAt,
                hist.Count);
        }).ToList();

        Rows = Filter switch
        {
            "overdue" => Rows.Where(r => r.DaysOverdue is > 0).ToList(),
            "all"     => Rows,
            _ /* open */ => Rows.Where(r => r.State != TaskState.Done).ToList(),
        };

        Rows = Rows
            .OrderBy(r => r.State == TaskState.Done ? 1 : 0)
            .ThenByDescending(r => r.DaysOverdue ?? -1)
            .ThenBy(r => r.DueDate)
            .ThenBy(r => r.SpeakerName)
            .ToList();
    }

    private static string BuildReminderHtml(
        string fullName, string taskTitle, string? description, DateOnly? due, string eventCode)
    {
        var firstName = string.IsNullOrWhiteSpace(fullName) ? "there" : fullName.Split(' ')[0];
        var dueText = due is null
            ? "no fixed due date"
            : $"due <strong>{due:yyyy-MM-dd}</strong>";
        var encDesc = string.IsNullOrWhiteSpace(description)
            ? string.Empty
            : $"<p>{System.Net.WebUtility.HtmlEncode(description)}</p>";

        return $@"<p>Hi {System.Net.WebUtility.HtmlEncode(firstName)},</p>
<p>Quick reminder from the {eventCode} team about the task <strong>{System.Net.WebUtility.HtmlEncode(taskTitle)}</strong> ({dueText}).</p>
{encDesc}
<p>Sign in to your Event Hub to mark it Done or update progress.</p>
<p>Cheers,<br/>ELDK-team</p>";
    }
}
