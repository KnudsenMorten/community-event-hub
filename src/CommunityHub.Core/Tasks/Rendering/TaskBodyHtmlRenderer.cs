using System.Net;
using System.Text;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Tasks.Model;

namespace CommunityHub.Core.Tasks.Rendering;

/// <summary>
/// §684.10 — the HUB flavour: the task body as it appears on a task page.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Text is HTML-encoded FIRST, then markup is applied</b> (§684.17). Task copy is
/// organizer-authored and rendered on participant-facing pages, so raw HTML in the source must never
/// execute — the property <c>TaskTextLinkifier</c> pins with its "raw HTML cannot execute" test,
/// kept here per node type. Every string that reaches the output goes through
/// <see cref="Escape"/>; the only tags emitted are the ones this class writes.</para>
///
/// <para>Styling reuses the classes already declared in <c>_LayoutStyles.cshtml</c>
/// (<c>a.task-link-btn</c>, <c>.task-act</c>) so a migrated task looks like the ones around it.</para>
/// </remarks>
public sealed class TaskBodyHtmlRenderer : TaskBodyRenderer
{
    /// <summary>
    /// The <c>id</c> of the form a <see cref="TaskDecision"/>'s buttons submit, by task id.
    /// </summary>
    /// <remarks>
    /// 🔒 The decision buttons live in the BODY but must post through ASP.NET's antiforgery
    /// machinery, which only a Razor <c>&lt;form method="post"&gt;</c> tag helper sets up. HTML5's
    /// <c>form=</c> attribute lets a button outside a form submit it, so the page owns the form (and
    /// its token) while the body owns the layout. Rendering a bare <c>&lt;form&gt;</c> here would
    /// emit a post with NO token, which the framework rejects — silently looking like a dead button.
    /// </remarks>
    public static string DecisionFormId(int taskId) => $"task-decision-{taskId}";

    protected override void Paragraph(TaskParagraph block, StringBuilder sb, TaskRenderContext context)
    {
        sb.Append("<p class=\"task-body__p\">");
        RenderInlines(block.Content, sb, context);
        sb.Append("</p>");
    }

    protected override void Section(TaskSection block, StringBuilder sb, TaskRenderContext context)
    {
        // 🔒 §669 — a section title is BOLD and sized, not merely underlined. The operator's
        // complaint was that the emphasis was inverted: "STEP 1 — ORDER" was underlined while lesser
        // details below it were bold, so the scannable structure was the quietest thing on the page.
        // A heading is now a TYPE, so that inversion cannot be re-authored.
        sb.Append("<div class=\"task-body__section\"><div class=\"task-body__section-title\">")
          .Append(Escape(block.Title))
          .Append("</div>");
        RenderBlocks(block.Children, sb, context);
        sb.Append("</div>");
    }

    protected override void List(TaskList block, StringBuilder sb, TaskRenderContext context)
    {
        var tag = block.Ordered ? "ol" : "ul";
        sb.Append("<").Append(tag).Append(" class=\"task-body__list\">");
        foreach (var item in block.Items)
        {
            sb.Append("<li>");
            RenderInlines(item.Content, sb, context);
            sb.Append("</li>");
        }
        sb.Append("</").Append(tag).Append(">");
    }

    protected override void Button(TaskButton block, StringBuilder sb, TaskRenderContext context)
    {
        var href = context.ResolveText(block.Href);

        // 🔒 §677 — a button gets its OWN LINE. The sponsor-wall task rendered an empty "•" followed
        // by a block button because a button could be inferred inside a bullet; a button is a BLOCK
        // now, and this wrapper is where "its own line under the bullet" is expressed.
        sb.Append("<div class=\"task-body__btn-row\">");

        var cssClass = block.Style == TaskButtonStyle.Primary
            ? "task-link-btn"
            : "task-link-btn task-link-btn--secondary";

        sb.Append("<a class=\"").Append(cssClass).Append("\" href=\"").Append(Escape(href)).Append('"');

        if (block.Target == TaskLinkTarget.External)
        {
            sb.Append(" target=\"_blank\" rel=\"noopener noreferrer\"");
        }

        // §672 — the marker read by the layout's interstitial handler, chosen by declared KIND
        // rather than by sniffing the URL. Separate storage keys mean dismissing one does not
        // silently dismiss the other.
        sb.Append(block.Interstitial switch
        {
            TaskInterstitial.Webshop => " data-webshop-interstitial=\"1\"",
            TaskInterstitial.Zoho => " data-zoho-interstitial=\"1\"",
            TaskInterstitial.None => string.Empty,
            _ => throw new NotSupportedException($"Unhandled interstitial {block.Interstitial}."),
        });

        sb.Append('>').Append(Escape(block.Label));

        // §653 — an external button SAYS it leaves the hub, with the same ↗ the navigation uses.
        // 🔒 §671: this follows the declared TARGET, so an in-hub button can no longer promise
        // "you are leaving the hub" while staying in it.
        if (block.Target == TaskLinkTarget.External)
        {
            sb.Append("<span class=\"ext-ico\" aria-hidden=\"true\">&#8599;</span>")
              .Append("<span class=\"visually-hidden\"> (opens in a new tab)</span>");
        }

        sb.Append("</a></div>");
    }

    protected override void Callout(TaskCallout block, StringBuilder sb, TaskRenderContext context)
    {
        var tone = block.Tone switch
        {
            TaskCalloutTone.Info => "info",
            TaskCalloutTone.Warning => "warning",
            _ => throw new NotSupportedException($"Unhandled callout tone {block.Tone}."),
        };
        sb.Append("<div class=\"task-body__callout task-body__callout--").Append(tone).Append("\">");
        RenderBlocks(block.Children, sb, context);
        sb.Append("</div>");
    }

    protected override void Address(TaskAddress block, StringBuilder sb, TaskRenderContext context)
    {
        sb.Append("<div class=\"task-body__address\"><div class=\"task-body__address-label\">")
          .Append(Escape(block.Label))
          // §688.7 — the address itself is BOLD (operator: "address should be bold"). It is the one
          // thing on the task a courier label is copied from, so it must be the thing the eye lands
          // on, not another line of grey prose.
          .Append("</div><div class=\"task-body__address-value\"><strong>")
          .Append(Escape(context.ResolveText(block.Value)).Replace("\n", "<br />"))
          .Append("</strong></div>");

        if (block.Marking is not null)
        {
            sb.Append("<div class=\"task-body__address-marking\">Mark all boxes: <strong>")
              .Append(Escape(context.ResolveText(block.Marking)))
              .Append("</strong></div>");
        }

        sb.Append("</div>");
    }

    protected override void Decision(TaskDecision block, StringBuilder sb, TaskRenderContext context)
    {
        sb.Append("<div class=\"task-body__decision\">");

        // Already answered ⇒ say WHICH, and offer to change it. 🔒 §670: "no interest" is a recorded
        // answer, so it must read as one — not as an unfinished task.
        if (context.Decision is { } given)
        {
            var answered = given == TaskDecisionAnswer.Accepted
                ? block.AcceptLabel
                : block.DeclineLabel;
            sb.Append("<div class=\"task-body__decision-answered\">&check; You answered: <strong>")
              .Append(Escape(answered))
              .Append("</strong> — you can change this until the deadline.</div>");
        }

        if (context.TaskId is int taskId)
        {
            var form = Escape(DecisionFormId(taskId));
            AppendDecisionButton(sb, form, block.Key, "accept", block.AcceptLabel, "task-act--complete");
            // §688.6 — the decline is RED (the master-class cancel colour), not a grey ghost. It
            // had no colour at all, so it read as disabled next to a solid green accept.
            AppendDecisionButton(sb, form, block.Key, "decline", block.DeclineLabel, "task-act--decline");
        }
        else
        {
            // No task id ⇒ this is not an interactive surface (a preview, or a body rendered outside
            // a page). Say where the choice is made rather than drawing dead buttons.
            sb.Append("<p class=\"task-body__p\">Open this task in the hub to answer.</p>");
        }

        sb.Append("</div>");
    }

    private static void AppendDecisionButton(
        StringBuilder sb, string formId, string key, string answer, string label, string cssClass)
    {
        sb.Append("<button type=\"submit\" class=\"task-act ").Append(cssClass)
          .Append("\" form=\"").Append(formId)
          .Append("\" name=\"decision\" value=\"").Append(Escape(key)).Append(':').Append(answer)
          .Append("\">").Append(Escape(label)).Append("</button>");
    }

    /// <summary>
    /// §688.12 — the PLACEMENT MARKER for an embedded component.
    /// </summary>
    /// <remarks>
    /// 🔒 A marker, not the component. The renderer cannot emit a working editor: the forms need an
    /// antiforgery token, which only a Razor tag helper produces. The row partial splits the
    /// rendered HTML on this element and drops the real partial in at exactly this point — so the
    /// BODY still decides where the component sits, and the PAGE still owns the form. Same division
    /// as the decision buttons.
    /// </remarks>
    public static string EmbedMarker(string component) =>
        $"<div data-task-embed=\"{WebUtility.HtmlEncode(component)}\"></div>";

    protected override void Embed(TaskEmbed block, StringBuilder sb, TaskRenderContext context) =>
        sb.Append(EmbedMarker(block.Component));

    protected override void Text(TaskText inline, StringBuilder sb, TaskRenderContext context) =>
        sb.Append(Escape(inline.Value));

    protected override void Emphasis(TaskEmphasis inline, StringBuilder sb, TaskRenderContext context)
    {
        var (open, close) = inline.Kind switch
        {
            TaskEmphasisKind.Bold => ("<strong>", "</strong>"),
            TaskEmphasisKind.Italic => ("<em>", "</em>"),
            TaskEmphasisKind.Underline => ("<u>", "</u>"),
            // §675 — inline-styled rather than class-based so it survives everywhere a body renders.
            TaskEmphasisKind.Highlight => (
                "<mark style=\"background:#fff3cd;color:#664d03;padding:0 3px;border-radius:3px;\">",
                "</mark>"),
            _ => throw new NotSupportedException($"Unhandled emphasis {inline.Kind}."),
        };
        sb.Append(open);
        RenderInlines(inline.Children, sb, context);
        sb.Append(close);
    }

    protected override void Link(TaskLink inline, StringBuilder sb, TaskRenderContext context)
    {
        var href = context.ResolveText(inline.Href);
        var external = !href.StartsWith('/');

        sb.Append("<a href=\"").Append(Escape(href)).Append('"');
        if (external) sb.Append(" target=\"_blank\" rel=\"noopener noreferrer\"");
        sb.Append('>');
        RenderInlines(inline.Label, sb, context);
        if (external) sb.Append("<span class=\"ext-ico\" aria-hidden=\"true\">&#8599;</span>");
        sb.Append("</a>");
    }

    protected override void MailLink(TaskMailLink inline, StringBuilder sb, TaskRenderContext context) =>
        sb.Append("<a href=\"mailto:").Append(Escape(inline.Address))
          .Append("\" style=\"color:#1565c0;text-decoration:underline;\">")
          .Append(Escape(inline.Address)).Append("</a>");

    protected override void Placeholder(TaskPlaceholder inline, StringBuilder sb, TaskRenderContext context) =>
        sb.Append(Escape(context.Resolve(inline.Key)));

    protected override void LineBreak(StringBuilder sb, TaskRenderContext context) =>
        sb.Append("<br />");

    protected override string Finish(StringBuilder sb) => sb.ToString();

    private static string Escape(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
}
