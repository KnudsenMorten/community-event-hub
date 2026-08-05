using CommunityHub.Core.Content;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// §680 — the wizard's WELCOME step: step 1 for every role that has copy.
///
/// <para>Read-only, like the §400 deadlines step: it renders <c>_WelcomeFields</c> and always
/// advances. Nothing is posted and nothing is stored, which is why the wizard services mark it
/// <c>Done: true</c> — a step that asks nothing must never be able to hold the progress bar below
/// 100% or become the step someone is "stuck" on.</para>
/// </summary>
public sealed class WelcomeStepHandler : IWizardStepHandler
{
    private readonly WelcomeFormService _service;

    public WelcomeStepHandler(WelcomeFormService service) => _service = service;

    /// <summary>Matches the key all four wizard services emit for this step.</summary>
    public string Key => WelcomeCopyStore.StepKey;

    /// <summary>
    /// 🔒 A FULL partial path, not the bare <c>_WelcomeFields</c> — §708.10a. A bare name resolves
    /// only against the CURRENT page's folder, so it works from <c>/Forms/Wizard</c> (same folder)
    /// and throws from any other host. That is precisely how a bare name took the whole
    /// <c>/Tasks</c> page down in production, past two green unit suites and a passing smoke.
    /// </summary>
    public string PartialName => "/Pages/Forms/_WelcomeFields.cshtml";

    public object? Model { get; private set; }

    public async Task LoadAsync(WizardStepContext ctx)
    {
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.Ct);
    }

    public async Task<WizardStepOutcome> SaveAsync(WizardStepContext ctx)
    {
        // Nothing is posted. Reload so the step re-renders truthfully if the host returns to it,
        // then advance — the same shape DeadlinesStepHandler uses.
        Model = await _service.LoadAsync(ctx.EventId, ctx.ParticipantId, ctx.Role, ctx.Ct);
        return WizardStepOutcome.Advance;
    }
}
