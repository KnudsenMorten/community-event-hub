namespace CommunityHub.Forms.Steps;

/// <summary>
/// §297 — inline wizard step for the sponsor Booth-materials section. Previously handler-less (left
/// the wizard); now renders current materials + video-URL add rows INLINE via
/// <c>_SponsorBoothMaterialsFields</c>, delegating to <see cref="SponsorBoothMaterialsFormService"/>.
/// Key "booth-materials" matches SponsorWizardService. Auto-registered via <see cref="IWizardStepHandler"/>.
/// </summary>
public sealed class SponsorBoothMaterialsStepHandler : IWizardStepHandler
{
    private readonly SponsorBoothMaterialsFormService _service;

    public SponsorBoothMaterialsStepHandler(SponsorBoothMaterialsFormService service) => _service = service;

    public string Key => "booth-materials";

    public string PartialName => "_SponsorBoothMaterialsFields";

    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx) =>
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Ct);

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        var model = new SponsorBoothMaterialsModel();
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.Email, ctx.ModelState, ctx.Ct);
    }
}
