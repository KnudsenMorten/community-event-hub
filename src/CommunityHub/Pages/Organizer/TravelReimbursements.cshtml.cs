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
        CommunityHub.Core.Entitlements.TravelClaimLock? claimLock = null,
        // §1387 — AI check + Accept / Send back. Optional for the same reason.
        CommunityHub.Core.Travel.TravelReviewService? review = null)
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
        _review = review;
    }

    private readonly CommunityHub.Core.Entitlements.TravelClaimLock? _claimLock;
    private readonly CommunityHub.Core.Travel.TravelReviewService? _review;   // §1387

    /// <summary>§1387 — the uploaded files per participant (no bytes), for the list + download links.</summary>
    public Dictionary<int, List<FileRow>> FilesByParticipant { get; private set; } = new();
    public record FileRow(int Id, string FileName, long SizeBytes, TravelDocumentKind Kind);
    public bool CanReview => _review is not null;
    public int CountNeedsReview { get; private set; }

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
        DateTimeOffset? UpdatedAt,
        DateTimeOffset? SubmittedAt = null, int ReopenCount = 0,
        // §1387 — the review.
        TravelReviewState ReviewState = TravelReviewState.None, TravelAiVerdict AiVerdict = TravelAiVerdict.NotChecked,
        string? AiSummary = null, IReadOnlyList<TravelCheckItem>? Checks = null, bool? HasCompany = null,
        DateTimeOffset? AiCheckedAt = null, string? ReviewNote = null, DateTimeOffset? ErpSentAt = null,
        int SentBackCount = 0);

    /// <summary>§1387 — "Needs your decision": submitted claims under review, newest response first.</summary>
    public List<Row> Queue { get; private set; } = new();

    /// <summary>§1387 — each claim's dialog log, newest step first (operator: "can we also get log of dialog").</summary>
    public Dictionary<int, List<TravelReviewLogEntry>> Dialog { get; private set; } = new();

    /// <summary>A time as the organizers read it (Copenhagen), e.g. "8 Nov 19:05".</summary>
    public static string Local(DateTimeOffset at)
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen"); }
        catch (TimeZoneNotFoundException) { tz = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time"); }
        return TimeZoneInfo.ConvertTime(at, tz).ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A response is "new" for a day after the speaker submitted it.</summary>
    public bool IsNew(Row r) => r.SubmittedAt is { } s && _clock.GetUtcNow() - s < TimeSpan.FromHours(24);

    /// <summary>
    /// True when the Reopen button may be offered on a row: the claim is submitted (frozen) and this
    /// host can actually reopen. 🔒 The handler existed since §768.10 D5 but no button ever called it
    /// (found 2026-10-22: a speaker locked out with a missing invoice) — so the view asks here.
    /// </summary>
    public bool CanReopen(Row r) => r.SubmittedAt is not null && _claimLock is not null;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!me.Role.HasOrganizerAccess()) { AccessDenied = true; return Page(); }
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
        // A write, so the same gate as MarkPaid / MarkUnpaid: an acting-as or secretary session must
        // not reopen a claim (and the audit row would name the wrong person).
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

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
            if (_review is not null)
                await _review.LogAsync(me.EventId, id, TravelReviewLogKinds.Reopened, "Reopened the claim (no mail to the speaker).", me.Email, ct);
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
            if (_review is not null)
                await _review.LogAsync(row, TravelReviewLogKinds.Paid,
                    "Marked as paid" + (row.PaidNotes is null ? "." : $": {row.PaidNotes}"), me.Email, ct);

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

    // ---- §1387 — document review ---------------------------------------------------------------

    /// <summary>Download one uploaded file (organizers only; never a speaker's own route).</summary>
    public async Task<IActionResult> OnGetFileAsync(int fileId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!me.Role.HasOrganizerAccess()) return Forbid();
        var f = await _db.TravelReceipts.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == fileId && r.EventId == me.EventId, ct);
        if (f is null) return NotFound();
        return File(f.Content, string.IsNullOrWhiteSpace(f.ContentType) ? "application/octet-stream" : f.ContentType, f.FileName);
    }

    /// <summary>Run the AI check on one claim. Sends nothing.</summary>
    public async Task<IActionResult> OnPostValidateAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }
        if (_review is null) { Message = "The AI check is not available on this host."; }
        else
        {
            var v = await _review.ValidateAsync(me.EventId, id, ct);
            Message = v is null ? $"Claim #{id} has no reimbursement request to check."
                : $"Claim #{id}: {Verdict(v.Verdict)} — {v.Summary}";
        }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Run the AI check on every submitted, not-yet-accepted claim. Sends nothing.</summary>
    public async Task<IActionResult> OnPostValidateAllAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }
        Message = _review is null
            ? "The AI check is not available on this host."
            : $"Checked {await _review.ValidateAllSubmittedAsync(me.EventId, ct)} submitted claim(s). Nobody was e-mailed.";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Accept the documents: the claim goes to the finance inbox (once).</summary>
    public async Task<IActionResult> OnPostAcceptAsync(int id, string? note, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }
        Message = _review is null ? "Review is not available on this host."
            : (await _review.AcceptAsync(me.EventId, id, me.Email, note, ct)).Message;
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Send back to the speaker: the claim re-opens and the speaker is e-mailed the note + missing items.</summary>
    public async Task<IActionResult> OnPostSendBackAsync(int id, string? note, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }
        Message = _review is null ? "Review is not available on this host."
            : (await _review.SendBackAsync(me.EventId, id, me.Email, note, ct)).Message;
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    // ---- previews (operator 2026-11-08: "maybe show mail before send") -------------------------

    /// <summary>The mail being previewed, if any; "sendback" or "accept".</summary>
    public CommunityHub.Core.Travel.TravelMailPreview? Preview { get; private set; }
    public string? PreviewKind { get; private set; }
    public int PreviewClaimId { get; private set; }
    public string? PreviewNote { get; private set; }
    public string? PreviewName { get; private set; }

    /// <summary>Show the exact send-back mail; nothing is sent and the claim is unchanged.</summary>
    public async Task<IActionResult> OnPostPreviewSendBackAsync(int id, string? note, CancellationToken ct)
        => await PreviewAsync(id, note, "sendback", ct);

    /// <summary>Show the exact finance mail Accept would send; nothing is sent and the claim is unchanged.</summary>
    public async Task<IActionResult> OnPostPreviewAcceptAsync(int id, string? note, CancellationToken ct)
        => await PreviewAsync(id, note, "accept", ct);

    private async Task<IActionResult> PreviewAsync(int id, string? note, string kind, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }
        if (_review is null) Message = "Review is not available on this host.";
        else
        {
            Preview = kind == "accept"
                ? await _review.PreviewAcceptAsync(me.EventId, id, note, me.Email, ct)
                : await _review.PreviewSendBackAsync(me.EventId, id, note, ct);
            if (Preview is null) Message = $"Claim #{id} was not found.";
            PreviewKind = kind;
            PreviewClaimId = id;
            PreviewNote = note;
        }
        await LoadAsync(me.EventId, ct);
        PreviewName = Rows.FirstOrDefault(r => r.Id == id)?.Name;
        return Page();
    }

    /// <summary>Accept one checklist line (missing or in question) — operator 2026-11-08: "accept/override each topic in question".</summary>
    public async Task<IActionResult> OnPostAcceptCheckAsync(int id, int index, string? checkNote, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }
        Message = _review is null ? "Review is not available on this host."
            : (await _review.AcceptCheckAsync(me.EventId, id, index, me.Email, checkNote, ct)).Message;
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Undo an organizer's acceptance of one checklist line.</summary>
    public async Task<IActionResult> OnPostUndoCheckAsync(int id, int index, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }
        Message = _review is null ? "Review is not available on this host."
            : (await _review.UndoCheckAsync(me.EventId, id, index, me.Email, ct)).Message;
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public static string Verdict(TravelAiVerdict v) => v switch
    {
        TravelAiVerdict.Approved => "Approved",
        TravelAiVerdict.NotApproved => "Not approved",
        TravelAiVerdict.CouldNotCheck => "Could not check",
        _ => "Not checked",
    };

    public static string ReviewText(TravelReviewState s) => s switch
    {
        TravelReviewState.UnderReview => "Under review",
        TravelReviewState.Accepted => "Accepted",
        TravelReviewState.SentBack => "Sent back",
        _ => "",
    };

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
            if (_review is not null)
                await _review.LogAsync(row, TravelReviewLogKinds.Unpaid, "Reverted to unpaid.", me.Email, ct);
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
                t.IsPaid, t.PaidAt, t.PaidNotes, t.UpdatedAt,
                t.SubmittedAt, t.ReopenCount,
                t.ReviewState, t.AiVerdict, t.AiSummary, t.AiFindingsJson, t.HasCompany, t.AiCheckedAt, t.ReviewNote, t.ErpSentAt,
                t.SentBackCount
            })
            .ToListAsync(ct);

        var all = data.Select(d => new Row(
            d.Id, d.ParticipantId, d.FullName, d.Email, d.Role.ToString(),
            d.RequestReimbursement, d.OriginCity,
            d.ClaimAmountEur, d.Explanation,
            d.IsPaid, d.PaidAt, d.PaidNotes, d.UpdatedAt,
            d.SubmittedAt, d.ReopenCount,
            d.ReviewState, d.AiVerdict, d.AiSummary,
            CommunityHub.Core.Travel.TravelReviewService.Checks(new TravelReimbursement { AiFindingsJson = d.AiFindingsJson }),
            d.HasCompany, d.AiCheckedAt, d.ReviewNote, d.ErpSentAt, d.SentBackCount)).ToList();
        if (_review is not null) Dialog = await _review.DialogAsync(eventId, ct);

        // §1387 — the files, without their bytes.
        FilesByParticipant = (await _db.TravelReceipts
                .Where(r => r.EventId == eventId)
                .OrderBy(r => r.UploadedAt)
                .Select(r => new { r.ParticipantId, Row = new FileRow(r.Id, r.FileName, r.SizeBytes, r.Kind) })
                .ToListAsync(ct))
            .GroupBy(x => x.ParticipantId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Row).ToList());

        var requesting = all.Where(r => r.RequestReimbursement).ToList();
        CountRequesting     = requesting.Count;
        CountPaid           = requesting.Count(r => r.IsPaid);
        TotalClaimedEur     = requesting.Sum(r => r.ClaimAmountEur ?? 0);
        TotalPaidEur        = requesting.Where(r => r.IsPaid).Sum(r => r.ClaimAmountEur ?? 0);
        TotalOutstandingEur = TotalClaimedEur - TotalPaidEur;
        CountNeedsReview    = requesting.Count(r => r.ReviewState == TravelReviewState.UnderReview);
        Queue = requesting.Where(r => r.ReviewState == TravelReviewState.UnderReview && r.SubmittedAt is not null)
            .OrderByDescending(r => r.SubmittedAt).ToList();

        Rows = Filter switch
        {
            "review"      => requesting.Where(r => r.ReviewState == TravelReviewState.UnderReview).ToList(),   // §1387
            "outstanding" => requesting.Where(r => !r.IsPaid).ToList(),
            "paid"        => requesting.Where(r =>  r.IsPaid).ToList(),
            "requesting"  => requesting,
            _ /* all */   => all,
        };
        Rows = Filter == "review"
            ? Rows.OrderByDescending(r => r.SubmittedAt).ToList()   // newest response first
            : Rows
                .OrderBy(r => r.IsPaid)
                .ThenByDescending(r => r.ClaimAmountEur ?? 0)
                .ThenBy(r => r.Name)
                .ToList();
    }
}
