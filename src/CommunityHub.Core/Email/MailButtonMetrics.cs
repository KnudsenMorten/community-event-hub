namespace CommunityHub.Core.Email;

/// <summary>
/// §1089 — how wide a mail button has to be so its label is never cut off.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Desktop Outlook renders mail with the WORD engine</b>, which will not size a VML
/// <c>roundrect</c> to its content — the width must be stated in pixels up front. State it too
/// small and Word WRAPS the label inside a box with a fixed <c>height:44px</c>, so the second line
/// is clipped and the button reads <i>"Approve all 1 as"</i> with the category missing. That is the
/// defect the operator photographed on 2026-08-19.</para>
///
/// <para>🔴 <b>The old estimate was <c>label.Length * 9 + 48</c>, and it was duplicated verbatim in
/// two renderers</b> (<see cref="Organizer.SpeakerApprovalService"/> and the task-body renderer).
/// It assumed 9px per character — about right for Segoe UI Bold at 15px, and NOT right for what
/// Word actually uses when Aptos and Segoe UI are both missing and it falls back a step. A per-char
/// average is a guess about a font we do not control on a machine we cannot see.</para>
///
/// <para>🔑 So the guess is deliberately generous rather than accurate. <b>The two failure modes are
/// not symmetrical:</b> too wide is a button with extra padding, which nobody reports; too narrow
/// is a clipped label, which is unreadable and — on an approval button — hides WHICH action it
/// performs. When the cost of the errors differs this much, bias the estimate toward the cheap one.
/// ⇒ <b>13px per character plus 64px of padding</b>, roughly 45% of headroom over the measured
/// width of bold 15px Segoe UI, clamped to stay inside the 560px mail column.</para>
///
/// <para>⚠️ Headroom is the ONLY defence in the VML path — there is no <c>white-space:nowrap</c> in
/// the Word engine. The modern-client anchor gets <c>nowrap</c> as well, so it cannot wrap at any
/// width.</para>
/// </remarks>
public static class MailButtonMetrics
{
    /// <summary>Widest a button may get — keeps it inside the ~560px mail column.</summary>
    public const int MaxWidthPx = 520;

    /// <summary>Narrowest a button may get, so a two-word label still looks like a button.</summary>
    public const int MinWidthPx = 200;

    /// <summary>
    /// The pixel width to declare for a button carrying <paramref name="label"/>.
    /// </summary>
    /// <remarks>
    /// Measure the label the READER sees. Callers that HTML-encode first would count
    /// <c>&amp;amp;</c> as five characters instead of one — harmless here (it only over-estimates,
    /// which is the safe direction) but worth knowing before anyone "corrects" it.
    /// </remarks>
    public static int WidthPx(string? label) =>
        Math.Clamp(((label?.Length ?? 0) * 13) + 64, MinWidthPx, MaxWidthPx);
}
