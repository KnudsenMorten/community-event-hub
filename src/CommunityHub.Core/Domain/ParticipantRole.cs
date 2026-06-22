namespace CommunityHub.Core.Domain;

/// <summary>
/// The role a participant has for an edition. Drives the personalized hub:
/// each role sees a tailored landing page (CONTEXT.md section 4). One role
/// per person per edition; roles are admin-set, never self-selected.
/// </summary>
public enum ParticipantRole
{
    /// <summary>Organizing team. Sees everything; can manage other participants.</summary>
    Organizer = 0,

    /// <summary>
    /// Session speaker. Hotel, dinner, speaker deadlines, resources. The
    /// pre-day vs main-day distinction (formerly the dropped
    /// <c>MasterclassSpeaker</c> role, value 2) now lives on
    /// <see cref="SpeakerProfile.SpeakingPreDay"/> /
    /// <see cref="SpeakerProfile.SpeakingMainDay"/> and on the entitlement rules
    /// (<see cref="Entitlements.OrderEntitlements"/>), not on the role itself.
    /// </summary>
    Speaker = 1,

    // Value 2 was MasterclassSpeaker (dropped); the gap is intentional so
    // existing stored Speaker (1) / Volunteer (3) values keep their meaning.

    /// <summary>Volunteer. Shifts, tasks, dinner, resources.</summary>
    Volunteer = 3,

    /// <summary>
    /// Sponsor contact. Sponsor companies/contacts/roles come from the
    /// Company Manager plugin (CONTEXT.md 11g); this role marks a Participant
    /// that signs in to the sponsor side of the hub.
    /// </summary>
    Sponsor = 4,

    /// <summary>
    /// Event attendee who bought a ticket and may have a Master Class seat.
    /// Sees the attendee area (CONTEXT.md 9z) - their booking status and a
    /// deep-link to Zoho Bookings. Reconciled from Zoho, not organizer-managed.
    /// </summary>
    Attendee = 5,

    /// <summary>Press / photo / video crew — sees Hotel + Dinner + Lunch like staff.</summary>
    Media = 6,

    /// <summary>Event partner org — sees Hotel + Dinner + Lunch + task management, like staff.</summary>
    EventPartner = 7
}
