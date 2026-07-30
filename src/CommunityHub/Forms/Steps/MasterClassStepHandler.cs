namespace CommunityHub.Forms.Steps;

/// <summary>
/// §352 — the INLINE wizard step for Master Class selection.
///
/// <para><b>Why this class exists at all.</b> A wizard step renders inline only when an
/// <see cref="IWizardStepHandler"/> is registered whose <see cref="Key"/> matches the step key;
/// otherwise the host degrades to a LINK-OUT ("Open: …"). <c>AttendeeWizardService</c> declared
/// both <c>masterclass</c> and <c>party</c> steps, but only <c>party</c> had a handler — so the
/// Master Class step navigated the attendee AWAY from Get Started and could therefore never be
/// completed in-flow. Operator, 2026-07-26: <i>"the result is also that the user never gets to
/// step 2 of the get started wizard."</i> Adding this handler is the entire fix.</para>
///
/// <para>Owns NO chrome and NO business logic: it renders the shared <c>_MasterClassFields</c>
/// partial and delegates to <see cref="MasterClassFormService"/>, which in turn delegates to the
/// SAME <c>MasterClassSignupService</c> that <c>/Attendee</c> uses — so the inline step and the
/// standing page cannot diverge. Mirrors <see cref="PartyStepHandler"/> exactly.</para>
/// </summary>
public sealed class MasterClassStepHandler : IWizardStepHandler
{
    private readonly MasterClassFormService _service;

    public MasterClassStepHandler(MasterClassFormService service) => _service = service;

    /// <summary>Matches the "masterclass" key emitted by <c>AttendeeWizardService</c> + the resx key.</summary>
    public string Key => "masterclass";

    /// <summary>Fields-only partial (no &lt;form&gt;/chrome) rendered inside the host's one form.</summary>
    public string PartialName => "_MasterClassFields";

    /// <summary>The render model handed to the partial; carries posted values back on Invalid.</summary>
    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx)
    {
        Model = await _service.LoadAsync(ctx.EventId, ctx.Email, ctx.Ct);
    }

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        // Bind the posted fields into a FRESH model (editable values come purely from the POST).
        // The attendee identity is resolved from the signed-in participant inside the service,
        // never bound — an attendee must not be able to post someone else's id.
        var model = new MasterClassFormModel();
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.Email, ctx.BaseUrl, ctx.ModelState, ctx.Ct);
    }
}
