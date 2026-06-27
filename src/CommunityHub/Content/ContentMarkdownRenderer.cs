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
    public bool TryRender(string slug, out string html)
    {
        html = string.Empty;
        var path = ResolvePath(slug);
        if (!File.Exists(path)) return false;

        var markdown = File.ReadAllText(path);
        html = Markdown.ToHtml(markdown, _pipeline);
        return true;
    }

    /// <summary>
    /// Read the slug's RAW markdown source (un-rendered), or null when the file is
    /// missing. Used by Otto's grounding builder to feed the role-scoped content to
    /// the assistant as plain text — the caller is responsible for the role-gate.
    /// </summary>
    public string? TryReadMarkdown(string slug)
    {
        var path = ResolvePath(slug);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
