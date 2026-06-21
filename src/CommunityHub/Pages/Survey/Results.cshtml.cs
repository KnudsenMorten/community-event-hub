using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Surveys;
using CommunityHub.Surveys;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Survey;

/// <summary>
/// PUBLIC anonymous dashboard for a survey. Aggregates the responses
/// stored by <see cref="IndexModel"/> and joins them against the JSON
/// catalog so track names, topic titles, and category groupings come from
/// the same source the wizard uses.
///
/// The aggregation math lives in <see cref="SurveySummaryService"/> so the
/// organizer management surface (/Organizer/Surveys) shows the exact same
/// numbers — this page is a thin presenter over that shared service.
///
/// Designed to be share-able with speakers signing up via the Call for
/// Speakers — so they can align their abstract to what attendees want.
/// </summary>
[AllowAnonymous]
public class ResultsModel : PageModel
{
    private readonly SurveyDefinitionProvider _definitions;
    private readonly SurveySummaryService _summary;
    private readonly EventEditionConfigLoader _eventConfigLoader;
    private readonly EventConfigOptions _eventConfigOptions;
    private readonly ILogger<ResultsModel> _logger;

    public ResultsModel(
        SurveyDefinitionProvider definitions,
        SurveySummaryService summary,
        EventEditionConfigLoader eventConfigLoader,
        EventConfigOptions eventConfigOptions,
        ILogger<ResultsModel> logger)
    {
        _definitions = definitions;
        _summary = summary;
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
        _logger = logger;
    }

    public SurveyDefinition? Survey { get; private set; }
    public string Slug { get; private set; } = string.Empty;

    public int TotalResponses { get; private set; }
    public DateTimeOffset? LatestResponseAt { get; private set; }

    /// <summary>
    /// The edition timezone (IANA id) so the "latest response" stamp reads in
    /// the venue's local time, not raw UTC (REQUIREMENTS §21).
    /// </summary>
    public string? TimezoneId { get; private set; }

    /// <summary>The "latest response" date in edition-local time, or "—" when none.</summary>
    public string LatestResponseDateLocal =>
        LatestResponseAt is { } at
            ? EventLocalTime.ToLocal(at, TimezoneId).ToString("d MMM yyyy")
            : "—";

    /// <summary>The "latest response" time-of-day in edition-local time (with zone), or blank.</summary>
    public string LatestResponseTimeLocal =>
        LatestResponseAt is { } at
            ? $"{EventLocalTime.ToLocal(at, TimezoneId):HH:mm} {EventLocalTime.ZoneLabel(at, TimezoneId)}"
            : "";

    public List<SurveySummaryService.TrackStat> TrackStats { get; private set; } = new();
    public List<SurveySummaryService.TopicStat> TopTopicsOverall { get; private set; } = new();
    public Dictionary<string, List<SurveySummaryService.TopicStat>> TopicsByTrack { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<SurveyLevel, int> LevelTotals { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        Slug = slug ?? string.Empty;
        Survey = _definitions.TryGet(Slug);
        if (Survey is null) return NotFound();

        // Edition timezone for showing the "latest response" stamp in local time
        // (REQUIREMENTS §21). Best-effort — a broken/missing config must never
        // 500 this public page, so fall back to a blank id (⇒ honest UTC).
        try
        {
            TimezoneId = _eventConfigLoader
                .Load(_eventConfigOptions.EventConfigPath)
                .Dates?.Timezone;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Survey results: failed to load edition timezone from {Path}",
                _eventConfigOptions.EventConfigPath);
            TimezoneId = null;
        }

        var summary = await _summary.BuildSummaryAsync(Slug, SurveyCatalog.From(Survey), ct);

        TotalResponses = summary.TotalResponses;
        LatestResponseAt = summary.LatestResponseAt;
        TrackStats = summary.TrackStats.ToList();
        TopTopicsOverall = summary.TopTopicsOverall.ToList();
        TopicsByTrack = summary.TopicsByTrack.ToDictionary(
            kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        LevelTotals = new Dictionary<SurveyLevel, int>(summary.LevelTotals);

        return Page();
    }
}
