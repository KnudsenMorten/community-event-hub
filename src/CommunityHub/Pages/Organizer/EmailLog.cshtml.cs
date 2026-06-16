using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer Email Log (requirement 10a-3): every outbound email recorded by the
/// central <c>LoggingEmailSender</c> path. Lists ALL sends for the edition and
/// per-person, filterable by <b>name OR email</b> (substring, case-insensitive),
/// newest first. This is the audit view over the <see cref="EmailLog"/> table —
/// distinct from the Email Center's idempotency-ledger (<c>SentReminder</c>) view.
/// </summary>
[Authorize]
public class EmailLogModel : PageModel
{
    private const int PageSize = 200;

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;

    public EmailLogModel(CommunityHubDbContext db, ICurrentParticipantAccessor participant)
    {
        _db = db;
        _participant = participant;
    }

    public bool AccessDenied { get; private set; }

    /// <summary>Free-text filter applied to BOTH the recipient name and address.</summary>
    [BindProperty(SupportsGet = true)] public string? Filter { get; set; }

    /// <summary>Optional category filter.</summary>
    [BindProperty(SupportsGet = true)] public string? Category { get; set; }

    public List<EmailLog> Rows { get; private set; } = new();
    public List<string> Categories { get; private set; } = new();
    public int TotalCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Categories = await _db.EmailLogs
            .Where(e => e.EventId == me.EventId)
            .Select(e => e.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(ct);

        var q = _db.EmailLogs.Where(e => e.EventId == me.EventId);

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

        return Page();
    }
}
