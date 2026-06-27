namespace CommunityHub.Core.Assistant;

/// <summary>
/// Configuration for the AiHelper INTAKE capability (REQUIREMENTS §137): the bug/feature
/// keyword detector and the two routing addresses. Bound from the <c>Feedback</c> config
/// section. All values are config-overridable so an operator can retune the keyword set or
/// redirect the mailboxes without a code change; the defaults are the ELDK27 wiring.
/// </summary>
public sealed class FeedbackIntakeOptions
{
    public const string SectionName = "Feedback";

    /// <summary>Master switch. False ⇒ intake detection no-ops (nothing captured/emailed).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Case-insensitive, whole-word keywords that mark a message as a BUG/ERROR report.
    /// Defaults cover English + Danish ("fejl"). Multi-word entries are matched as a phrase.
    /// </summary>
    public string[] BugKeywords { get; set; } = { "bug", "error", "fejl" };

    /// <summary>
    /// Case-insensitive, whole-word keywords that mark a message as a FEATURE request.
    /// Defaults cover English + Danish ("forslag"). Multi-word entries are matched as a phrase.
    /// </summary>
    public string[] FeatureKeywords { get; set; } = { "feature", "feature request", "forslag" };

    /// <summary>Where detected bug/feature reports are emailed (the dev mailbox).</summary>
    public string BugFeatureEmailTo { get; set; } = "mok@expertslive.dk";

    /// <summary>Where explicit "contact the organizers" messages are emailed.</summary>
    public string OrganizerEmailTo { get; set; } = "info@expertslive.dk";

    /// <summary>Subject-line prefix for intake emails (the edition tag).</summary>
    public string SubjectPrefix { get; set; } = "[ELDK27]";
}
