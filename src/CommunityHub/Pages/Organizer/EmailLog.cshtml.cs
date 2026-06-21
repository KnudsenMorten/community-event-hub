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

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer Email Log (requirement 10a-3): every outbound email recorded by the
/// central <c>LoggingEmailSender</c> path. Lists ALL sends for the edition and
/// per-person, filterable by <b>name OR email</b> (substring, case-insensitive),
/// newest first. This is the audit view over the <see cref="EmailLog"/> table —
/// distinct from the Email Center's idempotency-ledger (<c>SentReminder</c>) view.
///
/// Recovery (REQUIREMENTS §20 Participant "EmailLog resend-on-failure"): a FAILED
/// row that captured a participant + a template gets a <b>Re-send</b> action that
/// retries through the proven <see cref="EmailResendService"/> (per-participant
/// path: effective To + secondary CC + allowlist-gated logging sender) and reports
/// an honest success / no-op / failure flash. Raw/ad-hoc rows (broadcast, PIN) are
/// not resendable from the log and show no button.
/// </summary>
[Authorize]
public class EmailLogModel : PageModel
{
    private const int PageSize = 200;

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly EmailResendService _resend;
    private readonly IStringLocalizer<SharedResource> _loc;

    public EmailLogModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        EmailResendService resend,
        IStringLocalizer<SharedResource> loc)
    {
        _db = db;
        _participant = participant;
        _resend = resend;
        _loc = loc;
    }

    public bool AccessDenied { get; private set; }

    /// <summary>Free-text filter applied to BOTH the recipient name and address.</summary>
    [BindProperty(SupportsGet = true)] public string? Filter { get; set; }

    /// <summary>Optional category filter.</summary>
    [BindProperty(SupportsGet = true)] public string? Category { get; set; }

    public List<EmailLog> Rows { get; private set; } = new();
    public List<string> Categories { get; private set; } = new();
    public int TotalCount { get; private set; }

    /// <summary>The honest outcome of a just-attempted re-send, rendered as a flash. Null on a plain GET.</summary>
    public ActionResultSummary? ResendResult { get; private set; }

    /// <summary>Whether a logged row can be re-sent from here (failed + has participant + template).</summary>
    public static bool CanResend(EmailLog row) => EmailResendService.IsResendable(row);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// Re-send a single FAILED log row. NOT idempotency-gated (the organizer chose
    /// to retry); the recovery send writes its own fresh log row, so the retry's
    /// success/failure is itself audited. Honest flash via the shared summarizer.
    /// </summary>
    public async Task<IActionResult> OnPostResendAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var result = await _resend.ResendAsync(me.EventId, id, ct);
        ResendResult = ToSummary(result);

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        Categories = await _db.EmailLogs
            .Where(e => e.EventId == eventId)
            .Select(e => e.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(ct);

        var q = _db.EmailLogs.Where(e => e.EventId == eventId);

        if (!string.IsNullOrWhiteSpace(Category))
        {
            q = q.Where(e => e.Category == Category);
        }

        if (!string.IsNullOrWhiteSpace(Filter))
        {
            // Filter by name OR email — substring, case-insensitive.
            var f = Filter.Trim();
            q = q.Where(e =>
                e.ToEmail.Contains(f)
                || (e.RecipientName != null && e.RecipientName.Contains(f))
                || e.CcEmails.Contains(f));
        }

        TotalCount = await q.CountAsync(ct);
        Rows = await q
            .OrderByDescending(e => e.SentAt)
            .Take(PageSize)
            .ToListAsync(ct);
    }

    /// <summary>Map the resend outcome to the shared honest-flash shape (English via the resources).</summary>
    private ActionResultSummary ToSummary(EmailResendResult r) => r.Outcome switch
    {
        EmailResendOutcome.Sent => new ActionResultSummary
        {
            Outcome = ActionOutcome.Succeeded,
            Kind = FlashKind.Success,
            Message = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _loc["EmailLog.Resend.Sent"].Value, r.TemplateName, r.ToEmail),
            Count = 1,
        },
        EmailResendOutcome.NotFailed =>
            ActionResultSummarizer.NoOp(_loc["EmailLog.Resend.NotFailed"].Value),
        EmailResendOutcome.NotResendable =>
            ActionResultSummarizer.NoOp(_loc["EmailLog.Resend.NotResendable"].Value),
        EmailResendOutcome.ParticipantGone =>
            ActionResultSummarizer.NoOp(_loc["EmailLog.Resend.ParticipantGone"].Value),
        EmailResendOutcome.NotFound =>
            ActionResultSummarizer.Failure(_loc["EmailLog.Resend.NotFound"].Value),
        _ => ActionResultSummarizer.Failure(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _loc["EmailLog.Resend.Failed"].Value, r.Error)),
    };
}
