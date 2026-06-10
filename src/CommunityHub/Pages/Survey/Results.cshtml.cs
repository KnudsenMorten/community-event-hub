using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Surveys;
using CommunityHub.Surveys;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Survey;

/// <summary>
/// PUBLIC anonymous dashboard for a survey. Aggregates the responses
/// stored by <see cref="IndexModel"/> and joins them against the JSON
/// catalog at render time so track names, topic titles, and category
/// groupings come from the same source the wizard uses.
///
/// Designed to be share-able with speakers signing up via the Call for
/// Speakers — so they can align their abstract to what attendees want.
/// </summary>
[AllowAnonymous]
public class ResultsModel : PageModel
{
    private readonly SurveyDefinitionProvider _definitions;
    private readonly CommunityHubDbContext _db;

    public ResultsModel(SurveyDefinitionProvider definitions, CommunityHubDbContext db)
    {
        _definitions = definitions;
        _db = db;
    }

    public SurveyDefinition? Survey { get; private set; }
    public string Slug { get; private set; } = string.Empty;

    public int TotalResponses { get; private set; }
    public DateTimeOffset? LatestResponseAt { get; private set; }

    public List<TrackStat> TrackStats { get; private set; } = new();
    public List<TopicStat> TopTopicsOverall { get; private set; } = new();
    public Dictionary<string, List<TopicStat>> TopicsByTrack { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<SurveyLevel, int> LevelTotals { get; private set; } = new();

    /// <summary>Aggregated stats for one track shown in step-1 mix.</summary>
    public record TrackStat(string TrackId, string Name, int ResponseCount, int Percent);

    /// <summary>
    /// One topic ranked by weighted demand. Top pick = 3, 2nd = 2, 3rd = 1.
    /// Higher score = more wanted. Level distribution stored alongside so
    /// each topic card shows the Advanced / Expert / Black Belt split.
    /// </summary>
    public record TopicStat(
        string TopicId, string Title, string Category, string TrackId, string TrackName,
        int PickCount, int WeightedScore,
        int AdvancedCount, int ExpertCount, int BlackBeltCount);

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        Slug = slug ?? string.Empty;
        Survey = _definitions.TryGet(Slug);
        if (Survey is null) return NotFound();

        // Pull just the lightweight projection we need; no need to load
        // entire entities. SurveySlug index makes this an efficient seek.
        var picks = await _db.SurveyResponsePicks
            .Where(p => p.Response.SurveySlug == Slug)
            .Select(p => new
            {
                p.TopicId,
                p.Rank,
                p.DesiredLevel,
                ResponseId = p.SurveyResponseId,
                p.Response.SelectedTrackId,
                p.Response.SubmittedAt,
            })
            .ToListAsync(ct);

        var byResponse = picks.GroupBy(p => p.ResponseId).ToList();
        TotalResponses = byResponse.Count;
        LatestResponseAt = picks.Count > 0 ? picks.Max(p => p.SubmittedAt) : null;

        // --- Track distribution (step 1) -----------------------------
        var trackCounts = byResponse
            .GroupBy(g => g.First().SelectedTrackId)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        TrackStats = Survey.Tracks
            .Select(t =>
            {
                trackCounts.TryGetValue(t.Id, out var c);
                var pct = TotalResponses == 0 ? 0 : (int)Math.Round(100.0 * c / TotalResponses);
                return new TrackStat(t.Id, t.Name, c, pct);
            })
            .OrderByDescending(s => s.ResponseCount)
            .ThenBy(s => s.Name)
            .ToList();

        // --- Topic popularity (step 2) -------------------------------
        // Weighted demand: top pick = 3, 2nd = 2, 3rd = 1. Higher = more wanted.
        var topicAgg = picks
            .GroupBy(p => p.TopicId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => new
            {
                Count     = g.Count(),
                Weighted  = g.Sum(x => x.Rank == 1 ? 3 : x.Rank == 2 ? 2 : 1),
                Adv       = g.Count(x => x.DesiredLevel == SurveyLevel.Advanced),
                Expert    = g.Count(x => x.DesiredLevel == SurveyLevel.Expert),
                BlackBelt = g.Count(x => x.DesiredLevel == SurveyLevel.BlackBelt),
            }, StringComparer.OrdinalIgnoreCase);

        var allTopicStats = new List<TopicStat>();
        foreach (var track in Survey.Tracks)
        {
            foreach (var topic in track.Topics)
            {
                topicAgg.TryGetValue(topic.Id, out var a);
                allTopicStats.Add(new TopicStat(
                    topic.Id, topic.Title, topic.Category, track.Id, track.Name,
                    a?.Count ?? 0, a?.Weighted ?? 0,
                    a?.Adv ?? 0, a?.Expert ?? 0, a?.BlackBelt ?? 0));
            }
        }

        TopTopicsOverall = allTopicStats
            .OrderByDescending(s => s.WeightedScore)
            .ThenByDescending(s => s.PickCount)
            .ThenBy(s => s.Title)
            .Take(15)
            .ToList();

        TopicsByTrack = allTopicStats
            .GroupBy(s => s.TrackId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(s => s.WeightedScore)
                      .ThenByDescending(s => s.PickCount)
                      .ThenBy(s => s.Title)
                      .ToList(),
                StringComparer.OrdinalIgnoreCase);

        // --- Level totals (step 3) -----------------------------------
        LevelTotals = new Dictionary<SurveyLevel, int>
        {
            [SurveyLevel.Advanced]  = picks.Count(p => p.DesiredLevel == SurveyLevel.Advanced),
            [SurveyLevel.Expert]    = picks.Count(p => p.DesiredLevel == SurveyLevel.Expert),
            [SurveyLevel.BlackBelt] = picks.Count(p => p.DesiredLevel == SurveyLevel.BlackBelt),
        };

        return Page();
    }
}
