namespace CommunityHub.Pages.Shared;

/// <summary>
/// View model for the shared <c>_ConfirmModal</c> partial (REQUIREMENTS §21
/// shared UX components): a reusable, accessible confirmation dialog shown before
/// a destructive / bulk / send action. The triggering control opts in with
/// <c>data-ceh-confirm="&lt;Id&gt;"</c>; the shared layout JS opens this modal,
/// traps focus, supports Esc / backdrop / Cancel, and replays the original action
/// (form submit or link) when Confirm is pressed.
/// </summary>
/// <param name="Id">
/// DOM id of the modal; the trigger references it via <c>data-ceh-confirm="Id"</c>.
/// </param>
/// <param name="Title">Dialog heading (e.g. "Send broadcast?").</param>
/// <param name="Body">
/// Body text describing the action. A <c>{count}</c> token, if present, is
/// replaced by a styled count pill (the trigger supplies the value via
/// <c>data-ceh-count</c>; falls back to <see cref="DefaultCount"/>).
/// </param>
/// <param name="ConfirmLabel">Confirm-button label (e.g. "Send now").</param>
/// <param name="CancelLabel">Cancel-button label.</param>
/// <param name="Danger">When true, styles Confirm as a destructive (red) action.</param>
/// <param name="DefaultCount">
/// Initial count rendered into the pill before JS overrides it from the trigger
/// (so the figure is correct even with JS disabled / server-rendered).
/// </param>
public sealed record ConfirmModalModel(
    string Id,
    string Title,
    string Body,
    string ConfirmLabel,
    string CancelLabel,
    bool Danger = false,
    string? DefaultCount = null,

    /// <summary>
    /// §1146f — when set, the user must TYPE this exact word before Confirm is enabled.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-28: <i>"make sure that the delete button requires a seconday
    /// confirmation like type in DELETE"</i>.</para>
    ///
    /// <para>🔑 <b>The point is to break muscle memory, not to add a step.</b> A plain modal on a
    /// button that sits at the end of every row is dismissed reflexively — the dialog is in the same
    /// place every time and Enter is already under the finger. Typing a word cannot be done by
    /// reflex, and it is the one confirmation that reliably survives a fast, repetitive page.</para>
    ///
    /// <para>🔒 Matched EXACTLY (trimmed). A case-insensitive compare would let "delete" through,
    /// which is close enough to reflex to defeat the purpose.</para>
    ///
    /// <para>Progressive enhancement is preserved: with JS off the modal is inert and the trigger
    /// performs its native action, exactly as before this option existed.</para>
    /// </remarks>
    string? RequireTypedWord = null);
