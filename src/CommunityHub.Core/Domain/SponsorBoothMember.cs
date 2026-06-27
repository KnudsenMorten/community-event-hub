namespace CommunityHub.Core.Domain;

/// <summary>
/// A booth member's permission level in Zoho Backstage.
/// <list type="bullet">
/// <item><b>Admin</b> — update profile, invite/manage members, capture &amp; manage
/// leads, export lead report, manage inquiries.</item>
/// <item><b>Staff</b> — update profile, capture &amp; manage leads, manage inquiries.</item>
/// </list>
/// </summary>
public enum BoothMemberRole
{
    Admin = 0,
    Staff = 1,
}

/// <summary>
/// One exhibitor booth member, maintained in CEH (add/edit/delete) and synced to
/// the company's Zoho Backstage exhibitor on "Save &amp; Sync". Scoped to
/// (EventId, SponsorCompanyId); exhibitor companies only.
/// </summary>
public class SponsorBoothMember
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>WooCommerce / Company Manager company id (same key as SponsorInfo).</summary>
    public string SponsorCompanyId { get; set; } = string.Empty;

    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public BoothMemberRole Role { get; set; } = BoothMemberRole.Staff;

    /// <summary>Set once the member has been pushed to Zoho (dedupes the create-bulk by email).</summary>
    public bool SyncedToZoho { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// Soft-delete tombstone. Zoho Backstage NOW supports per-member delete
    /// (<c>DELETE …/members/{id}</c> → <c>{"status":"success"}</c>), so the hub deletes the
    /// member in Zoho too on removal (member ONLY — never the exhibitor/sponsor RECORD, §56).
    /// We STILL tombstone rather than hard-delete: the row is hidden from the UI and counts,
    /// and the add-only sync skips re-pulling a tombstoned email — belt-and-braces so a removal
    /// can never be resurrected even if the Zoho delete fails. Re-adding the same email revives
    /// the row. Null = active.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; set; }
}
