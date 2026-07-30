namespace CommunityHub.Forms.Steps;

/// <summary>
/// The inline wizard step for the sponsor Event-Coordinator section (§32 / §285 Phase 2). Owns NO
/// chrome and NO form logic — it renders the shared <c>_SponsorCoordinatorFields</c> partial and
/// delegates load + save (incl. the Zoho-Backstage contact sync) to
/// <see cref="SponsorCoordinatorFormService"/>, the SAME logic the standalone Company Details
/// section uses, so the inline step and the page behave identically. Discovered + registered
/// automatically via <see cref="IWizardStepHandler"/>.
///
/// <para>Key "coordinator" matches the step emitted by <c>SponsorWizardService</c> (and the
/// <c>SponsorWiz.Step.coordinator</c> resx label), so the host renders it INLINE instead of the
/// fallback section link. The step's plan completion (coordinator email present) flips to done on
/// save (§291).</para>
/// </summary>
public sealed class SponsorCoordinatorStepHandler : IWizardStepHandler
{
    private readonly SponsorCoordinatorFormService _service;

    public SponsorCoordinatorStepHandler(SponsorCoordinatorFormService service) => _service = service;

    public string Key => "coordinator";

    public string PartialName => "_SponsorCoordinatorFields";

    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx) =>
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Ct);

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        var model = new SponsorCoordinatorModel();
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.Email, ctx.ModelState, ctx.Ct);
    }
}
