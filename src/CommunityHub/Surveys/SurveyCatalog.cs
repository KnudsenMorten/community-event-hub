using CommunityHub.Core.Domain;
using CommunityHub.Core.Surveys;

namespace CommunityHub.Surveys;

/// <summary>
/// Adapter from the JSON-backed <see cref="SurveyDefinition"/> (web layer) to the
/// lightweight catalog projection <see cref="SurveySummaryService.CatalogTrack"/>
/// that the Core aggregation service consumes. Keeps Core free of a dependency on
/// the web-layer survey provider while letting both the public results page and the
/// organizer surface feed the same math the same catalog.
/// </summary>
public static class SurveyCatalog
{
    public static IReadOnlyList<SurveySummaryService.CatalogTrack> From(SurveyDefinition def) =>
        def.Tracks
            .Select(t => new SurveySummaryService.CatalogTrack(
                t.Id,
                t.Name,
                t.Topics
                    .Select(topic => new SurveySummaryService.CatalogTopic(
                        topic.Id, topic.Title, topic.Category))
                    .ToList()))
            .ToList();
}
