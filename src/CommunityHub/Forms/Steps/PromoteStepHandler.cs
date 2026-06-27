namespace CommunityHub.Forms.Steps;

/// <summary>
/// The inline wizard step for the speaker "Help to promote your session(s)" form
/// (REQUIREMENTS §148 / §116). It owns NO chrome and NO form logic — it renders the shared
/// <c>_PromoteFields</c> partial and delegates load + save to <see cref="PromoteFormService"/>
/// (the SAME service the standalone <c>/Speaker/Promote</c> page uses), so the inline step
/// and the standalone page behave identically. Discovered + registered automatically via
/// <see cref="IWizardStepHandler"/>.
///
/// <para>Promote is a MANUAL "mark done" step with no editable inputs, so <see cref="SaveAsync"/>
/// never reports field errors: Save&amp;next ensures the per-speaker task (idempotent) and marks
/// the <c>promote:{pid}</c> task Done, then advances; a non-speaker is excluded as NotRelevant.</para>
/// </summary>
public sealed class PromoteStepHandler : IWizardStepHandler
{
    private readonly PromoteFormService _service;

    public PromoteStepHandler(PromoteFormService service) => _service = service;

    /// <summary>Matches the "promote" key emitted by SpeakerWizardService + the resx key.</summary>
    public string Key => "promote";

    /// <summary>Fields-only partial (no &lt;form&gt;/chrome) rendered inside the host's one form.
    /// App-relative path (not just a name) because the partial lives under <c>/Pages/Speaker</c>
    /// while the wizard host renders from <c>/Pages/Forms</c> — default partial resolution would
    /// not find it across directories.</summary>
    public string PartialName => "/Pages/Speaker/_PromoteFields.cshtml";

    /// <summary>The render model handed to the partial.</summary>
    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx)
    {
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.Ct);
    }

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        // Manual completion: EnsureTask (idempotent) + mark the promote: task Done. The
        // relevance gate (speaker-only) is re-checked server-side inside the service; a
        // non-speaker yields NotRelevant and is skipped.
        var outcome = await _service.SaveAsync(ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.Ct);

        // Keep the render model populated so a re-render (if the host ever needs one) shows
        // the post-save state, exactly as the standalone page does.
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.Ct);
        return outcome;
    }
}
