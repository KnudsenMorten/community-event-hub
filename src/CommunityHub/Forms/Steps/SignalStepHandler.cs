namespace CommunityHub.Forms.Steps;

/// <summary>
/// The inline wizard step for the Signal-groups form (REQUIREMENTS §148, §109). It owns NO
/// chrome and NO form logic — it renders the shared <c>_SignalFields</c> partial and delegates
/// load + save to <see cref="SignalFormService"/> (the SAME service the standalone
/// <c>/Forms/Signal</c> page uses), so the inline step and the standalone page behave identically.
/// The step has no posted fields — joining is external — so Save&amp;next acts as MARK-DONE:
/// <see cref="SaveAsync"/> always advances when the role is in scope and returns
/// <see cref="WizardStepOutcome.NotRelevant"/> when the gate excludes it. Discovered + registered
/// automatically via <see cref="IWizardStepHandler"/>.
/// </summary>
public sealed class SignalStepHandler : IWizardStepHandler
{
    private readonly SignalFormService _service;

    public SignalStepHandler(SignalFormService service) => _service = service;

    /// <summary>Matches the "signal" key emitted by SpeakerWizardService / RoleWizardService + the resx key.</summary>
    public string Key => "signal";

    /// <summary>Fields-only partial (no &lt;form&gt;/chrome) rendered inside the host's one form.</summary>
    public string PartialName => "_SignalFields";

    /// <summary>The render model handed to the partial.</summary>
    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx)
    {
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.Ct);
    }

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        // No posted fields to bind — Save&next is a mark-done. The service re-derives the
        // relevance gate server-side (=> NotRelevant when out of scope) and marks the task Done.
        var model = new SignalFormModel { Role = ctx.Role };
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.ModelState, ctx.Ct);
    }
}
