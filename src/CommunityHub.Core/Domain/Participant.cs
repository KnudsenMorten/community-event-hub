namespace CommunityHub.Core.Domain;

/// <summary>
/// One participant, scoped to one event edition. The email is the identity
/// used for PIN login (CONTEXT.md section 5). A person attending two editions
/// has two Participant rows - one per EventId.
/// </summary>
public class Participant
{
    public int Id { get; set; }

    // --- Edition scope ------------------------------------------------------
    /// <summary>The edition this profile belongs to. Every query is scoped by this.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    // --- Identity -----------------------------------------------------------
    /// <summary>
    /// Login identity. Stored lower-cased and trimmed. Unique within an edition.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    /// <summary>Optional - phone, for organizer contact only.</summary>
    public string? Phone { get; set; }

    // --- Role ---------------------------------------------------------------
    /// <summary>Admin-set. Drives the personalized hub. One role per person.</summary>
    public ParticipantRole Role { get; set; }

    /// <summary>
    /// For a Sponsor-role participant: the WooCommerce / Company Manager
    /// company id (the order's _cm_company_id) this contact belongs to. Lets
    /// the sponsor area show and edit only this company's tasks. Null for
    /// non-sponsor participants, and for sponsors whose company is not yet
    /// known. There is no company *entity* in the hub - this is just the
    /// external id carried for scoping (CONTEXT.md 11g).
    /// </summary>
    public string? SponsorCompanyId { get; set; }

    /// <summary>
    /// False = the person cannot log in (e.g. withdrew). Login checks this.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>
    /// When the participant first saw (and acknowledged) the welcome landing
    /// page. Null = they have never been redirected through /Welcome — the hub
    /// landing page will bounce them there on next visit. Set to UtcNow when
    /// they click "Continue" on /Welcome.
    /// </summary>
    public DateTimeOffset? WelcomeShownAt { get; set; }

    // --- Navigation ---------------------------------------------------------
    public ICollection<ParticipantTask> AssignedTasks { get; set; } = new List<ParticipantTask>();
    public ICollection<LoginPin> LoginPins { get; set; } = new List<LoginPin>();
}
