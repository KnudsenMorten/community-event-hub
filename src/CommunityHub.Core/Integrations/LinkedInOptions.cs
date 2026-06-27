namespace CommunityHub.Core.Integrations;

/// <summary>
/// Configuration for the live LinkedIn company-page publisher (REQUIREMENTS §19/§31).
/// Binds from the <c>"LinkedIn"</c> section; env-var form uses <c>__</c>
/// (e.g. <c>LinkedIn__AccessToken</c>). The access token / OAuth secrets come from
/// Key Vault app settings — never source.
///
/// <b>Safety:</b> <see cref="DryRun"/> defaults to <c>true</c>, so even when the
/// publisher is enabled and credentialed it does NOT call LinkedIn — it logs what it
/// WOULD post and leaves the post queued. Real posting requires explicitly setting
/// <c>LinkedIn__DryRun=false</c> (on top of <see cref="Enabled"/> + a token + the
/// SoMe posting switch + a configured company page).
/// </summary>
public sealed class LinkedInOptions
{
    public const string SectionName = "LinkedIn";

    /// <summary>Master switch. When false the Null publisher is registered (no calls).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// HARD HOLD. When true (default), the live publisher NEVER calls LinkedIn — it
    /// logs the intended post and returns "not posted" so the dispatcher leaves the
    /// post queued. Flip to false only when you actually want posts to go out.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>LinkedIn REST base (default the public API host).</summary>
    public string ApiBaseUrl { get; set; } = "https://api.linkedin.com";

    /// <summary>The <c>LinkedIn-Version</c> header (yyyymm). Bump as LinkedIn versions the API.</summary>
    public string ApiVersion { get; set; } = "202405";

    /// <summary>
    /// A ready OAuth2 member access token with <c>w_organization_social</c> (admin of
    /// the company page). Used directly when the refresh-token triplet below is absent.
    /// </summary>
    public string? AccessToken { get; set; }

    // --- Optional refresh-token flow (preferred for long-lived posting) -------
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? RefreshToken { get; set; }
    public string TokenEndpoint { get; set; } = "https://www.linkedin.com/oauth/v2/accessToken";

    /// <summary>True when a token can be obtained (a static token, or the refresh triplet).</summary>
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(AccessToken)
        || (!string.IsNullOrWhiteSpace(ClientId)
            && !string.IsNullOrWhiteSpace(ClientSecret)
            && !string.IsNullOrWhiteSpace(RefreshToken));
}
