namespace CommunityHub.Forms.Steps;

/// <summary>
/// The inline wizard step for the Profile form (REQUIREMENTS §148). It owns NO chrome and
/// NO form logic — it renders the shared <c>_ProfileFields</c> partial and delegates load +
/// save to <see cref="ProfileFormService"/> (the SAME service the standalone <c>/Profile</c>
/// page uses), so the inline step and the standalone page behave identically. Discovered +
/// registered automatically via <see cref="IWizardStepHandler"/>.
///
/// <para>Profile is the universal first step ("tell us how to reach you") for the roles the
/// generic role wizard serves (Volunteer / Organizer / Media / EventPartner). It has no
/// entitlement gate, so <see cref="SaveAsync"/> only ever returns Advance or Invalid; the
/// NotRelevant path is kept for symmetry with the gated steps. Speakers get a richer
/// 'details' step instead, so the speaker-only side-effects in the service stay gated.</para>
/// </summary>
public sealed class ProfileStepHandler : IWizardStepHandler
{
    private readonly ProfileFormService _service;

    public ProfileStepHandler(ProfileFormService service) => _service = service;

    /// <summary>Matches the "profile" key emitted by RoleWizardService + the resx key.</summary>
    public string Key => "profile";

    /// <summary>Fields-only partial (no &lt;form&gt;/chrome) rendered inside the host's one form.</summary>
    public string PartialName => "_ProfileFields";

    /// <summary>The render model handed to the partial; carries posted values back on Invalid.</summary>
    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx)
    {
        // LoadAsync returns null only when the signed-in participant has no row — which
        // cannot happen inside the wizard (the host already resolved the participant);
        // fall back to an empty model so the partial always has something to bind to.
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.Ct)
                ?? new ProfileFormModel { Role = ctx.Role };
    }

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        // Relevance gate — profile is universal, but keep the symmetry with gated steps.
        if (!await _service.IsRelevantAsync(ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.Ct))
            return WizardStepOutcome.NotRelevant;

        // Bind the posted fields into a FRESH model (editable values come purely from the
        // POST, exactly as the standalone page's [BindProperty] did). SaveAsync re-derives
        // all display state, so on Invalid the SAME partial re-renders with the user's input
        // + the ModelState errors.
        var model = new ProfileFormModel { Role = ctx.Role };
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.ModelState, ctx.Ct);
    }
}
