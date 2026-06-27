using System.Text;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Resources;
using CommunityHub.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CommunityHub.Pages.Forms;

[Authorize]
public class TravelModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;
    private readonly IStringLocalizer<SharedResource> _loc;
    private readonly IEmailSender _email;
    private readonly IEmailContextAccessor? _emailContext;
    private readonly ILogger<TravelModel> _logger;

    public TravelModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock,
        IStringLocalizer<SharedResource> loc,
        IEmailSender email,
        ILogger<TravelModel> logger,
        IEmailContextAccessor? emailContext = null)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
        _loc = loc;
        _email = email;
        _logger = logger;
        _emailContext = emailContext;
    }

    public static readonly ParticipantRole[] EligibleRoles =
    {
        ParticipantRole.Speaker,
    };

    /// <summary>
    /// FEATURE B: travel reimbursement is gated by ENTITLEMENT
    /// (<see cref="OrderItem.TravelReimbursement"/>), not role alone. Only a
    /// Supported speaker is entitled — a sponsor-self-funded / organizer-funded
    /// speaker is NOT, so they are denied the form even though their role is
    /// Speaker. The historical role set was speaker-only, so there is no
    /// non-speaker access to preserve here.
    /// </summary>
    private Task<bool> IsEligibleAsync(CurrentParticipant me, CancellationToken ct) =>
        FormEntitlementGate.IsEntitledAsync(
            _db, me.EventId, me.ParticipantId, OrderItem.TravelReimbursement, ct);

    public const decimal Cap300 = 300m;
    public const decimal Cap400 = 400m;
    public const decimal Cap800 = 800m;   // intercontinental single-speaker tier

    /// <summary>Choice token submitted from the dropdown.</summary>
    public const string Choice300   = "300";
    public const string Choice400   = "400";
    public const string Choice800   = "800";
    public const string ChoiceOther = "other";
    public const string ChoiceNone  = "";   // "I'm not claiming reimbursement"

    public const string SubmitInvoiceTaskKey = "travel:submit-ticket-invoice";

    /// <summary>
    /// REQUIREMENTS §48 — the ERP/bookkeeping inbox that must receive a copy of the
    /// reimbursement request with the receipt/invoice file(s) attached when a speaker
    /// submits Step 2. This is an e-conomic mailbox, NOT an imported participant, so
    /// the send MUST be ring-exempt (see SendErpCopyAsync) or the transport ring gate
    /// drops it as an unknown recipient.
    /// </summary>
    public const string ErpInbox = "773bilag1685551@e-conomic.dk";

    /// <summary>Accepted receipt/invoice upload types (matches the form copy: PDF / JPG / PNG).</summary>
    private static readonly string[] AllowedExt = { ".pdf", ".jpg", ".jpeg", ".png" };
    private const long MaxReceiptBytes = 15 * 1024 * 1024; // 15 MB per file

    [BindProperty] public bool   RequestReimbursement { get; set; }
    [BindProperty] public string? OriginCity { get; set; }
    /// <summary>One of: "", "300", "400", "other".</summary>
    [BindProperty] public string? AmountChoice { get; set; }
    /// <summary>Only used when AmountChoice == "other".</summary>
    [BindProperty] public decimal? OtherAmountEur { get; set; }
    [BindProperty] public string? Explanation { get; set; }

    /// <summary>Step 1: the receipt/invoice file the speaker is uploading.</summary>
    [BindProperty] public IFormFile? ReceiptFile { get; set; }

    public string FullName { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public ParticipantRole Role { get; private set; }
    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }
    public DateOnly? SubmitInvoiceDueDate { get; private set; }

    /// <summary>REQUIREMENTS §51 — when this reimbursement row was last saved (UpdatedAt); null = never saved.</summary>
    public DateTimeOffset? LastSavedAt { get; private set; }

    /// <summary>The speaker's already-uploaded receipts (Step 1 evidence).</summary>
    public List<ReceiptView> Receipts { get; private set; } = new();
    /// <summary>Step-1 gate: true once at least one receipt has been uploaded.</summary>
    public bool HasReceipt => Receipts.Count > 0;

    public record ReceiptView(int Id, string FileName, long SizeBytes, DateTimeOffset UploadedAt);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        FullName = me.FullName;
        Email = me.Email;
        Role = me.Role;
        if (!await IsEligibleAsync(me, ct))
        {
            AccessDenied = true;
            return Page();
        }

        SubmitInvoiceDueDate = await GetInvoiceDueDateAsync(me.EventId, ct);
        await LoadReceiptsAsync(me.EventId, me.ParticipantId, ct);

        var existing = await _db.TravelReimbursements.FirstOrDefaultAsync(
            t => t.EventId == me.EventId && t.ParticipantId == me.ParticipantId, ct);
        if (existing is not null)
        {
            RequestReimbursement = existing.RequestReimbursement;
            OriginCity = existing.OriginCity;
            Explanation = existing.Explanation;
            LastSavedAt = existing.UpdatedAt;

            (AmountChoice, OtherAmountEur) = DecomposeAmount(existing.ClaimAmountEur);
        }
        else
        {
            // Default = "No, not claiming" so the form is opt-in.
            RequestReimbursement = false;
        }
        return Page();
    }

    /// <summary>
    /// STEP 1 — upload a single receipt/invoice file. Stores the bytes in the DB so
    /// (a) the Step-2 gate can be answered without SharePoint and (b) the file can be
    /// attached to the ERP-inbox email on Step-2 submit. Returns to the form.
    /// </summary>
    public async Task<IActionResult> OnPostUploadReceiptAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        FullName = me.FullName;
        Email = me.Email;
        Role = me.Role;
        if (!await IsEligibleAsync(me, ct))
        {
            AccessDenied = true;
            return Page();
        }

        SubmitInvoiceDueDate = await GetInvoiceDueDateAsync(me.EventId, ct);

        if (ReceiptFile is null || ReceiptFile.Length == 0)
        {
            Error = "Please choose a receipt or invoice file to upload.";
            await ReloadStateAsync(me, ct);
            return Page();
        }
        var ext = Path.GetExtension(ReceiptFile.FileName).ToLowerInvariant();
        if (!AllowedExt.Contains(ext))
        {
            Error = "Unsupported file type. Upload a PDF, JPG or PNG.";
            await ReloadStateAsync(me, ct);
            return Page();
        }
        if (ReceiptFile.Length > MaxReceiptBytes)
        {
            Error = "File is too large (max 15 MB).";
            await ReloadStateAsync(me, ct);
            return Page();
        }

        using var ms = new MemoryStream();
        await ReceiptFile.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        _db.TravelReceipts.Add(new TravelReceipt
        {
            EventId = me.EventId,
            ParticipantId = me.ParticipantId,
            FileName = Path.GetFileName(ReceiptFile.FileName),
            ContentType = string.IsNullOrWhiteSpace(ReceiptFile.ContentType)
                ? "application/octet-stream"
                : ReceiptFile.ContentType,
            Content = bytes,
            SizeBytes = bytes.LongLength,
            UploadedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);

        Message = $"Uploaded {Path.GetFileName(ReceiptFile.FileName)}. You can now request reimbursement (Step 2).";
        await ReloadStateAsync(me, ct);
        return Page();
    }

    /// <summary>STEP 1 — remove a previously uploaded receipt.</summary>
    public async Task<IActionResult> OnPostDeleteReceiptAsync(int receiptId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        FullName = me.FullName;
        Email = me.Email;
        Role = me.Role;
        if (!await IsEligibleAsync(me, ct))
        {
            AccessDenied = true;
            return Page();
        }

        SubmitInvoiceDueDate = await GetInvoiceDueDateAsync(me.EventId, ct);

        var row = await _db.TravelReceipts.FirstOrDefaultAsync(
            r => r.Id == receiptId
                 && r.EventId == me.EventId
                 && r.ParticipantId == me.ParticipantId, ct);
        if (row is not null)
        {
            _db.TravelReceipts.Remove(row);
            await _db.SaveChangesAsync(ct);
            Message = $"Removed {row.FileName}.";
        }

        await ReloadStateAsync(me, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        FullName = me.FullName;
        Email = me.Email;
        Role = me.Role;
        if (!await IsEligibleAsync(me, ct))
        {
            AccessDenied = true;
            return Page();
        }

        SubmitInvoiceDueDate = await GetInvoiceDueDateAsync(me.EventId, ct);
        await LoadReceiptsAsync(me.EventId, me.ParticipantId, ct);

        // ---- STEP-2 GATE (REQUIREMENTS §48). Step 2 (requesting reimbursement) is
        // BLOCKED until Step 1 (≥1 receipt uploaded) is complete. The client disables
        // the Step-2 controls, but the server re-checks regardless so a JS-off / forged
        // POST is rejected with a clear message and nothing is saved or emailed.
        if (RequestReimbursement && !HasReceipt)
        {
            Error = "Upload at least one receipt or invoice in Step 1 before requesting reimbursement.";
            return Page();
        }

        // ---- Field-level validation (REQUIREMENTS §21 shared validation pattern).
        // Only meaningful when the user IS claiming; the opt-out path needs none.
        if (RequestReimbursement)
        {
            if (string.IsNullOrEmpty(AmountChoice))
            {
                ModelState.AddModelError(nameof(AmountChoice), _loc["Travel.ErrPickAmount"]);
            }
            else if (AmountChoice == ChoiceOther)
            {
                if (OtherAmountEur is not > 0)
                {
                    ModelState.AddModelError(nameof(OtherAmountEur), _loc["Travel.ErrOtherAmount"]);
                }
                if (string.IsNullOrWhiteSpace(Explanation))
                {
                    ModelState.AddModelError(nameof(Explanation), _loc["Travel.ErrOtherExplanation"]);
                }
            }
        }

        if (!ModelState.IsValid)
        {
            // Re-render with the field errors; do not touch the database.
            return Page();
        }

        var row = await _db.TravelReimbursements.FirstOrDefaultAsync(
            t => t.EventId == me.EventId && t.ParticipantId == me.ParticipantId, ct);

        if (row is null)
        {
            row = new TravelReimbursement
            {
                EventId = me.EventId,
                ParticipantId = me.ParticipantId,
                CreatedAt = _clock.GetUtcNow(),
                UpdatedAt = _clock.GetUtcNow(),
            };
            _db.TravelReimbursements.Add(row);
        }
        else
        {
            row.UpdatedAt = _clock.GetUtcNow();
        }

        row.RequestReimbursement = RequestReimbursement;
        row.OriginCity = string.IsNullOrWhiteSpace(OriginCity) ? null : OriginCity.Trim();

        if (RequestReimbursement)
        {
            row.ClaimAmountEur = ComposeAmount(AmountChoice, OtherAmountEur);
            row.Explanation = string.IsNullOrWhiteSpace(Explanation) ? null : Explanation.Trim();
        }
        else
        {
            // Not claiming: clear the claim fields so the row reflects opt-out.
            row.ClaimAmountEur = null;
            row.Explanation = null;
        }

        await _db.SaveChangesAsync(ct);

        await SyncSubmitInvoiceTaskAsync(me.EventId, me.ParticipantId,
            wantsTask: RequestReimbursement, ct);

        // The reimbursement row was actually saved on the claim path, so mark the
        // "Submit flight ticket + invoice" task Done (State + CompletedAt), mirroring
        // the other forms. The opt-out path REMOVES the task (in SyncSubmitInvoiceTaskAsync)
        // rather than completing it, so this only runs when claiming.
        if (RequestReimbursement)
        {
            await MarkSubmitInvoiceTaskDoneAsync(me.EventId, me.ParticipantId, ct);
        }

        // ---- ERP-INBOX COPY (REQUIREMENTS §48). On a real reimbursement request,
        // also email the bookkeeping inbox with the receipt file(s) + claim details
        // attached. Sent RING-EXEMPT because the ERP mailbox is not a ring-gated
        // participant. A mail failure must not fail the user's save.
        if (RequestReimbursement)
        {
            try
            {
                await SendErpCopyAsync(me, row, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Travel reimbursement ERP-inbox email failed for participant {Pid}", me.ParticipantId);
            }
        }

        Message = RequestReimbursement
            ? "Your travel reimbursement request has been saved and forwarded to our bookkeeping inbox " +
              "with your receipt(s) attached. A 'Submit flight ticket + invoice' task has been added to your hub."
            : "Saved. You are not claiming reimbursement; any submit-ticket+invoice task has been removed.";
        return Page();
    }

    /// <summary>
    /// Email the ERP/bookkeeping inbox a copy of the reimbursement request with the
    /// uploaded receipt/invoice file(s) attached. Subject is EXACTLY
    /// "Travel Rebursement - &lt;name&gt; - ELDK27" per REQUIREMENTS §48 (spelling as
    /// specified). Sent RING-EXEMPT (EmailContext.RingExempt = true) so the non-
    /// participant ERP recipient is not dropped by the transport ring gate.
    /// </summary>
    private async Task SendErpCopyAsync(CurrentParticipant me, TravelReimbursement row, CancellationToken ct)
    {
        var receipts = await _db.TravelReceipts
            .Where(r => r.EventId == me.EventId && r.ParticipantId == me.ParticipantId)
            .OrderBy(r => r.UploadedAt)
            .ToListAsync(ct);

        var attachments = receipts
            .Where(r => r.Content is { Length: > 0 })
            .Select(r => new EmailAttachment(r.FileName, r.Content, r.ContentType))
            .ToList();

        var subject = $"Travel Rebursement - {me.FullName} - ELDK27";

        var amount = row.ClaimAmountEur?.ToString("0.##") ?? "(not specified)";
        var enc = (string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var body = new StringBuilder();
        body.Append("<p>A travel reimbursement request has been submitted via the Experts Live DK Event Hub.</p>");
        body.Append("<table style=\"border-collapse:collapse;font-size:14px;\">");
        body.Append($"<tr><td style=\"padding:2px 12px 2px 0;\"><strong>Name</strong></td><td>{enc(me.FullName)}</td></tr>");
        body.Append($"<tr><td style=\"padding:2px 12px 2px 0;\"><strong>Email</strong></td><td>{enc(me.Email)}</td></tr>");
        body.Append($"<tr><td style=\"padding:2px 12px 2px 0;\"><strong>Origin city</strong></td><td>{enc(row.OriginCity)}</td></tr>");
        body.Append($"<tr><td style=\"padding:2px 12px 2px 0;\"><strong>Amount claimed (EUR)</strong></td><td>{enc(amount)}</td></tr>");
        body.Append($"<tr><td style=\"padding:2px 12px 2px 0;\"><strong>Explanation</strong></td><td>{enc(row.Explanation)}</td></tr>");
        body.Append("</table>");
        body.Append($"<p style=\"font-size:14px;\">Attached file(s): {attachments.Count}</p>");

        // RING-EXEMPT: the ERP mailbox is not a ring-gated participant, so without this
        // the transport ring gate would drop it as an unknown recipient. The global
        // kill switch + DEV redirect still apply.
        using (_emailContext?.Set(new EmailContext(
            "travel-reimbursement-erp", me.EventId, me.ParticipantId, me.FullName, RingExempt: true)))
        {
            if (attachments.Count > 0)
            {
                await _email.SendWithAttachmentsAsync(ErpInbox, subject, body.ToString(), attachments, ct);
            }
            else
            {
                // Defensive: the Step-2 gate guarantees ≥1 receipt, so this branch
                // should not normally run. Send the details without attachments rather
                // than silently dropping the ERP notification.
                await _email.SendAsync(ErpInbox, subject, body.ToString(), ct);
            }
        }
    }

    private async Task ReloadStateAsync(CurrentParticipant me, CancellationToken ct)
    {
        await LoadReceiptsAsync(me.EventId, me.ParticipantId, ct);
        var existing = await _db.TravelReimbursements.FirstOrDefaultAsync(
            t => t.EventId == me.EventId && t.ParticipantId == me.ParticipantId, ct);
        if (existing is not null)
        {
            RequestReimbursement = existing.RequestReimbursement;
            OriginCity = existing.OriginCity;
            Explanation = existing.Explanation;
            LastSavedAt = existing.UpdatedAt;
            (AmountChoice, OtherAmountEur) = DecomposeAmount(existing.ClaimAmountEur);
        }
    }

    private async Task LoadReceiptsAsync(int eventId, int participantId, CancellationToken ct)
    {
        Receipts = await _db.TravelReceipts
            .Where(r => r.EventId == eventId && r.ParticipantId == participantId)
            .OrderBy(r => r.UploadedAt)
            .Select(r => new ReceiptView(r.Id, r.FileName, r.SizeBytes, r.UploadedAt))
            .ToListAsync(ct);
    }

    private static (string?, decimal?) DecomposeAmount(decimal? amount)
    {
        if (amount is null) return (ChoiceNone, null);
        if (amount == Cap300) return (Choice300, null);
        if (amount == Cap400) return (Choice400, null);
        if (amount == Cap800) return (Choice800, null);
        return (ChoiceOther, amount);
    }

    private static decimal? ComposeAmount(string? choice, decimal? other) => choice switch
    {
        Choice300 => Cap300,
        Choice400 => Cap400,
        Choice800 => Cap800,
        ChoiceOther => other is > 0 ? other : null,
        _ => null,
    };

    private async Task<DateOnly?> GetInvoiceDueDateAsync(int eventId, CancellationToken ct)
    {
        var startDate = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => (DateOnly?)e.StartDate)
            .FirstOrDefaultAsync(ct);
        return startDate?.AddDays(-30);
    }

    private async Task SyncSubmitInvoiceTaskAsync(
        int eventId, int participantId, bool wantsTask, CancellationToken ct)
    {
        var sourceKey = $"{SubmitInvoiceTaskKey}:{participantId}";
        var existing = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId
                 && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey, ct);

        if (wantsTask)
        {
            if (existing is null)
            {
                var due = await GetInvoiceDueDateAsync(eventId, ct);
                _db.Tasks.Add(new ParticipantTask
                {
                    EventId = eventId,
                    AssignedParticipantId = participantId,
                    Title = "Submit flight ticket + invoice for reimbursement",
                    Description =
                        $"Send your economy flight ticket + invoice to ELDK by {due:dd/MM/yyyy} " +
                        "(30 days before the event).",
                    DueDate = due,
                    State = TaskState.Open,
                    SourceKey = sourceKey,
                    CreatedAt = _clock.GetUtcNow(),
                });
                await _db.SaveChangesAsync(ct);
            }
        }
        else if (existing is not null && existing.State != TaskState.Done)
        {
            _db.Tasks.Remove(existing);
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Mark the "Submit flight ticket + invoice" task Done (State + CompletedAt) once a
    /// reimbursement claim has been saved. SourceKey matches SyncSubmitInvoiceTaskAsync.
    /// </summary>
    private async Task MarkSubmitInvoiceTaskDoneAsync(
        int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = $"{SubmitInvoiceTaskKey}:{participantId}";
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId
                 && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey, ct);
        if (task is null || task.State == TaskState.Done) return;
        task.State = TaskState.Done;
        task.CompletedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }
}
