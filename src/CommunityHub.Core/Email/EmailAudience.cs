namespace CommunityHub.Core.Email;

/// <summary>
/// §515 — WHO a template is written to, so the Settings page can file each mail under the role it
/// belongs to instead of one undifferentiated "E-mails" chapter.
///
/// <para>Operator 2026-07-28: <i>"i would like to configure indiidual ring per mail, like ring 2
/// for speakers welome and ring 1 for sponsors. and then have fx welcome mail for speakers under
/// speakers section"</i>. Filing is half the request; the other half is the per-template ring.</para>
///
/// <para>This is the AUDIENCE, not the sender: a hotel release-deadline warning goes to organizers
/// even though it is about speakers, so it files under <see cref="Organizer"/>.</para>
/// </summary>
public enum EmailAudience
{
    /// <summary>Reaches more than one role (sign-in codes, broadcasts, invitations).</summary>
    Everyone = 0,
    Speaker = 1,
    Sponsor = 2,
    Volunteer = 3,
    Attendee = 4,
    Media = 5,
    EventPartner = 6,
    /// <summary>Internal ops mail — digests, alerts and deadline warnings for the organizers.</summary>
    Organizer = 7,
}
