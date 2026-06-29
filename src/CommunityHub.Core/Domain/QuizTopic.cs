namespace CommunityHub.Core.Domain;

/// <summary>
/// The topic of a "fun IT games" quiz (REQUIREMENTS §171). Three shipped topics —
/// each gets its own quiz, question pool and leaderboard. Stored as an int (the
/// model-wide enum convention, e.g. <see cref="ParticipantRole"/>) so adding a
/// topic later never reshuffles existing rows.
/// </summary>
public enum QuizTopic
{
    /// <summary>Artificial intelligence (Copilot, LLM basics, responsible AI).</summary>
    Ai = 0,

    /// <summary>Microsoft Intune / endpoint management.</summary>
    Intune = 1,

    /// <summary>Security best practices (identity, MFA, phishing, zero trust).</summary>
    Security = 2,
}
