namespace CommunityHub.Forms.Steps;

/// <summary>
/// The inline wizard step for the Swag form (REQUIREMENTS §148). It owns NO chrome and NO
/// form logic — it renders the shared <c>_SwagFields</c> partial and delegates load + save to
/// <see cref="SwagFormService"/> (the SAME service the standalone <c>/Forms/Swag</c> page uses),
/// so the inline step and the standalone page behave identically. Discovered + registered
/// automatically via <see cref="IWizardStepHandler"/>. Mirrors the reference
/// <see cref="HotelStepHandler"/>.
/// </summary>
public sealed class SwagStepHandler : IWizardStepHandler
{
    private readonly SwagFormService _service;

    public SwagStepHandler(SwagFormService service) => _service = service;

    /// <summary>Matches the "swag" key emitted by SpeakerWizardService / RoleWizardService + the resx key.</summary>
    public string Key => "swag";

    /// <summary>Fields-only partial (no &lt;form&gt;/chrome) rendered inside the host's one form.</summary>
    public string PartialName => "_SwagFields";

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

        // Bind the posted fields into a FRESH model (the editable values come purely from the
        // POST, exactly as the standalone page's [BindProperty] did). Carry the participant's
        // display identity so the re-rendered partial shows name/email on Invalid.
        var model = new SwagFormModel
        {
            Role = ctx.Role,
            FullName = ctx.FullName,
            Email = ctx.Email,
        };
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.FullName, ctx.Email,
            ctx.Role, ctx.ModelState, ctx.Ct);
    }
}
