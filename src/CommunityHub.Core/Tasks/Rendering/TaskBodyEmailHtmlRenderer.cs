using System.Net;
using System.Text;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Tasks.Model;

namespace CommunityHub.Core.Tasks.Rendering;

/// <summary>
/// §684.10 — the E-MAIL flavour: a task body inside a reminder mail.
/// </summary>
/// <remarks>
/// <para>🔒 <b>E-mail degradation is a hard constraint, and desktop Outlook is why.</b> Outlook
/// renders with the WORD engine, which ignores <c>border-radius</c> and will happily paint dark text
/// on a dark button. A button therefore emits a <b>VML roundrect</b> in the <c>mso</c> conditional
/// plus an ordinary anchor for every other client — the operator-verified standard already used by
/// the welcome templates. Block types make that a per-type decision instead of a regex that "mostly"
/// survives.</para>
///
/// <para>🔒 <b>The reminder body renders through THIS renderer, from the SAME tree as the page</b>
/// (§684.12). A task cannot say one thing on its page and another in the chase mail — which is a
/// real risk while <c>TaskReminderBuilder</c> reads a separately-stored <c>Description</c>.</para>
///
/// <para>Everything is table-and-inline-style based: no classes, no external CSS, no flexbox. Mail
/// clients strip <c>&lt;style&gt;</c> blocks and none of them implement modern layout.</para>
/// </remarks>
public sealed class TaskBodyEmailHtmlRenderer : TaskBodyRenderer
{
    private const string Font = "Aptos,'Segoe UI',Arial,sans-serif";
    private const string Blue = "#1565c0";

    protected override void Paragraph(TaskParagraph block, StringBuilder sb, TaskRenderContext context)
    {
        sb.Append("<p style=\"margin:0 0 14px;font-family:").Append(Font)
          .Append(";font-size:15px;line-height:1.5;color:#1f2933;\">");
        RenderInlines(block.Content, sb, context);
        sb.Append("</p>");
    }

    protected override void Section(TaskSection block, StringBuilder sb, TaskRenderContext context)
    {
        // §669 — bold and sized, matching the hub. The heading is the scannable structure; in a mail
        // it is also the only navigation there is.
        sb.Append("<p style=\"margin:20px 0 8px;font-family:").Append(Font)
          .Append(";font-size:16px;font-weight:700;color:#0b3d66;\">")
          .Append(Escape(block.Title))
          .Append("</p>");
        RenderBlocks(block.Children, sb, context);
    }

    protected override void List(TaskList block, StringBuilder sb, TaskRenderContext context)
    {
        var tag = block.Ordered ? "ol" : "ul";
        sb.Append('<').Append(tag)
          .Append(" style=\"margin:0 0 14px;padding-left:22px;font-family:").Append(Font)
          .Append(";font-size:15px;line-height:1.5;color:#1f2933;\">");
        foreach (var item in block.Items)
        {
            sb.Append("<li style=\"margin:0 0 6px;\">");
            RenderInlines(item.Content, sb, context);
            sb.Append("</li>");
        }
        sb.Append("</").Append(tag).Append('>');
    }

    protected override void Button(TaskButton block, StringBuilder sb, TaskRenderContext context)
    {
        var href = Escape(context.ResolveText(block.Href));
        var label = Escape(block.Label);
        var fill = block.Style == TaskButtonStyle.Primary ? Blue : "#4b5563";

        // VML needs an explicit pixel width — the WORD engine does not size a roundrect to its
        // content. §1089: the estimate lives in ONE place now. It used to be `label.Length * 9 + 48`
        // here AND in SpeakerApprovalService, and the two copies were wrong in the same way — too
        // tight, so Word wrapped a long label and the fixed 44px height clipped the second line.
        var width = Email.MailButtonMetrics.WidthPx(label);

        sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" ")
          .Append("style=\"margin:6px 0 16px;\"><tr><td align=\"left\">")
          .Append("<!--[if mso]>")
          .Append("<v:roundrect xmlns:v=\"urn:schemas-microsoft-com:vml\" ")
          .Append("xmlns:w=\"urn:schemas-microsoft-com:office:word\" href=\"").Append(href)
          .Append("\" style=\"height:44px;v-text-anchor:middle;width:").Append(width)
          .Append("px;\" arcsize=\"50%\" stroke=\"f\" fillcolor=\"").Append(fill).Append("\">")
          .Append("<w:anchorlock/>")
          .Append("<center style=\"color:#ffffff;font-family:").Append(Font)
          .Append(";font-size:15px;font-weight:bold;\">").Append(label).Append("</center>")
          .Append("</v:roundrect>")
          .Append("<![endif]-->")
          .Append("<!--[if !mso]><!-- -->")
          .Append("<a href=\"").Append(href)
          .Append("\" style=\"background-color:").Append(fill)
          .Append(";border-radius:999px;color:#ffffff;display:inline-block;font-family:").Append(Font)
          .Append(";font-size:15px;font-weight:700;line-height:44px;text-align:center;")
          .Append("text-decoration:none;white-space:nowrap;width:").Append(width)
          .Append("px;-webkit-text-size-adjust:none;\">").Append(label).Append("</a>")
          .Append("<!--<![endif]-->")
          .Append("</td></tr></table>");
    }

    protected override void Callout(TaskCallout block, StringBuilder sb, TaskRenderContext context)
    {
        var (background, border) = block.Tone switch
        {
            TaskCalloutTone.Info => ("#eef5fc", "#c3d9ef"),
            TaskCalloutTone.Warning => ("#fff8e6", "#f0d878"),
            _ => throw new NotSupportedException($"Unhandled callout tone {block.Tone}."),
        };

        // A bordered TABLE, not a styled div: Outlook drops padding and background on a div, so the
        // "we could not check your orders" warning would arrive looking like ordinary prose.
        sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" ")
          .Append("width=\"100%\" style=\"margin:0 0 14px;background:").Append(background)
          .Append(";border:1px solid ").Append(border)
          .Append(";border-radius:6px;\"><tr><td style=\"padding:12px 14px;\">");
        RenderBlocks(block.Children, sb, context);
        sb.Append("</td></tr></table>");
    }

    protected override void Address(TaskAddress block, StringBuilder sb, TaskRenderContext context)
    {
        sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" ")
          .Append("width=\"100%\" style=\"margin:0 0 14px;background:#f7f8fa;border:1px solid #dfe3e8;")
          .Append("border-radius:6px;\"><tr><td style=\"padding:12px 14px;font-family:").Append(Font)
          .Append(";font-size:15px;line-height:1.5;color:#1f2933;\">")
          .Append("<div style=\"font-weight:700;margin-bottom:4px;\">").Append(Escape(block.Label))
          // §688.7 — bold, matching the hub. A courier label gets copied from this line.
          .Append("</div><div><strong>")
          .Append(Escape(context.ResolveText(block.Value)).Replace("\n", "<br />"))
          .Append("</strong></div>");

        if (block.Marking is not null)
        {
            sb.Append("<div style=\"margin-top:8px;\">Mark all boxes: <strong>")
              .Append(Escape(context.ResolveText(block.Marking)))
              .Append("</strong></div>");
        }

        sb.Append("</td></tr></table>");
    }

    protected override void Decision(TaskDecision block, StringBuilder sb, TaskRenderContext context)
    {
        if (context.Decision is { } given)
        {
            var answered = given == TaskDecisionAnswer.Accepted
                ? block.AcceptLabel
                : block.DeclineLabel;
            Paragraph(
                new TaskParagraph(new TaskInline[]
                {
                    new TaskText("You answered: "),
                    new TaskEmphasis(TaskEmphasisKind.Bold, new TaskInline[] { new TaskText(answered) }),
                    new TaskText(". You can change this until the deadline."),
                }),
                sb,
                context);
        }

        // 🔒 ONE button to the hub, not two fake ones. A mail cannot POST, so rendering "No interest"
        // and "We would like to participate" here would either do nothing or need a GET that changes
        // state — and a link that records a decision when a mail scanner follows it is how an
        // answer gets logged that nobody gave.
        if (context.TaskPageUrl is { Length: > 0 } url)
        {
            Button(
                new TaskButton(
                    $"{block.AcceptLabel} / {block.DeclineLabel}",
                    url,
                    TaskButtonStyle.Primary,
                    TaskLinkTarget.External,
                    TaskInterstitial.None),
                sb,
                context);
        }
        else
        {
            Paragraph(
                new TaskParagraph(new TaskInline[]
                {
                    new TaskText($"Choose {block.AcceptLabel} or {block.DeclineLabel} on your task "
                                 + "page in the hub."),
                }),
                sb,
                context);
        }
    }

    /// <summary>
    /// §688.12 — an embedded editor cannot exist in an inbox, so it becomes a BUTTON to the hub.
    /// </summary>
    protected override void Embed(TaskEmbed block, StringBuilder sb, TaskRenderContext context)
    {
        if (context.TaskPageUrl is { Length: > 0 } url)
        {
            Button(
                new TaskButton(
                    "Manage this in the hub", url,
                    TaskButtonStyle.Primary, TaskLinkTarget.External, TaskInterstitial.None),
                sb, context);
        }
        else
        {
            Paragraph(
                new TaskParagraph(new TaskInline[]
                {
                    new TaskText("Manage this on your task page in the hub."),
                }),
                sb, context);
        }
    }

    protected override void Text(TaskText inline, StringBuilder sb, TaskRenderContext context) =>
        sb.Append(Escape(inline.Value));

    protected override void Emphasis(TaskEmphasis inline, StringBuilder sb, TaskRenderContext context)
    {
        var (open, close) = inline.Kind switch
        {
            TaskEmphasisKind.Bold => ("<strong>", "</strong>"),
            TaskEmphasisKind.Italic => ("<em>", "</em>"),
            TaskEmphasisKind.Underline => ("<span style=\"text-decoration:underline;\">", "</span>"),
            // Outlook ignores <mark> entirely, so the highlight is a styled span with an explicit
            // background AND an explicit colour — the WORD engine will not infer readable text.
            TaskEmphasisKind.Highlight => (
                "<span style=\"background:#fff3cd;color:#664d03;padding:0 3px;\">", "</span>"),
            _ => throw new NotSupportedException($"Unhandled emphasis {inline.Kind}."),
        };
        sb.Append(open);
        RenderInlines(inline.Children, sb, context);
        sb.Append(close);
    }

    protected override void Link(TaskLink inline, StringBuilder sb, TaskRenderContext context)
    {
        sb.Append("<a href=\"").Append(Escape(context.ResolveText(inline.Href)))
          .Append("\" style=\"color:").Append(Blue).Append(";text-decoration:underline;\">");
        RenderInlines(inline.Label, sb, context);
        sb.Append("</a>");
    }

    protected override void MailLink(TaskMailLink inline, StringBuilder sb, TaskRenderContext context) =>
        sb.Append("<a href=\"mailto:").Append(Escape(inline.Address))
          .Append("\" style=\"color:").Append(Blue).Append(";text-decoration:underline;\">")
          .Append(Escape(inline.Address)).Append("</a>");

    protected override void Placeholder(TaskPlaceholder inline, StringBuilder sb, TaskRenderContext context) =>
        sb.Append(Escape(context.Resolve(inline.Key)));

    protected override void LineBreak(StringBuilder sb, TaskRenderContext context) =>
        sb.Append("<br />");

    protected override string Finish(StringBuilder sb) => sb.ToString();

    private static string Escape(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
}
