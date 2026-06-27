namespace CommunityHub.Forms.Steps;

/// <summary>
/// The inline wizard step for the Travel-reimbursement form (REQUIREMENTS §148, §48). It owns
/// NO chrome and NO form logic — it renders the shared <c>_TravelFields</c> partial and delegates
/// load + the two Step-1 sub-actions + the Step-2 save to <see cref="TravelFormService"/> (the
/// SAME service the standalone <c>/Forms/Travel</c> page uses), so the inline step and the
/// standalone page behave identically. Discovered + registered automatically via
/// <see cref="IWizardStepHandler"/>.
///
/// <para>This is a TWO-STEP form: in addition to the host's Save&amp;next (the Step-2 claim), the
/// partial posts named in-step sub-actions <c>__action=uploadReceipt|deleteReceipt</c>. Those are
/// handled HERE (before the main save) and re-render the SAME step (Invalid) so the participant
/// stays put while building up their receipts. The host form must be <c>multipart/form-data</c>
/// for the upload — the partial's upload button carries <c>formenctype="multipart/form-data"</c>
/// so the file posts correctly without the generic host needing to know about files.</para>
/// </summary>
public sealed class TravelStepHandler : IWizardStepHandler
{
    private readonly TravelFormService _service;

    public TravelStepHandler(TravelFormService service) => _service = service;

    /// <summary>Matches the "travel" key emitted by RoleWizardService + the resx key.</summary>
    public string Key => "travel";

    /// <summary>Fields-only partial (no &lt;form&gt;/chrome) rendered inside the host's one form.</summary>
    public string PartialName => "_TravelFields";

    /// <summary>The render model handed to the partial; carries posted values back on Invalid.</summary>
    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx)
    {
        Model = await _service.LoadAsync(
            ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.FullName, ctx.Email, ctx.Ct);
    }

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        // Relevance gate — skip the step entirely when the participant isn't entitled.
        if (!await _service.IsRelevantAsync(ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.Ct))
            return WizardStepOutcome.NotRelevant;

        // Bind the posted fields into a FRESH model (editable values come purely from the POST,
        // exactly as the standalone page's [BindProperty] did). The IFormFile binds from the
        // multipart body when the upload sub-action posts.
        var model = new TravelFormModel { Role = ctx.Role, FullName = ctx.FullName, Email = ctx.Email };
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        // ---- Step-1 SUB-ACTIONS run BEFORE the main save and re-render the SAME step (Invalid).
        var action = ctx.Request.HasFormContentType ? ctx.Request.Form["__action"].ToString() : string.Empty;
        if (string.Equals(action, "uploadReceipt", StringComparison.Ordinal))
        {
            await _service.UploadReceiptAsync(model, ctx.EventId, ctx.ParticipantId, ctx.Ct);
            return WizardStepOutcome.Invalid;
        }
        if (action.StartsWith("deleteReceipt", StringComparison.Ordinal))
        {
            await _service.DeleteReceiptAsync(model, ParseReceiptId(action), ctx.EventId, ctx.ParticipantId, ctx.Ct);
            return WizardStepOutcome.Invalid;
        }

        // ---- Main Step-2 save (validate + persist + side-effects) via the shared service.
        return await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.Email, ctx.FullName, ctx.Role, ctx.ModelState, ctx.Ct);
    }

    /// <summary>The delete sub-action carries the row id as <c>deleteReceipt:{id}</c>.</summary>
    private static int ParseReceiptId(string action)
    {
        var i = action.IndexOf(':');
        return i >= 0 && int.TryParse(action.AsSpan(i + 1), out var id) ? id : 0;
    }
}
