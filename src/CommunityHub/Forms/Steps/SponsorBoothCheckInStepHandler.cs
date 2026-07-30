namespace CommunityHub.Forms.Steps;

/// <summary>
/// The inline wizard step for the sponsor Booth-check-in section (§229 / §285 Phase 2). Owns NO
/// chrome and NO form logic — it renders the shared <c>_SponsorBoothCheckInFields</c> partial and
/// delegates load + save to <see cref="SponsorBoothCheckInFormService"/> (the SAME logic the
/// standalone Company Details section uses), so the inline step and the page behave identically.
/// Discovered + registered automatically via <see cref="IWizardStepHandler"/>.
///
/// <para>Key "booth-checkin" matches the step emitted by <c>SponsorWizardService</c> (and the
/// <c>SponsorWiz.Step.booth-checkin</c> resx label), so the host resolves this handler for that
/// plan step and renders it INLINE instead of the fallback section link. The step's plan
/// completion (BoothCheckInSlot present) flips to done on save (§291).</para>
/// </summary>
public sealed class SponsorBoothCheckInStepHandler : IWizardStepHandler
{
    private readonly SponsorBoothCheckInFormService _service;

    public SponsorBoothCheckInStepHandler(SponsorBoothCheckInFormService service) => _service = service;

    public string Key => "booth-checkin";

    public string PartialName => "_SponsorBoothCheckInFields";

    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx) =>
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Ct);

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        // Bind the posted radio into a FRESH model (values come purely from the POST); on Invalid
        // the SAME partial re-renders with the ModelState error.
        var model = new SponsorBoothCheckInModel();
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.Email, ctx.ModelState, ctx.Ct);
    }
}
