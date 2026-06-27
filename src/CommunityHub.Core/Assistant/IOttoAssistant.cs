namespace CommunityHub.Core.Assistant;

/// <summary>
/// Otto, the grounded AI Community Helper (REQUIREMENTS §129). Answers a question
/// using ONLY the already-authorized <see cref="OttoContext"/> grounding it is given.
/// Implementations must never throw to the caller — a disabled/unconfigured Otto or a
/// backend error returns an <see cref="OttoAnswer"/> with <c>Available=false</c> and a
/// friendly message.
/// </summary>
public interface IOttoAssistant
{
    /// <summary>True when Otto is enabled + fully configured (the gate).</summary>
    bool Available { get; }

    /// <summary>
    /// Answer <paramref name="question"/> grounded strictly in <paramref name="context"/>.
    /// </summary>
    Task<OttoAnswer> AskAsync(string question, OttoContext context, CancellationToken ct = default);
}
