namespace CommunityHub.Core.Domain;

/// <summary>
/// The MIDDLE level of the volunteer work structure: a sub-area within one
/// <see cref="VolunteerCategory"/> (e.g. under "Registration": "Badge desk",
/// "Welcome table"). Belongs to exactly one category and owns many
/// <see cref="VolunteerTask"/> rows. Carries <see cref="EventId"/> denormalized
/// from its parent so every query can scope by edition without a join.
/// </summary>
public class VolunteerSubcategory
{
    public int Id { get; set; }

    // --- Edition scope (denormalized from the parent category) --------------
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    // --- Parent -------------------------------------------------------------
    public int CategoryId { get; set; }
    public VolunteerCategory Category { get; set; } = null!;

    // --- Content ------------------------------------------------------------
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    // --- Navigation ---------------------------------------------------------
    public ICollection<VolunteerTask> Tasks { get; set; } = new List<VolunteerTask>();
}
