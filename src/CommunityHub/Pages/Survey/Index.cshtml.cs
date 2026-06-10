using System.Security.Cryptography;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Surveys;
using CommunityHub.Surveys;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Pages.Survey;

/// <summary>
/// PUBLIC anonymous survey wizard. Three steps, single Razor page:
///   1. Pick one technical track.
///   2. Rank three topic picks within that track.
///   3. Pick the desired session level for each of the three picks.
///
/// Step transitions are client-side (no server round-trip between steps);
/// only the final submit POSTs. Once submitted, the same page renders the
/// thank-you state with a link to the public results dashboard.
///
/// Catalog (tracks / topics / level examples) lives in JSON under
/// App_Data/Surveys/{slug}.json — editing the JSON does not require a code
/// change or a DB migration.
/// </summary>
[AllowAnonymous]
public class IndexModel : PageModel
{
    private readonly SurveyDefinitionProvider _definitions;
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<IndexModel> _log;

    public IndexModel(
        SurveyDefinitionProvider definitions,
        CommunityHubDbContext db,
        TimeProvider clock,
        ILogger<IndexModel> log)
    {
        _definitions = definitions;
        _db = db;
        _clock = clock;
        _log = log;
    }

    // --- View state -------------------------------------------------------
    public SurveyDefinition? Survey { get; private set; }
    public string Slug { get; private set; } = string.Empty;
    public bool SubmittedOk { get; private set; }
    public string? ErrorMessage { get; private set; }

    // --- Form binding (single POST on submit) -----------------------------
    [BindProperty] public string SelectedTrackId { get; set; } = string.Empty;
    [BindProperty] public string? Pick1TopicId { get; set; }
    [BindProperty] public string? Pick2TopicId { get; set; }
    [BindProperty] public string? Pick3TopicId { get; set; }
    [BindProperty] public SurveyLevel? Pick1Level { get; set; }
    [BindProperty] public SurveyLevel? Pick2Level { get; set; }
    [BindProperty] public SurveyLevel? Pick3Level { get; set; }
    [BindProperty] public string? Comment { get; set; }

    /// <summary>
    /// Honeypot. Hidden CSS-off-screen on the page. Humans cannot see it; bots
    /// fill every input. Any non-empty value -> silent 200 OK (no DB write).
    /// </summary>
    [BindProperty] public string? Website { get; set; }

    public IActionResult OnGet(string slug)
    {
        Slug = slug ?? string.Empty;
        Survey = _definitions.TryGet(Slug);
        if (Survey is null) return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string slug, CancellationToken ct)
    {
        Slug = slug ?? string.Empty;
        Survey = _definitions.TryGet(Slug);
        if (Survey is null) return NotFound();

        // Honeypot. Pretend success without writing anything.
        if (!string.IsNullOrWhiteSpace(Website))
        {
            _log.LogInformation("Survey honeypot tripped for slug={Slug} from {Ip}", Slug, HttpContext.Connection.RemoteIpAddress);
            SubmittedOk = true;
            return Page();
        }

        // --- Validate the wizard payload --------------------------------
        var track = Survey.FindTrack(SelectedTrackId);
        if (track is null)
        {
            ErrorMessage = "Pick a track in step 1 before submitting.";
            return Page();
        }

        var picks = new[]
        {
            (TopicId: Pick1TopicId, Level: Pick1Level, Rank: 1),
            (TopicId: Pick2TopicId, Level: Pick2Level, Rank: 2),
            (TopicId: Pick3TopicId, Level: Pick3Level, Rank: 3),
        };

        if (picks.Any(p => string.IsNullOrWhiteSpace(p.TopicId)))
        {
            ErrorMessage = "Rank three different topics in step 2 before submitting.";
            return Page();
        }
        if (picks.Select(p => p.TopicId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
        {
            ErrorMessage = "Your three picks must all be different topics.";
            return Page();
        }
        // All picked topic ids must belong to the selected track (defense in
        // depth — the wizard UI only shows in-track topics, but a hand-rolled
        // POST should not be able to mix tracks).
        var topicIdsInTrack = track.Topics.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (picks.Any(p => !topicIdsInTrack.Contains(p.TopicId!)))
        {
            ErrorMessage = "One of your picks is not a topic in the selected track.";
            return Page();
        }
        if (picks.Any(p => p.Level is null))
        {
            ErrorMessage = "Pick a level (Introduction / Advanced / Expert) for each of your three picks.";
            return Page();
        }

        // --- Write the response -----------------------------------------
        var response = new SurveyResponse
        {
            SurveySlug      = Survey.Slug,
            SelectedTrackId = track.Id,
            Comment         = string.IsNullOrWhiteSpace(Comment) ? null : Comment.Trim(),
            SubmittedAt     = _clock.GetUtcNow(),
            IpHash          = HashIp(HttpContext.Connection.RemoteIpAddress?.ToString()),
            Picks = picks.Select(p => new SurveyResponsePick
            {
                Rank          = p.Rank,
                TopicId       = p.TopicId!,
                DesiredLevel  = p.Level!.Value,
            }).ToList(),
        };
        _db.SurveyResponses.Add(response);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _log.LogWarning(ex, "Survey response DB write failed for slug={Slug}", Slug);
            ErrorMessage = "We hit a problem saving your response. Please try again in a moment.";
            return Page();
        }

        _log.LogInformation(
            "Survey response saved: slug={Slug} id={Id} track={TrackId} picks={Picks}",
            Slug, response.Id, response.SelectedTrackId,
            string.Join("|", response.Picks.Select(p => $"{p.Rank}:{p.TopicId}={p.DesiredLevel}")));

        SubmittedOk = true;
        return Page();
    }

    private static string? HashIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ip));
        return Convert.ToHexString(bytes)[..32];
    }
}
