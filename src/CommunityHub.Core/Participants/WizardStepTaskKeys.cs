namespace CommunityHub.Core.Participants;

/// <summary>
/// Stable <see cref="Domain.ParticipantTask.SourceKey"/> builders for the Get-Started
/// wizard steps whose completion is a DATA signal (not a form-owned auto-task and not a
/// manual mark-done) — the steps that, before §173e, had NO matching task at all:
/// the speaker Calendar-email + Speaker-details steps, the generic-role Profile step, and
/// the Code-of-Conduct / Privacy acceptance step every role ends on.
///
/// <para>These four keys are owned by the <c>WizardStepTaskSeeder</c> (which ensures the task
/// EXISTS, idempotent, per participant) and kept in sync — both ways — by
/// <see cref="FormTaskReconciler"/>: the step's data signal is true ⇒ the task is Done; the
/// signal goes false (the step is un-answered) ⇒ the task reopens. Defined here in Core so the
/// reconciler (Core) and the seeder (Web) agree on the exact SourceKey string. The other steps
/// reuse SourceKeys that already exist: <c>hotel-form:</c> … <c>swag-form:</c> /
/// <c>travel:submit-ticket-invoice:</c> (logistics forms), <c>speakerdl:</c> (speaker
/// deadlines), <c>signal:</c> / <c>promote:</c> (manual mark-done), <c>party-form:</c>
/// (PartyTaskSeeder).</para>
/// </summary>
public static class WizardStepTaskKeys
{
    public const string CalendarPrefix = "calendar:";
    public const string SpeakerDetailsPrefix = "speaker-details:";
    public const string ProfilePrefix = "profile:";
    public const string AcceptPrefix = "accept:";
    public const string AvailabilityPrefix = "availability:";

    /// <summary>Speaker "Calendar email (optional)" step task.</summary>
    public static string Calendar(int participantId) => $"{CalendarPrefix}{participantId}";

    /// <summary>Speaker "Speaker details" step task.</summary>
    public static string SpeakerDetails(int participantId) => $"{SpeakerDetailsPrefix}{participantId}";

    /// <summary>Generic-role "Your profile" step task.</summary>
    public static string Profile(int participantId) => $"{ProfilePrefix}{participantId}";

    /// <summary>"Code of Conduct &amp; Privacy" acceptance step task (all roles).</summary>
    public static string Accept(int participantId) => $"{AcceptPrefix}{participantId}";

    /// <summary>
    /// Volunteer "My Availability" step task (§148 per-day availability form). §234 (7a):
    /// this got its OWN key — it previously reused <c>volunteer-form:</c>, which belongs to
    /// the SHIFTS wizard, cross-wiring two different live forms' completion signals.
    /// </summary>
    public static string Availability(int participantId) => $"{AvailabilityPrefix}{participantId}";
}
