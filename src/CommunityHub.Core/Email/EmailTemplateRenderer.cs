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
///
/// HTML-ENCODING CONTRACT (REQUIREMENTS §10c-4 — "encode at the seam").
/// Token values are HTML-encoded by the renderer at substitution time, so a
/// name/title containing &lt;, &amp; or " can never break the markup of a
/// branded email — senders no longer need their own per-value Enc(...). The
/// only exception is a small, explicit set of tokens whose values are
/// deliberately HTML fragments built by the sender (e.g. a &lt;tr&gt; list, a
/// pre-encoded paragraph block). Those carry the <c>Html</c>/<c>Block</c>
/// naming suffix, plus the renderer-internal <c>bodyContent</c>; see
/// <see cref="RawHtmlTokens"/>. The email <c>Subject</c> header is plain text
/// and is returned un-encoded; only the HTML body is encoded.
/// </summary>
public sealed class EmailTemplateRenderer
{
    private static readonly Regex TokenRx =
        new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Tokens whose values are intentional, sender-built HTML fragments and
    /// must be inserted verbatim (NOT re-encoded). Everything else is encoded.
    /// Membership is by exact name OR by the conventional <c>Html</c>/<c>Block</c>
    /// suffix, so a new fragment token named e.g. <c>summaryHtml</c> is raw by
    /// default. Keep user/event free-text OUT of this set.
    /// </summary>
    internal static readonly IReadOnlySet<string> RawHtmlTokens =
        new HashSet<string>(StringComparer.Ordinal)
        {
            // Renderer-internal: the already-rendered content fragment.
            "bodyContent",
            // dueText carries a <strong>…</strong> span (its date is app-formatted).
            "dueText",
            // Master Class waitlist-signup terms: an optional, sender-built <p> block
            // (the auto-switch consent) or empty when not consented.
            "waitlistTerms",
            // Master Class reassignment "what the ticket holds": a sender-built <p>
            // block (either the inherited class name in <strong>, or a choose-one note).
            "heldMasterClass",
        };

    private static bool IsRawHtmlToken(string token) =>
        RawHtmlTokens.Contains(token)
        || token.EndsWith("Html", StringComparison.Ordinal)
        || token.EndsWith("Block", StringComparison.Ordinal);

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

        // The Subject HEADER is plain text (no HTML context), so it is
        // substituted raw — never HTML-encoded.
        var subject = Substitute(subjectLine, tokens, encode: false);

        // The HTML BODY is substituted with per-value HTML-encoding (the seam),
        // except for the explicit raw-HTML token set.
        var bodyContent = Substitute(fragment, tokens, encode: true);

        // The layout's {{bodyContent}} is the already-rendered (and already
        // encoded) fragment, so it must pass through verbatim. {{subject}} in
        // the layout body (if any) is plain-text content and gets encoded like
        // every other body token.
        var layoutTokens = new Dictionary<string, string>(tokens)
        {
            ["bodyContent"] = bodyContent,
            ["subject"] = subject,
        };
        var html = Substitute(_layoutTemplate, layoutTokens, encode: true);

        return new RenderedEmail(subject, html);
    }

    /// <summary>
    /// Render a content template's BODY FRAGMENT ONLY: strip the <c>Subject:</c>
    /// first line and substitute the body tokens (HTML-encoded at the seam, except
    /// the raw-HTML token set), but do NOT wrap it in the <c>_layout.html</c> shell.
    /// For surfaces that want the content but not the email chrome (e.g. an in-portal
    /// welcome card).
    /// </summary>
    public string RenderBodyFragment(
        string contentTemplate,
        IReadOnlyDictionary<string, string> tokens)
    {
        var fragment = StripSubjectLine(contentTemplate, out _);
        return Substitute(fragment, tokens, encode: true);
    }

    /// <summary>
    /// Replace every {{token}} with its mapped value (missing =&gt; "").
    /// When <paramref name="encode"/> is true, each value is HTML-encoded at the
    /// seam, except tokens in the raw-HTML set (see <see cref="IsRawHtmlToken"/>)
    /// which are inserted verbatim.
    /// </summary>
    private static string Substitute(
        string template, IReadOnlyDictionary<string, string> tokens, bool encode) =>
        TokenRx.Replace(template, m =>
        {
            var name = m.Groups[1].Value;
            if (!tokens.TryGetValue(name, out var value))
            {
                return string.Empty;
            }
            return encode && !IsRawHtmlToken(name)
                ? System.Net.WebUtility.HtmlEncode(value)
                : value;
        });

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
