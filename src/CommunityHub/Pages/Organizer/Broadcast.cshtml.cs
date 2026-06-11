using System.Text;
using System.Text.RegularExpressions;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Role broadcast: an organizer composes one message and the hub sends it
/// individually (branded layout, personal "Hi {firstName}") to every ACTIVE
/// participant in the selected role groups, optionally plus the reconciled
/// attendees.
///
/// Safety model:
///   - Every delivery is recorded in the SentReminder ledger with
///     OccasionKey = broadcast:&lt;subject-slug&gt;, so re-submitting the same
///     subject (double-click, retry after a partial failure) only mails the
///     recipients NOT yet in the ledger - a broadcast is resume-safe and
///     never double-sends.
///   - The form previews the exact recipient count before anything is sent.
///   - Per-recipient failures are caught + counted; one bad address does not
///     abort the run.
///   - DEV redirects all mail (Email__RedirectAllTo); PROD can be scoped
///     with the Email__OnlySendTo allowlist while testing.
/// </summary>
[Authorize]
public class BroadcastModel : PageModel
{
    private const string ReminderType = "broadcast";

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly EmailTemplateProvider _templates;
    private readonly IEmailSender _emailSender;
    private readonly TimeProvider _clock;
    private readonly ILogger<BroadcastModel> _log;

    public BroadcastModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        EmailTemplateProvider templates,
        IEmailSender emailSender,
        TimeProvider clock,
        ILogger<BroadcastModel> log)
    {
        _db = db;
        _participant = participant;
        _templates = templates;
        _emailSender = emailSender;
        _clock = clock;
        _log = log;
    }

    public bool AccessDenied { get; private set; }
    public string? ActionMessage { get; private set; }
    public bool ActionIsError { get; private set; }

    [BindProperty] public List<ParticipantRole> Roles { get; set; } = new();
    [BindProperty] public bool IncludeAttendees { get; set; }
    [BindProperty] public string Subject { get; set; } = string.Empty;
    [BindProperty] public string Message { get; set; } = string.Empty;

    /// <summary>Distinct recipient count for the current selection (preview).</summary>
    public int? RecipientCount { get; private set; }
    public int? AlreadySentCount { get; private set; }
    public string? PreviewHtml { get; private set; }

    public IActionResult OnGet()
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        return Page();
    }

    /// <summary>Preview: resolve recipients + render the branded body. Sends nothing.</summary>
    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) return Forbid();

        if (!Validate()) return Page();

        var recipients = await ResolveRecipientsAsync(me.EventId, ct);
        RecipientCount = recipients.Count;
        AlreadySentCount = await CountAlreadySentAsync(me.EventId, recipients, ct);

        var rendered = Render("Alex");
        PreviewHtml = rendered.HtmlBody;
        return Page();
    }

    /// <summary>Send to every selected recipient not already in the ledger.</summary>
    public async Task<IActionResult> OnPostSendAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) return Forbid();

        if (!Validate()) return Page();

        var recipients = await ResolveRecipientsAsync(me.EventId, ct);
        var occasion = OccasionKey();
        var already = await _db.SentReminders
            .Where(s => s.EventId == me.EventId
                        && s.ReminderType == ReminderType
                        && s.OccasionKey == occasion)
            .Select(s => s.RecipientEmail)
            .ToListAsync(ct);
        var alreadySet = new HashSet<string>(already, StringComparer.OrdinalIgnoreCase);

        int sent = 0, skipped = 0, failed = 0;
        foreach (var r in recipients)
        {
            if (alreadySet.Contains(r.Email)) { skipped++; continue; }
            try
            {
                var rendered = Render(r.FirstName);
                await _emailSender.SendAsync(r.Email, rendered.Subject, rendered.HtmlBody, ct);
                _db.SentReminders.Add(new SentReminder
                {
                    EventId = me.EventId,
                    RecipientEmail = r.Email,
                    ReminderType = ReminderType,
                    OccasionKey = occasion,
                    SentAt = _clock.GetUtcNow(),
                });
                // Save per send: a crash mid-run keeps the ledger truthful, so
                // a re-send resumes instead of double-mailing the first half.
                await _db.SaveChangesAsync(ct);
                sent++;
            }
            catch (Exception ex)
            {
                failed++;
                _log.LogWarning(ex, "Broadcast to {Email} failed.", r.Email);
            }
        }

        ActionIsError = failed > 0;
        ActionMessage = $"Broadcast '{Subject}': {sent} sent, {skipped} skipped "
            + $"(already received this subject), {failed} failed.";
        RecipientCount = recipients.Count;
        return Page();
    }

    private bool Validate()
    {
        if (Roles.Count == 0 && !IncludeAttendees)
        { ActionIsError = true; ActionMessage = "Pick at least one recipient group."; return false; }
        if (string.IsNullOrWhiteSpace(Subject))
        { ActionIsError = true; ActionMessage = "Subject is required."; return false; }
        if (string.IsNullOrWhiteSpace(Message))
        { ActionIsError = true; ActionMessage = "Message is required."; return false; }
        return true;
    }

    private sealed record Recipient(string Email, string FirstName);

    private async Task<List<Recipient>> ResolveRecipientsAsync(int eventId, CancellationToken ct)
    {
        var list = new List<Recipient>();
        if (Roles.Count > 0)
        {
            list.AddRange(await _db.Participants
                .Where(p => p.EventId == eventId && p.IsActive && Roles.Contains(p.Role))
                .Select(p => new Recipient(p.Email, p.FullName))
                .ToListAsync(ct));
        }
        if (IncludeAttendees)
        {
            list.AddRange(await _db.Attendees
                .Where(a => a.EventId == eventId)
                .Select(a => new Recipient(a.Email, a.FirstName))
                .ToListAsync(ct));
        }

        // Distinct by email; first name falls back to "there" in Render.
        return list
            .GroupBy(r => r.Email, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<int> CountAlreadySentAsync(
        int eventId, List<Recipient> recipients, CancellationToken ct)
    {
        var occasion = OccasionKey();
        var emails = recipients.Select(r => r.Email).ToList();
        return await _db.SentReminders
            .CountAsync(s => s.EventId == eventId
                             && s.ReminderType == ReminderType
                             && s.OccasionKey == occasion
                             && emails.Contains(s.RecipientEmail), ct);
    }

    private RenderedEmail Render(string fullName)
    {
        var first = string.IsNullOrWhiteSpace(fullName) ? "there" : fullName.Split(' ')[0];
        var tokens = _templates.NewTokenSet();
        tokens["subject"] = System.Net.WebUtility.HtmlEncode(Subject.Trim());
        tokens["firstName"] = System.Net.WebUtility.HtmlEncode(first);
        tokens["messageHtml"] = ToParagraphHtml(Message);
        return _templates.Render("broadcast", tokens);
    }

    /// <summary>Plain-text message -> encoded paragraph HTML (blank line =
    /// new paragraph; single newline = line break). Organizers type text,
    /// never HTML, so everything is encoded.</summary>
    private static string ToParagraphHtml(string text)
    {
        var sb = new StringBuilder();
        var paragraphs = Regex.Split(text.Replace("\r\n", "\n").Trim(), "\n{2,}");
        foreach (var p in paragraphs)
        {
            var encoded = System.Net.WebUtility.HtmlEncode(p).Replace("\n", "<br>");
            sb.Append("<p style=\"margin:0 0 16px;\">").Append(encoded).Append("</p>");
        }
        return sb.ToString();
    }

    /// <summary>Stable per-subject dedup key: same subject = same broadcast.</summary>
    private string OccasionKey()
    {
        var slug = Regex.Replace(Subject.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 80) slug = slug[..80];
        return $"broadcast:{slug}";
    }
}
