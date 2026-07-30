using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer SoMe-graphics review console (REQUIREMENTS §18 steps 3 + 4).
///  - REVIEW QUEUE: generated-but-not-released speaker/session graphics; the
///    organizer RELEASES one (the hard gate — until then the speaker can't see it).
///  - OVERRULE: replace a generated graphic with the organizer's own PNG. The
///    stable key / path / link stay identical (replace-in-place) so the
///    hub→SharePoint link never breaks.
///  - INTERNAL SPONSOR GRAPHICS: listed here for the organizers' own SoMe posts —
///    these are NEVER shown to the sponsor.
/// Organizer-only. Mobile-first.
/// </summary>
[Authorize]
public class GraphicsModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly GraphicsService _graphics;
    private readonly SpeakerGraphicsReadyNotifier _ready;

    public GraphicsModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        GraphicsService graphics,
        SpeakerGraphicsReadyNotifier ready)
    {
        _db = db;
        _participant = participant;
        _graphics = graphics;
        _ready = ready;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public IReadOnlyList<Row> ReviewQueue { get; private set; } = Array.Empty<Row>();
    public IReadOnlyList<Row> SponsorGraphics { get; private set; } = Array.Empty<Row>();

    [BindProperty] public int AssetId { get; set; }
    [BindProperty] public IFormFile? Replacement { get; set; }

    public sealed record Row(
        int Id, string Type, string? Subject, string Status,
        bool Overridden, string? Url, bool HasFile);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostReleaseAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var asset = await _graphics.ReleaseAsync(me.EventId, AssetId, me.Email, ct);

        // §436: the release IS the moment the speaker can do something with the graphic, so
        // the "your graphics are ready — Open Help Promote" mail goes out now rather than at
        // the next 08:30 sweep. Idempotent through the shared ledger key: releasing a second
        // graphic for the same speaker does not send a second mail.
        var notified = await _ready.NotifyAsync(
            me.EventId,
            asset.ParticipantId is null ? Array.Empty<int>() : new[] { asset.ParticipantId.Value },
            ct);
        Message = notified > 0
            ? "Graphic released to the speaker — and they have been emailed a Help Promote link."
            : "Graphic released to the speaker.";

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostUnreleaseAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        await _graphics.UnreleaseAsync(me.EventId, AssetId, ct);
        Message = "Graphic pulled back (hidden from the speaker again).";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostPullSharePointGraphicsAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var result = await _graphics.PullSessionGraphicsAsync(me.EventId, ct);
        if (result.Matched == 0 && result.Unmatched == 0)
        {
            Message = "SharePoint graphics pull is not configured yet — nothing was pulled. "
                + "Set the SharePoint site + the MasterClass / Sessions folder paths to enable it.";
        }
        else
        {
            Message = $"Pulled session graphics from SharePoint: {result.Matched} matched, "
                + $"{result.Unmatched} session(s) had no matching file. Matched graphics are now "
                + "in the review queue below.";
        }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostOverruleAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        if (Replacement is null || Replacement.Length == 0)
        {
            Error = "Choose a PNG file to upload as the replacement.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        using var ms = new MemoryStream();
        await Replacement.CopyToAsync(ms, ct);
        await _graphics.OverruleAsync(me.EventId, AssetId, ms.ToArray(), ct);
        Message = "Replacement uploaded. The link to the graphic stays the same.";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        var queue = await _graphics.GetReviewQueueAsync(eventId, type: null, ct);
        var sponsors = await _graphics.GetInternalSponsorGraphicsAsync(eventId, ct);

        // Resolve speaker names + session titles for friendlier rows.
        var participantIds = queue.Concat(sponsors)
            .Where(g => g.ParticipantId is not null).Select(g => g.ParticipantId!.Value).Distinct().ToList();
        var names = await _db.Participants
            .Where(p => participantIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.FullName, ct);

        Row Map(GraphicAsset g)
        {
            string? subject = g.Type switch
            {
                GraphicAssetType.Sponsor => g.SponsorCompanyId,
                _ => g.ParticipantId is not null && names.TryGetValue(g.ParticipantId.Value, out var n) ? n : null,
            };
            return new Row(g.Id, g.Type.ToString(), subject, g.Status.ToString(),
                g.IsOrganizerOverridden, g.SharePointUrl, !string.IsNullOrEmpty(g.SharePointUrl));
        }

        ReviewQueue = queue.Where(g => g.Type != GraphicAssetType.Sponsor).Select(Map).ToList();
        SponsorGraphics = sponsors.Select(Map).ToList();
    }
}
