using System.Text.RegularExpressions;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Email Center for organizers. Three jobs:
///   1. PREVIEW every branded template with realistic sample tokens, exactly
///      as the reminder jobs render it (same provider, same layout). A
///      missing/broken template shows its error here instead of failing
///      silently at 06:00 in a Function app.
///   2. TEST-SEND the rendered preview to the signed-in organizer.
///   3. LEDGER: browse SentReminder (what the engine actually delivered),
///      with a 7-day per-type pulse so a dead reminder job is visible.
/// </summary>
[Authorize]
public class EmailCenterModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly EmailTemplateProvider _templates;
    private readonly EmailTemplateOptions _templateOptions;
    private readonly IEmailSender _emailSender;
    private readonly ParticipantEmailService _participantEmail;
    private readonly EmailTestSendPlanner _testSendPlanner;
    private readonly TimeProvider _clock;

    public EmailCenterModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        EmailTemplateProvider templates,
        IOptions<EmailTemplateOptions> templateOptions,
        IEmailSender emailSender,
        ParticipantEmailService participantEmail,
        EmailTestSendPlanner testSendPlanner,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _templates = templates;
        _templateOptions = templateOptions.Value;
        _emailSender = emailSender;
        _participantEmail = participantEmail;
        _testSendPlanner = testSendPlanner;
        _clock = clock;
    }

    public bool AccessDenied { get; private set; }

    // --- Preview ----------------------------------------------------------
    public List<string> TemplateNames { get; private set; } = new();
    [BindProperty(SupportsGet = true)] public string? Template { get; set; }
    public string? PreviewSubject { get; private set; }
    public string? PreviewHtml { get; private set; }
    public string? PreviewError { get; private set; }
    public List<string> PreviewTokens { get; private set; } = new();
    public string? ActionMessage { get; private set; }

    // --- Test-send to an arbitrary address (REQUIREMENTS §21 organizer) -------
    [BindProperty] public string? TestAddress { get; set; }

    // --- Ledger -------------------------------------------------------------
    public List<SentReminder> Ledger { get; private set; } = new();
    public List<(string Type, int Count)> WeekPulse { get; private set; } = new();
    public List<string> LedgerTypes { get; private set; } = new();
    [BindProperty(SupportsGet = true)] public string? TypeFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? EmailFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }

    // --- Send to a person (10a-2) + secondary email (10a-5) -----------------
    public List<(int Id, string Label)> ActiveParticipants { get; private set; } = new();
    [BindProperty] public int PersonId { get; set; }
    [BindProperty] public string? PersonTemplate { get; set; }
    [BindProperty] public string? SecondaryEmail { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        ActionMessage = Msg;
        LoadTemplateList();
        await RenderPreviewAsync(me.EventId, ct);
        await LoadLedgerAsync(me.EventId, ct);
        await LoadActiveParticipantsAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadActiveParticipantsAsync(int eventId, CancellationToken ct)
    {
        ActiveParticipants = (await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.IsActive
                        && p.LifecycleState == ParticipantLifecycleState.Active)
            .OrderBy(p => p.FullName)
            .Select(p => new { p.Id, p.FullName, p.Email })
            .ToListAsync(ct))
            .Select(p => (p.Id, $"{p.FullName} <{p.Email}>"))
            .ToList();
    }

    /// <summary>Manual individual re-send (10a-2): send any template to one named
    /// person on demand. NOT idempotency-gated. Optionally also set/clear that
    /// person's secondary email (10a-5) before sending so the CC takes effect.</summary>
    public async Task<IActionResult> OnPostSendToPersonAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        LoadTemplateList();
        string msg;
        if (PersonId <= 0)
        {
            msg = "Pick a person first.";
        }
        else if (PersonTemplate is null || !TemplateNames.Contains(PersonTemplate))
        {
            msg = "Pick a template first.";
        }
        else
        {
            // Apply the secondary-email change (set or clear) before sending.
            await ApplySecondaryEmailAsync(me.EventId, PersonId, SecondaryEmail, ct);
            try
            {
                var to = await _participantEmail.SendTemplateToParticipantAsync(
                    me.EventId, PersonId, PersonTemplate, category: "manual-resend",
                    extraTokens: null, ct);
                msg = to is null
                    ? "That person was not found in this edition."
                    : $"Sent '{PersonTemplate}' to {to}.";
            }
            catch (Exception ex)
            {
                msg = $"Send failed: {ex.Message}";
            }
        }
        return RedirectToPage(new { Template, TypeFilter, EmailFilter, Msg = msg });
    }

    private async Task ApplySecondaryEmailAsync(
        int eventId, int participantId, string? secondary, CancellationToken ct)
    {
        var p = await _db.Participants.FirstOrDefaultAsync(
            x => x.Id == participantId && x.EventId == eventId, ct);
        if (p is null) return;
        var trimmed = string.IsNullOrWhiteSpace(secondary) ? null : secondary.Trim();
        if (p.SecondaryEmail != trimmed)
        {
            p.SecondaryEmail = trimmed;
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Send the selected template (sample tokens) to the signed-in
    /// organizer so they can check it in a real mail client.</summary>
    public async Task<IActionResult> OnPostTestSendAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        LoadTemplateList();
        string msg;
        if (Template is null || !TemplateNames.Contains(Template))
        {
            msg = "Pick a template first.";
        }
        else
        {
            try
            {
                var tokens = await BuildSampleTokensAsync(me.EventId, ct);
                var rendered = _templates.Render(Template, tokens);
                await _emailSender.SendAsync(
                    me.Email, $"[TEST] {rendered.Subject}", rendered.HtmlBody, ct);
                msg = $"Test mail '{Template}' sent to {me.Email}.";
            }
            catch (Exception ex)
            {
                msg = $"Test send failed: {ex.Message}";
            }
        }
        return RedirectToPage(new { Template, TypeFilter, EmailFilter, Msg = msg });
    }

    /// <summary>
    /// Test-send the selected template (sample tokens) to an ARBITRARY address the
    /// organizer types — for verifying delivery to a specific mailbox, e.g. a
    /// colleague or a role test account. The send is honest: the
    /// <see cref="EmailTestSendPlanner"/> decides up front (using the SAME
    /// redirect/allowlist gate as the sender) whether the address is invalid,
    /// would be dropped by the allowlist, would be redirected to the dev mailbox,
    /// or lands as typed — so a dropped/redirected send is never reported as a
    /// plain success.
    /// </summary>
    public async Task<IActionResult> OnPostTestSendToAddressAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        LoadTemplateList();
        string msg;
        if (Template is null || !TemplateNames.Contains(Template))
        {
            msg = "Pick a template first.";
        }
        else
        {
            var plan = _testSendPlanner.Plan(TestAddress);
            switch (plan.Outcome)
            {
                case EmailTestSendOutcome.InvalidAddress:
                    msg = "Enter a valid email address to test-send to.";
                    break;
                case EmailTestSendOutcome.DroppedByKillSwitch:
                    // No-op, NOT a success: tell the organizer it would be dropped
                    // so they don't wait for mail that will never arrive.
                    msg = $"Not sent: the global outbound-email kill switch is ON, "
                        + "so the hub would drop it. Turn off Email__KillSwitch to test-send.";
                    break;
                default:
                    try
                    {
                        var tokens = await BuildSampleTokensAsync(me.EventId, ct);
                        var rendered = _templates.Render(Template, tokens);
                        await _emailSender.SendAsync(
                            plan.TargetAddress!, $"[TEST] {rendered.Subject}", rendered.HtmlBody, ct);
                        msg = plan.Outcome == EmailTestSendOutcome.WouldRedirect
                            ? $"Test mail '{Template}' sent for {plan.TargetAddress} "
                              + $"(redirected to {plan.ActualRecipient} in this environment)."
                            : $"Test mail '{Template}' sent to {plan.TargetAddress}.";
                    }
                    catch (Exception ex)
                    {
                        msg = $"Test send failed: {ex.Message}";
                    }
                    break;
            }
        }
        return RedirectToPage(new { Template, TypeFilter, EmailFilter, Msg = msg });
    }

    private void LoadTemplateList()
    {
        var dir = _templateOptions.TemplateDirectory;
        if (Directory.Exists(dir))
        {
            TemplateNames = Directory.GetFiles(dir, "*.html")
                .Select(f => Path.GetFileNameWithoutExtension(f)!)
                .Where(n => !n.StartsWith('_'))
                .OrderBy(n => n)
                .ToList();
        }
    }

    private async Task RenderPreviewAsync(int eventId, CancellationToken ct)
    {
        // Template comes from a dropdown, but never trust it: only names that
        // exist in the enumerated list are rendered (no path traversal).
        if (Template is null) return;
        if (!TemplateNames.Contains(Template)) { PreviewError = "Unknown template."; return; }

        try
        {
            var tokens = await BuildSampleTokensAsync(eventId, ct);
            var path = Path.Combine(_templateOptions.TemplateDirectory, Template + ".html");
            PreviewTokens = Regex.Matches(System.IO.File.ReadAllText(path), @"\{\{(\w+)\}\}")
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t)
                .ToList();

            var rendered = _templates.Render(Template, tokens);
            PreviewSubject = rendered.Subject;
            PreviewHtml = rendered.HtmlBody;
        }
        catch (Exception ex)
        {
            PreviewError = ex.Message;
        }
    }

    /// <summary>
    /// Realistic sample values for every token any template uses. Branding
    /// tokens come from the live options (real logo/color); the event name is
    /// the real active edition so the preview matches production output.
    /// </summary>
    private async Task<Dictionary<string, string>> BuildSampleTokensAsync(
        int eventId, CancellationToken ct)
    {
        var evt = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.DisplayName, e.CommunityName })
            .FirstOrDefaultAsync(ct);

        var sampleDue = _clock.GetUtcNow().AddDays(7).ToString("dddd d MMMM yyyy");
        var tokens = _templates.NewTokenSet();
        tokens["firstName"] = "Alex";
        tokens["eventDisplayName"] = evt?.DisplayName ?? "the event";
        tokens["communityName"] = evt?.CommunityName ?? "the community";
        tokens["roleName"] = "speaker";
        tokens["roleGuidance"] = "You will find your hotel and dinner forms and "
            + "your session deadlines in the hub.";
        tokens["taskTitle"] = "Upload your company logo";
        tokens["taskLink"] = _templateOptions.HubUrl;
        tokens["taskListHtml"] =
            "<ul><li>Upload your company logo (due " + sampleDue + ")</li>"
            + "<li>Submit your session description</li></ul>";
        tokens["dueDate"] = sampleDue;
        tokens["state"] = "Open";
        tokens["formName"] = "Hotel form";
        tokens["formDeadline"] = sampleDue;
        tokens["sponsorCompany"] = "Example Sponsor ApS";
        tokens["masterClassList"] = "Master Class A, Master Class B";
        return tokens;
    }

    private async Task LoadLedgerAsync(int eventId, CancellationToken ct)
    {
        var weekAgo = _clock.GetUtcNow().AddDays(-7);
        WeekPulse = (await _db.SentReminders
            .Where(s => s.EventId == eventId && s.SentAt >= weekAgo)
            .GroupBy(s => s.ReminderType)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ToListAsync(ct))
            .Select(g => (g.Type, g.Count))
            .ToList();

        LedgerTypes = await _db.SentReminders
            .Where(s => s.EventId == eventId)
            .Select(s => s.ReminderType)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync(ct);

        var q = _db.SentReminders.Where(s => s.EventId == eventId);
        if (!string.IsNullOrWhiteSpace(TypeFilter))
            q = q.Where(s => s.ReminderType == TypeFilter);
        if (!string.IsNullOrWhiteSpace(EmailFilter))
            q = q.Where(s => s.RecipientEmail.Contains(EmailFilter.Trim()));

        Ledger = await q.OrderByDescending(s => s.SentAt)
            .Take(100)
            .ToListAsync(ct);
    }
}
