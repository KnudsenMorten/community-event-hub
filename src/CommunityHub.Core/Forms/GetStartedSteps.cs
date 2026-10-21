namespace CommunityHub.Forms;

/// <summary>
/// §1312 — which Get Started steps of the PERSONAL wizards (speaker, volunteer, crew, attendee) count as mandatory, for the
/// red step chips and the red "!" on the menu (operator 2026-09-27: "can we implement that for the other roles").
/// </summary>
/// <remarks>
/// These wizards already encode optional-ness in <c>Done</c>: a step nobody can be REQUIRED to finish is always done
/// (§732 Master Class waitlist, §400 deadlines summary, §680 welcome). So an unfinished step is one the person must act
/// on — except the few optional steps that still wait for a save, listed here.
/// </remarks>
public static class GetStartedSteps
{
    /// <summary>Optional steps that are only "done" once saved (§ 2026-06-27: the speaker's calendar e-mail, blank allowed).</summary>
    private static readonly HashSet<string> OptionalButSaved = new(StringComparer.Ordinal) { "calendar" };

    public static bool IsMandatory(string key) => !OptionalButSaved.Contains(key);

    /// <summary>Is a mandatory step still open?</summary>
    public static bool AnyMandatoryOpen(IEnumerable<(string Key, bool Done)> steps) =>
        steps.Any(s => !s.Done && IsMandatory(s.Key));
}
