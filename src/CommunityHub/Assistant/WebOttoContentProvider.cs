using CommunityHub.Content;
using CommunityHub.Core.Assistant;

namespace CommunityHub.Assistant;

/// <summary>
/// Web implementation of <see cref="IOttoContentProvider"/>: returns the raw markdown
/// of a content-hub page from disk via <see cref="ContentMarkdownRenderer"/>. It does
/// NOT decide who may see a page — <see cref="OttoGroundingBuilder"/> only ever asks
/// for slugs the role is allowed to see (ContentPageRegistry.ForRole), so this is a
/// pure reader.
/// </summary>
public sealed class WebOttoContentProvider : IOttoContentProvider
{
    private readonly ContentMarkdownRenderer _renderer;

    public WebOttoContentProvider(ContentMarkdownRenderer renderer) => _renderer = renderer;

    public string? GetContentMarkdown(string slug) => _renderer.TryReadMarkdown(slug);
}
