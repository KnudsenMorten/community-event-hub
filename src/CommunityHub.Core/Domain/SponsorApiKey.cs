namespace CommunityHub.Core.Domain;

/// <summary>
/// Per-sponsor-company API key that authenticates the sponsor leads
/// download endpoints (<c>/api/v1/sponsors/{cid}/leads.json</c> and
/// <c>/api/v1/sponsors/{cid}/leads.csv</c>).
///
/// Only the SHA256 HASH of the issued key is stored. The raw key is
/// shown ONCE at generation time on the organizer's Leads admin page;
/// after that there is no way to retrieve it -- the operator either
/// regenerates (which revokes any previous key for the same sponsor)
/// or asks the sponsor to keep their copy.
///
/// Scoped to (EventId, SponsorCompanyId). A sponsor that attends two
/// editions gets two separate keys, so revoking the ELDK27 key has no
/// effect on, e.g., a future ELDK28.
/// </summary>
public class SponsorApiKey
{
    public int Id { get; set; }

    /// <summary>Edition this key is bound to.</summary>
    public int EventId { get; set; }

    /// <summary>WooCommerce / Company Manager company id.</summary>
    public string SponsorCompanyId { get; set; } = string.Empty;

    /// <summary>SHA256 lowercase-hex of the raw key. Never store the raw key.</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>
    /// First 8 chars of the raw key. Shown in the organizer UI alongside
    /// the issue/revoke timestamps so the operator can identify which
    /// key the sponsor is using without revealing the secret.
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>Operator-facing label (e.g. "Generated 2026-06-11 for Acme Corp").</summary>
    public string? Label { get; set; }

    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
    public string? IssuedByEmail { get; set; }
}
