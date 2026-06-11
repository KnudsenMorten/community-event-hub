namespace CommunityHub.Core.Integrations.Sponsors;

/// <summary>
/// Deterministic per-sponsor leads-API token. Token is derived from
/// (EventId, SponsorCompanyId, TokenVersion, GlobalSecret); the operator
/// can re-derive any sponsor's current token any time without a database
/// lookup or a "regenerate" flow.
///
/// Per-sponsor revocation is supported via <see cref="BumpVersionAsync"/>:
/// bumping the version invalidates the previous token for THAT sponsor
/// only, without rotating the global secret (which would invalidate
/// everyone's tokens at once).
///
/// Storage: <see cref="TokenVersion"/> per (eventId, sponsorCompanyId)
/// is a small integer that lives in a DbSet&lt;SponsorTokenVersion&gt;
/// once the EF migration lands; before that, the in-memory implementation
/// defaults every pair to version 1, with bumps held in process memory.
///
/// The GLOBAL SECRET lives in Key Vault as
/// <c>sponsor-leads-global-secret</c> per environment and is read into
/// configuration as <c>SponsorLeads:GlobalSecret</c>. Pulled into this
/// service at construction time.
/// </summary>
public interface IDeterministicSponsorTokenService
{
    /// <summary>
    /// Derive the current token for (eventId, sponsorCompanyId). Uses
    /// the current <c>TokenVersion</c> for that pair. Returns a 32-char
    /// lowercase hex string (truncated SHA256). NEVER returns null --
    /// every sponsor has a derivable token.
    /// </summary>
    Task<string> DeriveAsync(int eventId, string sponsorCompanyId, CancellationToken ct);

    /// <summary>
    /// Return the current token version for (eventId, sponsorCompanyId).
    /// Defaults to 1 for any sponsor that's never been bumped.
    /// </summary>
    Task<int> GetVersionAsync(int eventId, string sponsorCompanyId, CancellationToken ct);

    /// <summary>
    /// Bump the token version for (eventId, sponsorCompanyId). The
    /// previous token immediately stops validating; <see cref="DeriveAsync"/>
    /// now returns the new token. Use when a sponsor reports their token
    /// has leaked OR when the sponsor's primary contact changed and you
    /// want to invalidate the leaked-to-the-old-contact value.
    /// </summary>
    Task<int> BumpVersionAsync(int eventId, string sponsorCompanyId, string? bumpedByEmail, CancellationToken ct);

    /// <summary>
    /// Constant-time compare a presented raw token against the current
    /// derived token for the sponsor. Used by the API auth filter.
    /// </summary>
    Task<bool> ValidateAsync(int eventId, string sponsorCompanyId, string rawToken, CancellationToken ct);
}
