namespace CommunityHub.Core.Assistant;

/// <summary>
/// The grounded AI Community Helper (code-named AiHelper; REQUIREMENTS §129). Answers a
/// question using ONLY the already-authorized <see cref="AiHelperContext"/> grounding it
/// is given. Implementations must never throw to the caller — a disabled/unconfigured
/// assistant or a backend error returns an <see cref="AiHelperAnswer"/> with
/// <c>Available=false</c> and a friendly message.
/// </summary>
public interface IAiHelperAssistant
{
    /// <summary>True when the assistant is enabled + fully configured (the gate).</summary>
    bool Available { get; }

    /// <summary>
    /// Answer <paramref name="question"/> grounded strictly in <paramref name="context"/>.
    /// </summary>
    Task<AiHelperAnswer> AskAsync(string question, AiHelperContext context, CancellationToken ct = default);
}
