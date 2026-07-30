using System.Text;
using CommunityHub.Core.Tasks.Data;
using CommunityHub.Core.Tasks.Model;

namespace CommunityHub.Core.Tasks.Rendering;

/// <summary>
/// §684.10 — ONE body, three renderers. The tree walk lives here; each flavour supplies only how a
/// block or an inline node LOOKS.
/// </summary>
/// <remarks>
/// <para>🔒 <b>This is the class that makes renderer drift a compile error.</b> Every block and
/// inline type is an <c>abstract</c> member, so adding one to the model breaks all three
/// subclasses until each has decided what to do with it. The markup this replaces could offer no
/// such thing: <c>TaskTextLinkifier</c> grew <c>*italic*</c> (§600.5) and <c>==highlight==</c>
/// (§675) while <c>TaskMarkup.ToPlainText</c> silently did not, so both markers leaked verbatim into
/// every calendar entry for two features running (§685). Nothing failed; nothing could.</para>
///
/// <para>The <c>switch</c> in <see cref="RenderBlocks"/> is the one place a new type must also be
/// listed. <c>TaskBodyRendererCoverageTests</c> reflects over every <see cref="TaskBlock"/> and
/// <see cref="TaskInline"/> subtype and asserts each renders in all three flavours, so forgetting it
/// is a red test rather than a runtime throw on a sponsor's page.</para>
/// </remarks>
public abstract class TaskBodyRenderer
{
    /// <summary>Render a parsed body in this flavour.</summary>
    public string Render(TaskBody body, TaskRenderContext context)
    {
        var sb = new StringBuilder();
        RenderBlocks(body.Blocks, sb, context);
        return Finish(sb);
    }

    /// <summary>Render a loose list of blocks (a provider's answer, a section's children).</summary>
    protected void RenderBlocks(
        IReadOnlyList<TaskBlock> blocks, StringBuilder sb, TaskRenderContext context)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TaskParagraph p: Paragraph(p, sb, context); break;
                case TaskSection s: Section(s, sb, context); break;
                case TaskList l: List(l, sb, context); break;
                case TaskButton b: Button(b, sb, context); break;
                case TaskCallout c: Callout(c, sb, context); break;
                case TaskAddress a: Address(a, sb, context); break;
                case TaskDecision d: Decision(d, sb, context); break;
                case TaskEmbed e: Embed(e, sb, context); break;
                case TaskData d: Data(d, sb, context); break;

                // Unreachable while TaskBlock's set is closed and this switch is complete —
                // TaskBodyRendererCoverageTests proves both. Kept as a loud failure rather than a
                // silent skip: a block that renders as nothing is how copy goes missing unnoticed.
                default:
                    throw new NotSupportedException(
                        $"{GetType().Name} has no case for block type {block.GetType().Name}. "
                        + "Add it to TaskBodyRenderer.RenderBlocks and to every renderer.");
            }
        }
    }

    /// <summary>Render inline content.</summary>
    protected void RenderInlines(
        IReadOnlyList<TaskInline> inlines, StringBuilder sb, TaskRenderContext context)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TaskText t: Text(t, sb, context); break;
                case TaskEmphasis e: Emphasis(e, sb, context); break;
                case TaskLink l: Link(l, sb, context); break;
                case TaskMailLink m: MailLink(m, sb, context); break;
                case TaskPlaceholder p: Placeholder(p, sb, context); break;
                case TaskLineBreak: LineBreak(sb, context); break;

                default:
                    throw new NotSupportedException(
                        $"{GetType().Name} has no case for inline type {inline.GetType().Name}. "
                        + "Add it to TaskBodyRenderer.RenderInlines and to every renderer.");
            }
        }
    }

    /// <summary>
    /// §684.9 / §555 — the THREE-STATE contract, enforced HERE rather than in each flavour.
    /// </summary>
    /// <remarks>
    /// <para>🔒 Deliberately NOT abstract. Whether "could not check" is visually distinct from
    /// "nothing found" is not a per-flavour styling choice — it is the rule §666 exists to enforce,
    /// and a flavour that got it wrong would tell a sponsor they had not booked a TV when the truth
    /// is that the webshop was down. Each flavour styles the callout; none of them decides
    /// this.</para>
    ///
    /// <para>A source with no resolved entry is treated as "could not check" too: the resolver puts
    /// an entry in for every source it walked, so a MISSING one means the resolver never ran — which
    /// is precisely not knowing.</para>
    /// </remarks>
    protected virtual void Data(TaskData data, StringBuilder sb, TaskRenderContext context)
    {
        if (!context.Data.TryGetValue(data.Source, out var result) || result is null)
        {
            RenderCouldNotCheck("We could not check this right now.", sb, context);
            return;
        }

        switch (result)
        {
            case TaskDataResult.Answer answer:
                RenderBlocks(answer.Blocks, sb, context);
                break;
            case TaskDataResult.NothingFound nothing:
                RenderBlocks(nothing.Blocks, sb, context);
                break;
            case TaskDataResult.CouldNotCheck couldNot:
                RenderCouldNotCheck(couldNot.Message, sb, context);
                break;
            default:
                throw new NotSupportedException(
                    $"Unhandled TaskDataResult {result.GetType().Name}.");
        }
    }

    /// <summary>
    /// "We could not check" renders as a WARNING in every flavour — reusing the flavour's own
    /// callout so it can never be mistaken for ordinary prose or for a "no".
    /// </summary>
    private void RenderCouldNotCheck(string message, StringBuilder sb, TaskRenderContext context) =>
        Callout(
            new TaskCallout(
                TaskCalloutTone.Warning,
                new TaskBlock[]
                {
                    new TaskParagraph(new TaskInline[] { new TaskText(message) }),
                }),
            sb,
            context);

    // --- per-flavour: blocks ------------------------------------------------
    protected abstract void Paragraph(TaskParagraph block, StringBuilder sb, TaskRenderContext context);
    protected abstract void Section(TaskSection block, StringBuilder sb, TaskRenderContext context);
    protected abstract void List(TaskList block, StringBuilder sb, TaskRenderContext context);
    protected abstract void Button(TaskButton block, StringBuilder sb, TaskRenderContext context);
    protected abstract void Callout(TaskCallout block, StringBuilder sb, TaskRenderContext context);
    protected abstract void Address(TaskAddress block, StringBuilder sb, TaskRenderContext context);
    protected abstract void Decision(TaskDecision block, StringBuilder sb, TaskRenderContext context);
    protected abstract void Embed(TaskEmbed block, StringBuilder sb, TaskRenderContext context);

    // --- per-flavour: inlines -----------------------------------------------
    protected abstract void Text(TaskText inline, StringBuilder sb, TaskRenderContext context);
    protected abstract void Emphasis(TaskEmphasis inline, StringBuilder sb, TaskRenderContext context);
    protected abstract void Link(TaskLink inline, StringBuilder sb, TaskRenderContext context);
    protected abstract void MailLink(TaskMailLink inline, StringBuilder sb, TaskRenderContext context);
    protected abstract void Placeholder(TaskPlaceholder inline, StringBuilder sb, TaskRenderContext context);
    protected abstract void LineBreak(StringBuilder sb, TaskRenderContext context);

    /// <summary>Final tidy-up of the accumulated output.</summary>
    protected abstract string Finish(StringBuilder sb);

    /// <summary>
    /// The plain-text form of inline content — used by every flavour for attributes and labels
    /// (a button's <c>title</c>, an ICS line). Shares the tree, so it cannot drift from the prose.
    /// </summary>
    protected static string PlainText(IReadOnlyList<TaskInline> inlines, TaskRenderContext context)
    {
        var sb = new StringBuilder();
        Walk(inlines, sb, context);
        return sb.ToString();

        static void Walk(IReadOnlyList<TaskInline> nodes, StringBuilder into, TaskRenderContext ctx)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case TaskText t: into.Append(t.Value); break;
                    case TaskEmphasis e: Walk(e.Children, into, ctx); break;
                    case TaskLink l: Walk(l.Label, into, ctx); break;
                    case TaskMailLink m: into.Append(m.Address); break;
                    case TaskPlaceholder p: into.Append(ctx.Resolve(p.Key)); break;
                    case TaskLineBreak: into.Append(' '); break;
                }
            }
        }
    }
}
