namespace CommunityHub.Forms.Steps;

/// <summary>
/// §297 — inline wizard step for the sponsor Contacts section (Contract Signers &amp; Event
/// Coordinators, mastered in e-conomic). Previously handler-less (left the wizard); now lists the ERP
/// contacts + a fail-soft inline add via <c>_SponsorContactsFields</c>, delegating to
/// <see cref="SponsorContactsFormService"/>. Key "contacts" matches SponsorWizardService.
/// Auto-registered via <see cref="IWizardStepHandler"/>.
/// </summary>
public sealed class SponsorContactsStepHandler : IWizardStepHandler
{
    private readonly SponsorContactsFormService _service;

    public SponsorContactsStepHandler(SponsorContactsFormService service) => _service = service;

    public string Key => "contacts";

    public string PartialName => "_SponsorContactsFields";

    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx) =>
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Ct);

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        var model = new SponsorContactsModel();
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.Email, ctx.ModelState, ctx.Ct);
    }
}
