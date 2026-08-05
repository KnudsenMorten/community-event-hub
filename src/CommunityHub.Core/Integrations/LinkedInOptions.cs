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
    /// <remarks>
    /// <para>🔴 <b>§824.14 — this was <c>202405</c> and that version is DEAD.</b> Measured against the
    /// live API on 2026-08-04 with a real org token: every call returned
    /// <c>HTTP 426 NONEXISTENT_VERSION "Requested version 20240501 is not active"</c>. The FIRST real
    /// post would have failed on this, and the failure names a version rather than a permission, so
    /// it would have read like an outage.</para>
    ///
    /// <para>The active window that day was <b>202601–202607</b> (202512 and older: 426;
    /// 202608: 426 — not yet released). <c>202607</c> is the newest active one, so it has the longest
    /// life before it ages out in turn.</para>
    ///
    /// <para>⚠️ <b>LinkedIn retires versions on a rolling ~12-month window, so this WILL expire again</b>
    /// — and nothing in CEH notices, because a 426 looks like any other failed publish. When a post
    /// fails, read the response body before assuming a token or permission problem: this is an app
    /// setting (<c>LinkedIn__ApiVersion</c>), so the fix is a setting change, not a deploy.</para>
    /// </remarks>
    public string ApiVersion { get; set; } = "202607";

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

    /// <summary>
    /// §824.10 — the OAuth scopes asked for on the consent screen, space-separated.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-04: <i>"add the read scope, then i will do the connect"</i>. The two
    /// WRITE scopes alone can POST but cannot LOOK ANYTHING UP, so a sponsor's organization id — which
    /// §824.3 needs to turn <c>{SponsorLinkedInUrl}</c> into a real company mention — was unreachable
    /// by construction, not by accident.</para>
    ///
    /// <para>🔒 <b>CONFIGURABLE RATHER THAN HARD-CODED, and that is the whole point.</b> LinkedIn
    /// refuses the ENTIRE consent screen when the app lacks product access for any single scope
    /// requested — so a scope added optimistically does not degrade gracefully, it BLOCKS the connect
    /// he is about to perform. Holding the list in configuration makes the fallback an app setting
    /// instead of a redeploy. §328 already recorded this failure mode ("the plain client-id/secret
    /// pair is the fallback if consent refuses the scopes"); this makes recovering from it free.</para>
    ///
    /// <para>⚠️ <b>If the consent screen errors, set <c>LinkedIn__Scopes</c> back to
    /// <c>w_member_social w_organization_social</c></b> and the connect behaves exactly as before —
    /// POSTING never depended on the read scopes, only the company-id lookup does.</para>
    /// </remarks>
    public string Scopes { get; set; } =
        "w_member_social w_organization_social r_organization_admin r_organization_social";

    /// <summary>The requested scopes as a list, so the connect page can show what is being approved.</summary>
    public IReadOnlyList<string> ScopeList =>
        (Scopes ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    /// <summary>True when a token can be obtained (a static token, or the refresh triplet).</summary>
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(AccessToken)
        || (!string.IsNullOrWhiteSpace(ClientId)
            && !string.IsNullOrWhiteSpace(ClientSecret)
            && !string.IsNullOrWhiteSpace(RefreshToken));
}
