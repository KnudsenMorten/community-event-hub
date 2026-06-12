namespace CommunityHub.Core.Domain;

/// <summary>
/// Durable per-sponsor token version for the deterministic leads-API
/// token (SHA256 over EventId + SponsorCompanyId + Version + GlobalSecret).
/// Bumping the version invalidates every previously-derived token for the
/// pair — that's the revocation mechanism, so it MUST survive restarts.
/// One row per (EventId, SponsorCompanyId); absent row = version 1.
/// </summary>
public class SponsorTokenVersion
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public string SponsorCompanyId { get; set; } = string.Empty;

    public int Version { get; set; } = 1;

    /// <summary>Audit: when the version was last bumped and by whom.</summary>
    public DateTimeOffset? BumpedAt { get; set; }
    public string? BumpedByEmail { get; set; }
}
