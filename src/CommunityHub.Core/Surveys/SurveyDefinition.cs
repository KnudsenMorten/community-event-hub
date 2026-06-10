namespace CommunityHub.Core.Surveys;

/// <summary>
/// In-memory model of one survey, loaded from a JSON file under
/// CommunityHub/App_Data/Surveys/. Topics, tracks, and per-level
/// example copy all live in JSON so editing the survey content does
/// not require a code change or a DB migration -- only responses are
/// persisted to the database (see <see cref="Domain.SurveyResponse"/>).
/// </summary>
public sealed class SurveyDefinition
{
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public string EventDate { get; set; } = string.Empty;
    public string Intro { get; set; } = string.Empty;
    /// <summary>Optional second-paragraph disclaimer rendered in italics under the intro.</summary>
    public string IntroDisclaimer { get; set; } = string.Empty;
    /// <summary>Optional third-paragraph thank-you / motivator under the intro + disclaimer.</summary>
    public string IntroThankYou { get; set; } = string.Empty;
    public string ThanksTitle { get; set; } = string.Empty;
    public string ThanksBody { get; set; } = string.Empty;
    public string ResultsLinkLabel { get; set; } = "Open the live results dashboard";
    public List<SurveyTrack> Tracks { get; set; } = new();

    public SurveyTrack? FindTrack(string id) =>
        Tracks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    public SurveyTopic? FindTopic(string topicId) =>
        Tracks.SelectMany(t => t.Topics)
              .FirstOrDefault(t => string.Equals(t.Id, topicId, StringComparison.OrdinalIgnoreCase));
}

public sealed class SurveyTrack
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Tagline { get; set; } = string.Empty;
    public List<SurveyTopic> Topics { get; set; } = new();
    public SurveyLevelExamples LevelExamples { get; set; } = new();
}

public sealed class SurveyTopic
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Optional per-topic Introduction/Advanced/Expert example copy. When
    /// null, the wizard falls back to the parent track's
    /// <see cref="SurveyTrack.LevelExamples"/>. Set when a topic warrants a
    /// more specific example than the track-wide generic.
    /// </summary>
    public SurveyLevelExamples? LevelExamples { get; set; }
}

public sealed class SurveyLevelExamples
{
    /// <summary>First-person "I can..." statement for Advanced (level 300).</summary>
    public string Advanced { get; set; } = string.Empty;
    /// <summary>First-person "I can..." statement for Expert (level 400).</summary>
    public string Expert { get; set; } = string.Empty;
    /// <summary>First-person "I can..." statement for Black Belt (level 500).</summary>
    public string BlackBelt { get; set; } = string.Empty;

    public string For(Domain.SurveyLevel level) => level switch
    {
        Domain.SurveyLevel.Advanced => Advanced,
        Domain.SurveyLevel.Expert => Expert,
        Domain.SurveyLevel.BlackBelt => BlackBelt,
        _ => string.Empty,
    };
}
