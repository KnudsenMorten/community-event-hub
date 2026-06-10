namespace CommunityHub.Core.Domain;

/// <summary>
/// One of the three (Rank=1..3) topic picks within a
/// <see cref="SurveyResponse"/>. TopicId references a topic in the JSON
/// catalog; DesiredLevel is the depth the respondent wants.
/// </summary>
public class SurveyResponsePick
{
    public int Id { get; set; }

    public int SurveyResponseId { get; set; }
    public SurveyResponse Response { get; set; } = null!;

    /// <summary>1, 2, or 3. 1 = first choice, 3 = third.</summary>
    public int Rank { get; set; }

    public string TopicId { get; set; } = string.Empty;

    public SurveyLevel DesiredLevel { get; set; }
}
