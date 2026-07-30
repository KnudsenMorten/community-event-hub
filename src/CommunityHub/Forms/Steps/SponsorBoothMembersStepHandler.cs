namespace CommunityHub.Forms.Steps;

/// <summary>
/// §297 — inline wizard step for the sponsor Booth-members section. Previously handler-less (the
/// wizard fell back to a link that left the wizard and showed nothing); now it renders the current
/// members + add rows INLINE via <c>_SponsorBoothMembersFields</c> and delegates to
/// <see cref="SponsorBoothMembersFormService"/>. Key "booth-members" matches the step emitted by
/// SponsorWizardService. Auto-registered via <see cref="IWizardStepHandler"/>.
/// </summary>
public sealed class SponsorBoothMembersStepHandler : IWizardStepHandler
{
    private readonly SponsorBoothMembersFormService _service;

    public SponsorBoothMembersStepHandler(SponsorBoothMembersFormService service) => _service = service;

    public string Key => "booth-members";

    public string PartialName => "_SponsorBoothMembersFields";

    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx) =>
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Ct);

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        var model = new SponsorBoothMembersModel();
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.Email, ctx.ModelState, ctx.Ct);
    }
}
