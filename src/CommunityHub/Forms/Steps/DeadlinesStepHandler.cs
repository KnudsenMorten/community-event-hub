namespace CommunityHub.Forms.Steps;

/// <summary>
/// §400 — the inline wizard step that shows every task and deadline living OUTSIDE Get Started.
///
/// <para>Read-only: it renders <c>_DeadlinesFields</c> and always advances. The calendar buttons on
/// the partial post to the HOST's own handlers (formaction), the same shape the party and Master
/// Class steps use for their invite buttons — a step handler saves the step, it does not own side
/// actions.</para>
/// </summary>
public sealed class DeadlinesStepHandler : IWizardStepHandler
{
    private readonly DeadlinesFormService _service;

    public DeadlinesStepHandler(DeadlinesFormService service) => _service = service;

    /// <summary>Matches the "deadlines" key every wizard service emits.</summary>
    public string Key => "deadlines";

    public string PartialName => "_DeadlinesFields";

    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx)
    {
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Ct);
    }

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        // Nothing is posted. Reload so the step re-renders truthfully if the host returns to it
        // (e.g. after one of the calendar buttons), then advance.
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Ct);
        return WizardStepOutcome.Advance;
    }
}
