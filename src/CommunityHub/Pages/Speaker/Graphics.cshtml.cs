using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Speaker;

/// <summary>
/// The speaker's SoMe-graphics page (REQUIREMENTS §18 step 5). Shows the speaker
/// their PRE-STAGED, RELEASED graphics (their headshot graphic + per-session
/// graphics) — and ONLY released ones (the organizer review gate). For each the
/// speaker can DOWNLOAD the PNG or open a LinkedIn/X share DRAFT in their own
/// context. Also the "I'm speaking at ELDK27" button that builds a LinkedIn DRAFT.
///
/// NEVER an auto-post: the buttons either download or open the network composer
/// prefilled; the speaker finalizes + posts himself.
/// </summary>
[Authorize]
public class GraphicsModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly GraphicsService _graphics;

    public GraphicsModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        GraphicsService graphics)
    {
        _db = db;
        _participant = participant;
        _graphics = graphics;
    }

    public static readonly ParticipantRole[] EligibleRoles =
        { ParticipantRole.Speaker, ParticipantRole.MasterclassSpeaker };

    public bool AccessDenied { get; private set; }
    public ParticipantRole Role { get; private set; }
    public bool CanPost { get; private set; }

    public string EventDisplayName { get; private set; } = "ELDK27";
    public string EventDates { get; private set; } = string.Empty;
    public string TicketUrl { get; private set; } = "eldk27.expertslive.dk";

    public IReadOnlyList<GraphicCard> Cards { get; private set; } = Array.Empty<GraphicCard>();

    /// <summary>The "I'm speaking at ELDK27" announcement draft (LinkedIn).</summary>
    public SocialShareDraft? AnnouncementDraft { get; private set; }

    public sealed record GraphicCard(
        int Id,
        string Kind,
        string? Title,
        string? DownloadUrl,
        bool HasStoredFile,
        SocialShareDraft LinkedInDraft,
        SocialShareDraft XDraft);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        Role = me.Role;
        if (!EligibleRoles.Contains(me.Role)) { AccessDenied = true; return Page(); }

        CanPost = _graphics.CanPostToSocial;
        await LoadEventFactsAsync(me.EventId, ct);

        var visible = await _graphics.GetSpeakerVisibleAsync(me.EventId, me.ParticipantId, ct);

        // Resolve session titles for per-session cards.
        var sessionIds = visible.Where(g => g.SessionId is not null).Select(g => g.SessionId!.Value).ToList();
        var sessionTitles = await _db.Sessions
            .Where(s => sessionIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Title, ct);

        var cards = new List<GraphicCard>();
        foreach (var g in visible)
        {
            var (kind, title) = g.Type switch
            {
                GraphicAssetType.Session =>
                    ("Session graphic",
                     g.SessionId is not null && sessionTitles.TryGetValue(g.SessionId.Value, out var t) ? t : "Session"),
                _ => ("Speaker graphic", (string?)null),
            };

            var liText = BuildText(title);
            cards.Add(new GraphicCard(
                g.Id, kind, title, g.SharePointUrl,
                !string.IsNullOrEmpty(g.SharePointUrl),
                _graphics.BuildSessionShareDraft(EventDisplayName, TicketUrl, title ?? EventDisplayName, g.SharePointUrl, SocialNetwork.LinkedIn),
                _graphics.BuildSessionShareDraft(EventDisplayName, TicketUrl, title ?? EventDisplayName, g.SharePointUrl, SocialNetwork.X)));
        }
        Cards = cards;

        AnnouncementDraft = _graphics.BuildSpeakingAnnouncementDraft(
            EventDisplayName, EventDates, TicketUrl, me.FullName, sessionTitle: null,
            graphicUrl: visible.FirstOrDefault(g => g.Type == GraphicAssetType.Speaker)?.SharePointUrl,
            SocialNetwork.LinkedIn);

        return Page();
    }

    private string BuildText(string? sessionTitle) =>
        string.IsNullOrWhiteSpace(sessionTitle)
            ? $"Catch me at {EventDisplayName}!"
            : $"Catch my session \"{sessionTitle}\" at {EventDisplayName}!";

    private async Task LoadEventFactsAsync(int eventId, CancellationToken ct)
    {
        var evt = await _db.Events.FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (evt is not null)
        {
            EventDisplayName = string.IsNullOrWhiteSpace(evt.DisplayName) ? "ELDK27" : evt.DisplayName;
            EventDates = $"{evt.StartDate:d MMM} – {evt.EndDate:d MMM yyyy}";
        }
    }
}
