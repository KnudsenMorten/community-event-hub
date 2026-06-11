using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations.Sponsors;

/// <summary>
/// Per-sponsor-company API key lifecycle. Hides the storage mechanism
/// (in-memory stub today, DbSet&lt;SponsorApiKey&gt; once the EF
/// migration lands) behind a stable interface, so the API auth filter
/// + the organizer admin UI don't change when persistence flips on.
/// </summary>
public interface ISponsorApiKeyService
{
    /// <summary>
    /// Generates a fresh API key for the (eventId, sponsorCompanyId)
    /// pair, revokes any previous active key for the same pair, stores
    /// the SHA256 hash, returns the RAW key to the caller. The raw
    /// value is irrecoverable after this call -- the operator MUST
    /// hand it to the sponsor immediately.
    /// </summary>
    Task<(string rawKey, SponsorApiKey row)> IssueAsync(
        int eventId, string sponsorCompanyId, string? issuedByEmail, string? label, CancellationToken ct);

    /// <summary>
    /// Looks up the current (non-revoked) key for the sponsor pair and
    /// returns its metadata. Returns <c>null</c> if no active key
    /// exists. The raw key is NEVER returned.
    /// </summary>
    Task<SponsorApiKey?> GetCurrentAsync(int eventId, string sponsorCompanyId, CancellationToken ct);

    /// <summary>
    /// Revokes the current key for the sponsor pair without issuing a
    /// new one. Idempotent -- safe to call when no active key exists.
    /// </summary>
    Task RevokeAsync(int eventId, string sponsorCompanyId, string? revokedByEmail, CancellationToken ct);

    /// <summary>
    /// Validates a raw API key against the active key for the sponsor
    /// pair. Used by the auth filter on the API controller. Returns
    /// <c>true</c> only when the key matches the active (non-revoked)
    /// row for THIS specific sponsor + event.
    /// </summary>
    Task<bool> ValidateAsync(int eventId, string sponsorCompanyId, string rawKey, CancellationToken ct);
}
