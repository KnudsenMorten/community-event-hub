using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// The render + edit model for the Travel-reimbursement step (REQUIREMENTS §148, §48). It is
/// shared by the standalone <c>/Forms/Travel</c> page AND the inline wizard step, and is the
/// model the <c>_TravelFields</c> partial binds to. The EDITABLE fields (top) are the only ones
/// model binding fills; the DISPLAY fields are <see cref="BindNeverAttribute"/> and are populated
/// by <see cref="TravelFormService"/> (load / upload / delete / save), never from the POST.
/// </summary>
public sealed class TravelFormModel
{
    // ----- amount caps + dropdown tokens (used by the partial + the service) ----
    public const decimal Cap300 = 300m;
    public const decimal Cap400 = 400m;
    public const decimal Cap800 = 800m;   // intercontinental single-speaker tier

    public const string Choice300   = "300";
    public const string Choice400   = "400";
    public const string Choice800   = "800";
    public const string ChoiceOther = "other";
    public const string ChoiceNone  = "";   // "I'm not claiming reimbursement"

    // ----- editable (bound from the POST) --------------------------------
    public bool RequestReimbursement { get; set; }
    public string? OriginCity { get; set; }
    /// <summary>One of: "", "300", "400", "800", "other".</summary>
    public string? AmountChoice { get; set; }
    /// <summary>Only used when AmountChoice == "other".</summary>
    public decimal? OtherAmountEur { get; set; }
    public string? Explanation { get; set; }

    /// <summary>Step 1: the receipt/invoice file the speaker is uploading (the upload sub-action).</summary>
    public IFormFile? ReceiptFile { get; set; }

    // ----- display-only (set by the service; never bound) -----------------
    [BindNever] public string FullName { get; set; } = string.Empty;
    [BindNever] public string Email { get; set; } = string.Empty;
    [BindNever] public ParticipantRole Role { get; set; }
    [BindNever] public string? Message { get; set; }
    [BindNever] public string? Error { get; set; }

    /// <summary>REQUIREMENTS §51 — when this reimbursement row was last saved (UpdatedAt); null = never.</summary>
    [BindNever] public DateTimeOffset? LastSavedAt { get; set; }
    [BindNever] public DateOnly? SubmitInvoiceDueDate { get; set; }

    /// <summary>The speaker's already-uploaded receipts (Step 1 evidence).</summary>
    [BindNever] public List<ReceiptView> Receipts { get; set; } = new();

    /// <summary>Step-1 gate: true once at least one receipt has been uploaded.</summary>
    [BindNever] public bool HasReceipt => Receipts.Count > 0;

    public record ReceiptView(int Id, string FileName, long SizeBytes, DateTimeOffset UploadedAt);
}

/// <summary>
/// Shared submit-service for the Travel-reimbursement form (REQUIREMENTS §148, §48). It
/// encapsulates the form's ENTIRE behavior — the OnGet load, the two Step-1 sub-actions
/// (upload / delete receipt) and the Step-2 validate / persist + ALL side-effects
/// (Step-2 receipt gate, SyncSubmitInvoiceTask + MarkSubmitInvoiceTaskDone, the
/// RING-EXEMPT ERP-inbox email with receipt attachments) — so that BOTH the standalone
/// <c>/Forms/Travel</c> page and the inline <see cref="TravelStepHandler"/> call the exact
/// same logic and stay identical. Implements <see cref="IWizardFormService"/> so it
/// self-registers by concrete type.
/// </summary>
public sealed class TravelFormService : IWizardFormService
{
    /// <summary>SourceKey prefix for the "submit ticket + invoice" auto-task — <c>travel:submit-ticket-invoice:{pid}</c>.</summary>
    public const string SubmitInvoiceTaskKey = "travel:submit-ticket-invoice";

    /// <summary>
    /// REQUIREMENTS §48 — the ERP/bookkeeping inbox that must receive a copy of the
    /// reimbursement request with the receipt/invoice file(s) attached when a speaker
    /// submits Step 2. This is an e-conomic mailbox, NOT an imported participant, so the
    /// send MUST be ring-exempt (see <see cref="SendErpCopyAsync"/>) or the transport ring
    /// gate drops it as an unknown recipient.
    /// </summary>
    public const string ErpInbox = "773bilag1685551@e-conomic.dk";

    /// <summary>Accepted receipt/invoice upload types (matches the form copy: PDF / JPG / PNG).</summary>
    private static readonly string[] AllowedExt = { ".pdf", ".jpg", ".jpeg", ".png" };
    private const long MaxReceiptBytes = 15 * 1024 * 1024; // 15 MB per file

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IStringLocalizer<SharedResource> _loc;
    private readonly IEmailSender _email;
    private readonly IEmailContextAccessor? _emailContext;
    private readonly ILogger<TravelFormService> _logger;

    public TravelFormService(
        CommunityHubDbContext db,
        TimeProvider clock,
        IStringLocalizer<SharedResource> loc,
        IEmailSender email,
        ILogger<TravelFormService> logger,
        IEmailContextAccessor? emailContext = null)
    {
        _db = db;
        _clock = clock;
        _loc = loc;
        _email = email;
        _logger = logger;
        _emailContext = emailContext;
    }

    /// <summary>
    /// Relevance gate (REQUIREMENTS §148): travel reimbursement is gated by ENTITLEMENT
    /// (<see cref="OrderItem.TravelReimbursement"/>), not role alone. Only a Supported speaker
    /// (and any role granted the item via override) is entitled — a self-funded /
    /// organizer-funded speaker is NOT, so the step is NotRelevant for them.
    /// </summary>
    public Task<bool> IsRelevantAsync(int eventId, int participantId, ParticipantRole role, CancellationToken ct) =>
        FormEntitlementGate.IsEntitledAsync(_db, eventId, participantId, OrderItem.TravelReimbursement, ct);

    /// <summary>Completion detection (REQUIREMENTS §148) — a <see cref="TravelReimbursement"/> row
    /// exists for (eventId, participantId). Mirrors RoleWizardService.</summary>
    public Task<bool> IsDoneAsync(int eventId, int participantId, CancellationToken ct) =>
        _db.TravelReimbursements.AnyAsync(t => t.EventId == eventId && t.ParticipantId == participantId, ct);

    /// <summary>
    /// Load the form's current state — the SAME load the standalone page's OnGet used: the
    /// invoice due date, the uploaded receipts, and any existing reimbursement row decomposed
    /// back into the dropdown choice + "other" amount. Returns a fully-populated model.
    /// </summary>
    public async Task<TravelFormModel> LoadAsync(
        int eventId, int participantId, ParticipantRole role, string fullName, string email, CancellationToken ct)
    {
        var model = new TravelFormModel { Role = role, FullName = fullName, Email = email };
        model.SubmitInvoiceDueDate = await GetInvoiceDueDateAsync(eventId, ct);
        await LoadFullStateAsync(model, eventId, participantId, ct);
        return model;
    }

    /// <summary>
    /// STEP 1 — upload a single receipt/invoice file (the <c>uploadReceipt</c> sub-action).
    /// Stores the bytes in the DB so (a) the Step-2 gate can be answered without SharePoint
    /// and (b) the file can be attached to the ERP-inbox email on Step-2 submit. Mutates the
    /// model's Message/Error + reloads its state so the SAME step re-renders.
    /// </summary>
    public async Task UploadReceiptAsync(TravelFormModel model, int eventId, int participantId, CancellationToken ct)
    {
        model.SubmitInvoiceDueDate ??= await GetInvoiceDueDateAsync(eventId, ct);

        var file = model.ReceiptFile;
        if (file is null || file.Length == 0)
        {
            model.Error = "Please choose a receipt or invoice file to upload.";
            await LoadFullStateAsync(model, eventId, participantId, ct);
            return;
        }
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExt.Contains(ext))
        {
            model.Error = "Unsupported file type. Upload a PDF, JPG or PNG.";
            await LoadFullStateAsync(model, eventId, participantId, ct);
            return;
        }
        if (file.Length > MaxReceiptBytes)
        {
            model.Error = "File is too large (max 15 MB).";
            await LoadFullStateAsync(model, eventId, participantId, ct);
            return;
        }

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        _db.TravelReceipts.Add(new TravelReceipt
        {
            EventId = eventId,
            ParticipantId = participantId,
            FileName = Path.GetFileName(file.FileName),
            ContentType = string.IsNullOrWhiteSpace(file.ContentType)
                ? "application/octet-stream"
                : file.ContentType,
            Content = bytes,
            SizeBytes = bytes.LongLength,
            UploadedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);

        model.Message = $"Uploaded {Path.GetFileName(file.FileName)}. You can now request reimbursement (Step 2).";
        await LoadFullStateAsync(model, eventId, participantId, ct);
    }

    /// <summary>STEP 1 — remove a previously uploaded receipt (the <c>deleteReceipt</c> sub-action).</summary>
    public async Task DeleteReceiptAsync(
        TravelFormModel model, int receiptId, int eventId, int participantId, CancellationToken ct)
    {
        model.SubmitInvoiceDueDate ??= await GetInvoiceDueDateAsync(eventId, ct);

        var row = await _db.TravelReceipts.FirstOrDefaultAsync(
            r => r.Id == receiptId && r.EventId == eventId && r.ParticipantId == participantId, ct);
        if (row is not null)
        {
            _db.TravelReceipts.Remove(row);
            await _db.SaveChangesAsync(ct);
            model.Message = $"Removed {row.FileName}.";
        }

        await LoadFullStateAsync(model, eventId, participantId, ct);
    }

    /// <summary>
    /// STEP 2 — validate + persist + run all side-effects (REQUIREMENTS §148, §48), the SAME
    /// logic the standalone page's OnPost ran. The Step-2 receipt gate + field validation write
    /// into <paramref name="modelState"/> / <see cref="TravelFormModel.Error"/>
    /// (=> <see cref="WizardStepOutcome.Invalid"/>); on success the reimbursement is upserted,
    /// the submit-invoice task is synced + marked done, and (on a real claim) the receipt files
    /// are emailed RING-EXEMPT to the ERP inbox, fail-soft. The receipt gate is RE-READ from the
    /// DB here so a crafted POST can never bypass it.
    /// </summary>
    public async Task<WizardStepOutcome> SaveAsync(
        TravelFormModel model, int eventId, int participantId, string email, string fullName,
        ParticipantRole role, ModelStateDictionary modelState, CancellationToken ct)
    {
        model.Role = role;
        model.FullName = fullName;
        model.Email = email;
        model.SubmitInvoiceDueDate = await GetInvoiceDueDateAsync(eventId, ct);

        // Re-read receipts for the gate (NOT the reimbursement row — the posted editable
        // values must survive into a re-render).
        await LoadReceiptsAsync(model, eventId, participantId, ct);

        // ---- STEP-2 GATE (REQUIREMENTS §48). Step 2 is BLOCKED until ≥1 receipt exists.
        // The client disables the Step-2 controls, but the server re-checks regardless so a
        // JS-off / forged POST is rejected with a clear message and nothing is saved/emailed.
        if (model.RequestReimbursement && !model.HasReceipt)
        {
            model.Error = "Upload at least one receipt or invoice in Step 1 before requesting reimbursement.";
            return WizardStepOutcome.Invalid;
        }

        // ---- Field-level validation (REQUIREMENTS §21). Only meaningful when claiming.
        if (model.RequestReimbursement)
        {
            if (string.IsNullOrEmpty(model.AmountChoice))
            {
                modelState.AddModelError(nameof(model.AmountChoice), _loc["Travel.ErrPickAmount"]);
            }
            else if (model.AmountChoice == TravelFormModel.ChoiceOther)
            {
                if (model.OtherAmountEur is not > 0)
                {
                    modelState.AddModelError(nameof(model.OtherAmountEur), _loc["Travel.ErrOtherAmount"]);
                }
                if (string.IsNullOrWhiteSpace(model.Explanation))
                {
                    modelState.AddModelError(nameof(model.Explanation), _loc["Travel.ErrOtherExplanation"]);
                }
            }
        }

        if (!modelState.IsValid)
        {
            // Re-render with the field errors; do not touch the database.
            return WizardStepOutcome.Invalid;
        }

        var row = await _db.TravelReimbursements.FirstOrDefaultAsync(
            t => t.EventId == eventId && t.ParticipantId == participantId, ct);

        if (row is null)
        {
            row = new TravelReimbursement
            {
                EventId = eventId,
                ParticipantId = participantId,
                CreatedAt = _clock.GetUtcNow(),
                UpdatedAt = _clock.GetUtcNow(),
            };
            _db.TravelReimbursements.Add(row);
        }
        else
        {
            row.UpdatedAt = _clock.GetUtcNow();
        }

        row.RequestReimbursement = model.RequestReimbursement;
        row.OriginCity = string.IsNullOrWhiteSpace(model.OriginCity) ? null : model.OriginCity.Trim();

        if (model.RequestReimbursement)
        {
            row.ClaimAmountEur = ComposeAmount(model.AmountChoice, model.OtherAmountEur);
            row.Explanation = string.IsNullOrWhiteSpace(model.Explanation) ? null : model.Explanation.Trim();
        }
        else
        {
            // Not claiming: clear the claim fields so the row reflects opt-out.
            row.ClaimAmountEur = null;
            row.Explanation = null;
        }

        await _db.SaveChangesAsync(ct);

        await SyncSubmitInvoiceTaskAsync(eventId, participantId, wantsTask: model.RequestReimbursement, ct);

        // The reimbursement row was actually saved on the claim path, so mark the
        // "Submit flight ticket + invoice" task Done. The opt-out path REMOVES the task
        // (in SyncSubmitInvoiceTaskAsync) rather than completing it, so this only runs when claiming.
        if (model.RequestReimbursement)
        {
            await MarkSubmitInvoiceTaskDoneAsync(eventId, participantId, ct);
        }

        // ---- ERP-INBOX COPY (REQUIREMENTS §48). On a real reimbursement request, also email
        // the bookkeeping inbox with the receipt file(s) + claim details attached. Sent
        // RING-EXEMPT because the ERP mailbox is not a ring-gated participant. A mail failure
        // must not fail the user's save.
        if (model.RequestReimbursement)
        {
            try
            {
                await SendErpCopyAsync(eventId, participantId, fullName, email, row, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Travel reimbursement ERP-inbox email failed for participant {Pid}", participantId);
            }
        }

        model.LastSavedAt = row.UpdatedAt;
        model.Message = model.RequestReimbursement
            ? "Your travel reimbursement request has been saved and forwarded to our bookkeeping inbox " +
              "with your receipt(s) attached. A 'Submit flight ticket + invoice' task has been added to your hub."
            : "Saved. You are not claiming reimbursement; any submit-ticket+invoice task has been removed.";
        return WizardStepOutcome.Advance;
    }

    // ----- internal load helpers ------------------------------------------

    /// <summary>Load receipts AND the existing reimbursement row (overwrites editable fields) —
    /// used by load + the upload/delete sub-actions, which re-show the persisted state.</summary>
    private async Task LoadFullStateAsync(TravelFormModel model, int eventId, int participantId, CancellationToken ct)
    {
        await LoadReceiptsAsync(model, eventId, participantId, ct);

        var existing = await _db.TravelReimbursements.FirstOrDefaultAsync(
            t => t.EventId == eventId && t.ParticipantId == participantId, ct);
        if (existing is not null)
        {
            model.RequestReimbursement = existing.RequestReimbursement;
            model.OriginCity = existing.OriginCity;
            model.Explanation = existing.Explanation;
            model.LastSavedAt = existing.UpdatedAt;
            (model.AmountChoice, model.OtherAmountEur) = DecomposeAmount(existing.ClaimAmountEur);
        }
        else
        {
            // Default = "No, not claiming" so the form is opt-in.
            model.RequestReimbursement = false;
        }
    }

    /// <summary>Load ONLY the uploaded receipts (for the Step-2 gate); leaves editable fields intact.</summary>
    private async Task LoadReceiptsAsync(TravelFormModel model, int eventId, int participantId, CancellationToken ct)
    {
        model.Receipts = await _db.TravelReceipts
            .Where(r => r.EventId == eventId && r.ParticipantId == participantId)
            .OrderBy(r => r.UploadedAt)
            .Select(r => new TravelFormModel.ReceiptView(r.Id, r.FileName, r.SizeBytes, r.UploadedAt))
            .ToListAsync(ct);
    }

    // ----- ERP-inbox email (REQUIREMENTS §48) -----------------------------
    private async Task SendErpCopyAsync(
        int eventId, int participantId, string fullName, string email, TravelReimbursement row, CancellationToken ct)
    {
        var receipts = await _db.TravelReceipts
            .Where(r => r.EventId == eventId && r.ParticipantId == participantId)
            .OrderBy(r => r.UploadedAt)
            .ToListAsync(ct);

        var attachments = receipts
            .Where(r => r.Content is { Length: > 0 })
            .Select(r => new EmailAttachment(r.FileName, r.Content, r.ContentType))
            .ToList();

        var subject = $"Travel Rebursement - {fullName} - ELDK27";

        var amount = row.ClaimAmountEur?.ToString("0.##") ?? "(not specified)";
        var enc = (string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var body = new StringBuilder();
        body.Append("<p>A travel reimbursement request has been submitted via the Experts Live DK Event Hub.</p>");
        body.Append("<table style=\"border-collapse:collapse;font-size:14px;\">");
        body.Append($"<tr><td style=\"padding:2px 12px 2px 0;\"><strong>Name</strong></td><td>{enc(fullName)}</td></tr>");
        body.Append($"<tr><td style=\"padding:2px 12px 2px 0;\"><strong>Email</strong></td><td>{enc(email)}</td></tr>");
        body.Append($"<tr><td style=\"padding:2px 12px 2px 0;\"><strong>Origin city</strong></td><td>{enc(row.OriginCity)}</td></tr>");
        body.Append($"<tr><td style=\"padding:2px 12px 2px 0;\"><strong>Amount claimed (EUR)</strong></td><td>{enc(amount)}</td></tr>");
        body.Append($"<tr><td style=\"padding:2px 12px 2px 0;\"><strong>Explanation</strong></td><td>{enc(row.Explanation)}</td></tr>");
        body.Append("</table>");
        body.Append($"<p style=\"font-size:14px;\">Attached file(s): {attachments.Count}</p>");

        // RING-EXEMPT: the ERP mailbox is not a ring-gated participant, so without this the
        // transport ring gate would drop it as an unknown recipient. The global kill switch +
        // DEV redirect still apply.
        using (_emailContext?.Set(new EmailContext(
            "travel-reimbursement-erp", eventId, participantId, fullName, RingExempt: true)))
        {
            if (attachments.Count > 0)
            {
                await _email.SendWithAttachmentsAsync(ErpInbox, subject, body.ToString(), attachments, ct);
            }
            else
            {
                // Defensive: the Step-2 gate guarantees ≥1 receipt, so this branch should not
                // normally run. Send the details without attachments rather than silently
                // dropping the ERP notification.
                await _email.SendAsync(ErpInbox, subject, body.ToString(), ct);
            }
        }
    }

    // ----- amount compose/decompose ---------------------------------------
    private static (string?, decimal?) DecomposeAmount(decimal? amount)
    {
        if (amount is null) return (TravelFormModel.ChoiceNone, null);
        if (amount == TravelFormModel.Cap300) return (TravelFormModel.Choice300, null);
        if (amount == TravelFormModel.Cap400) return (TravelFormModel.Choice400, null);
        if (amount == TravelFormModel.Cap800) return (TravelFormModel.Choice800, null);
        return (TravelFormModel.ChoiceOther, amount);
    }

    private static decimal? ComposeAmount(string? choice, decimal? other) => choice switch
    {
        TravelFormModel.Choice300 => TravelFormModel.Cap300,
        TravelFormModel.Choice400 => TravelFormModel.Cap400,
        TravelFormModel.Choice800 => TravelFormModel.Cap800,
        TravelFormModel.ChoiceOther => other is > 0 ? other : null,
        _ => null,
    };

    // ----- submit-invoice auto-task ---------------------------------------
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
