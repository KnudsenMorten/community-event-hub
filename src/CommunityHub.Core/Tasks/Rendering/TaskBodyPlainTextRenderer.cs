using System.Text;
using System.Text.RegularExpressions;
using CommunityHub.Core.Tasks.Model;

namespace CommunityHub.Core.Tasks.Rendering;

/// <summary>
/// §684.10 — the PLAIN-TEXT flavour: the ICS <c>DESCRIPTION</c> of a calendar reminder, and any
/// non-HTML mail body.
/// </summary>
/// <remarks>
/// <para>❗ <b>This is the renderer that silently fell behind, and §685 is the proof.</b>
/// <c>TaskMarkup.ToPlainText</c> implemented bold / underline / link / bullet while the web renderer
/// implemented sixteen passes. <c>*italic*</c> (§600.5) and <c>==highlight==</c> (§675) were added to
/// the web only, so both markers leaked verbatim into every calendar entry built from a task body —
/// for two features running, with nothing to catch it. Sharing the parsed tree is what makes that
/// impossible now: emphasis is a NODE, and this flavour renders it as its inner text, so a new
/// emphasis flavour is handled here the day it is added or the build goes red.</para>
///
/// <para>🔒 <b>A button is <c>label: url</c>.</b> There is nothing to click in a calendar entry, so
/// the URL must be present as text — dropping it would leave "Open the Sponsor Webshop" pointing at
/// nothing.</para>
/// </remarks>
public sealed class TaskBodyPlainTextRenderer : TaskBodyRenderer
{
    private static readonly Regex ExtraBlankLines = new(@"\n{3,}", RegexOptions.Compiled);

    protected override void Paragraph(TaskParagraph block, StringBuilder sb, TaskRenderContext context)
    {
        RenderInlines(block.Content, sb, context);
        sb.Append("\n\n");
    }

    protected override void Section(TaskSection block, StringBuilder sb, TaskRenderContext context)
    {
        sb.Append(block.Title).Append('\n');
        RenderBlocks(block.Children, sb, context);
        sb.Append('\n');
    }

    protected override void List(TaskList block, StringBuilder sb, TaskRenderContext context)
    {
        var n = 1;
        foreach (var item in block.Items)
        {
            sb.Append(block.Ordered ? $"{n++}. " : "• ");
            RenderInlines(item.Content, sb, context);
            sb.Append('\n');
        }
        sb.Append('\n');
    }

    protected override void Button(TaskButton block, StringBuilder sb, TaskRenderContext context) =>
        sb.Append(block.Label).Append(": ").Append(context.ResolveText(block.Href)).Append("\n\n");

    protected override void Callout(TaskCallout block, StringBuilder sb, TaskRenderContext context)
    {
        // A warning is MARKED, not merely spaced. Plain text has no colour, and "we could not check
        // your orders" reading as ordinary prose is exactly the §555 confusion this must avoid.
        if (block.Tone == TaskCalloutTone.Warning) sb.Append("! ");
        RenderBlocks(block.Children, sb, context);
    }

    protected override void Address(TaskAddress block, StringBuilder sb, TaskRenderContext context)
    {
        sb.Append(block.Label).Append(":\n").Append(context.ResolveText(block.Value)).Append('\n');
        if (block.Marking is not null)
        {
            sb.Append("Mark all boxes: ").Append(context.ResolveText(block.Marking)).Append('\n');
        }
        sb.Append('\n');
    }

    protected override void Decision(TaskDecision block, StringBuilder sb, TaskRenderContext context)
    {
        sb.Append("Choose: ").Append(block.AcceptLabel).Append(" / ").Append(block.DeclineLabel)
          .Append('\n');

        // 🔒 Never draw a choice you cannot make here. A calendar entry has no buttons, so this says
        // where the answer is given rather than implying the two options are actionable inline.
        sb.Append(context.TaskPageUrl is { Length: > 0 } url
            ? $"Answer this in the hub: {url}\n\n"
            : "Answer this on your task page in the hub.\n\n");
    }

    /// <summary>
    /// §688.12 — an embedded editor cannot exist in a calendar entry, so it degrades to a POINTER.
    /// </summary>
    /// <remarks>
    /// 🔒 Rendering nothing would silently drop the task's whole point from the ICS description —
    /// the reader would see the surrounding prose and no way to act on it.
    /// </remarks>
    protected override void Embed(TaskEmbed block, StringBuilder sb, TaskRenderContext context) =>
        sb.Append(context.TaskPageUrl is { Length: > 0 } url
            ? $"Manage this on your task page in the hub: {url}\n\n"
            : "Manage this on your task page in the hub.\n\n");

    protected override void Text(TaskText inline, StringBuilder sb, TaskRenderContext context) =>
        sb.Append(inline.Value);

    /// <summary>
    /// Emphasis carries no plain-text markers. 🔒 §685: the whole defect was that the markers
    /// SURVIVED here — a calendar entry reading "**EURO 500**" or "==pricing is high==".
    /// </summary>
    protected override void Emphasis(TaskEmphasis inline, StringBuilder sb, TaskRenderContext context) =>
        RenderInlines(inline.Children, sb, context);

    protected override void Link(TaskLink inline, StringBuilder sb, TaskRenderContext context)
    {
        var href = context.ResolveText(inline.Href);
        var label = PlainText(inline.Label, context);
        sb.Append(string.Equals(label, href, StringComparison.Ordinal) ? href : $"{label}: {href}");
    }

    protected override void MailLink(TaskMailLink inline, StringBuilder sb, TaskRenderContext context) =>
        sb.Append(inline.Address);

    protected override void Placeholder(TaskPlaceholder inline, StringBuilder sb, TaskRenderContext context) =>
        sb.Append(context.Resolve(inline.Key));

    protected override void LineBreak(StringBuilder sb, TaskRenderContext context) =>
        sb.Append('\n');

    protected override string Finish(StringBuilder sb) =>
        ExtraBlankLines.Replace(sb.ToString().Replace("\r\n", "\n"), "\n\n").Trim();
}
