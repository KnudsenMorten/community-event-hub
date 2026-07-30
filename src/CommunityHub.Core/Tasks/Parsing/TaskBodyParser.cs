using CommunityHub.Core.Tasks.Model;

namespace CommunityHub.Core.Tasks.Parsing;

/// <summary>
/// §684.8 — parses an authored task body (Markdown prose + fenced directives) into the closed
/// <see cref="TaskBlock"/> model. No JSON anywhere (§684.5 constraint 2).
/// </summary>
/// <remarks>
/// <para><b>The format.</b> Prose is ordinary Markdown. Anything that is NOT prose is a fenced
/// directive — explicit, named, and impossible to produce by accident:</para>
/// <code>
/// Ship your packages to the address below.
///
/// :::section STEP 1 — ORDER
/// Filter on **Package Handling** and add the services you need.
///
/// :::button
/// label: Open the Sponsor Webshop
/// href: {{configuratorUrl}}
/// style: primary
/// interstitial: webshop
/// :::
/// :::
///
/// :::callout warning
/// Pricing is set by the venue and the freight partner and is out of our hands.
/// :::
///
/// :::address
/// label: Shipment to
/// value: {{shippingAddressDsv}}
/// marking: {{editionCode}} / Booth {{boothNumber}} / {{companyName}}
/// :::
///
/// :::data tvPurchase
/// :::
/// </code>
///
/// <para>🔒 <b>The directive set is CLOSED.</b> An unknown directive renders as NOTHING and raises a
/// <see cref="TaskBodyDiagnostic"/> — never as raw text on a sponsor's page. The same diagnostic
/// fails <c>TaskBodyCatalogTests</c> at build time, so a typo is a red test rather than a live
/// defect. Loud where it is cheap, harmless where it is not (§682).</para>
/// </remarks>
public static class TaskBodyParser
{
    private const string Fence = ":::";

    /// <summary>Parse an authored body. Never throws; malformed input yields diagnostics.</summary>
    public static TaskBody Parse(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return TaskBody.Empty;

        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var diagnostics = new List<TaskBodyDiagnostic>();
        var index = 0;

        var blocks = ParseBlocks(lines, ref index, diagnostics, insideDirective: false);

        return new TaskBody(blocks, diagnostics);
    }

    /// <summary>
    /// Parse blocks until the input ends or (when <paramref name="insideDirective"/>) a bare
    /// <c>:::</c> closer is reached, which is consumed.
    /// </summary>
    private static List<TaskBlock> ParseBlocks(
        string[] lines,
        ref int i,
        List<TaskBodyDiagnostic> diagnostics,
        bool insideDirective)
    {
        var blocks = new List<TaskBlock>();
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            var content = TaskInlineParser.Parse(string.Join("\n", paragraph));
            if (content.Count > 0) blocks.Add(new TaskParagraph(content));
            paragraph.Clear();
        }

        while (i < lines.Length)
        {
            var raw = lines[i];
            var line = raw.Trim();

            if (line == Fence)
            {
                FlushParagraph();
                if (insideDirective) { i++; return blocks; }

                diagnostics.Add(new TaskBodyDiagnostic(
                    i + 1, "a closing ':::' with no directive open — delete it or open a directive."));
                i++;
                continue;
            }

            if (line.StartsWith(Fence, StringComparison.Ordinal))
            {
                FlushParagraph();
                var block = ParseDirective(lines, ref i, diagnostics);
                if (block is not null) blocks.Add(block);
                continue;
            }

            if (line.Length == 0)
            {
                FlushParagraph();
                i++;
                continue;
            }

            if (TryReadListMarker(line, out var ordered, out _))
            {
                FlushParagraph();
                blocks.Add(ParseList(lines, ref i, ordered));
                continue;
            }

            paragraph.Add(line);
            i++;
        }

        FlushParagraph();

        if (insideDirective)
        {
            diagnostics.Add(new TaskBodyDiagnostic(
                lines.Length, "a directive was never closed — add a ':::' line."));
        }

        return blocks;
    }

    /// <summary>
    /// Is this line a list item? <c>* </c> / <c>- </c> for bullets, <c>1. </c> for ordered.
    /// </summary>
    private static bool TryReadListMarker(string line, out bool ordered, out string content)
    {
        ordered = false;
        content = string.Empty;

        // ParseList probes ahead to find where the run ends, so it reaches blank lines and the end
        // of the document. Guard first: indexing line[0] here threw IndexOutOfRange on the very
        // first body that put a blank line after a bullet list, which is the ordinary way to author
        // one.
        if (line.Length == 0) return false;

        // Bullet. Requires the trailing space, so "**bold** at line start" is prose, not a bullet —
        // the ordering hazard that forced TaskTextLinkifier's bullet pass to run before its bold
        // pass simply does not arise when the two are decided by different code.
        if ((line[0] == '*' || line[0] == '-') && line.Length > 1 && line[1] == ' ')
        {
            content = line[2..].Trim();
            return content.Length > 0;
        }

        // Ordered: one or more digits, then '.' or ')', then a space.
        var d = 0;
        while (d < line.Length && char.IsAsciiDigit(line[d])) d++;
        if (d > 0 && d + 1 < line.Length && (line[d] == '.' || line[d] == ')') && line[d + 1] == ' ')
        {
            ordered = true;
            content = line[(d + 2)..].Trim();
            return content.Length > 0;
        }

        return false;
    }

    /// <summary>Collect the consecutive run of list items of the same kind.</summary>
    private static TaskList ParseList(string[] lines, ref int i, bool ordered)
    {
        var items = new List<TaskListItem>();

        while (i < lines.Length)
        {
            var line = lines[i].Trim();
            if (!TryReadListMarker(line, out var itemOrdered, out var content)) break;
            if (itemOrdered != ordered) break;

            items.Add(new TaskListItem(TaskInlineParser.Parse(content)));
            i++;
        }

        return new TaskList(ordered, items);
    }

    /// <summary>
    /// Parse one <c>:::name arg</c> directive, consuming through its closing <c>:::</c>.
    /// Returns null when the directive is unknown or unusable — it renders as nothing.
    /// </summary>
    private static TaskBlock? ParseDirective(
        string[] lines,
        ref int i,
        List<TaskBodyDiagnostic> diagnostics)
    {
        var openLine = i + 1;
        var header = lines[i].Trim()[Fence.Length..].Trim();
        i++;

        var space = header.IndexOf(' ');
        var name = (space < 0 ? header : header[..space]).ToLowerInvariant();
        var arg = space < 0 ? string.Empty : header[(space + 1)..].Trim();

        switch (name)
        {
            case "section":
            {
                var children = ParseBlocks(lines, ref i, diagnostics, insideDirective: true);
                if (arg.Length == 0)
                {
                    diagnostics.Add(new TaskBodyDiagnostic(
                        openLine, "':::section' needs a title — ':::section STEP 1 — ORDER'."));
                    return null;
                }
                return new TaskSection(arg, children);
            }

            case "callout":
            {
                var children = ParseBlocks(lines, ref i, diagnostics, insideDirective: true);
                var tone = arg.ToLowerInvariant() switch
                {
                    "" or "info" => TaskCalloutTone.Info,
                    "warning" => TaskCalloutTone.Warning,
                    _ => (TaskCalloutTone?)null,
                };
                if (tone is null)
                {
                    diagnostics.Add(new TaskBodyDiagnostic(
                        openLine, $"unknown callout tone '{arg}' — use 'info' or 'warning'."));
                    return null;
                }
                return new TaskCallout(tone.Value, children);
            }

            case "button":
            {
                var fields = ReadFields(lines, ref i, diagnostics);
                return BuildButton(fields, openLine, diagnostics);
            }

            case "address":
            {
                var fields = ReadFields(lines, ref i, diagnostics);
                var label = fields.GetValueOrDefault("label", "").Trim();
                var value = fields.GetValueOrDefault("value", "").Trim();
                if (value.Length == 0)
                {
                    diagnostics.Add(new TaskBodyDiagnostic(
                        openLine, "':::address' needs a 'value:' — the address itself."));
                    return null;
                }
                var marking = fields.GetValueOrDefault("marking", "").Trim();
                return new TaskAddress(
                    label.Length > 0 ? label : "Shipment to",
                    value,
                    marking.Length > 0 ? marking : null);
            }

            case "data":
            {
                SkipToClose(lines, ref i, openLine, diagnostics);
                if (arg.Length == 0)
                {
                    diagnostics.Add(new TaskBodyDiagnostic(
                        openLine, "':::data' needs a provider name — ':::data tvPurchase'."));
                    return null;
                }
                return new TaskData(arg);
            }

            case "embed":
            {
                SkipToClose(lines, ref i, openLine, diagnostics);
                if (arg.Length == 0)
                {
                    diagnostics.Add(new TaskBodyDiagnostic(
                        openLine, "':::embed' needs a component name — ':::embed boothMembers'."));
                    return null;
                }
                // 🔒 CLOSED SET, checked at parse time. An embed the row partial does not know how
                // to render would leave a silent hole where the sponsor expects to do the work —
                // worse than a link, because nothing indicates anything is missing.
                if (!arg.Equals("boothMembers", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(new TaskBodyDiagnostic(
                        openLine,
                        $"unknown embed component '{arg}'. The set is closed: boothMembers."));
                    return null;
                }
                return new TaskEmbed(arg);
            }

            case "decision":
            {
                var fields = ReadFields(lines, ref i, diagnostics);
                if (arg.Length == 0)
                {
                    diagnostics.Add(new TaskBodyDiagnostic(
                        openLine, "':::decision' needs a key — ':::decision appGame'."));
                    return null;
                }
                var accept = fields.GetValueOrDefault("accept", "").Trim();
                var decline = fields.GetValueOrDefault("decline", "").Trim();
                if (accept.Length == 0 || decline.Length == 0)
                {
                    diagnostics.Add(new TaskBodyDiagnostic(
                        openLine,
                        "':::decision' needs both an 'accept:' and a 'decline:' label — declining is "
                        + "a recorded answer, not a way to dismiss the task."));
                    return null;
                }
                var form = fields.GetValueOrDefault("form", "").Trim();
                return new TaskDecision(arg, accept, decline, form.Length > 0 ? form : null);
            }

            default:
                // Quiet skip: an unknown directive gets ONE diagnostic naming the directive, not a
                // second one per line of its body. The author's mistake is the name; complaining
                // that ":::marquee" also "takes no body" buries the sentence that helps them.
                SkipToClose(lines, ref i, openLine, diagnostics, reportBody: false);
                diagnostics.Add(new TaskBodyDiagnostic(
                    openLine,
                    $"unknown directive ':::{name}'. The set is closed: section, callout, button, "
                    + "address, data, decision."));
                return null;
        }
    }

    /// <summary>
    /// §684.8 / §672 / §671 — build a button from its declared fields.
    /// </summary>
    private static TaskBlock? BuildButton(
        Dictionary<string, string> fields,
        int openLine,
        List<TaskBodyDiagnostic> diagnostics)
    {
        var label = fields.GetValueOrDefault("label", "").Trim();
        var href = fields.GetValueOrDefault("href", "").Trim();

        if (label.Length == 0 || href.Length == 0)
        {
            diagnostics.Add(new TaskBodyDiagnostic(
                openLine, "':::button' needs both a 'label:' and an 'href:'."));
            return null;
        }

        // §684.17 — href shape is validated as a typed field instead of by a regex over prose.
        // A placeholder is accepted here and validated again once resolved, at render.
        if (!IsAllowedHref(href))
        {
            diagnostics.Add(new TaskBodyDiagnostic(
                openLine,
                $"button href '{href}' is not allowed — use an in-hub route ('/Sponsor/…'), an "
                + "http(s) URL, or a {{placeholder}}."));
            return null;
        }

        var style = fields.GetValueOrDefault("style", "").Trim().ToLowerInvariant() switch
        {
            "" or "primary" => TaskButtonStyle.Primary,
            "secondary" => TaskButtonStyle.Secondary,
            _ => (TaskButtonStyle?)null,
        };
        if (style is null)
        {
            diagnostics.Add(new TaskBodyDiagnostic(
                openLine, "button 'style:' must be 'primary' or 'secondary'."));
            return null;
        }

        // 🔒 §671 — the TARGET DEFAULTS FROM THE HREF, which is what makes that bug unrepeatable.
        // "Open Booth Members" carried the ↗ external indicator and opened a new tab while pointing
        // at an in-hub page, so the button contradicted the sentence above it. An app-relative route
        // is in-hub; anything else leaves the hub. An explicit 'target:' can still override (a
        // {{placeholder}} href resolves to either), but nobody has to remember to set it.
        var declaredTarget = fields.GetValueOrDefault("target", "").Trim().ToLowerInvariant();
        var target = declaredTarget switch
        {
            "" => href.StartsWith('/') ? TaskLinkTarget.InHub : TaskLinkTarget.External,
            "inhub" or "in-hub" or "internal" => TaskLinkTarget.InHub,
            "external" => TaskLinkTarget.External,
            _ => (TaskLinkTarget?)null,
        };
        if (target is null)
        {
            diagnostics.Add(new TaskBodyDiagnostic(
                openLine, "button 'target:' must be 'inhub' or 'external'."));
            return null;
        }

        var interstitial = fields.GetValueOrDefault("interstitial", "").Trim().ToLowerInvariant()
            switch
            {
                "" or "none" => TaskInterstitial.None,
                "webshop" => TaskInterstitial.Webshop,
                "zoho" => TaskInterstitial.Zoho,
                _ => (TaskInterstitial?)null,
            };
        if (interstitial is null)
        {
            diagnostics.Add(new TaskBodyDiagnostic(
                openLine, "button 'interstitial:' must be 'webshop', 'zoho' or 'none'."));
            return null;
        }

        // 🔒 An in-hub button never raises a sign-in hand-off notice: there is no second system to
        // explain. §176/§487 — a notice where none is needed is noise, and noise is how people learn
        // to click through the one dialog that mattered.
        if (target == TaskLinkTarget.InHub && interstitial != TaskInterstitial.None)
        {
            diagnostics.Add(new TaskBodyDiagnostic(
                openLine,
                "an in-hub button cannot carry an interstitial — that notice explains landing in a "
                + "DIFFERENT system with its own sign-in."));
            return null;
        }

        return new TaskButton(label, href, style.Value, target.Value, interstitial.Value);
    }

    /// <summary>§684.17 — the allow-list of button destinations.</summary>
    private static bool IsAllowedHref(string href) =>
        href.StartsWith('/')
        || href.StartsWith("{{", StringComparison.Ordinal)
        || href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Read a field-shaped directive body (<c>key: value</c> lines) through its closing <c>:::</c>.
    /// A continuation line (no colon) appends to the previous field, so a long address can be
    /// wrapped in the source.
    /// </summary>
    private static Dictionary<string, string> ReadFields(
        string[] lines,
        ref int i,
        List<TaskBodyDiagnostic> diagnostics)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? last = null;

        while (i < lines.Length)
        {
            var line = lines[i].Trim();

            if (line == Fence) { i++; return fields; }

            if (line.StartsWith(Fence, StringComparison.Ordinal))
            {
                diagnostics.Add(new TaskBodyDiagnostic(
                    i + 1, "a directive that takes fields cannot contain another directive."));
                return fields;
            }

            if (line.Length == 0) { i++; continue; }

            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                last = line[..colon].Trim();
                fields[last] = line[(colon + 1)..].Trim();
            }
            else if (last is not null)
            {
                fields[last] = (fields[last] + "\n" + line).Trim();
            }
            else
            {
                diagnostics.Add(new TaskBodyDiagnostic(
                    i + 1, $"expected a 'key: value' line inside the directive, got '{line}'."));
            }

            i++;
        }

        diagnostics.Add(new TaskBodyDiagnostic(
            lines.Length, "a directive was never closed — add a ':::' line."));
        return fields;
    }

    /// <summary>Consume through the closing <c>:::</c> of a directive whose body is ignored.</summary>
    private static void SkipToClose(
        string[] lines,
        ref int i,
        int openLine,
        List<TaskBodyDiagnostic> diagnostics,
        bool reportBody = true)
    {
        while (i < lines.Length)
        {
            var line = lines[i].Trim();
            i++;
            if (line == Fence) return;
            if (reportBody && line.Length > 0)
            {
                diagnostics.Add(new TaskBodyDiagnostic(
                    i, "this directive takes no body — everything before its ':::' is ignored."));
            }
        }

        diagnostics.Add(new TaskBodyDiagnostic(
            openLine, "a directive was never closed — add a ':::' line."));
    }
}
