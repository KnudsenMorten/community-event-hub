using CommunityHub.Auth;
using CommunityHub.Core.Config;
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
    private readonly EmailTemplateProvider _templates;
    private readonly TimeProvider _clock;
    private readonly IEmailContextAccessor? _context;

    // Optional edition-config source for the per-role contact footer (this page is
    // speaker-only, so the SPEAKER contact is used). Null-safe → blank/fallback.
    private readonly EventEditionConfigLoader? _eventConfigLoader;
    private readonly EventConfigOptions? _eventConfigOptions;
    private IReadOnlyDictionary<string, string>? _placeholders;
    private string? _supportEmail;

    public SpeakerRemindersModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        IEmailSender emailSender,
        EmailTemplateProvider templates,
        TimeProvider clock,
        IEmailContextAccessor? context = null,
        EventEditionConfigLoader? eventConfigLoader = null,
        EventConfigOptions? eventConfigOptions = null)
    {
        _db = db;
        _participant = participant;
        _emailSender = emailSender;
        _templates = templates;
        _clock = clock;
        _context = context;
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
    }

    private const string DefaultSupportEmail = "info@expertslive.dk";

    private (IReadOnlyDictionary<string, string> Placeholders, string SupportEmail) ContactConfig()
    {
        if (_placeholders is not null && _supportEmail is not null)
        {
            return (_placeholders, _supportEmail);
        }

        var placeholders = (IReadOnlyDictionary<string, string>)
            new Dictionary<string, string>();
        var supportEmail = DefaultSupportEmail;

        if (_eventConfigLoader is not null)
        {
            try
            {
                var path = _eventConfigOptions?.EventConfigPath
                           ?? new EventConfigOptions().EventConfigPath;
                var cfg = _eventConfigLoader.Load(path);
                placeholders = cfg.Placeholders ?? placeholders;
                if (cfg.Placeholders is not null
                    && cfg.Placeholders.TryGetValue("supportEmail", out var se)
                    && !string.IsNullOrWhiteSpace(se))
                {
                    supportEmail = se;
                }
            }
            catch { /* fail-safe: footer falls back to the default support email */ }
        }

        _placeholders = placeholders;
        _supportEmail = supportEmail;
        return (placeholders, supportEmail);
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
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

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
            .Select(x => new
            {
                x.Id,
                x.Email,
                x.FullName,
                ContactEmailOverride = _db.SpeakerProfiles
                    .Where(sp => sp.ParticipantId == x.Id)
                    .Select(sp => sp.ContactEmailOverride)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        if (p is null)
        {
            Message = "Participant not found or is inactive.";
            await LoadRowsAsync(me.EventId, ct);
            return Page();
        }

        // ALL speaker mail goes to the effective address (override ?? Sessionize).
        var toEmail = SpeakerProfile.EffectiveEmailFor(p.Email, p.ContactEmailOverride);

        var eventCode = await _db.Events
            .Where(e => e.Id == me.EventId)
            .Select(e => e.Code)
            .FirstOrDefaultAsync(ct) ?? "Event Hub";

        var rendered = BuildReminderEmail(p.FullName, task.Title, task.Description, task.DueDate, eventCode);
        try
        {
            // Ring-governed by the reminder-jobs feature (operator 2026-06-22).
            using (_context?.Set(new EmailContext(
                ReminderType, me.EventId, p.Id, p.FullName,
                FeatureKey: "reminder-jobs")))
            {
                await _emailSender.SendAsync(toEmail, rendered.Subject, rendered.HtmlBody, ct);
            }
            _db.SentReminders.Add(new SentReminder
            {
                EventId = me.EventId,
                // Ledger keys on the IDENTITY address so reminder history stays
                // stable across override changes (LoadRowsAsync matches on it).
                RecipientEmail = p.Email,
                ReminderType = ReminderType,
                OccasionKey = $"manual:task-{task.Id}:{_clock.GetUtcNow().UtcDateTime:yyyyMMddHHmmss}",
                SentAt = _clock.GetUtcNow(),
            });
            await _db.SaveChangesAsync(ct);
            Message = $"Reminder sent to {toEmail}.";
        }
        catch (Exception ex)
        {
            Message = $"Could not send reminder to {toEmail}: {ex.Message}";
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
                        && t.AssignedParticipant!.Role == ParticipantRole.Speaker)
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

    private RenderedEmail BuildReminderEmail(
        string fullName, string taskTitle, string? description, DateOnly? due, string eventCode)
    {
        var firstName = string.IsNullOrWhiteSpace(fullName) ? "there" : fullName.Split(' ')[0];
        // dueText carries a <strong> span (raw-HTML token); descriptionBlock is
        // a sender-built HTML paragraph with the free-text already encoded.
        // Plain tokens (firstName/eventCode/taskTitle) are HTML-encoded by the
        // renderer at the seam — pass raw text. REQUIREMENTS §10c-4.
        var dueText = due is null
            ? "no fixed due date"
            : $"due <strong>{due:dd/MM/yyyy}</strong>";
        var descriptionBlock = string.IsNullOrWhiteSpace(description)
            ? string.Empty
            : $"<p style=\"margin:0 0 16px;\">{System.Net.WebUtility.HtmlEncode(description)}</p>";

        var tokens = _templates.NewTokenSet();
        tokens["firstName"] = firstName;
        tokens["eventCode"] = eventCode;
        tokens["taskTitle"] = taskTitle;
        tokens["dueText"] = dueText;
        tokens["descriptionBlock"] = descriptionBlock;

        // Per-role contact footer — this page reminds SPEAKERS only.
        var (placeholders, supportEmail) = ContactConfig();
        RoleContact.AddTo(tokens, ParticipantRole.Speaker, placeholders, supportEmail);

        return _templates.Render("task-manual-reminder", tokens);
    }
}
