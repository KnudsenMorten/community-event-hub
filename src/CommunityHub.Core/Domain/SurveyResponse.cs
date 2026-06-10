namespace CommunityHub.Core.Domain;

/// <summary>
/// One anonymous response to a survey (e.g. ELDK27 topics).
/// Picks 1..3 are siblings in <see cref="Picks"/>, each with a Rank and
/// a DesiredLevel. Track + Topic ids reference string ids in the JSON
/// survey definition -- no FK to a DB table for the catalog because the
/// catalog lives in JSON and can change between editions.
/// </summary>
public class SurveyResponse
{
    public int Id { get; set; }

    /// <summary>
    /// Survey slug, e.g. "eldk27-topics". Matches the JSON file basename
    /// under App_Data/Surveys/.
    /// </summary>
    public string SurveySlug { get; set; } = string.Empty;

    /// <summary>
    /// Track id picked in step 1 (e.g. "security", "intune").
    /// </summary>
    public string SelectedTrackId { get; set; } = string.Empty;

    /// <summary>
    /// Free-text "where are you working / role / what brings you to ELDK"
    /// — optional, capped, never personally identifying by design.
    /// </summary>
    public string? Comment { get; set; }

    public DateTimeOffset SubmittedAt { get; set; }

    /// <summary>
    /// SHA-256 of the requester's IP (truncated). Used to soft-rate-limit
    /// double-submissions; never PII'd back to the IP.
    /// </summary>
    public string? IpHash { get; set; }

    public List<SurveyResponsePick> Picks { get; set; } = new();
}
