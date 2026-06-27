namespace CommunityHub.Forms.Steps;

/// <summary>
/// The inline wizard step for the Hotel form (REQUIREMENTS §148, REFERENCE handler).
/// It owns NO chrome and NO form logic — it renders the shared <c>_HotelFields</c> partial
/// and delegates load + save to <see cref="HotelFormService"/> (the SAME service the
/// standalone <c>/Forms/Hotel</c> page uses), so the inline step and the standalone page
/// behave identically. Discovered + registered automatically via <see cref="IWizardStepHandler"/>.
///
/// <para>PATTERN for per-form agents: copy this class verbatim, swap the four bindings —
/// <see cref="Key"/> (must match the wizard-service step key + resx key), <see cref="PartialName"/>
/// (your fields-only partial), the injected <c>XxxFormService</c>, and the concrete
/// <c>XxxFormModel</c> used in <see cref="LoadAsync"/> / <see cref="SaveAsync"/>.</para>
/// </summary>
public sealed class HotelStepHandler : IWizardStepHandler
{
    private readonly HotelFormService _service;

    public HotelStepHandler(HotelFormService service) => _service = service;

    /// <summary>Matches the "hotel" key emitted by SpeakerWizardService / RoleWizardService + the resx key.</summary>
    public string Key => "hotel";

    /// <summary>Fields-only partial (no &lt;form&gt;/chrome) rendered inside the host's one form.</summary>
    public string PartialName => "_HotelFields";

    /// <summary>The render model handed to the partial; carries posted values back on Invalid.</summary>
    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx)
    {
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.Ct);
    }

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        // Relevance gate — skip the step entirely when the participant isn't entitled.
        if (!await _service.IsRelevantAsync(ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.Ct))
            return WizardStepOutcome.NotRelevant;

        // Bind the posted fields into a FRESH model (the editable values come purely from
        // the POST, exactly as the standalone page's [BindProperty] did). SaveAsync re-derives
        // all display state (lock / placement / message), so on Invalid the SAME partial
        // re-renders with the user's input + ModelState errors.
        var model = new HotelFormModel { Role = ctx.Role };
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.Email, ctx.FullName,
            ctx.Role, ctx.ModelState, ctx.Ct);
    }
}
