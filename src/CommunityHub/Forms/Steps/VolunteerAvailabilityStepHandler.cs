namespace CommunityHub.Forms.Steps;

/// <summary>
/// The inline wizard step for the volunteer "My Availability" form (REQUIREMENTS §148).
/// It owns NO chrome and NO form logic — it renders the shared <c>_AvailabilityFields</c>
/// partial and delegates load + save to <see cref="VolunteerAvailabilityFormService"/>
/// (the SAME service the standalone <c>/volunteer/availability</c> page uses), so the inline
/// step and the standalone page behave identically. Discovered + registered automatically
/// via <see cref="IWizardStepHandler"/>. Mirrors the reference <c>HotelStepHandler</c>.
/// </summary>
public sealed class VolunteerAvailabilityStepHandler : IWizardStepHandler
{
    private readonly VolunteerAvailabilityFormService _service;

    public VolunteerAvailabilityStepHandler(VolunteerAvailabilityFormService service) => _service = service;

    /// <summary>Matches the "availability" step key emitted by RoleWizardService + the resx key.</summary>
    public string Key => "availability";

    /// <summary>Fields-only partial (no &lt;form&gt;/chrome) rendered inside the host's one form.</summary>
    public string PartialName => "_AvailabilityFields";

    /// <summary>The render model handed to the partial; carries posted values back on re-render.</summary>
    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx)
    {
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Ct);
    }

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        // Relevance gate — availability is volunteer-only; skip for everyone else.
        if (!VolunteerAvailabilityFormService.IsRelevant(ctx.Role))
            return WizardStepOutcome.NotRelevant;

        // Bind the posted per-day choices into a FRESH model (the editable values come purely
        // from the POST, exactly as the standalone page's [BindProperty] Inputs did). SaveAsync
        // re-derives all display state (Days / LastSavedAt / Notice).
        var model = new VolunteerAvailabilityFormModel();
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.FullName, ctx.Email, ctx.Ct);
    }
}
