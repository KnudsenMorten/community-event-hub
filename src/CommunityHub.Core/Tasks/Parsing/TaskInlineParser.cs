using System.Text;
using System.Text.RegularExpressions;
using CommunityHub.Core.Tasks.Model;

namespace CommunityHub.Core.Tasks.Parsing;

/// <summary>
/// §684.8 — parses the inline markup of a task body into <see cref="TaskInline"/> nodes.
/// </summary>
/// <remarks>
/// <para>🔒 <b>ONE left-to-right scan, not a stack of ordered regex passes.</b> This is the whole
/// point of the change. <c>TaskTextLinkifier</c> applies 16 regex passes to a STRING, each rewriting
/// the output of the last, with ordering constraints between them: §600.5 had to place the italic
/// pass after the bullet and bold passes, and §675 picked <c>==</c> for highlight specifically
/// because it was the one delimiter that could not collide with the others. Every new formatting
/// need added a pass and a new collision risk.</para>
///
/// <para>Scanning once removes the category. At any position exactly one marker can open, longest
/// first (<c>**</c> before <c>*</c>), and its content is parsed recursively — so an asterisk pair
/// inside a bold span is simply inner content, not "an earlier pass already ate it". Adding a
/// marker cannot reorder or collide with an existing one.</para>
///
/// <para><b>Unmatched delimiters are LITERAL, never an error.</b> An author writing "2 * 3" or a
/// stray underscore gets those characters on the page. A body must never fail to render because of
/// punctuation (§682).</para>
/// </remarks>
public static class TaskInlineParser
{
    /// <summary>
    /// Bare URLs inside literal text. Excludes trailing sentence punctuation so
    /// "see https://foo.dk/x." does not swallow the full stop.
    /// </summary>
    private static readonly Regex BareUrl = new(
        @"https?://[^\s<>""'`]+?(?=[\s<>""'`]|[.,;:!?)\]}](?:\s|$)|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Bare e-mail addresses inside literal text. Requires a real TLD (2+ chars).</summary>
    private static readonly Regex BareEmail = new(
        @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b",
        RegexOptions.Compiled);

    /// <summary>
    /// The emphasis delimiters, LONGEST FIRST. Order in this array is the only ordering rule in the
    /// parser and it exists for one reason: <c>**</c> must be tried before <c>*</c>, or every bold
    /// span would open as an empty italic.
    /// </summary>
    private static readonly (string Delimiter, TaskEmphasisKind Kind)[] Emphasis =
    {
        ("**", TaskEmphasisKind.Bold),
        ("==", TaskEmphasisKind.Highlight),
        ("__", TaskEmphasisKind.Underline),
        ("*",  TaskEmphasisKind.Italic),
    };

    /// <summary>Parse one run of inline markup.</summary>
    /// <param name="text">The source text. Newlines become <see cref="TaskLineBreak"/>.</param>
    public static IReadOnlyList<TaskInline> Parse(string? text)
    {
        var nodes = new List<TaskInline>();
        if (string.IsNullOrEmpty(text)) return nodes;

        var literal = new StringBuilder();
        var i = 0;

        void FlushLiteral()
        {
            if (literal.Length == 0) return;
            AppendLiteral(nodes, literal.ToString());
            literal.Clear();
        }

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\n')
            {
                FlushLiteral();
                nodes.Add(new TaskLineBreak());
                i++;
                continue;
            }

            // {{placeholder}} — resolved at RENDER time (§684.9 / §666), never at seed time.
            if (c == '{' && Match(text, i, "{{"))
            {
                var close = text.IndexOf("}}", i + 2, StringComparison.Ordinal);
                var key = close > i + 2 ? text[(i + 2)..close].Trim() : null;
                if (key is { Length: > 0 } && !key.Contains('\n'))
                {
                    FlushLiteral();
                    nodes.Add(new TaskPlaceholder(key));
                    i = close + 2;
                    continue;
                }
            }

            // [label](href) — an INLINE link. A BUTTON is a :::button directive and a block; the two
            // are deliberately different things (§677).
            if (c == '[')
            {
                var link = TryReadLink(text, i);
                if (link is not null)
                {
                    FlushLiteral();
                    nodes.Add(new TaskLink(Parse(link.Value.Label), link.Value.Href));
                    i = link.Value.End;
                    continue;
                }
            }

            var opened = false;
            foreach (var (delimiter, kind) in Emphasis)
            {
                if (!Match(text, i, delimiter)) continue;

                var span = TryReadEmphasis(text, i, delimiter);
                if (span is null) continue;   // no valid closer ⇒ fall through to literal

                FlushLiteral();
                nodes.Add(new TaskEmphasis(kind, Parse(span.Value.Inner)));
                i = span.Value.End;
                opened = true;
                break;
            }
            if (opened) continue;

            literal.Append(c);
            i++;
        }

        FlushLiteral();
        return nodes;
    }

    private static bool Match(string s, int i, string token) =>
        i + token.Length <= s.Length && string.CompareOrdinal(s, i, token, 0, token.Length) == 0;

    /// <summary>
    /// Read an emphasis span opening at <paramref name="start"/>. Returns null when there is no
    /// valid closer, in which case the delimiter is literal text.
    /// </summary>
    /// <remarks>
    /// The open/close rules mirror the regexes this replaces: the opener must be followed by a
    /// non-space, the content carries no newline (emphasis never spans a line), and the content may
    /// not contain the delimiter's own character — so <c>**a**</c> can never be read as an italic
    /// <c>*</c> wrapping <c>*a*</c>.
    /// </remarks>
    private static (string Inner, int End)? TryReadEmphasis(string s, int start, string delimiter)
    {
        var contentStart = start + delimiter.Length;
        if (contentStart >= s.Length || char.IsWhiteSpace(s[contentStart])) return null;

        var marker = delimiter[0];
        for (var j = contentStart; j < s.Length; j++)
        {
            if (s[j] == '\n') return null;              // never spans a line

            if (s[j] != marker) continue;

            // A single-char delimiter must not match the first half of its doubled form
            // (an italic scan must not stop on the "**" that closes a bold span).
            if (delimiter.Length == 1 && Match(s, j, new string(marker, 2))) return null;
            if (!Match(s, j, delimiter)) return null;   // "==a=b==" — a lone marker inside: literal

            var inner = s[contentStart..j];
            // Closer must be preceded by a non-space, and the span must be non-empty.
            if (inner.Length == 0 || char.IsWhiteSpace(inner[^1])) return null;

            return (inner, j + delimiter.Length);
        }

        return null;
    }

    /// <summary>Read a <c>[label](href)</c> starting at <paramref name="start"/>, or null.</summary>
    private static (string Label, string Href, int End)? TryReadLink(string s, int start)
    {
        var labelEnd = -1;
        for (var j = start + 1; j < s.Length; j++)
        {
            if (s[j] == '\n') return null;
            if (s[j] == ']') { labelEnd = j; break; }
        }
        if (labelEnd < 0 || labelEnd + 1 >= s.Length || s[labelEnd + 1] != '(') return null;

        var hrefEnd = -1;
        for (var j = labelEnd + 2; j < s.Length; j++)
        {
            if (s[j] == '\n' || char.IsWhiteSpace(s[j])) return null;
            if (s[j] == ')') { hrefEnd = j; break; }
        }
        if (hrefEnd < 0) return null;

        var label = s[(start + 1)..labelEnd];
        var href = s[(labelEnd + 2)..hrefEnd];
        return label.Length == 0 || href.Length == 0 ? null : (label, href, hrefEnd + 1);
    }

    /// <summary>
    /// Split a run of LITERAL text into text / bare-URL / bare-e-mail nodes.
    /// </summary>
    /// <remarks>
    /// Run on literal text only — after markup scanning, so a URL inside a <c>[label](href)</c> can
    /// never be matched a second time. That double-match is exactly what
    /// <c>TaskTextLinkifier</c> needed its sentinel/side-buffer dance to avoid; here the two simply
    /// cannot meet.
    /// </remarks>
    private static void AppendLiteral(List<TaskInline> nodes, string literal)
    {
        var cursor = 0;
        foreach (Match m in BareUrl.Matches(literal))
        {
            if (m.Index < cursor) continue;
            AppendEmails(nodes, literal[cursor..m.Index]);
            nodes.Add(new TaskLink(new TaskInline[] { new TaskText(m.Value) }, m.Value));
            cursor = m.Index + m.Length;
        }
        AppendEmails(nodes, literal[cursor..]);
    }

    private static void AppendEmails(List<TaskInline> nodes, string literal)
    {
        if (literal.Length == 0) return;

        var cursor = 0;
        foreach (Match m in BareEmail.Matches(literal))
        {
            if (m.Index < cursor) continue;
            if (m.Index > cursor) nodes.Add(new TaskText(literal[cursor..m.Index]));
            nodes.Add(new TaskMailLink(m.Value));
            cursor = m.Index + m.Length;
        }
        if (cursor < literal.Length) nodes.Add(new TaskText(literal[cursor..]));
    }
}
