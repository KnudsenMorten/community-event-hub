namespace CommunityHub.Forms.Steps;

/// <summary>
/// The inline wizard step for the Lunch form (REQUIREMENTS §148). It owns NO chrome and
/// NO form logic — it renders the shared <c>_LunchFields</c> partial and delegates load +
/// save to <see cref="LunchFormService"/> (the SAME service the standalone <c>/Forms/Lunch</c>
/// page uses), so the inline step and the standalone page behave identically. Discovered +
/// registered automatically via <see cref="IWizardStepHandler"/>.
/// </summary>
public sealed class LunchStepHandler : IWizardStepHandler
{
    private readonly LunchFormService _service;

    public LunchStepHandler(LunchFormService service) => _service = service;

    /// <summary>Matches the "lunch" key emitted by SpeakerWizardService / RoleWizardService + the resx key.</summary>
    public string Key => "lunch";

    /// <summary>Fields-only partial (no &lt;form&gt;/chrome) rendered inside the host's one form.</summary>
    public string PartialName => "_LunchFields";

    /// <summary>The render model handed to the partial; carries posted values back on Invalid.</summary>
    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx)
    {
        Model = await _service.LoadAsync(
            ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.FullName, ctx.Email, ctx.Ct);
    }

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        // Relevance gate — skip the step entirely when no lunch day applies (or the pre-day
        // is auto-counted: MC speaker / crew).
        if (!await _service.IsRelevantAsync(ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.Ct))
            return WizardStepOutcome.NotRelevant;

        // Bind the posted fields into a FRESH model (the editable values come purely from the
        // POST, exactly as the standalone page's [BindProperty] did). SaveAsync re-derives all
        // display state (visibility / labels), so on Invalid the SAME partial re-renders with
        // the user's input + ModelState errors.
        var model = new LunchFormModel { Role = ctx.Role };
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.FullName, ctx.Email,
            ctx.Role, ctx.ModelState, ctx.Ct);
    }
}
