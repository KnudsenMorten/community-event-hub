namespace CommunityHub.Core.Tasks.Model;

/// <summary>
/// §684.8 — ONE structural block of a task body. The set is CLOSED: a renderer switches over it,
/// so adding a block type makes the compiler (and <c>TaskBodyRendererCoverageTests</c>) list every
/// renderer that must learn it.
/// </summary>
/// <remarks>
/// 🔒 <b>Structure never comes from inline markup again.</b> <c>__**HEADING**__</c> is now
/// <see cref="TaskSection"/>, so the §669 underline-vs-bold inversion cannot recur — a heading
/// stopped being a markup convention and became a type.
/// </remarks>
public abstract record TaskBlock;

/// <summary>A prose paragraph.</summary>
public sealed record TaskParagraph(IReadOnlyList<TaskInline> Content) : TaskBlock;

/// <summary>
/// A titled section — <c>:::section STEP 1 — ORDER</c>. Replaces the <c>__**…**__</c> convention.
/// </summary>
public sealed record TaskSection(
    string Title,
    IReadOnlyList<TaskBlock> Children) : TaskBlock;

/// <summary>One item of a list. Inline content ONLY — see <see cref="TaskList"/>.</summary>
public sealed record TaskListItem(IReadOnlyList<TaskInline> Content);

/// <summary>
/// A bulleted or numbered list.
/// </summary>
/// <remarks>
/// 🔒 <b>Items hold INLINE content only, deliberately.</b> §677: the sponsor-wall task rendered an
/// empty "•" followed by a block button, because a button was inferred from a bare URL inside a
/// bullet. A <see cref="TaskButton"/> is a BLOCK and a list item cannot contain one, so that layout
/// is unrepresentable rather than merely discouraged.
/// </remarks>
public sealed record TaskList(
    bool Ordered,
    IReadOnlyList<TaskListItem> Items) : TaskBlock;

/// <summary>How prominent a button is. Explicit, never inferred from position.</summary>
public enum TaskButtonStyle
{
    Primary = 0,
    Secondary = 1,
}

/// <summary>Where a button navigates.</summary>
public enum TaskLinkTarget
{
    /// <summary>An app-relative in-hub route — same tab, no external indicator.</summary>
    InHub = 0,

    /// <summary>
    /// Leaves the hub — new tab, plus the §653 "↗ (opens in a new tab)" indicator. 🔒 §671: the
    /// indicator follows the TARGET, so an in-hub button can never keep promising "you are leaving
    /// the hub" while staying in it.
    /// </summary>
    External = 1,
}

/// <summary>
/// §672 — WHICH sign-in hand-off notice a button raises, chosen by KIND rather than by sniffing the
/// URL. The real confusion these explain is not the new tab: it is landing in a DIFFERENT system
/// with its own sign-in and assuming the hub logged you out (§487).
/// </summary>
/// <remarks>
/// 🔒 Selection stays EXPLICIT, never "external ⇒ warn". §176/§487: a hand-off notice on links that
/// have no separate sign-in is noise, and noise is how people learn to click through the one dialog
/// that mattered.
/// </remarks>
public enum TaskInterstitial
{
    None = 0,

    /// <summary>The external sponsor webshop — its own account and password.</summary>
    Webshop = 1,

    /// <summary>A Zoho Backstage exhibitor-dashboard deep link — e-mail + one-time PIN.</summary>
    Zoho = 2,
}

/// <summary>
/// A first-class button — <c>:::button</c>. Label, destination, prominence, target and interstitial
/// are all DECLARED.
/// </summary>
/// <remarks>
/// §684.2: "you cannot lay out what you cannot name". Every one of these fields used to be either
/// inferred from URL syntax or unavailable.
/// </remarks>
public sealed record TaskButton(
    string Label,
    string Href,
    TaskButtonStyle Style,
    TaskLinkTarget Target,
    TaskInterstitial Interstitial) : TaskBlock;

/// <summary>The tone of a callout.</summary>
public enum TaskCalloutTone
{
    Info = 0,
    Warning = 1,
}

/// <summary>A set-apart note — <c>:::callout warning</c>.</summary>
public sealed record TaskCallout(
    TaskCalloutTone Tone,
    IReadOnlyList<TaskBlock> Children) : TaskBlock;

/// <summary>
/// A shipping address plus its box marking — <c>:::address</c>.
/// </summary>
/// <remarks>
/// 🔒 §670 warns that two copies of a shipping address is how one of them silently goes stale after
/// a venue change. ONE directive with the address and the marking together means the attendee-bag
/// task and the App-Game task cannot drift apart: both name the same placeholder.
/// </remarks>
public sealed record TaskAddress(
    string Label,
    string Value,
    string? Marking) : TaskBlock;

/// <summary>
/// §684.9 — live data, resolved when the page RENDERS, through a named provider.
/// </summary>
/// <remarks>
/// 🔒 The provider answers with three states (answer / nothing-found / could-not-check) and they
/// render DIFFERENTLY. §666: rendering "you have not booked a TV" from a FAILED lookup pushes a
/// sponsor into buying a second one. Making it a type means a provider author cannot forget the
/// third case.
/// </remarks>
public sealed record TaskData(string Source) : TaskBlock;

/// <summary>
/// §688.12 / §671 — an INTERACTIVE COMPONENT rendered inside the task body.
/// </summary>
/// <remarks>
/// <para>Operator §671: <i>"if possible embed the booth members into the task (if room permits in
/// the page), otherwise link to page in ceh"</i>, restated 2026-07-29 as <i>"make it more
/// embedded"</i>. The task becomes the place the work is DONE, rather than a set of instructions
/// pointing somewhere else — which is §600's original complaint about the old task format.</para>
///
/// <para>🔒 <b>The body decides WHERE the component sits; the page owns the FORM.</b> Same split as
/// <see cref="TaskDecision"/>: the renderer emits a placement marker, and the Razor row substitutes
/// the real partial (with its antiforgery token) at that point. A renderer cannot emit a working
/// form — it has no token — and a view cannot decide layout without parsing the body.</para>
///
/// <para>🔒 <b>The mail and calendar flavours MUST degrade to a link.</b> An editable grid cannot
/// exist in an inbox or an ICS entry, and rendering nothing would silently drop the task's whole
/// point from the chase mail.</para>
/// </remarks>
public sealed record TaskEmbed(string Component) : TaskBlock;

/// <summary>
/// §684.13 / §670 / §676 — the SHARED two-button decision. Built ONCE, with the follow-up form as
/// an OPTION of it rather than a copy of it.
/// </summary>
/// <remarks>
/// <para>🔒 §676 is explicit that this must not be a copy of §670: the App Game offers "send prior"
/// vs "bring it to the event", while the attendee bag can only be shipped in advance (the bags are
/// packed before the event, so bringing it is always too late). That difference lives in the
/// FORM this points at — <see cref="AcceptForm"/> — not in a second component.</para>
///
/// <para>🔒 Declining is a REAL recorded state, not a way to dismiss the task. Organizers need to
/// know who declined versus who never answered; today both look like "not complete".</para>
/// </remarks>
public sealed record TaskDecision(
    string Key,
    string AcceptLabel,
    string DeclineLabel,
    string? AcceptForm) : TaskBlock;
