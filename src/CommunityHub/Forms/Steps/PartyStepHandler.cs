namespace CommunityHub.Forms.Steps;

/// <summary>
/// The inline wizard step for the Party sign-up form (REQUIREMENTS §164, §148). It owns NO
/// chrome and NO form logic — it renders the shared <c>_PartyFields</c> partial and delegates
/// load + save to <see cref="PartyFormService"/> (the SAME service the standalone <c>/Party</c>
/// page logic uses), so the inline step and the standalone page behave identically. Discovered +
/// registered automatically via <see cref="IWizardStepHandler"/>. Mirrors the reference
/// <c>DinnerStepHandler</c>.
/// </summary>
public sealed class PartyStepHandler : IWizardStepHandler
{
    private readonly PartyFormService _service;

    public PartyStepHandler(PartyFormService service) => _service = service;

    /// <summary>Matches the "party" key emitted by SpeakerWizardService / RoleWizardService + the resx key.</summary>
    public string Key => "party";

    /// <summary>Fields-only partial (no &lt;form&gt;/chrome) rendered inside the host's one form.</summary>
    public string PartialName => "_PartyFields";

    /// <summary>The render model handed to the partial; carries posted values back on Invalid.</summary>
    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx)
    {
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.Ct);
    }

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        // Bind the posted fields into a FRESH model (editable values come purely from the POST).
        // Name + email are taken from the signed-in participant inside the service, never bound.
        var model = new PartyFormModel { Role = ctx.Role };
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.Email, ctx.FullName,
            ctx.Role, ctx.ModelState, ctx.Ct);
    }
}
