using System.Text;
using System.Text.RegularExpressions;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Role broadcast: an organizer picks a reusable starting template (or starts
/// blank), edits it, narrows the audience (role groups, activity status, exclude
/// the synthetic test cast, optionally attendees), previews the exact filtered
/// recipient list AND the branded body, then sends one personalized message
/// individually to everyone selected.
///
/// Safety model:
///   - The audience is resolved by the pure <see cref="BroadcastAudienceFilter"/>
///     so the previewed count + list is byte-for-byte what gets sent.
///   - Test users (<see cref="Participant.IsTestUser"/>) are excluded by default
///     so a real broadcast never mails the go-live test cast.
///   - Every delivery is recorded in the SentReminder ledger with
///     OccasionKey = broadcast:&lt;subject-slug&gt;, so re-submitting the same
///     subject only mails recipients NOT yet in the ledger — resume-safe, never
///     double-sends.
///   - Per-recipient failures are caught + counted; one bad address does not
///     abort the run.
///   - DEV redirects all mail (Email__RedirectAllTo); PROD can be scoped with
///     the Email__OnlySendTo allowlist while testing.
/// </summary>
[Authorize]
public class BroadcastModel : PageModel
{
    private const string ReminderType = "broadcast";
    private const int PreviewListCap = 200;

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly EmailTemplateProvider _templates;
    private readonly IEmailSender _emailSender;
    private readonly TimeProvider _clock;
    private readonly ILogger<BroadcastModel> _log;
    private readonly IStringLocalizer<SharedResource> _loc;
    private readonly IEmailContextAccessor? _context;

    public BroadcastModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        EmailTemplateProvider templates,
        IEmailSender emailSender,
        TimeProvider clock,
        ILogger<BroadcastModel> log,
        IStringLocalizer<SharedResource> loc,
        IEmailContextAccessor? context = null)
    {
        _db = db;
        _participant = participant;
        _templates = templates;
        _emailSender = emailSender;
        _clock = clock;
        _log = log;
        _loc = loc;
        _context = context;
    }

    public bool AccessDenied { get; private set; }
    public string? ActionMessage { get; private set; }
    public bool ActionIsError { get; private set; }

    /// <summary>
    /// The honest send confirmation for the last broadcast SEND, rendered by the
    /// shared <c>_Flash</c> toast (REQUIREMENTS §21). Shaped by the pure
    /// <see cref="ActionResultSummarizer"/>: "sent at &lt;time&gt; — N recipient(s)"
    /// on a real send, a no-op when everyone was already-sent / dropped, or a
    /// failure with the reason. Null on the preview / load-template paths (those
    /// keep using <see cref="ActionMessage"/>).
    /// </summary>
    public ActionResultSummary? SendResult { get; private set; }

    /// <summary>Localized format bundle for the action-result confirmation.</summary>
    private ActionResultSummarizer.Formats Formats => new(
        SentFormat: _loc["Action.Sent"].Value,
        ProvisionedFormat: _loc["Action.Provisioned"].Value,
        ProvisionedNoUrlFormat: _loc["Action.ProvisionedNoUrl"].Value,
        NoOpFormat: _loc["Action.NoOp"].Value,
        FailedFormat: _loc["Action.Failed"].Value);

    // --- Audience -----------------------------------------------------------
    [BindProperty] public List<ParticipantRole> Roles { get; set; } = new();
    [BindProperty] public bool IncludeAttendees { get; set; }
    [BindProperty] public BroadcastStatusFilter Status { get; set; } = BroadcastStatusFilter.ActiveOnly;
    [BindProperty] public bool ExcludeTestUsers { get; set; } = true;

    // --- Message ------------------------------------------------------------
    [BindProperty] public string Subject { get; set; } = string.Empty;
    [BindProperty] public string Message { get; set; } = string.Empty;
    /// <summary>The reusable template the organizer chose to load (LoadTemplate handler).</summary>
    [BindProperty] public string? TemplateKey { get; set; }

    /// <summary>The selectable built-in starting templates.</summary>
    public IReadOnlyList<BroadcastTemplate> AvailableTemplates => BroadcastTemplates.BuiltIn;

    // --- Preview ------------------------------------------------------------
    /// <summary>Distinct recipient count for the current selection (preview).</summary>
    public int? RecipientCount { get; private set; }
    public int? AlreadySentCount { get; private set; }
    public string? PreviewHtml { get; private set; }
    /// <summary>The actual filtered recipients shown before sending (capped for display).</summary>
    public IReadOnlyList<BroadcastRecipient> PreviewRecipients { get; private set; } =
        Array.Empty<BroadcastRecipient>();
    public bool PreviewListTruncated { get; private set; }

    public IActionResult OnGet()
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        return Page();
    }

    /// <summary>Load a reusable template into Subject/Message (sends nothing,
    /// resolves no recipients — the organizer edits the loaded text first).</summary>
    public IActionResult OnPostLoadTemplate()
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var tpl = BroadcastTemplates.Find(TemplateKey);
        if (tpl is null)
        {
            ActionIsError = true;
            ActionMessage = "Unknown template.";
            return Page();
        }

        Subject = tpl.Subject;
        Message = tpl.Body;
        ActionMessage = tpl.Key == "blank"
            ? "Cleared the message — start writing."
            : $"Loaded the '{tpl.DisplayName}' template. Edit it, then preview.";
        return Page();
    }

    /// <summary>Preview: resolve the filtered recipient list + render the branded
    /// body. Sends nothing.</summary>
    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!Validate()) return Page();

        var recipients = await ResolveRecipientsAsync(me.EventId, ct);
        RecipientCount = recipients.Count;
        AlreadySentCount = await CountAlreadySentAsync(me.EventId, recipients, ct);

        PreviewRecipients = recipients.Take(PreviewListCap).ToList();
        PreviewListTruncated = recipients.Count > PreviewListCap;

        var eventName = await EventNameAsync(me.EventId, ct);
        var rendered = RenderFor("Alex", eventName);
        PreviewHtml = rendered.HtmlBody;
        return Page();
    }

    /// <summary>Send to every selected recipient not already in the ledger.</summary>
    public async Task<IActionResult> OnPostSendAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!Validate()) return Page();

        var recipients = await ResolveRecipientsAsync(me.EventId, ct);
        var eventName = await EventNameAsync(me.EventId, ct);
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
                var rendered = RenderFor(r.FirstName, eventName);
                // Ring-governed by the broadcast-email feature (operator 2026-06-22).
                using (_context?.Set(new EmailContext(
                    ReminderType, me.EventId, null, r.FirstName,
                    FeatureKey: "broadcast-email")))
                {
                    await _emailSender.SendAsync(r.Email, rendered.Subject, rendered.HtmlBody, ct);
                }
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

        // Honest confirmation: a real send (>=1 sent) is a success with the time +
        // count, partial failures noted; a run that reached NOBODY (everyone
        // already-sent / all dropped / all failed) is a no-op or failure, never a
        // green success. The breakdown is carried as the reason so the organizer
        // sees skipped/failed too. Shaped by the pure ActionResultSummarizer.
        var breakdown = $"({skipped} already received this subject"
            + (failed > 0 ? $", {failed} failed" : "") + ".)";
        SendResult = ActionResultSummarizer.ForSend(
            anySent: sent > 0,
            recipientCount: sent,
            at: _clock.GetUtcNow(),
            reason: sent > 0
                ? breakdown
                : (failed > 0
                    ? $"every attempt failed; {skipped} were already sent this subject."
                    : $"no new recipients — all {skipped} already received this subject."),
            failed: failed,
            formats: Formats);
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

    private async Task<IReadOnlyList<BroadcastRecipient>> ResolveRecipientsAsync(
        int eventId, CancellationToken ct)
    {
        // Load the candidate rows, then narrow with the pure filter so the
        // previewed list is exactly what the send loop iterates.
        List<Participant> participants = new();
        if (Roles.Count > 0)
        {
            participants = await _db.Participants
                .Where(p => p.EventId == eventId && Roles.Contains(p.Role))
                .ToListAsync(ct);

            // Universal sponsor-email audience rule (REQUIREMENTS §7c): when a
            // broadcast targets the Sponsor role, only a company's EVENT-COORDINATOR
            // contacts receive it (signer-only excluded, both-roles included). Drop
            // non-coordinator sponsor rows BEFORE the pure audience filter so the
            // previewed list equals the sent list. Non-sponsor roles are untouched.
            participants = participants
                .Where(p => p.Role != ParticipantRole.Sponsor || p.IsEventCoordinator)
                .ToList();
        }

        List<CommunityHub.Core.Domain.Attendee> attendees = new();
        if (IncludeAttendees)
        {
            attendees = await _db.Attendees
                .Where(a => a.EventId == eventId)
                .ToListAsync(ct);
        }

        var options = new BroadcastAudienceOptions
        {
            Roles = Roles,
            IncludeAttendees = IncludeAttendees,
            Status = Status,
            ExcludeTestUsers = ExcludeTestUsers,
        };
        return BroadcastAudienceFilter.Resolve(options, participants, attendees);
    }

    private async Task<int> CountAlreadySentAsync(
        int eventId, IReadOnlyList<BroadcastRecipient> recipients, CancellationToken ct)
    {
        var occasion = OccasionKey();
        var emails = recipients.Select(r => r.Email).ToList();
        return await _db.SentReminders
            .CountAsync(s => s.EventId == eventId
                             && s.ReminderType == ReminderType
                             && s.OccasionKey == occasion
                             && emails.Contains(s.RecipientEmail), ct);
    }

    private async Task<string> EventNameAsync(int eventId, CancellationToken ct)
    {
        var name = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => e.DisplayName)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(name) ? "the event" : name;
    }

    /// <summary>Render one recipient's branded email: substitute {Token}s in the
    /// organizer's subject + message, then encode the body into paragraph HTML.</summary>
    private RenderedEmail RenderFor(string firstName, string eventName)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FirstName"] = string.IsNullOrWhiteSpace(firstName) ? "there" : firstName,
            ["EventName"] = eventName,
        };

        var subject = BroadcastTemplates.Substitute(Subject.Trim(), values);
        var bodyText = BroadcastTemplates.Substitute(Message, values);

        // The renderer HTML-encodes plain tokens at the seam (the {{subject}}
        // header stays plain, {{firstName}} is encoded for the body), so pass
        // raw values here. messageHtml is the organizer's free text turned into
        // safe paragraph HTML (each paragraph already encoded) — a raw-HTML
        // token that must pass through verbatim. REQUIREMENTS §10c-4.
        var tokens = _templates.NewTokenSet();
        tokens["subject"] = subject;
        tokens["firstName"] = values["FirstName"];
        tokens["messageHtml"] = ToParagraphHtml(bodyText);
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
