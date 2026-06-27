namespace CommunityHub.Forms.Steps;

/// <summary>
/// The inline wizard step for the Calendar-email form (REQUIREMENTS §148) — the FIRST speaker
/// step. It owns NO chrome and NO form logic — it renders the shared <c>_CalendarEmailFields</c>
/// partial and delegates load + save to <see cref="CalendarEmailFormService"/> (the SAME service
/// the standalone <c>/Forms/CalendarEmail</c> page uses), so the inline step and the standalone
/// page behave identically. Discovered + registered automatically via <see cref="IWizardStepHandler"/>.
///
/// <para>This step is OPTIONAL: an alternate calendar address. <see cref="SaveAsync"/> ALWAYS
/// stamps the speaker-acted marker (even when the field is left blank — a "Skip that still saves"),
/// so the step counts as Done and the wizard can advance. Non-speaker => NotRelevant.</para>
/// </summary>
public sealed class CalendarEmailStepHandler : IWizardStepHandler
{
    private readonly CalendarEmailFormService _service;

    public CalendarEmailStepHandler(CalendarEmailFormService service) => _service = service;

    /// <summary>Matches the "calendar" key emitted by SpeakerWizardService + the resx key.</summary>
    public string Key => "calendar";

    /// <summary>Fields-only partial (no &lt;form&gt;/chrome) rendered inside the host's one form.</summary>
    public string PartialName => "_CalendarEmailFields";

    /// <summary>The render model handed to the partial; carries posted values back on Invalid.</summary>
    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx)
    {
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.Email, ctx.Ct);
    }

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        // Relevance gate — this step is speaker-only.
        if (!CalendarEmailFormService.IsRelevant(ctx.Role))
            return WizardStepOutcome.NotRelevant;

        // Bind the posted field into a FRESH model (the editable value comes purely from the POST,
        // exactly as the standalone page's [BindProperty] did). SaveAsync re-derives all display
        // state, so on Invalid the SAME partial re-renders with the user's input + ModelState errors.
        var model = new CalendarEmailFormModel { Role = ctx.Role, PrimaryEmail = ctx.Email };
        await ctx.TryUpdateModelAsync(model);
        Model = model;

        return await _service.SaveAsync(
            model, ctx.EventId, ctx.ParticipantId, ctx.Email, ctx.FullName,
            ctx.Role, ctx.ModelState, ctx.Ct);
    }
}
