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

    /// <summary>
    /// §6.5 — the RATING / FREE-TEXT / CHOICE questions of a post-event survey.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Empty for the existing preliminary survey, and that is the compatibility contract.</b>
    /// A definition with <see cref="Tracks"/> and no questions renders exactly the three-step wizard
    /// people are answering in production today; a definition with questions and no tracks renders a
    /// question list. Neither had to learn about the other.
    /// </remarks>
    public List<SurveyQuestion> Questions { get; set; } = new();

    /// <summary>True when this is a question-list survey rather than the track wizard.</summary>
    public bool IsQuestionSurvey => Questions.Count > 0;

    /// <summary>
    /// §6.5 — close this survey automatically this many months after the EVENT ENDS. Null ⇒ it only
    /// closes when an organizer closes it.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>A NUMBER OF MONTHS, never a date.</b> The work order asks for "1 month after the
    /// event ends — for ELDK27 that is 10 March 2027" and then says to implement it as a
    /// <i>derived</i> date. Writing <c>2027-03-10</c> into the JSON would be correct exactly once:
    /// the next edition would inherit a close date belonging to the previous event, and the survey
    /// would arrive already closed with nothing to explain why.</para>
    ///
    /// <para>⚠️ It lives in the JSON rather than in code because §6.5 requires the question sets in
    /// "editable configuration or seed data, never hardcoded", and the closing rule is part of the
    /// survey, not part of the engine.</para>
    /// </remarks>
    public int? ClosesMonthsAfterEventEnd { get; set; }

    /// <summary>
    /// When this survey closes on its own, given the event's end date. Null ⇒ never.
    /// </summary>
    /// <remarks>
    /// The boundary is the START of the day after, so a survey that closes "1 month after" is still
    /// open for the whole of its final day. Cutting it off at midnight-into-that-day would take a
    /// day away from every respondent for no reason anybody could see.
    /// </remarks>
    public DateTimeOffset? AutoCloseAt(DateOnly eventEnd) =>
        ClosesMonthsAfterEventEnd is not { } months
            ? null
            : new DateTimeOffset(
                eventEnd.AddMonths(months).AddDays(1).ToDateTime(TimeOnly.MinValue),
                TimeSpan.Zero);

    /// <summary>
    /// Problems that make this definition unusable — checked at LOAD, so a broken survey is found
    /// before its link is mailed rather than by the first person who opens it.
    /// </summary>
    public IReadOnlyList<string> ValidateQuestions()
    {
        var problems = Questions
            .Select(q => q.Validate())
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

        var duplicate = Questions
            .GroupBy(q => q.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        // Two questions sharing an id would overwrite each other's answers — the answer row is
        // unique per (response, question).
        if (duplicate is not null)
            problems.Add($"Two questions share the id '{duplicate.Key}'.");

        return problems;
    }

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
