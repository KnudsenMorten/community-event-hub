namespace CommunityHub.Forms;

/// <summary>
/// One step CARD in the shared Get-Started stepper (REQUIREMENTS §161) — the sponsor-style
/// checklist row used by EVERY role's "Get started" page. A card is ALWAYS rendered, whether
/// the step is done or pending, and ALWAYS carries an Edit/Open link to the step's edit
/// surface — so a completed step stays revisitable (no "all done — dead-end").
/// </summary>
/// <param name="Number">1-based position shown in the list.</param>
/// <param name="Title">Resolved (localized) step title.</param>
/// <param name="Description">Resolved (localized) one-line description.</param>
/// <param name="Done">true = done (green check), false = pending, null = guided link whose
/// state can't be determined (shown, never counted — e.g. the sponsor ERP-contacts step).</param>
/// <param name="Url">Where the Edit/Open link points (the step's route, or a section anchor).</param>
/// <param name="Blocker">
/// §949 — a short, ALREADY-LOCALISED sentence naming what would complete this step (e.g. "To finish
/// this step, add: Phone number"). Rendered only when the step is pending. Null = nothing more
/// specific to say than the description already says.
/// </param>
/// <remarks>
/// <para>🔴 <b>§949, reported twice.</b> A pending step used to be a chip without a tick and nothing
/// else, so a person whose only gap was below the fold could not tell what to do — the platform kept
/// answering "not done" without ever answering "why". It lives on the SHARED card rather than on the
/// role wizard alone because <b>every data-backed step has the same silence</b>; speaker and sponsor
/// steps can populate it the moment they can name a field.</para>
/// </remarks>
public sealed record WizardStepperCard(
    int Number, string Title, string Description, bool? Done, string Url, string? Blocker = null);

/// <summary>
/// View-model for the shared <c>_WizardStepper</c> partial (REQUIREMENTS §161): the one
/// stepper layout shared by the speaker, role and sponsor "Get started" pages so they can
/// never drift. Renders a progress header (<see cref="DoneCount"/> of <see cref="TotalSteps"/>
/// + <see cref="Percent"/>%), a progress bar, a "Continue" CTA (or the all-done banner), then
/// EVERY step as a card. Progress math mirrors the original sponsor/speaker views: a step
/// whose state is undeterminable (<c>Done == null</c>) is shown but never counted as done and
/// never makes the wizard "all done".
/// </summary>
/// <param name="Heading">Page heading (e.g. "Get started").</param>
/// <param name="Intro">Intro paragraph above the progress bar.</param>
/// <param name="Cards">Every step, in order.</param>
/// <param name="ContinueUrl">The "Continue" CTA target (the next pending step), or null when all done.</param>
/// <param name="ContinueLabel">The "Continue" CTA label, or null when all done.</param>
/// <param name="AllDoneMessage">Banner shown at 100% — must still invite editing any step below.</param>
/// <param name="EditLabel">Link label for a DONE step (e.g. "Edit").</param>
/// <param name="OpenLabel">Link label for a PENDING / guided step (e.g. "Open").</param>
public sealed record WizardStepperVm(
    string Heading,
    string Intro,
    IReadOnlyList<WizardStepperCard> Cards,
    string? ContinueUrl,
    string? ContinueLabel,
    string AllDoneMessage,
    string EditLabel,
    string OpenLabel)
{
    /// <summary>Total steps shown (every card is numbered + counted toward the denominator).</summary>
    public int TotalSteps => Cards.Count;

    /// <summary>Steps with a confirmed completion (<c>Done == true</c>); null/false don't count.</summary>
    public int DoneCount => Cards.Count(c => c.Done == true);

    /// <summary>Whole-percent progress (0 when there are no steps).</summary>
    public int Percent => TotalSteps == 0 ? 0 : (int)Math.Round(100.0 * DoneCount / TotalSteps);

    /// <summary>True only once every step is confirmed done — an undeterminable (null) step
    /// keeps the wizard "not all done", exactly as the original sponsor view did.</summary>
    public bool AllDone => TotalSteps > 0 && DoneCount >= TotalSteps;
}
