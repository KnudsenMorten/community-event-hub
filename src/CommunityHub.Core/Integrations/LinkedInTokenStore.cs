using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §324: a minted LinkedIn OAuth token (the org/page posting token today; member
/// tokens can join later via <see cref="Kind"/>). Minted ONCE by an organizer through
/// the /Organizer/LinkedInConnect consent flow (the Key Vault holds the APP's client
/// id/secret but no token — LinkedIn only issues tokens through member consent).
/// Access tokens live ~60 days; refresh tokens (when the app has refresh enabled)
/// ~365 — <see cref="LinkedInTokenStore"/> auto-refreshes near expiry.
/// </summary>
public class LinkedInOAuthToken
{
    public int Id { get; set; }

    /// <summary>Token kind — "org" (page posting) today; "member:{participantId}" later.</summary>
    public string Kind { get; set; } = "org";

    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Who connected it (audit).</summary>
    public string? ConnectedByEmail { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>§324b: the token owner's author URN (member tokens:
    /// <c>urn:li:person:{sub}</c> from /v2/userinfo). Null for the org token.</summary>
    public string? SubjectUrn { get; set; }
}

/// <summary>
/// §324: DB-backed token source for <see cref="LiveLinkedInPostPublisher"/>. Returns
/// the stored org token, refreshing it via the OAuth refresh grant when it is close
/// to expiry AND a refresh token + the app credentials are available. Returns null
/// when nothing is connected yet — the publisher then reports "not configured"
/// honestly instead of failing mid-post.
/// </summary>
public sealed class LinkedInTokenStore
{
    /// <summary>The org/page token row kind.</summary>
    public const string OrgKind = "org";

    private readonly CommunityHubDbContext _db;
    private readonly LinkedInOptions _options;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly ILogger<LinkedInTokenStore>? _log;

    public LinkedInTokenStore(
        CommunityHubDbContext db, LinkedInOptions options, HttpClient http,
        TimeProvider clock, ILogger<LinkedInTokenStore>? log = null)
    {
        _db = db;
        _options = options;
        _http = http;
        _clock = clock;
        _log = log;
    }

    /// <summary>True when an org token row exists (regardless of freshness).</summary>
    public Task<bool> IsConnectedAsync(CancellationToken ct = default) =>
        _db.LinkedInOAuthTokens.AsNoTracking().AnyAsync(t => t.Kind == OrgKind, ct);

    /// <summary>The stored org token's state for the connect page (null = not connected).</summary>
    public Task<LinkedInOAuthToken?> GetOrgTokenRowAsync(CancellationToken ct = default) =>
        _db.LinkedInOAuthTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Kind == OrgKind, ct);

    /// <summary>Upsert the org token after a consent/exchange (one row).</summary>
    public async Task SaveOrgTokenAsync(
        string accessToken, string? refreshToken, int expiresInSeconds, string? byEmail,
        CancellationToken ct = default)
    {
        var row = await _db.LinkedInOAuthTokens.FirstOrDefaultAsync(t => t.Kind == OrgKind, ct);
        if (row is null)
        {
            row = new LinkedInOAuthToken { Kind = OrgKind };
            _db.LinkedInOAuthTokens.Add(row);
        }
        row.AccessToken = accessToken;
        if (!string.IsNullOrWhiteSpace(refreshToken)) row.RefreshToken = refreshToken;
        row.ExpiresAt = _clock.GetUtcNow().AddSeconds(Math.Max(60, expiresInSeconds));
        row.ConnectedByEmail = byEmail;
        row.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    // ---- §324b: MEMBER tokens (the speaker's own context) ---------------------

    /// <summary>The member token row kind for a participant.</summary>
    public static string MemberKind(int participantId) => $"member:{participantId}";

    /// <summary>The speaker's stored member token row (null = not connected).</summary>
    public Task<LinkedInOAuthToken?> GetMemberTokenRowAsync(int participantId, CancellationToken ct = default) =>
        _db.LinkedInOAuthTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Kind == MemberKind(participantId), ct);

    /// <summary>Upsert a speaker's member token + person URN after consent.</summary>
    public async Task SaveMemberTokenAsync(
        int participantId, string accessToken, string? refreshToken, int expiresInSeconds,
        string personUrn, string? byEmail, CancellationToken ct = default)
    {
        var kind = MemberKind(participantId);
        var row = await _db.LinkedInOAuthTokens.FirstOrDefaultAsync(t => t.Kind == kind, ct);
        if (row is null)
        {
            row = new LinkedInOAuthToken { Kind = kind };
            _db.LinkedInOAuthTokens.Add(row);
        }
        row.AccessToken = accessToken;
        if (!string.IsNullOrWhiteSpace(refreshToken)) row.RefreshToken = refreshToken;
        row.ExpiresAt = _clock.GetUtcNow().AddSeconds(Math.Max(60, expiresInSeconds));
        row.SubjectUrn = personUrn;
        row.ConnectedByEmail = byEmail;
        row.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>A usable member token + author URN (auto-refresh like the org token);
    /// null when not connected / expired.</summary>
    public async Task<(string AccessToken, string PersonUrn)?> GetMemberAccessAsync(
        int participantId, CancellationToken ct = default)
    {
        var row = await _db.LinkedInOAuthTokens.FirstOrDefaultAsync(
            t => t.Kind == MemberKind(participantId), ct);
        if (row is null || string.IsNullOrWhiteSpace(row.SubjectUrn)) return null;
        var token = await FreshTokenAsync(row, ct);
        return token is null ? null : (token, row.SubjectUrn!);
    }

    /// <summary>
    /// A usable org access token, auto-refreshed when within 7 days of expiry and a
    /// refresh token + app credentials exist. Null when not connected / irrecoverably
    /// expired (the caller reports "connect LinkedIn first").
    /// </summary>
    public async Task<string?> GetOrgAccessTokenAsync(CancellationToken ct = default)
    {
        var row = await _db.LinkedInOAuthTokens.FirstOrDefaultAsync(t => t.Kind == OrgKind, ct);
        if (row is null) return null;
        return await FreshTokenAsync(row, ct);
    }

    /// <summary>Shared freshness/refresh logic for any token row.</summary>
    private async Task<string?> FreshTokenAsync(LinkedInOAuthToken row, CancellationToken ct)
    {

        var now = _clock.GetUtcNow();
        if (row.ExpiresAt > now.AddDays(7)) return row.AccessToken;

        // Near/at expiry — try the refresh grant when possible.
        if (!string.IsNullOrWhiteSpace(row.RefreshToken)
            && !string.IsNullOrWhiteSpace(_options.ClientId)
            && !string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            try
            {
                using var resp = await _http.PostAsync(_options.TokenEndpoint,
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = row.RefreshToken!,
                        ["client_id"] = _options.ClientId!,
                        ["client_secret"] = _options.ClientSecret!,
                    }), ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    row.AccessToken = doc.RootElement.GetProperty("access_token").GetString()!;
                    if (doc.RootElement.TryGetProperty("refresh_token", out var rt)
                        && rt.ValueKind == System.Text.Json.JsonValueKind.String)
                        row.RefreshToken = rt.GetString();
                    var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei)
                        ? ei.GetInt32() : 60 * 60 * 24 * 30;
                    row.ExpiresAt = now.AddSeconds(expiresIn);
                    row.UpdatedAt = now;
                    await _db.SaveChangesAsync(ct);
                    _log?.LogInformation("LinkedIn org token refreshed (expires {ExpiresAt}).", row.ExpiresAt);
                }
                else
                {
                    _log?.LogWarning("LinkedIn token refresh failed ({Status}): {Body}",
                        (int)resp.StatusCode, body.Length > 300 ? body[..300] : body);
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "LinkedIn token refresh threw.");
            }
        }

        // Still-valid (even if aging) tokens are usable until the hard expiry.
        return row.ExpiresAt > now ? row.AccessToken : null;
    }
}
