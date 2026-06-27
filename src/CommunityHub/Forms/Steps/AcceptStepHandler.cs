namespace CommunityHub.Forms.Steps;

/// <summary>
/// The inline wizard step for the Accept (Code of Conduct + Privacy) form
/// (REQUIREMENTS §148/§119). It owns NO chrome and NO form logic — it renders the shared
/// <c>_AcceptFields</c> partial and delegates load + save to <see cref="AcceptFormService"/>
/// (the SAME service the standalone <c>/Forms/Accept</c> page uses), so the inline step and
/// the standalone page behave identically. Discovered + registered automatically via
/// <see cref="IWizardStepHandler"/>.
///
/// <para>This is the always-last step in every role's wizard (SpeakerWizardService /
/// RoleWizardService both append "accept" last), so it applies to all roles.</para>
/// </summary>
public sealed class AcceptStepHandler : IWizardStepHandler
{
    private readonly AcceptFormService _service;

    public AcceptStepHandler(AcceptFormService service) => _service = service;

    /// <summary>Matches the "accept" key emitted by SpeakerWizardService / RoleWizardService.</summary>
    public string Key => "accept";

    /// <summary>Fields-only partial (no &lt;form&gt;/chrome) rendered inside the host's one form.</summary>
    public string PartialName => "_AcceptFields";

    /// <summary>The render model handed to the partial; carries posted values back on Invalid.</summary>
    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx)
    {
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Ct);
    }

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        // Relevance gate — accept applies to every role, so this never excludes; kept for parity.
        if (!await _service.IsRelevantAsync(ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.Ct))
            return WizardStepOutcome.NotRelevant;

        // Bind the posted checkbox into a FRESH model (the editable value comes purely from the
        // POST, exactly as the standalone page's [BindProperty] did). SaveAsync re-derives all
        // display state, so on Invalid the SAME partial re-renders with the user's input +
        // ModelState errors.
        var model = new AcceptFormModel();
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.Email, ctx.ModelState, ctx.Ct);
    }
}
