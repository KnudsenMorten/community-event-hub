namespace CommunityHub.Forms.Steps;

/// <summary>
/// The inline wizard step for the sponsor Logos section (§32 / §285 Phase 2). Owns NO chrome and NO
/// upload logic — it renders the shared <c>_SponsorLogosFields</c> partial (three file inputs) and
/// delegates load + upload to <see cref="SponsorLogosFormService"/>, the SAME versioned-SharePoint
/// upload + persist + audit the standalone Company Details "Logos &amp; artwork" section runs, so the
/// two stay consistent. Discovered + registered automatically via <see cref="IWizardStepHandler"/>.
///
/// <para>Key "logos" matches the step emitted by <c>SponsorWizardService</c> (and the
/// <c>SponsorWiz.Step.logos</c> resx label), so the host renders it INLINE. The step's plan
/// completion (LogoRasterPath / LogoVectorPath present) flips to done on save (§291). Requires the
/// host form to be multipart — see /Forms/Wizard.</para>
/// </summary>
public sealed class SponsorLogosStepHandler : IWizardStepHandler
{
    private readonly SponsorLogosFormService _service;

    public SponsorLogosStepHandler(SponsorLogosFormService service) => _service = service;

    public string Key => "logos";

    public string PartialName => "_SponsorLogosFields";

    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx) =>
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Ct);

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        var model = new SponsorLogosModel();
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.Email, ctx.ModelState, ctx.Ct);
    }
}
