namespace CommunityHub.Forms.Steps;

/// <summary>
/// The inline wizard step for the sponsor Company-details section (§32 / §285 Phase 2). Owns NO
/// chrome and NO form logic — it renders the shared <c>_SponsorCompanyFields</c> partial and
/// delegates load + save (incl. URL/length validation + the Zoho-Backstage sync) to
/// <see cref="SponsorCompanyFormService"/>, the SAME logic the standalone Company Details section
/// uses, so the inline step and the page behave identically. Discovered + registered automatically
/// via <see cref="IWizardStepHandler"/>.
///
/// <para>Key "company" matches the step emitted by <c>SponsorWizardService</c> (and the
/// <c>SponsorWiz.Step.company</c> resx label), so the host renders it INLINE. It MUST NOT be
/// "details" — that is the SPEAKER details step's global key, and wizard handler keys share ONE
/// dictionary across every role; a collision let this sponsor company-branding partial hijack the
/// speaker details step in Release (last-wins). The step's plan completion (website or company
/// description present) flips to done on save (§291).</para>
/// </summary>
public sealed class SponsorCompanyStepHandler : IWizardStepHandler
{
    private readonly SponsorCompanyFormService _service;

    public SponsorCompanyStepHandler(SponsorCompanyFormService service) => _service = service;

    public string Key => "company";

    public string PartialName => "_SponsorCompanyFields";

    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx) =>
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Ct);

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        var model = new SponsorCompanyModel();
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.Email, ctx.ModelState, ctx.Ct);
    }
}
