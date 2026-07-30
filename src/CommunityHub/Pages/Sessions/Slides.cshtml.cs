using CommunityHub.Core.Integrations.Graphics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Sessions;

/// <summary>
/// §322c: PUBLIC (anonymous) session-slides catalogue — every session of the active
/// edition with View / Download for its EFFECTIVE deck (§322d: final wins over preview),
/// streamed through the hub proxy routes with the APP REGISTRATION's SharePoint
/// credentials. §322f: filter by TRACK + multi-select batch download (one ZIP with the
/// selected sessions' decks — e.g. "all Azure-track slides" in two clicks). Attendees
/// need no login and never see a SharePoint URL.
/// </summary>
[AllowAnonymous]
public class SlidesModel : PageModel
{
    private readonly SpeakerPresentationService _presentations;

    /// <summary>§467 — the configured Office-viewer size ceiling in BYTES (0 = check disabled).</summary>
    public long OfficeViewerMaxBytes { get; }

    public SlidesModel(
        SpeakerPresentationService presentations,
        Microsoft.Extensions.Options.IOptions<GraphicsSharePointOptions>? spOptions = null)
    {
        _presentations = presentations;
        OfficeViewerMaxBytes = spOptions?.Value.OfficeViewerMaxBytes ?? 0;
    }

    /// <summary>
    /// §467 — TRUE when the deck is a viewable TYPE but too LARGE for the embedded Office viewer,
    /// which refuses it with "File too large". The listing uses this to grey out "View" WITH a
    /// reason while leaving "Download" working, instead of offering a button guaranteed to fail.
    ///
    /// <para>Unknown size (null) ⇒ FALSE: only disable when we positively KNOW the file is over
    /// the ceiling. Guessing would hide a perfectly viewable deck.</para>
    /// </summary>
    public bool TooLargeToView(string? fileName, long? sizeBytes) =>
        CanView(fileName)
        && OfficeViewerMaxBytes > 0
        && sizeBytes is long n
        && n > OfficeViewerMaxBytes;

    /// <summary>The ceiling rendered for humans, e.g. "10 MB".</summary>
    public string OfficeViewerMaxDisplay =>
        OfficeViewerMaxBytes <= 0 ? "" : $"{OfficeViewerMaxBytes / (1024 * 1024)} MB";

    /// <summary>§322f: the selected track filter (querystring; null/empty = all tracks).</summary>
    [BindProperty(SupportsGet = true)]
    public string? Track { get; set; }

    /// <summary>§322g: the selected SPEAKER filter (multi-select; empty = all speakers).
    /// A session matches when ANY of its speakers is selected.</summary>
    [BindProperty(SupportsGet = true)]
    public List<string> Speakers { get; set; } = new();

    public IReadOnlyList<PublicSessionSlides> Sessions { get; private set; } = new List<PublicSessionSlides>();
    public IReadOnlyList<string> Tracks { get; private set; } = new List<string>();
    public IReadOnlyList<string> AllSpeakers { get; private set; } = new List<string>();
    public bool Configured { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Configured = _presentations.CanRead(PresentationKind.Preview)
                     || _presentations.CanRead(PresentationKind.Final);
        if (!Configured) return;

        var all = await _presentations.ListPublicAsync(ct);
        Tracks = all.Select(s => s.Track)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AllSpeakers = all.SelectMany(s => s.SpeakerNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        IEnumerable<PublicSessionSlides> filtered = all;
        if (!string.IsNullOrWhiteSpace(Track))
        {
            filtered = filtered.Where(s => string.Equals(s.Track, Track, StringComparison.OrdinalIgnoreCase));
        }
        var picked = Speakers.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (picked.Count > 0)
        {
            filtered = filtered.Where(s => s.SpeakerNames.Any(
                n => picked.Contains(n, StringComparer.OrdinalIgnoreCase)));
        }
        Sessions = filtered.ToList();
    }

    /// <summary>True when the deck's type can render in the browser / Office web viewer.</summary>
    public static bool CanView(string? fileName) =>
        fileName is not null
        && (fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase));
}
