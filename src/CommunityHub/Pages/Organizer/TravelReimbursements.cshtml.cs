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
public class TravelReimbursementsModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;
    private readonly IEmailSender _emailSender;
    private readonly EmailTemplateProvider _templates;
    private readonly CommunityHub.Branding.ActiveEventNameProvider _activeEvent;
    private readonly ILogger<TravelReimbursementsModel> _logger;
    private readonly IEmailContextAccessor? _context;

    public TravelReimbursementsModel(
        CommunityHubDbContext db, ICurrentParticipantAccessor participant,
        TimeProvider clock, IEmailSender emailSender,
        EmailTemplateProvider templates,
        CommunityHub.Branding.ActiveEventNameProvider activeEvent,
        ILogger<TravelReimbursementsModel> logger,
        IEmailContextAccessor? context = null,
        // §6.10 / §768.10 D5 — the reopen. Optional to match this page's existing constructions.
        CommunityHub.Core.Entitlements.TravelClaimLock? claimLock = null)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
        _emailSender = emailSender;
        _templates = templates;
        _activeEvent = activeEvent;
        _logger = logger;
        _context = context;
        _claimLock = claimLock;
    }

    private readonly CommunityHub.Core.Entitlements.TravelClaimLock? _claimLock;

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }

    [BindProperty(SupportsGet = true)] public string Filter { get; set; } = "all";

    public List<Row> Rows { get; private set; } = new();
    public int CountRequesting { get; private set; }
    public int CountPaid { get; private set; }
    public decimal TotalClaimedEur { get; private set; }
    public decimal TotalPaidEur { get; private set; }
    public decimal TotalOutstandingEur { get; private set; }

    public record Row(
        int Id, int ParticipantId, string Name, string Email, string Role,
        bool RequestReimbursement, string? OriginCity,
        decimal? ClaimAmountEur, string? Explanation,
        bool IsPaid, DateTimeOffset? PaidAt, string? PaidNotes,
        DateTimeOffset? UpdatedAt);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// §6.10 / §768.10 D5 — return a submitted claim to the speaker so they can add what is missing.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>This handler is why the freeze is safe to ship at all.</b> Work-order §6.10 named the
    /// consequence of locking without it: a speaker who submits after uploading 3 of 5 receipts is
    /// locked out <b>with no recovery path</b>. Audit-logged inside
    /// <c>TravelClaimLock.ReopenAsync</c> — who, when, and how many times, because a claim reopened
    /// repeatedly is a story someone will want to read later.
    /// </remarks>
    public async Task<IActionResult> OnPostReopenAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var row = await _db.TravelReimbursements
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id && t.EventId == me.EventId, ct);

        if (row is null)
        {
            Message = $"Claim #{id} was not found.";
        }
        else if (_claimLock is null)
        {
            // An honest refusal beats a button that silently does nothing.
            Message = "Reopening is not available on this host.";
        }
        else if (await _claimLock.ReopenAsync(me.EventId, row.ParticipantId, me.Email, ct))
        {
            Message = $"Claim #{id} is open again — the speaker can add files and submit it once more.";
        }
        else
        {
            Message = $"Claim #{id} is not submitted, so there is nothing to reopen.";
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostMarkPaidAsync(int id, string? notes, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var row = await _db.TravelReimbursements
            .FirstOrDefaultAsync(t => t.Id == id && t.EventId == me.EventId, ct);
        if (row is not null)
        {
            row.IsPaid = true;
            row.PaidAt = _clock.GetUtcNow();
            row.PaidNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
            await _db.SaveChangesAsync(ct);

            // Notify the speaker that their reimbursement was paid out.
            var p = await _db.Participants
                .Where(x => x.Id == row.ParticipantId)
                .Select(x => new { x.Email, x.FullName, x.EventId })
                .FirstOrDefaultAsync(ct);
            if (p is not null)
            {
                try
                {
                    var eventCode = await _db.Events
                        .Where(e => e.Id == p.EventId)
                        .Select(e => e.Code)
                        .FirstOrDefaultAsync(ct) ?? "Event Hub";

                    var firstName = string.IsNullOrWhiteSpace(p.FullName) ? "there" : p.FullName.Split(' ')[0];
                    var amount = row.ClaimAmountEur?.ToString("0") ?? "";
                    var notesBlock = string.IsNullOrWhiteSpace(row.PaidNotes)
                        ? ""
                        : $"<p style=\"margin:0 0 16px;\">Organizer note: {System.Net.WebUtility.HtmlEncode(row.PaidNotes)}</p>";

                    // §169: the claimant IS a Participant — pass their id so the {{hubUrl}}
                    // CTA is their personal /go/{token} auto-login magic-link (fail-safe:
                    // no participant / any error ⇒ plain hub URL, never throws).
                    var tokens = _templates.NewTokenSet(row.ParticipantId);
                    tokens["firstName"] = firstName;
                    tokens["eventCode"] = eventCode;
                    tokens["amount"] = amount;
                    tokens["notesBlock"] = notesBlock;
                    tokens["communityName"] = _activeEvent.GetCommunityName();

                    var rendered = _templates.Render("travel-reimbursement-paid", tokens);
                    // Ring-governed by the travel-reimbursement-email feature (operator 2026-06-22).
                    // 🔒 §707.2b — the key was ALREADY here, but as the FIRST argument, which is
                    // `Category`, not `TemplateName`. That reads as wired and is not: the gate never saw
                    // a mail identity and fell back to the feature ring. Exactly the trap §707.2 flagged
                    // (check the argument POSITION, not the name). Now passed as TemplateName too.
                    using (_context?.Set(new EmailContext(
                        "travel-reimbursement-paid", p.EventId, row.ParticipantId, p.FullName,
                        TemplateName: "travel-reimbursement-paid",
                        FeatureKey: "travel-reimbursement-email")))
                    {
                        await _emailSender.SendAsync(p.Email, rendered.Subject, rendered.HtmlBody, ct);
                    }
                    Message = $"Marked claim #{id} as paid and notified {p.Email}.";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to notify {Email} of paid reimbursement", p.Email);
                    Message = $"Marked claim #{id} as paid. (Notification email could not be sent: {ex.Message})";
                }
            }
            else
            {
                Message = $"Marked claim #{id} as paid.";
            }
        }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostMarkUnpaidAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var row = await _db.TravelReimbursements
            .FirstOrDefaultAsync(t => t.Id == id && t.EventId == me.EventId, ct);
        if (row is not null)
        {
            row.IsPaid = false;
            row.PaidAt = null;
            row.PaidNotes = null;
            await _db.SaveChangesAsync(ct);
            Message = $"Reverted claim #{id} to unpaid.";
        }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        var data = await _db.TravelReimbursements
            .Where(t => t.EventId == eventId)
            .Join(_db.Participants, t => t.ParticipantId, p => p.Id, (t, p) => new
            {
                t.Id, t.ParticipantId,
                p.FullName, p.Email, p.Role,
                t.RequestReimbursement, t.OriginCity,
                t.ClaimAmountEur, t.Explanation,
                t.IsPaid, t.PaidAt, t.PaidNotes, t.UpdatedAt
            })
            .ToListAsync(ct);

        var all = data.Select(d => new Row(
            d.Id, d.ParticipantId, d.FullName, d.Email, d.Role.ToString(),
            d.RequestReimbursement, d.OriginCity,
            d.ClaimAmountEur, d.Explanation,
            d.IsPaid, d.PaidAt, d.PaidNotes, d.UpdatedAt)).ToList();

        var requesting = all.Where(r => r.RequestReimbursement).ToList();
        CountRequesting     = requesting.Count;
        CountPaid           = requesting.Count(r => r.IsPaid);
        TotalClaimedEur     = requesting.Sum(r => r.ClaimAmountEur ?? 0);
        TotalPaidEur        = requesting.Where(r => r.IsPaid).Sum(r => r.ClaimAmountEur ?? 0);
        TotalOutstandingEur = TotalClaimedEur - TotalPaidEur;

        Rows = Filter switch
        {
            "outstanding" => requesting.Where(r => !r.IsPaid).ToList(),
            "paid"        => requesting.Where(r =>  r.IsPaid).ToList(),
            "requesting"  => requesting,
            _ /* all */   => all,
        };
        Rows = Rows
            .OrderBy(r => r.IsPaid)
            .ThenByDescending(r => r.ClaimAmountEur ?? 0)
            .ThenBy(r => r.Name)
            .ToList();
    }
}
