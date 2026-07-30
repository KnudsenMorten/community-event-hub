using CommunityHub.Core.Config;
using Markdig;

namespace CommunityHub.Content;

/// <summary>
/// Renders the operator-authored CONTENT-HUB markdown (REQUIREMENTS §104–§123)
/// to HTML for the generic <c>/Info/{slug}</c> page. The prose lives in
/// <c>config/content/&lt;edition&gt;/{slug}.md</c>; images referenced as
/// <c>/content/&lt;edition&gt;/…</c> resolve from <c>wwwroot</c> via the static-files
/// middleware. Markdig is configured with the advanced extension set (tables,
/// auto-links, etc.). The content is trusted (in-repo, operator-pasted), so the
/// rendered HTML is emitted raw; raw HTML in the source is kept (e.g. authoring
/// comments) rather than escaped.
/// </summary>
public sealed class ContentMarkdownRenderer
{
    // Per-edition content folder. Mirrors the staged data path
    // (config/content/eldk27/) and the csproj copy rule.
    private const string ContentDir = "config/content/eldk27";

    private readonly MarkdownPipeline _pipeline;

    public ContentMarkdownRenderer()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();
    }

    /// <summary>The resolved on-disk path for a slug's markdown file.</summary>
    public string ResolvePath(string slug) =>
        ConfigPaths.Resolve($"{ContentDir}/{slug}.md");

    /// <summary>
    /// Render the slug's markdown to HTML. Returns false (and empty html) when
    /// the file is missing so the page can show a friendly empty state instead
    /// of 500-ing.
    /// </summary>
    public bool TryRender(string slug, out string html) => TryRender(slug, out html, publicView: false);

    /// <summary>
    /// §327g — the marker pair fencing a block as SIGNED-IN ONLY. A public render drops
    /// everything between them (inclusive).
    ///
    /// <para>A marker rather than a second copy of the file, so the public page and the in-hub
    /// page can never drift apart: one source, one edit, and the part that is not for the open
    /// internet is fenced in place where an author editing the page can see it.</para>
    /// </summary>
    public const string InternalOnlyStart = "<!-- internal-only:start -->";
    public const string InternalOnlyEnd = "<!-- internal-only:end -->";

    /// <summary>
    /// Render a content page. With <paramref name="publicView"/> true, blocks fenced by
    /// <see cref="InternalOnlyStart"/> / <see cref="InternalOnlyEnd"/> are removed BEFORE
    /// rendering — so the omitted text never reaches the HTML at all, not merely hidden by CSS.
    /// </summary>
    public bool TryRender(string slug, out string html, bool publicView) =>
        TryRender(slug, out html, publicView, isFeatureEnabled: null);

    /// <summary>
    /// As above, plus the §351-5 feature directives: with <paramref name="isFeatureEnabled"/>
    /// supplied, every <c>[feature:key]</c> line whose key is OFF for the edition is removed before
    /// rendering. Null (tests, and any caller with no edition in hand) leaves the directives to
    /// resolve as "shown", with the prefix stripped either way.
    /// </summary>
    public bool TryRender(
        string slug, out string html, bool publicView, Func<string, bool>? isFeatureEnabled)
    {
        html = string.Empty;
        var path = ResolvePath(slug);
        if (!File.Exists(path)) return false;

        var markdown = File.ReadAllText(path);
        if (publicView) markdown = StripInternalOnly(markdown);
        // §348 BUG: in the INTERNAL view the fence markers used to be left in the text and handed
        // straight to Markdig — and an HTML comment inside a markdown TABLE terminates the table, so
        // every row after the fence rendered as literal "| cell | cell |" text (observed on
        // /Info/ceh-introduction's Quick links: rows 5-8 broke out of the table). The markers are a
        // build-time DIRECTIVE, not content, so they must never reach the renderer in EITHER view —
        // here we drop just the marker LINES and keep the fenced content.
        else markdown = RemoveInternalOnlyMarkers(markdown);

        // §351-5: same reasoning as the marker lines above — a directive is never content, so it is
        // resolved out of the markdown BEFORE Markdig sees it, in both views.
        markdown = ApplyFeatureDirectives(markdown, isFeatureEnabled ?? (_ => true));

        html = Markdown.ToHtml(markdown, _pipeline);
        // §327g helper lives below (StripInternalOnly).
        html = OpenLinksInNewTab(html);
        return true;
    }

    /// <summary>
    /// §429 — every embedded link on a CONTENT page opens in a new tab.
    ///
    /// <para>Operator 2026-07-27: <i>"this link (and any others embedded links) must always open a
    /// new tab, instead of opening in the same tab - bad experience"</i>, pointing at the
    /// <c>survey results</c> link inside the Session Guidelines.</para>
    ///
    /// <para><b>What was wrong.</b> Since 2026-06-28 this re-targeted only <c>http(s)://</c> links.
    /// The one he clicked is <c>/survey/eldk27-topics/results</c> — root-relative, therefore
    /// untouched, therefore it replaced the guidelines he was in the middle of reading. The
    /// external/internal distinction was the wrong axis: what matters is that a CONTENT page is
    /// something you are READING, and any link that navigates away from it loses your place.</para>
    ///
    /// <para><b>Fragment links are excluded</b> (<c>href="#…"</c>) — those move you WITHIN the page
    /// you are already on, and opening a second copy of the page to jump to its own heading would
    /// be the very annoyance this fixes.</para>
    ///
    /// <para><c>rel="noopener noreferrer"</c> goes on every one: on external links it denies the
    /// opened page access to <c>window.opener</c>, and it costs nothing on internal ones.</para>
    /// </summary>
    public static string OpenLinksInNewTab(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;

        // Matched as a WHOLE opening tag and rewritten through an evaluator, deliberately —
        // NOT as "<a href=…" with the href assumed first. Content markdown contains
        // hand-authored raw HTML, and the very first sweep found
        // `<a class="task-link-btn" href="/speaker-template/download">`, which a
        // position-dependent pattern silently skips. §411 is the standing scar here: a
        // find-and-replace that matches nothing looks exactly like one that succeeded.
        return System.Text.RegularExpressions.Regex.Replace(
            html,
            "<a\\b[^>]*>",
            m =>
            {
                var tag = m.Value;

                // Already declares a target ⇒ the author decided; leave it exactly as written.
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        tag, "\\btarget\\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    return tag;
                }

                var href = System.Text.RegularExpressions.Regex.Match(
                    tag, "\\bhref\\s*=\\s*\"([^\"]*)\"",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // No href (an anchor target like <a id="…">), or a pure in-page fragment —
                // both stay put. Opening a second copy of the page to reach its own heading
                // is the annoyance this rule exists to remove.
                if (!href.Success || href.Groups[1].Value.StartsWith('#')) return tag;

                // §661 — a FILE DOWNLOAD must stay in the same tab.
                if (IsFileDownload(href.Groups[1].Value)) return tag;

                return string.Concat(
                    tag.AsSpan(0, tag.Length - 1),
                    " target=\"_blank\" rel=\"noopener noreferrer\">");
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// §661 — is this href a FILE DOWNLOAD rather than a page to read?
    ///
    /// <para>Operator 2026-07-29, on the speaker-template button: <i>"funy experience when i click
    /// this - it is like it opens up a page and then it closes again and start the file download"</i>.</para>
    ///
    /// <para><b>Why §429's rule does not apply here.</b> §429 re-targets every content link because
    /// navigating away "loses your place" on a page you are reading. A download never navigates away
    /// at all — the response is <c>Content-Disposition: attachment</c>, so the current page stays
    /// exactly where it is. Opening a tab for one gives the browser a document it cannot render, and
    /// it closes the tab again the moment the attachment arrives. That flash IS the bug: the new tab
    /// bought nothing and cost a visible glitch.</para>
    ///
    /// <para>🔒 The comment above deliberately named <c>/speaker-template/download</c> as proof the
    /// sweep reached hand-authored HTML. It did — this is the link it reached, and re-targeting it
    /// was wrong. Matching the whole tag was still right; only the exclusion was missing.</para>
    ///
    /// <para>Two shapes, because the hub uses both: a route whose last segment is <c>download</c> or
    /// ends <c>-download</c> (<c>/speaker-template/download</c>, <c>/logo-pack/download</c>,
    /// <c>/session-slides/{id}/{slug}/download</c>, <c>/session-slides/batch-download</c>), and a
    /// direct link to a document file extension.</para>
    /// </summary>
    public static bool IsFileDownload(string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return false;

        // Compare the PATH only — a query string or fragment must not hide the segment.
        var path = href.Split('?', '#')[0].TrimEnd('/');
        if (path.Length == 0) return false;

        var lastSlash = path.LastIndexOf('/');
        var last = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;

        if (last.Equals("download", StringComparison.OrdinalIgnoreCase)
            || last.EndsWith("-download", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return DownloadFileExtensions.Any(
            ext => last.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Document types the hub links to directly — a click on one downloads (or hands off to
    /// a native viewer); it never renders as the page you were reading.</summary>
    private static readonly string[] DownloadFileExtensions =
    {
        ".pdf", ".potx", ".pptx", ".ppt", ".docx", ".doc", ".xlsx", ".xls", ".zip", ".csv", ".ics",
    };

    /// <summary>
    /// §351-5 — resolve every feature key used across the page's directives ONCE (a key repeats
    /// across many rows), giving the caller a cheap synchronous lookup for
    /// <see cref="TryRender(string, out string, bool, Func{string, bool})"/>.
    ///
    /// <para>A null gate returns "everything shown", so a caller without an edition — or a test —
    /// behaves exactly as before this existed.</para>
    /// </summary>
    public async Task<Func<string, bool>> BuildFeatureLookupAsync(
        string slug,
        CommunityHub.Core.Settings.FeatureGateService? gate,
        int eventId,
        CancellationToken ct = default)
    {
        if (gate is null) return _ => true;

        var markdown = TryReadMarkdown(slug);
        if (string.IsNullOrEmpty(markdown)) return _ => true;

        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var key in FeatureKeysIn(markdown))
        {
            map[key] = await gate.IsFeatureEnabledAsync(key, eventId, ct);
        }
        // Unlisted key ⇒ shown. The keys ARE all listed (they come from the same file), so this only
        // covers a caller passing a lookup around; it never hides content by accident.
        return key => !map.TryGetValue(key, out var on) || on;
    }

    /// <summary>
    /// Read the slug's RAW markdown source (un-rendered), or null when the file is
    /// missing. Used by the AI Helper's grounding builder to feed the role-scoped content
    /// to the assistant as plain text — the caller is responsible for the role-gate.
    /// </summary>
    public string? TryReadMarkdown(string slug)
    {
        var path = ResolvePath(slug);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>
    /// §327g — remove every <see cref="InternalOnlyStart"/>…<see cref="InternalOnlyEnd"/>
    /// block (markers included) from raw markdown.
    ///
    /// <para>FAIL-CLOSED on a malformed fence: an unterminated start marker drops everything
    /// from it to the end of the document. An author who forgets the closing marker loses
    /// content from the PUBLIC page — visibly, and safely — rather than publishing the block
    /// they meant to withhold.</para>
    /// </summary>
    public static string StripInternalOnly(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;

        var sb = new System.Text.StringBuilder(markdown.Length);
        var cursor = 0;

        while (true)
        {
            var start = markdown.IndexOf(InternalOnlyStart, cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(markdown, cursor, markdown.Length - cursor);
                break;
            }

            sb.Append(markdown, cursor, start - cursor);

            var end = markdown.IndexOf(InternalOnlyEnd, start, StringComparison.Ordinal);
            if (end < 0) break;   // unterminated ⇒ drop the rest (fail closed)

            cursor = end + InternalOnlyEnd.Length;
        }

        return sb.ToString();
    }

    /// <summary>
    /// §348 — remove the fence MARKER LINES while KEEPING the fenced content, for the internal
    /// view. The markers are a build-time directive; passing them through to Markdig broke any
    /// markdown construct they sat inside — most visibly a TABLE, which an HTML comment
    /// terminates, dumping every later row out as literal <c>| cell | cell |</c> text.
    ///
    /// <para>Whole LINES are removed (not just the marker text) so the fence never leaves a blank
    /// line behind inside a table body, which would end the table just as the comment did.</para>
    /// </summary>
    public static string RemoveInternalOnlyMarkers(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;
        if (!markdown.Contains(InternalOnlyStart, StringComparison.Ordinal)
            && !markdown.Contains(InternalOnlyEnd, StringComparison.Ordinal))
        {
            return markdown;
        }

        var kept = markdown
            .Split('\n')
            .Where(line =>
            {
                var t = line.Trim();
                return t != InternalOnlyStart && t != InternalOnlyEnd;
            });

        return string.Join("\n", kept);
    }

    /// <summary>
    /// §351-5 — the FEATURE directive (operator 2026-07-26: <i>"dont include features which are
    /// turned off"</i>).
    ///
    /// <para>A line prefixed <c>[feature:some-key]</c> describes something that only exists when
    /// <c>some-key</c> is enabled for the edition. Feature ON ⇒ the prefix is stripped and the line
    /// stays; feature OFF ⇒ the whole line is dropped. Either way the directive never reaches
    /// Markdig, so it can never appear as literal text.</para>
    ///
    /// <para><b>Why a line PREFIX and not an HTML-comment fence like internal-only.</b> What has to
    /// be hidden is individual TABLE ROWS in the features-per-role tables, and §348 is the scar: an
    /// HTML comment inside a markdown table TERMINATES it, so every row after the comment renders as
    /// literal <c>| cell | cell |</c>. A prefix lives inside the line it governs and cannot break the
    /// construct around it.</para>
    ///
    /// <para>This replaces a static list with no way to stay in step with the flags. The alternative
    /// on the table was to delete the currently-off rows by hand — fast, but stale the next time a
    /// flag moves. An unknown key resolves to the catalog fallback (shown), and
    /// <c>ContentFeatureDirectiveTests</c> asserts every key used in the content files is a REAL
    /// catalog key, so a typo is a red test rather than a silently missing feature.</para>
    /// </summary>
    public const string FeaturePrefix = "[feature:";

    /// <summary>
    /// Apply the §351-5 feature directives. <paramref name="isEnabled"/> answers for one key; the
    /// caller owns the edition-scoped lookup (and should cache — a key repeats across rows).
    /// </summary>
    public static string ApplyFeatureDirectives(string markdown, Func<string, bool> isEnabled)
    {
        if (string.IsNullOrEmpty(markdown)
            || !markdown.Contains(FeaturePrefix, StringComparison.Ordinal))
        {
            return markdown;
        }

        var kept = new List<string>();
        foreach (var line in markdown.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith(FeaturePrefix, StringComparison.Ordinal))
            {
                kept.Add(line);
                continue;
            }

            var close = trimmed.IndexOf(']');
            if (close < 0)
            {
                // Malformed directive: KEEP the line rather than swallow it. It will look wrong on
                // the page, which is the right failure — visible, and never a silent deletion.
                kept.Add(line);
                continue;
            }

            var key = trimmed[(FeaturePrefix.Length)..close].Trim();
            if (!isEnabled(key)) continue;            // feature OFF ⇒ the row is not written down

            kept.Add(trimmed[(close + 1)..].TrimStart());
        }

        return string.Join("\n", kept);
    }

    /// <summary>Every feature key referenced by a feature directive in <paramref name="markdown"/>.</summary>
    public static IReadOnlyList<string> FeatureKeysIn(string markdown)
    {
        var keys = new List<string>();
        if (string.IsNullOrEmpty(markdown)) return keys;

        foreach (var line in markdown.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith(FeaturePrefix, StringComparison.Ordinal)) continue;
            var close = trimmed.IndexOf(']');
            if (close < 0) continue;
            var key = trimmed[(FeaturePrefix.Length)..close].Trim();
            if (key.Length > 0 && !keys.Contains(key)) keys.Add(key);
        }
        return keys;
    }
}
