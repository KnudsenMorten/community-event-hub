namespace CommunityHub.Core.Domain;

/// <summary>
/// One row per event edition (ELDK27, ELDK28, ...). This is what makes the
/// app evergreen: the code never changes per year - a new edition is a new
/// Event row. Every year-specific entity carries an <see cref="Event"/>
/// reference via EventId, so all of an edition's data is scoped to its row.
/// See CONTEXT.md section 3 (multi-event design).
/// </summary>
public class Event
{
    public int Id { get; set; }

    /// <summary>
    /// The community / organization this edition belongs to, e.g. "Experts
    /// Live Denmark". This is DATA, not code: the codebase is generic
    /// (CommunityHub) and another community sets its own name here. Shown in
    /// the UI and emails so participants see their community's name.
    /// </summary>
    public string CommunityName { get; set; } = string.Empty;

    /// <summary>Short code, e.g. "ELDK27". Unique. Used in config file names.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Display name shown to participants, e.g. "Experts Live Denmark 2027".</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>First main event day.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>Last main event day.</summary>
    public DateOnly EndDate { get; set; }

    /// <summary>Optional pre-day (master classes), the day before StartDate.</summary>
    public DateOnly? PreDayDate { get; set; }

    /// <summary>Venue name, e.g. "Bella Center Copenhagen".</summary>
    public string VenueName { get; set; } = string.Empty;

    /// <summary>
    /// Per-edition hostname this edition is reached at,
    /// e.g. "hub.eldk27.expertslive.dk". The app itself is evergreen; only
    /// the DNS label changes per year.
    /// </summary>
    public string HubHostname { get; set; } = string.Empty;

    /// <summary>
    /// Exactly one Event is the active edition at a time. The PIN login and
    /// the role hub resolve "the current event" from this flag.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>Date after which participant records lock (no further self-service edits).</summary>
    public DateOnly? LockDate { get; set; }

    /// <summary>
    /// Organizer master switch for calendar INVITATIONS (REQUIREMENTS §193). When
    /// false, the hub's "Add Reminder" / "Email me a calendar invite" actions send
    /// nothing for the whole edition (<see cref="CommunityHub.Core.Email.CalendarInviteEmailService"/>
    /// short-circuits). Defaults <b>true</b> so existing editions keep working; an
    /// organizer can disable it on <c>/Organizer/CalendarSettings</c>. (§201: the old
    /// per-user iCal feed + "Add to my calendar" subscription this once gated were
    /// removed.)
    /// </summary>
    public bool CalendarSyncEnabled { get; set; } = true;

    /// <summary>
    /// Organizer switch for the AUTOMATIC calendar invitations (REQUIREMENTS §257).
    /// When <b>false</b> (the default), the hub NEVER auto-pushes a
    /// <c>METHOD:REQUEST</c> calendar invite into an inbox on a form submit / hotel
    /// placement / master-class selection — the four automatic invites (Dinner, Hotel,
    /// Hotel-placement, Master-Class) are suppressed and the confirmation e-mail instead
    /// carries a manual &ldquo;Add to calendar&rdquo; Google/Outlook link the recipient
    /// can click. When an organizer turns it <b>on</b>, those four flows attach the
    /// invite as before. This is DISTINCT from <see cref="CalendarSyncEnabled"/>, which
    /// governs the user-initiated &ldquo;Email me a calendar invite&rdquo; actions and
    /// the manual add-to-calendar links: the operator wanted the manual option to stay
    /// available while the automatic push is off, so the two switches are decoupled.
    /// Defaults <b>false</b> per the operator directive 2026-07-10.
    /// </summary>
    public bool AutoCalendarInvitesEnabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // --- Navigation ---------------------------------------------------------
    public ICollection<Participant> Participants { get; set; } = new List<Participant>();
    public ICollection<ParticipantTask> Tasks { get; set; } = new List<ParticipantTask>();
}
