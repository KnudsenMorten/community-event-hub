namespace CommunityHub.Core.Domain;

/// <summary>
/// The TOP level of the volunteer work structure: a broad area of work for an
/// edition (e.g. "Registration", "Sponsor area", "A/V"). Each category is owned
/// by two people:
///
///  - <see cref="LeadParticipantId"/> — the <b>volunteer lead</b>, an ORGANIZER
///    who provides oversight of the category, and
///  - <see cref="SupervisorParticipantId"/> — the <b>supervisor</b>, a VOLUNTEER
///    appointed from the volunteer pool who actually RUNS the category day to
///    day. Appointing a supervisor is an organizer action that ELEVATES that
///    volunteer to category-scoped management rights (manage this category's
///    subcategories/tasks, coordinate its volunteers, answer help requests).
///
/// A category owns many <see cref="VolunteerSubcategory"/> rows; each subcategory
/// owns many <see cref="VolunteerTask"/> rows. Everything is edition-scoped via
/// <see cref="EventId"/> so a new edition is just a new set of rows — no schema
/// change (matches the rest of the hub's per-edition model).
/// </summary>
public class VolunteerCategory
{
    public int Id { get; set; }

    // --- Edition scope ------------------------------------------------------
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    // --- Content ------------------------------------------------------------
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    // --- Ownership ----------------------------------------------------------
    /// <summary>
    /// The volunteer lead = an ORGANIZER participant who oversees this category.
    /// Nullable: a category can exist before a lead is named. The service layer
    /// enforces that the chosen participant has <see cref="ParticipantRole.Organizer"/>.
    /// </summary>
    public int? LeadParticipantId { get; set; }
    public Participant? LeadParticipant { get; set; }

    /// <summary>
    /// The supervisor = a VOLUNTEER participant appointed from the pool to run
    /// this category. Nullable: a category can exist before a supervisor is
    /// appointed. The service layer enforces that the chosen participant has
    /// <see cref="ParticipantRole.Volunteer"/>; appointing them grants the
    /// category-scoped management rights (this row IS the grant — there is no
    /// separate role flip, keeping the supervisor a normal volunteer everywhere
    /// else).
    /// </summary>
    public int? SupervisorParticipantId { get; set; }
    public Participant? SupervisorParticipant { get; set; }

    /// <summary>
    /// The ELDK lead for this Bucket — the go-to person FOR THE SUPERVISORS,
    /// forming the third tier (volunteer → supervisor → ELDK lead). Free text (a
    /// name) because the ELDK lead is not necessarily a hub <see cref="Participant"/>.
    /// (Added 2026-06-15 with the Buckets feature.)
    /// </summary>
    public string? EldkLeadName { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    // --- Navigation ---------------------------------------------------------
    public ICollection<VolunteerSubcategory> Subcategories { get; set; } = new List<VolunteerSubcategory>();
    public ICollection<VolunteerHelpRequest> HelpRequests { get; set; } = new List<VolunteerHelpRequest>();

    /// <summary>
    /// The Bucket's supervisor(s) — ONE OR MORE volunteers acting as the go-to
    /// person for the bucket. This is the multi-supervisor model added with
    /// Buckets; the legacy single <see cref="SupervisorParticipantId"/> column is
    /// kept for back-compat and treated as a member of this set by the service.
    /// </summary>
    public ICollection<VolunteerBucketSupervisor> Supervisors { get; set; } = new List<VolunteerBucketSupervisor>();
}
