using System.Text.RegularExpressions;

namespace CommunityHub.Core.Email;

/// <summary>A rendered email: a subject line and an HTML body.</summary>
public sealed record RenderedEmail(string Subject, string HtmlBody);

/// <summary>
/// Renders the hub's branded emails (CONTEXT.md section 11d). Each email is a
/// content template (e.g. task-deadline-reminder.html) whose first line is
/// "Subject: ..." and whose remaining lines are an HTML fragment with
/// {{token}} placeholders. The fragment is substituted, then placed into the
/// shared branded shell (_layout.html) as {{bodyContent}}.
///
/// Editing _layout.html restyles every email; editing one content template
/// changes only that email. No HTML is hard-coded in C#.
/// </summary>
public sealed class EmailTemplateRenderer
{
    private static readonly Regex TokenRx =
        new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    private readonly string _layoutTemplate;

    /// <param name="layoutTemplate">
    /// The full text of _layout.html (its first line is "Subject: {{subject}}",
    /// which is ignored for the layout - the subject comes from the content
    /// template). Load it once at startup and reuse this renderer.
    /// </param>
    public EmailTemplateRenderer(string layoutTemplate)
    {
        _layoutTemplate = StripSubjectLine(layoutTemplate, out _);
    }

    /// <summary>
    /// Render a content template into a finished email.
    /// </summary>
    /// <param name="contentTemplate">
    /// The full text of a content template file (first line "Subject: ...").
    /// </param>
    /// <param name="tokens">
    /// Token values. Used for both the content fragment and the layout shell;
    /// a token missing from the map is replaced with an empty string.
    /// </param>
    public RenderedEmail Render(
        string contentTemplate,
        IReadOnlyDictionary<string, string> tokens)
    {
        // The content template carries its own Subject: line.
        var fragment = StripSubjectLine(contentTemplate, out var subjectLine);

        var subject = Substitute(subjectLine, tokens);
        var bodyContent = Substitute(fragment, tokens);

        // The layout's {{bodyContent}} and {{subject}} are filled, then any
        // remaining theme tokens ({{brandColor}}, {{logoUrl}}, ...) substituted.
        var layoutTokens = new Dictionary<string, string>(tokens)
        {
            ["bodyContent"] = bodyContent,
            ["subject"] = subject,
        };
        var html = Substitute(_layoutTemplate, layoutTokens);

        return new RenderedEmail(subject, html);
    }

    /// <summary>Replace every {{token}} with its mapped value (missing =&gt; "").</summary>
    private static string Substitute(
        string template, IReadOnlyDictionary<string, string> tokens) =>
        TokenRx.Replace(template, m =>
            tokens.TryGetValue(m.Groups[1].Value, out var value)
                ? value
                : string.Empty);

    /// <summary>
    /// Split a "Subject: ..." first line off a template. Returns the body and
    /// outputs the subject text (without the "Subject:" prefix). If there is
    /// no such line, the whole input is the body and the subject is empty.
    /// </summary>
    private static string StripSubjectLine(string template, out string subject)
    {
        subject = string.Empty;
        var newlineIndex = template.IndexOf('\n');
        if (newlineIndex < 0)
        {
            return template;
        }

        var firstLine = template[..newlineIndex].TrimEnd('\r');
        if (firstLine.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase))
        {
            subject = firstLine["Subject:".Length..].Trim();
            return template[(newlineIndex + 1)..];
        }
        return template;
    }
}
