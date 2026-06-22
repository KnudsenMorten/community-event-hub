using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Email;

/// <summary>
/// Maps a participant's PRIMARY <see cref="ParticipantRole"/> to the per-role
/// welcome content-template key (e.g. <c>welcome-speaker</c>). Both the welcome
/// EMAIL (<see cref="Reminders.WelcomeWithLoginEmailService"/>) and the
/// first-login PORTAL welcome view render the variant this router selects, so a
/// recipient sees one consistent, role-tailored welcome in both places.
///
/// <para>Every key has a generic, publish-safe default under
/// <c>templates/emails/</c>; an edition's exact copy lives only in the
/// private <c>config/email-templates/</c> layer (which wins via
/// <see cref="EmailTemplateProvider"/> resolution).</para>
/// </summary>
public static class WelcomeVariants
{
    /// <summary>
    /// The per-role welcome template key for <paramref name="role"/>, or
    /// <c>null</c> for roles that get NO welcome. Operator decision 2026-06-22:
    /// ORGANIZERS get no welcome, and ATTENDEES are not sent a platform welcome
    /// (they are covered by the Master Class confirmed-seat mail) — both return
    /// null so neither the welcome email nor the first-login portal welcome fires.
    /// </summary>
    public static string? TemplateKeyFor(ParticipantRole role) => role switch
    {
        ParticipantRole.Speaker      => "welcome-speaker",
        ParticipantRole.Volunteer    => "welcome-volunteer",
        ParticipantRole.Sponsor      => "welcome-sponsor",
        ParticipantRole.Media        => "welcome-media",
        ParticipantRole.EventPartner => "welcome-eventpartner",
        // Organizer + Attendee (and any future role) get no welcome.
        _                            => null,
    };

    /// <summary>The full set of welcome template keys (one per welcomed role) — for catalog/registration.</summary>
    public static readonly IReadOnlyList<string> AllTemplateKeys = new[]
    {
        "welcome-speaker",
        "welcome-volunteer",
        "welcome-sponsor",
        "welcome-media",
        "welcome-eventpartner",
    };
}
