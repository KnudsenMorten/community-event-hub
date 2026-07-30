namespace CommunityHub.Core.Tasks.Model;

/// <summary>
/// §684.8 — ONE inline node of a task body, parsed ONCE into a tree and then rendered per
/// flavour (HTML / plain text / e-mail HTML).
/// </summary>
/// <remarks>
/// <para>🔒 <b>Why a tree and not another regex pass.</b> The markup this replaces is 16 ordered
/// regex passes in <c>TaskTextLinkifier</c> (web) against 6 in <c>TaskMarkup.ToPlainText</c>
/// (ICS/plain mail). They drifted silently for two features running — §675's <c>==highlight==</c>
/// and §600.5's <c>*italic*</c> were added to the web renderer and never mirrored into the other,
/// so the raw markers leaked verbatim into every calendar entry (§685). Parsing once and rendering
/// the SAME tree three ways makes that class of drift structurally impossible: a renderer either
/// handles the node or it does not compile / does not pass
/// <c>TaskBodyRendererCoverageTests</c>.</para>
///
/// <para>The set is CLOSED and lives here. Adding a node means updating all three renderers —
/// which is the point.</para>
/// </remarks>
public abstract record TaskInline;

/// <summary>Literal text. Already decoded from the source; renderers do their own escaping.</summary>
public sealed record TaskText(string Value) : TaskInline;

/// <summary>
/// The four emphasis flavours the authored bodies use. Kept as ONE node with a kind rather than
/// four near-identical records: every renderer switches over the kind, and
/// <c>TaskBodyRendererCoverageTests</c> asserts EVERY enum member renders in all three flavours —
/// a stronger guarantee than four types, because adding a member turns a test red rather than
/// merely being ignorable.
/// </summary>
public enum TaskEmphasisKind
{
    /// <summary><c>**bold**</c>.</summary>
    Bold = 0,

    /// <summary><c>*italic*</c> (§600.5).</summary>
    Italic = 1,

    /// <summary>
    /// <c>__underline__</c>. 🔒 §669: underline is NO LONGER how a heading is written — a heading is
    /// <c>:::section</c>. This remains only for genuine inline underlining inside prose.
    /// </summary>
    Underline = 2,

    /// <summary><c>==highlight==</c> (§675) — the one line in a task that must not be missed.</summary>
    Highlight = 3,
}

/// <summary>Emphasised inline content. Nests (bold inside a link label, etc.).</summary>
public sealed record TaskEmphasis(
    TaskEmphasisKind Kind,
    IReadOnlyList<TaskInline> Children) : TaskInline;

/// <summary>
/// An INLINE link — <c>[label](href)</c> sitting inside a sentence. It renders as ordinary link
/// text, never as a block button.
/// </summary>
/// <remarks>
/// 🔒 §677 is the reason this is distinct from <see cref="Model.TaskButton"/>: a button used to exist
/// only because <c>TaskTextLinkifier</c> turned any URL into one, which is how a bullet whose only
/// content was a link rendered as an empty "•" plus a block button. A button is now a DIRECTIVE and
/// a block; an inline link is inline. That layout is no longer representable.
/// </remarks>
public sealed record TaskLink(
    IReadOnlyList<TaskInline> Label,
    string Href) : TaskInline;

/// <summary>An e-mail address. Renders as a plain mailto link inline with the prose, never a button.</summary>
public sealed record TaskMailLink(string Address) : TaskInline;

/// <summary>
/// An unresolved <c>{{placeholder}}</c>, resolved at RENDER time from the render context.
/// </summary>
/// <remarks>
/// <para>🔒 §684.1 / §666 — this is the node that moves placeholder resolution from SEED time to
/// RENDER time. Today <c>SponsorOrderPullService</c> resolves <c>{{…}}</c> during the WooCommerce
/// pull and stores finished prose, so any live value is frozen at the moment the pull ran.</para>
///
/// <para>An UNKNOWN key renders as nothing and raises a diagnostic — never as literal
/// <c>{{braces}}</c> on a sponsor's page.</para>
/// </remarks>
public sealed record TaskPlaceholder(string Key) : TaskInline;

/// <summary>A hard line break WITHIN a paragraph (a single newline in the authored source).</summary>
public sealed record TaskLineBreak : TaskInline;
