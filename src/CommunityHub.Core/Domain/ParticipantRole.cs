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

    /// <summary>Session speaker. Hotel, dinner, speaker deadlines, resources.</summary>
    Speaker = 1,

    /// <summary>Masterclass / pre-day speaker. As Speaker, plus pre-day items.</summary>
    MasterclassSpeaker = 2,

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

    /// <summary>Video crew (recording / streaming). Sees Hotel + Dinner like staff.</summary>
    Video = 6,

    /// <summary>Photo crew. Sees Hotel + Dinner like staff.</summary>
    Camera = 7
}
