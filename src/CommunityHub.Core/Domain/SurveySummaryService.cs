using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Domain;

/// <summary>
/// Single home for the survey RESPONSE aggregation + the small organizer-controlled
/// survey ADMIN state (open/closed flag, response reset). Both the public results
/// dashboard (<c>/survey/{slug}/results</c>) and the organizer management surface
/// (<c>/Organizer/Surveys</c>) call this so the demand math + state rules are written
/// exactly once.
///
/// The survey CATALOG (tracks/topics) lives in JSON and is owned by the web-layer
/// <c>SurveyDefinitionProvider</c>; to keep Core free of that dependency the caller
/// passes the catalog in as a lightweight <see cref="CatalogTrack"/> projection.
/// Survey RESPONSES + STATE are DB rows owned here.
/// </summary>
public sealed class SurveySummaryService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<SurveySummaryService> _log;

    public SurveySummaryService(
        CommunityHubDbContext db,
        TimeProvider clock,
        ILogger<SurveySummaryService> log)
    {
        _db = db;
        _clock = clock;
        _log = log;
    }

    // --- Catalog projection (passed in from the JSON definition) -------------
    public sealed record CatalogTopic(string TopicId, string Title, string Category);
    public sealed record CatalogTrack(string TrackId, string Name, IReadOnlyList<CatalogTopic> Topics);

    // --- Aggregated result shapes (shared by public + organizer) ------------
    public sealed record TrackStat(string TrackId, string Name, int ResponseCount, int Percent);

    public sealed record TopicStat(
        string TopicId, string Title, string Category, string TrackId, string TrackName,
        int PickCount, int WeightedScore,
        int AdvancedCount, int ExpertCount, int BlackBeltCount);

    public sealed record SurveySummary(
        int TotalResponses,
        DateTimeOffset? LatestResponseAt,
        IReadOnlyList<TrackStat> TrackStats,
        IReadOnlyList<TopicStat> TopTopicsOverall,
        IReadOnlyDictionary<string, List<TopicStat>> TopicsByTrack,
        IReadOnlyDictionary<SurveyLevel, int> LevelTotals);

    /// <summary>
    /// Build the full demand summary for a survey by joining its persisted
    /// responses against the supplied JSON catalog. Top pick weighs 3, 2nd 2,
    /// 3rd 1 (higher weighted score = more wanted). Identical math to the public
    /// results dashboard — this IS that math, lifted out of the page so it is
    /// reused, not duplicated.
    /// </summary>
    public async Task<SurveySummary> BuildSummaryAsync(
        string slug, IReadOnlyList<CatalogTrack> tracks, CancellationToken ct)
    {
        var picks = await _db.SurveyResponsePicks
            .Where(p => p.Response.SurveySlug == slug)
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
        var total = byResponse.Count;
        DateTimeOffset? latest = picks.Count > 0 ? picks.Max(p => p.SubmittedAt) : null;

        // --- Track distribution (step 1) -----------------------------------
        var trackCounts = byResponse
            .GroupBy(g => g.First().SelectedTrackId)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var trackStats = tracks
            .Select(t =>
            {
                trackCounts.TryGetValue(t.TrackId, out var c);
                var pct = total == 0 ? 0 : (int)Math.Round(100.0 * c / total);
                return new TrackStat(t.TrackId, t.Name, c, pct);
            })
            .OrderByDescending(s => s.ResponseCount)
            .ThenBy(s => s.Name)
            .ToList();

        // --- Topic popularity (step 2) -------------------------------------
        var topicAgg = picks
            .GroupBy(p => p.TopicId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => new
            {
                Count = g.Count(),
                Weighted = g.Sum(x => x.Rank == 1 ? 3 : x.Rank == 2 ? 2 : 1),
                Adv = g.Count(x => x.DesiredLevel == SurveyLevel.Advanced),
                Expert = g.Count(x => x.DesiredLevel == SurveyLevel.Expert),
                BlackBelt = g.Count(x => x.DesiredLevel == SurveyLevel.BlackBelt),
            }, StringComparer.OrdinalIgnoreCase);

        var allTopicStats = new List<TopicStat>();
        foreach (var track in tracks)
        {
            foreach (var topic in track.Topics)
            {
                topicAgg.TryGetValue(topic.TopicId, out var a);
                allTopicStats.Add(new TopicStat(
                    topic.TopicId, topic.Title, topic.Category, track.TrackId, track.Name,
                    a?.Count ?? 0, a?.Weighted ?? 0,
                    a?.Adv ?? 0, a?.Expert ?? 0, a?.BlackBelt ?? 0));
            }
        }

        var topOverall = allTopicStats
            .OrderByDescending(s => s.WeightedScore)
            .ThenByDescending(s => s.PickCount)
            .ThenBy(s => s.Title)
            .Take(15)
            .ToList();

        var byTrack = allTopicStats
            .GroupBy(s => s.TrackId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(s => s.WeightedScore)
                      .ThenByDescending(s => s.PickCount)
                      .ThenBy(s => s.Title)
                      .ToList(),
                StringComparer.OrdinalIgnoreCase);

        var levelTotals = new Dictionary<SurveyLevel, int>
        {
            [SurveyLevel.Advanced] = picks.Count(p => p.DesiredLevel == SurveyLevel.Advanced),
            [SurveyLevel.Expert] = picks.Count(p => p.DesiredLevel == SurveyLevel.Expert),
            [SurveyLevel.BlackBelt] = picks.Count(p => p.DesiredLevel == SurveyLevel.BlackBelt),
        };

        return new SurveySummary(total, latest, trackStats, topOverall, byTrack, levelTotals);
    }

    /// <summary>Total responses for a slug (cheap COUNT; used by the list page).</summary>
    public Task<int> CountResponsesAsync(string slug, CancellationToken ct) =>
        _db.SurveyResponses.CountAsync(r => r.SurveySlug == slug, ct);

    // --- Open / closed state ------------------------------------------------

    /// <summary>
    /// Is this survey currently accepting public submissions? A survey with no
    /// persisted <see cref="SurveyState"/> row is OPEN (the historical default),
    /// so existing surveys keep working after this feature ships.
    /// </summary>
    public async Task<bool> IsOpenAsync(string slug, CancellationToken ct)
    {
        var row = await _db.SurveyStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SurveySlug == slug, ct);
        return row?.IsOpen ?? true;
    }

    /// <summary>Open/closed state for many slugs in one query (defaults to OPEN).</summary>
    public async Task<IReadOnlyDictionary<string, bool>> GetOpenStatesAsync(
        IEnumerable<string> slugs, CancellationToken ct)
    {
        var set = slugs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = await _db.SurveyStates.AsNoTracking()
            .Where(s => set.Contains(s.SurveySlug))
            .ToListAsync(ct);
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var slug in set) map[slug] = true; // default open
        foreach (var r in rows) map[r.SurveySlug] = r.IsOpen;
        return map;
    }

    /// <summary>
    /// Set the open/closed flag for a survey, upserting the state row. Writes an
    /// audit log line. Returns the new state.
    /// </summary>
    public async Task<bool> SetOpenAsync(string slug, bool isOpen, string? actorEmail, CancellationToken ct)
    {
        var row = await _db.SurveyStates.FirstOrDefaultAsync(s => s.SurveySlug == slug, ct);
        if (row is null)
        {
            row = new SurveyState { SurveySlug = slug };
            _db.SurveyStates.Add(row);
        }
        row.IsOpen = isOpen;
        row.UpdatedAt = _clock.GetUtcNow();
        row.UpdatedByEmail = actorEmail;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Survey state changed: slug={Slug} isOpen={IsOpen} by={Actor}",
            slug, isOpen, actorEmail ?? "(unknown)");
        return isOpen;
    }

    // --- Reset (destructive, organizer-gated) -------------------------------

    /// <summary>
    /// Delete EVERY response (and its picks, via cascade) for exactly this
    /// slug — the "start fresh before sending it out" action. Scoped strictly
    /// to the target slug; other surveys' rows are untouched. Writes an audit
    /// log line with the deleted count. Returns the number of responses deleted.
    /// </summary>
    public async Task<int> ResetResponsesAsync(string slug, string? actorEmail, CancellationToken ct)
    {
        // Load the target slug's responses WITH their picks so the in-memory
        // cascade deletes both sides. EF Core's configured Cascade FK also
        // removes the picks, but loading them keeps the change-tracker honest
        // and the count exact regardless of provider cascade semantics.
        var responses = await _db.SurveyResponses
            .Include(r => r.Picks)
            .Where(r => r.SurveySlug == slug)
            .ToListAsync(ct);

        if (responses.Count == 0)
        {
            _log.LogInformation(
                "Survey reset: slug={Slug} had no responses; nothing deleted (by={Actor})",
                slug, actorEmail ?? "(unknown)");
            return 0;
        }

        foreach (var r in responses)
        {
            _db.SurveyResponsePicks.RemoveRange(r.Picks);
        }
        _db.SurveyResponses.RemoveRange(responses);
        await _db.SaveChangesAsync(ct);

        _log.LogWarning(
            "Survey reset: deleted {Count} responses (+picks) for slug={Slug} by={Actor}",
            responses.Count, slug, actorEmail ?? "(unknown)");
        return responses.Count;
    }
}
