namespace CommunityHub.Forms;

/// <summary>
/// Stable <see cref="Core.Domain.ParticipantTask.SourceKey"/> builders for the
/// Get-Started wizard steps whose completion is a MANUAL "mark done" (no form data
/// to detect): the Signal-groups join (§109) and the speaker LinkedIn promote
/// (§116). Mirrors the speakerdl: convention so these tasks live alongside the
/// participant's other tasks and reminders. Per-participant scoped, idempotent.
/// </summary>
public static class WizardStepTasks
{
    /// <summary>SourceKey for the "Join Signal groups" task (§109).</summary>
    public static string Signal(int participantId) => $"signal:{participantId}";

    /// <summary>SourceKey for the speaker "Help to promote your session(s)" task (§116).</summary>
    public static string Promote(int participantId) => $"promote:{participantId}";
}
