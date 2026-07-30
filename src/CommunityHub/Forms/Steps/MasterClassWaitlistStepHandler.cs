namespace CommunityHub.Forms.Steps;

/// <summary>
/// §384 — the INLINE wizard step for WAITLIST selection (step 2 of the Master Class flow).
///
/// <para>Mirrors <see cref="MasterClassStepHandler"/> exactly: no chrome, no business logic. It
/// renders <c>_MasterClassWaitlistFields</c> and delegates to
/// <see cref="MasterClassWaitlistFormService"/>, which delegates in turn to the same
/// <c>MasterClassSignupService</c> every other Master Class surface uses.</para>
///
/// <para>A step is the wizard's SUPPORTED extension point — a handler whose <see cref="Key"/>
/// matches a step the wizard service emits renders inline; without one the host degrades to a
/// link-out. That is why this design needed no change to the shared wizard host, unlike the per-row
/// action buttons that were considered and rejected (§377).</para>
/// </summary>
public sealed class MasterClassWaitlistStepHandler : IWizardStepHandler
{
    private readonly MasterClassWaitlistFormService _service;

    public MasterClassWaitlistStepHandler(MasterClassWaitlistFormService service) => _service = service;

    /// <summary>Matches the "masterclass-waitlist" key emitted by <c>AttendeeWizardService</c>.</summary>
    public string Key => "masterclass-waitlist";

    /// <summary>Fields-only partial (no &lt;form&gt;/chrome) rendered inside the host's one form.</summary>
    public string PartialName => "_MasterClassWaitlistFields";

    /// <summary>The render model handed to the partial; carries posted values back on Invalid.</summary>
    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx)
    {
        Model = await _service.LoadAsync(ctx.EventId, ctx.Email, ctx.Ct);
    }

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        // Fresh model: editable values come purely from the POST. The attendee identity is resolved
        // from the signed-in participant inside the service and never bound, so nobody can post
        // someone else's id.
        var model = new MasterClassWaitlistFormModel();
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.Email, ctx.BaseUrl, ctx.ModelState, ctx.Ct);
    }
}
