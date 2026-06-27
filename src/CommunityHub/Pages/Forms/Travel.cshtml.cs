using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Resources;
using CommunityHub.Forms.Steps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// Travel-reimbursement form (REQUIREMENTS §48). A two-step form: Step 1 uploads the
/// receipt/invoice file(s); Step 2 submits the reimbursement claim (gated until ≥1 receipt
/// exists) which upserts the request, syncs the "submit ticket + invoice" task and emails the
/// receipts RING-EXEMPT to the ERP inbox.
///
/// <para>REQUIREMENTS §148: this standalone page is now a thin SHELL — it renders the shared
/// <c>_TravelFields</c> partial and delegates load + the two Step-1 sub-actions + the Step-2
/// validate / persist / side-effects to <see cref="TravelFormService"/>. The SAME service backs
/// the inline wizard step (<c>TravelStepHandler</c>), so the standalone page and the wizard
/// behave identically. The page stays deep-linked from My Tasks / emails.</para>
///
/// <para>The form's <c>[BindProperty]</c> fields + the amount-cap constants are RETAINED as the
/// page's public surface (they are part of the contract the validation unit tests drive); the
/// page binds them, builds a <see cref="TravelFormModel"/> and hands it to the shared service,
/// so the logic itself lives in exactly one place.</para>
/// </summary>
[Authorize]
public class TravelModel : PageModel
{
    private readonly TravelFormService _travel;
    private readonly ICurrentParticipantAccessor _participant;

    /// <summary>Primary (DI) constructor — injects the shared <see cref="TravelFormService"/>.</summary>
    [ActivatorUtilitiesConstructor]
    public TravelModel(TravelFormService travel, ICurrentParticipantAccessor participant)
    {
        _travel = travel;
        _participant = participant;
    }

    /// <summary>
    /// Backward-compatible constructor (raw deps) used by the existing field-validation unit
    /// tests. It builds the SAME shared service from the supplied deps, so the delegated logic
    /// is identical to the DI path.
    /// </summary>
    public TravelModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock,
        IStringLocalizer<SharedResource> loc,
        IEmailSender email,
        ILogger<TravelModel> logger,
        IEmailContextAccessor? emailContext = null)
        : this(
            new TravelFormService(db, clock, loc, email, NullLogger<TravelFormService>.Instance, emailContext),
            participant)
    {
    }

    // ----- amount caps + dropdown tokens (public contract; mirror TravelFormModel) ----
    public const decimal Cap300 = TravelFormModel.Cap300;
    public const decimal Cap400 = TravelFormModel.Cap400;
    public const decimal Cap800 = TravelFormModel.Cap800;

    public const string Choice300   = TravelFormModel.Choice300;
    public const string Choice400   = TravelFormModel.Choice400;
    public const string Choice800   = TravelFormModel.Choice800;
    public const string ChoiceOther = TravelFormModel.ChoiceOther;
    public const string ChoiceNone  = TravelFormModel.ChoiceNone;

    // ----- bound editable fields (flat names match the _TravelFields partial) ----
    [BindProperty] public bool   RequestReimbursement { get; set; }
    [BindProperty] public string? OriginCity { get; set; }
    /// <summary>One of: "", "300", "400", "800", "other".</summary>
    [BindProperty] public string? AmountChoice { get; set; }
    /// <summary>Only used when AmountChoice == "other".</summary>
    [BindProperty] public decimal? OtherAmountEur { get; set; }
    [BindProperty] public string? Explanation { get; set; }
    /// <summary>Step 1: the receipt/invoice file the speaker is uploading.</summary>
    [BindProperty] public IFormFile? ReceiptFile { get; set; }

    /// <summary>The shared render model the <c>_TravelFields</c> partial binds to.</summary>
    public TravelFormModel Form { get; private set; } = new();

    /// <summary>True when the participant is NOT entitled to travel reimbursement (gate).</summary>
    public bool AccessDenied { get; private set; }

    public string? Message { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Step-1 gate: true once at least one receipt has been uploaded.</summary>
    public bool HasReceipt => Form.Receipts.Count > 0;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        if (!await _travel.IsRelevantAsync(me.EventId, me.ParticipantId, me.Role, ct))
        {
            AccessDenied = true;
            Form = new TravelFormModel { Role = me.Role };
            return Page();
        }

        Form = await _travel.LoadAsync(me.EventId, me.ParticipantId, me.Role, me.FullName, me.Email, ct);
        SyncFromForm();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        if (!await _travel.IsRelevantAsync(me.EventId, me.ParticipantId, me.Role, ct))
        {
            AccessDenied = true;
            Form = new TravelFormModel { Role = me.Role };
            return Page();
        }

        // Build the shared model from the bound editable fields, then route exactly as the inline
        // wizard step does. The page always re-renders (no PRG) — same as before the refactor.
        Form = new TravelFormModel
        {
            Role = me.Role,
            FullName = me.FullName,
            Email = me.Email,
            RequestReimbursement = RequestReimbursement,
            OriginCity = OriginCity,
            AmountChoice = AmountChoice,
            OtherAmountEur = OtherAmountEur,
            Explanation = Explanation,
            ReceiptFile = ReceiptFile,
        };

        // Step-1 sub-actions are posted as __action=uploadReceipt / deleteReceipt:{id}. Guard the
        // form read so the validation unit tests (which set properties directly, no form body) hit
        // the main save path.
        var action = Request.HasFormContentType ? Request.Form["__action"].ToString() : string.Empty;
        if (string.Equals(action, "uploadReceipt", StringComparison.Ordinal))
        {
            await _travel.UploadReceiptAsync(Form, me.EventId, me.ParticipantId, ct);
            SyncFromForm();
            return Page();
        }
        if (action.StartsWith("deleteReceipt", StringComparison.Ordinal))
        {
            await _travel.DeleteReceiptAsync(Form, ParseReceiptId(action), me.EventId, me.ParticipantId, ct);
            SyncFromForm();
            return Page();
        }

        await _travel.SaveAsync(
            Form, me.EventId, me.ParticipantId, me.Email, me.FullName, me.Role, ModelState, ct);
        SyncFromForm();
        return Page();
    }

    /// <summary>Surface the shared model's flash + claim state on the page (for rendering + the tests).</summary>
    private void SyncFromForm()
    {
        Message = Form.Message;
        Error = Form.Error;
        RequestReimbursement = Form.RequestReimbursement;
        OriginCity = Form.OriginCity;
        AmountChoice = Form.AmountChoice;
        OtherAmountEur = Form.OtherAmountEur;
        Explanation = Form.Explanation;
    }

    /// <summary>The delete sub-action carries the row id as <c>deleteReceipt:{id}</c>.</summary>
    private static int ParseReceiptId(string action)
    {
        var i = action.IndexOf(':');
        return i >= 0 && int.TryParse(action.AsSpan(i + 1), out var id) ? id : 0;
    }
}
