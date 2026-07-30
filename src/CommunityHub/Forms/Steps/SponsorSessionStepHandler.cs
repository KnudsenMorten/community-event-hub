namespace CommunityHub.Forms.Steps;

/// <summary>
/// §292 — inline wizard step for the sponsor Speaking session (shown only when the sponsor bought a
/// "Sponsor Sessions" slot, gated by <c>SponsorInfo.HasSponsorSession</c> in SponsorWizardService).
/// Renders the <c>_SponsorSessionFields</c> partial and delegates to <see cref="SponsorSessionFormService"/>
/// (which stores the session in CEH for later Zoho sync and creates a real Speaker participant per
/// speaker). Discovered + registered automatically via <see cref="IWizardStepHandler"/>. Key "session".
/// </summary>
public sealed class SponsorSessionStepHandler : IWizardStepHandler
{
    private readonly SponsorSessionFormService _service;

    public SponsorSessionStepHandler(SponsorSessionFormService service) => _service = service;

    // §648 — ONE source for the key, shared with the sponsor task's FormStepKey and the config's
    // "form": "session", so three copies of the same string cannot drift apart.
    public string Key => SponsorSessionFormService.SessionFormStepKey;

    public string PartialName => "_SponsorSessionFields";

    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx) =>
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Ct);

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        var model = new SponsorSessionModel();
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.Email, ctx.ModelState, ctx.Ct);
    }
}
