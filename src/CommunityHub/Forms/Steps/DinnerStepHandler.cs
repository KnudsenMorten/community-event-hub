namespace CommunityHub.Forms.Steps;

/// <summary>
/// The inline wizard step for the Dinner (Appreciation Dinner RSVP) form (REQUIREMENTS §148).
/// It owns NO chrome and NO form logic — it renders the shared <c>_DinnerFields</c> partial
/// and delegates load + save to <see cref="DinnerFormService"/> (the SAME service the
/// standalone <c>/Forms/Dinner</c> page uses), so the inline step and the standalone page
/// behave identically. Discovered + registered automatically via <see cref="IWizardStepHandler"/>.
/// Mirrors the reference <c>HotelStepHandler</c>.
/// </summary>
public sealed class DinnerStepHandler : IWizardStepHandler
{
    private readonly DinnerFormService _service;

    public DinnerStepHandler(DinnerFormService service) => _service = service;

    /// <summary>Matches the "dinner" key emitted by SpeakerWizardService / RoleWizardService + the resx key.</summary>
    public string Key => "dinner";

    /// <summary>Fields-only partial (no &lt;form&gt;/chrome) rendered inside the host's one form.</summary>
    public string PartialName => "_DinnerFields";

    /// <summary>The render model handed to the partial; carries posted values back on Invalid.</summary>
    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx)
    {
        Model = await _service.LoadAsync(
            ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.Email, ctx.FullName, ctx.Ct);
    }

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        // Relevance gate — skip the step entirely when the participant isn't entitled.
        if (!await _service.IsRelevantAsync(ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.Ct))
            return WizardStepOutcome.NotRelevant;

        // Bind the posted fields into a FRESH model (the editable values come purely from
        // the POST, exactly as the standalone page's [BindProperty] did). SaveAsync re-derives
        // all display state (lock / context / message), so on Invalid the SAME partial
        // re-renders with the user's input + ModelState errors.
        var model = new DinnerFormModel { Role = ctx.Role };
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.Email, ctx.FullName,
            ctx.Role, ctx.ModelState, ctx.Ct);
    }
}
