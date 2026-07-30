using CommunityHub.Core.Tasks.Model;
using CommunityHub.Core.Tasks.Parsing;

namespace CommunityHub.Core.Tasks.Data;

/// <summary>
/// §684.9 / §555 — the answer from a <see cref="ITaskDataProvider"/>. THREE states, always, and
/// they render DIFFERENTLY.
/// </summary>
/// <remarks>
/// <para>🔒 <b>"Nothing found" and "could not look" are different facts.</b> §666 spells out the
/// cost of conflating them: rendering <i>"you have not booked a TV"</i> from a FAILED webshop lookup
/// pushes a sponsor into buying a second one. Making the distinction a TYPE means a provider author
/// cannot forget the third case — there is no way to return "no" without saying which "no" it
/// is.</para>
///
/// <para>This is also why §666 is blocked on the redesign at all: the old model resolved
/// placeholders at SEED time and stored finished prose, so by render time there was no longer any
/// memory that the lookup had failed. The wrong answer was PERSISTED.</para>
/// </remarks>
public abstract record TaskDataResult
{
    private TaskDataResult() { }

    /// <summary>We checked, and this is what we found.</summary>
    public sealed record Answer(IReadOnlyList<TaskBlock> Blocks) : TaskDataResult;

    /// <summary>We checked, and there is nothing. A POSITIVE statement, not an absence.</summary>
    public sealed record NothingFound(IReadOnlyList<TaskBlock> Blocks) : TaskDataResult;

    /// <summary>
    /// We could NOT check. Renders as a warning saying so — never as "nothing found".
    /// </summary>
    /// <param name="Message">
    /// The participant-facing sentence, e.g. "We could not check your webshop orders right now."
    /// The provider owns it: only the provider knows what it failed to look at.
    /// </param>
    /// <param name="TechnicalReason">For the log. Never rendered.</param>
    public sealed record CouldNotCheck(string Message, string? TechnicalReason = null) : TaskDataResult;

    /// <summary>Build an <see cref="Answer"/> from a snippet of body markup.</summary>
    /// <remarks>
    /// Providers author their output in the SAME format as a task body, so a provider's answer gets
    /// bold, buttons and callouts for free and renders identically in all three flavours. A provider
    /// composing HTML by hand would reintroduce exactly the drift §684.10 removes.
    /// </remarks>
    public static TaskDataResult Found(string markup) =>
        new Answer(TaskBodyParser.Parse(markup).Blocks);

    /// <summary>Build a <see cref="NothingFound"/> from a snippet of body markup.</summary>
    public static TaskDataResult NotFound(string markup) =>
        new NothingFound(TaskBodyParser.Parse(markup).Blocks);

    /// <summary>Build a <see cref="CouldNotCheck"/>.</summary>
    public static TaskDataResult Unavailable(string message, string? technicalReason = null) =>
        new CouldNotCheck(message, technicalReason);
}
