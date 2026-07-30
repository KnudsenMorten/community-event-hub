using CommunityHub.Core.Integrations.Graphics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Sessions;

/// <summary>
/// §322e: PUBLIC (anonymous) EMBEDDED slide viewer — the deck renders INSIDE the hub page
/// (iframe) so attendees never bounce to a raw file tab or an off-site look: a PDF renders
/// natively via the inline proxy route, a PPTX through the Office EMBED frame pointed at
/// the anonymous download URL. Route: /Sessions/Slides/{sessionId}/{kind}.
/// </summary>
[AllowAnonymous]
public class SlideViewModel : PageModel
{
    private readonly SpeakerPresentationService _presentations;

    public SlideViewModel(SpeakerPresentationService presentations) => _presentations = presentations;

    public string Title { get; private set; } = string.Empty;
    public string KindLabel { get; private set; } = string.Empty;
    public string? EmbedUrl { get; private set; }
    public string DownloadUrl { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(int sessionId, string kind, CancellationToken ct)
    {
        var presentationKind = string.Equals(kind, "final", StringComparison.OrdinalIgnoreCase)
            ? PresentationKind.Final
            : PresentationKind.Preview;

        // Cached listing (~90 s) — cheap enough to resolve the session + latest deck name.
        var row = (await _presentations.ListPublicAsync(ct))
            .FirstOrDefault(s => s.SessionId == sessionId);
        var file = presentationKind == PresentationKind.Final ? row?.FinalFileName : row?.PreviewFileName;
        if (row is null || file is null) return NotFound();

        Title = row.Title;
        KindLabel = presentationKind == PresentationKind.Final ? "Final slides" : "Preview slides";
        DownloadUrl = $"/session-slides/{sessionId}/{kind.ToLowerInvariant()}/download";

        if (file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            EmbedUrl = $"/session-slides/{sessionId}/{kind.ToLowerInvariant()}/view";
        }
        else if (file.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
        {
            var absolute = $"{Request.Scheme}://{Request.Host}{DownloadUrl}";
            EmbedUrl = "https://view.officeapps.live.com/op/embed.aspx?src=" + Uri.EscapeDataString(absolute);
        }
        // zip ⇒ EmbedUrl stays null and the page offers the download.

        return Page();
    }
}
