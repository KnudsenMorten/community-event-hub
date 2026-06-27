namespace CommunityHub.Forms.Steps;

/// <summary>
/// The inline wizard step for the Speaker Details form (REQUIREMENTS §148, §26c). It owns NO
/// chrome and NO form logic — it renders the shared <c>_DetailsFields</c> partial and delegates
/// load + save to <see cref="SpeakerDetailsFormService"/> (the SAME service the standalone
/// <c>/Speaker/Details</c> page uses on its plain Save path), so the inline step and the
/// standalone page behave identically. Discovered + registered automatically via
/// <see cref="IWizardStepHandler"/>. Mirrors the reference <c>HotelStepHandler</c>.
///
/// <para>The wizard Save&amp;next uses the plain Save path only — it never pushes to Zoho.
/// The standalone page keeps the "Save &amp; sync to Zoho" action.</para>
/// </summary>
public sealed class SpeakerDetailsStepHandler : IWizardStepHandler
{
    private readonly SpeakerDetailsFormService _service;

    public SpeakerDetailsStepHandler(SpeakerDetailsFormService service) => _service = service;

    /// <summary>Matches the "details" key emitted by SpeakerWizardService + the resx key.</summary>
    public string Key => "details";

    /// <summary>
    /// Fields-only partial (no &lt;form&gt;/chrome) rendered inside the host's one form. The
    /// partial lives under <c>/Pages/Speaker</c> (it is shared with the standalone Details page),
    /// so it is referenced by an app-relative path that resolves from the wizard host in
    /// <c>/Pages/Forms</c> as well as from the standalone page.
    /// </summary>
    public string PartialName => "/Pages/Speaker/_DetailsFields.cshtml";

    /// <summary>The render model handed to the partial; carries posted values back on Invalid.</summary>
    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx)
    {
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Email, ctx.Ct);
    }

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        // Relevance gate — skip the step entirely when the participant isn't a speaker.
        if (!_service.IsRelevant(ctx.Role))
            return WizardStepOutcome.NotRelevant;

        // Bind the posted fields into a FRESH model (the editable values come purely from the
        // POST, exactly as the standalone page's [BindProperty] did). Carry the sign-in email
        // so the re-rendered partial shows the read-only identity on Invalid.
        var model = new SpeakerDetailsFormModel { Email = ctx.Email };
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        var result = await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.Email, ctx.Role, ctx.ModelState, ctx.Ct);
        return result.Outcome;
    }
}
