namespace CommunityHub.Core.Tasks.Model;

/// <summary>
/// §688.12 / §708 — the CLOSED set of interactive components a task body may embed.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Closed, and checked at PARSE time.</b> An embed the row partial does not know how to
/// render leaves a silent hole exactly where the participant is expected to do the work — worse than
/// a link out, because nothing on the page indicates anything is missing. A typo in a body file is a
/// parse diagnostic and a failing catalogue test, not a blank space on someone's task page.</para>
///
/// <para>🔒 <b>One list, so the parser and the row partials cannot drift.</b> The name was previously
/// a string literal inside the parser's switch; adding the second component is precisely when that
/// becomes two places to keep in step.</para>
/// </remarks>
public static class TaskEmbedComponents
{
    /// <summary>§688.12 — the sponsor's booth-member editor, inside the task.</summary>
    public const string BoothMembers = "boothMembers";

    /// <summary>
    /// §708 / §707.57d — the speaker's preview/final deck upload, inside the task.
    /// </summary>
    /// <remarks>
    /// The task decides WHICH deck it is asking for from its own completion kind
    /// (<c>TaskCompletion.Artefact("preview"|"final")</c>), so one component name serves both
    /// bodies and neither can name the wrong kind.
    /// </remarks>
    public const string PresentationUpload = "presentationUpload";

    /// <summary>
    /// §708.10 — the participant's Get-Started FORM, inside the task, on the shared <c>/Tasks</c> page.
    /// </summary>
    /// <remarks>
    /// <para>ONE component name for all nine forms, deliberately — the task already states WHICH form
    /// it is in <see cref="Domain.ParticipantTask.FormStepKey"/>, stamped by the seeder that holds the
    /// step key. Naming the form in the body instead would let a body and its task disagree about what
    /// the row is asking for, which is the §708.4 failure in a new place. Same reasoning as
    /// <see cref="PresentationUpload"/>, which serves both decks off the task's completion kind.</para>
    /// </remarks>
    public const string StepForm = "stepForm";

    /// <summary>Every known component, in the canonical casing the renderer emits.</summary>
    public static IReadOnlyList<string> All { get; } =
        new[] { BoothMembers, PresentationUpload, StepForm };

    /// <summary>True when <paramref name="component"/> names a component the hub can render.</summary>
    public static bool IsKnown(string? component) =>
        Canonical(component) is not null;

    /// <summary>
    /// The canonical spelling of <paramref name="component"/>, or null when unknown. Case-insensitive
    /// so an author's capitalisation cannot produce a marker the row partial fails to match.
    /// </summary>
    public static string? Canonical(string? component) =>
        All.FirstOrDefault(
            c => string.Equals(c, component?.Trim(), StringComparison.OrdinalIgnoreCase));
}
