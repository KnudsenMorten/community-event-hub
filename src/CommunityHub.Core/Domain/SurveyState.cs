namespace CommunityHub.Core.Domain;

/// <summary>
/// Persistent per-survey administrative state, keyed by the survey
/// <see cref="SurveySlug"/> (the JSON file basename under
/// CommunityHub/App_Data/Surveys/, e.g. "eldk27-topics").
///
/// The survey CATALOG (tracks / topics / level examples) still lives in JSON —
/// this row only carries the small mutable bits an organizer controls at
/// runtime. Today that is the open/closed flag that gates whether the PUBLIC
/// <c>/survey/{slug}</c> page accepts new submissions; a closed survey still
/// renders its results.
///
/// NOT scoped to an <see cref="Event"/>: like <see cref="SurveyResponse"/>, a
/// survey is its own anonymous artefact and the slug is the only key. A row is
/// created lazily the first time an organizer toggles state; a survey with no
/// row is treated as OPEN (the historical default).
/// </summary>
public class SurveyState
{
    public int Id { get; set; }

    /// <summary>
    /// Survey slug — matches the JSON file basename under App_Data/Surveys/
    /// and <see cref="SurveyResponse.SurveySlug"/>. Unique.
    /// </summary>
    public string SurveySlug { get; set; } = string.Empty;

    /// <summary>
    /// When true (the default) the public survey page accepts submissions.
    /// When false the page shows a friendly "this survey is closed" state and
    /// rejects POSTs, while results remain viewable.
    /// </summary>
    public bool IsOpen { get; set; } = true;

    /// <summary>UTC time the state was last changed (open/close toggle or reset).</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Organizer who last changed the state (audit). Email, optional.</summary>
    public string? UpdatedByEmail { get; set; }
}
