namespace CommunityHub.Core.Volunteers;

/// <summary>
/// The AI seam for generating a task's <b>Pre-req</b> and <b>Expectations</b>
/// from its name. Deliberately an interface with two implementations:
///
///  - <see cref="HeuristicTaskGuidanceGenerator"/> — a no-dependency, always-on
///    fallback that derives sensible defaults from keywords in the title. Used
///    when no LLM is configured (the default), so the feature works out of the
///    box and the tests never need a network/secret.
///  - <see cref="LlmTaskGuidanceGenerator"/> — a real LLM provider (Claude
///    Messages API) used ONLY when an API key is configured via the existing
///    secret/config mechanism. The key is never committed.
///
/// Results are advisory: the organizer can always edit them, and re-running the
/// generator (regenerate) overwrites only when asked. On import, the generator
/// fills Pre-req/Expectations for tasks that are missing them.
/// </summary>
public interface ITaskGuidanceGenerator
{
    /// <summary>True when a real LLM backs this generator (a key is configured).
    /// The UI uses this to label the button ("Generate with AI" vs "Suggest").</summary>
    bool IsAiBacked { get; }

    /// <summary>
    /// Produce suggested Pre-req + Expectations text for a task title. The
    /// optional <paramref name="bucketName"/> / <paramref name="responsibleTeam"/>
    /// give the generator context. Never throws on a provider failure — falls back
    /// to the heuristic so a bad key or a network blip never breaks an import.
    /// </summary>
    Task<TaskGuidance> GenerateAsync(
        string taskTitle,
        string? bucketName = null,
        string? responsibleTeam = null,
        CancellationToken ct = default);
}

/// <summary>The generated guidance for one task. Either field may be blank if the
/// generator could not produce useful text for it.</summary>
public readonly record struct TaskGuidance(string Prerequisites, string Expectations)
{
    public static readonly TaskGuidance Empty = new(string.Empty, string.Empty);
}
