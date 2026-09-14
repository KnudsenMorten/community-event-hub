using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1142 — the ONE Zoho access token, shared by every INSTANCE of every host.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-27: <i>"i agree to make the token shareable in prod so any instance/module
/// can reuse it"</i>.</para>
///
/// <para>🔴 <b>Why the in-memory cache was not enough, measured.</b> §525 made the token a
/// SINGLETON, which is correct — but a singleton lives exactly as long as its process, and the
/// PROD jobs host ran <b>5,520 distinct instances in 24 hours</b> (~150–250 per hour, measured in
/// App Insights on 2026-08-27). Every cold instance that touched Zoho therefore minted its OWN
/// token. Zoho keeps at most <b>10 active access tokens per refresh token</b> and evicts the
/// oldest when an 11th is issued — so tokens were being retired out from under instances that were
/// still holding them, which is exactly the sporadic mid-life 401 that produced three
/// "[PROD] Signage agenda sync failed" mails in one day.</para>
///
/// <para>🔑 <b>The row is a cache, not a record.</b> It is fully re-derivable from the refresh
/// token, so it is deliberately NOT added to the platform backup: restoring an hour-old access
/// token would be worse than restoring nothing.</para>
///
/// <para>🔒 <b>Keyed by credential.</b> <see cref="ZohoTokenLease.CredentialKey"/> is a hash of the
/// client id + refresh token, so rotating the credential orphans the old row instead of serving a
/// token minted from a credential that no longer exists. DEV and PROD have separate databases AND
/// separate credentials (§1038b), so they cannot collide on either axis.</para>
/// </remarks>
public class ZohoTokenLease
{
    public int Id { get; set; }

    /// <summary>Hash of the credential this token was minted from. Unique.</summary>
    public string CredentialKey { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; set; }

    /// <summary>§783.12's failure cooldown, now shared — one host's throttle is every host's.</summary>
    public DateTimeOffset? RetryNotBeforeUtc { get; set; }

    /// <summary>
    /// Held by the one instance currently talking to Zoho's token endpoint. Everyone else waits and
    /// re-reads instead of refreshing too. Time-boxed so a crashed instance cannot wedge the lease.
    /// </summary>
    public DateTimeOffset? RefreshLeaseUntilUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>Diagnostics: how many grants have actually been made against this credential.</summary>
    public int RefreshCount { get; set; }
}

/// <summary>One instance's view of the shared row.</summary>
public readonly record struct ZohoSharedToken(
    string? AccessToken, DateTimeOffset ExpiresAtUtc, DateTimeOffset? RetryNotBeforeUtc);

/// <summary>
/// §1142 — the shared-token seam. An interface so the coordination can be tested without a
/// database, and so a store failure can fall back to in-memory behaviour rather than taking Zoho
/// down with it.
/// </summary>
public interface IZohoTokenStore
{
    Task<ZohoSharedToken?> ReadAsync(string credentialKey, CancellationToken ct = default);

    /// <summary>
    /// Atomically take the right to refresh. TRUE means this caller must talk to Zoho; FALSE means
    /// somebody else already is, and the caller should wait and re-read.
    /// </summary>
    Task<bool> TryClaimRefreshAsync(
        string credentialKey, DateTimeOffset leaseUntilUtc, DateTimeOffset nowUtc,
        CancellationToken ct = default);

    Task WriteTokenAsync(
        string credentialKey, string accessToken, DateTimeOffset expiresAtUtc,
        CancellationToken ct = default);

    Task WriteCooldownAsync(
        string credentialKey, DateTimeOffset retryNotBeforeUtc, CancellationToken ct = default);
}

/// <summary>
/// §1142 — the SQL implementation. Resolves a scoped <see cref="CommunityHubDbContext"/> per
/// operation because <see cref="ZohoAccessTokenCache"/> is a singleton and the context is scoped.
/// </summary>
public sealed class SqlZohoTokenStore : IZohoTokenStore
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SqlZohoTokenStore>? _log;

    public SqlZohoTokenStore(IServiceScopeFactory scopes, ILogger<SqlZohoTokenStore>? log = null)
    {
        _scopes = scopes;
        _log = log;
    }

    /// <summary>
    /// 🔴 §1142 — FAIL-SOFT MUST NOT MEAN FAIL-SILENT.
    /// </summary>
    /// <remarks>
    /// <para>The cache swallows every exception from this store on purpose — a storage blip must not
    /// take down all 16 Zoho integrations. But swallowing it in the caller means a genuinely broken
    /// store (a migration that did not apply, a permission lost) looks exactly like a healthy one:
    /// every instance quietly mints its own token again and the fleet is back to the §1142 bug with
    /// nothing to see.</para>
    ///
    /// <para>⚠️ That is §335's rule — <i>"the only symptom is that nothing happens"</i> — and it bit
    /// during this very deploy: with the store silent, there was no way to tell from the outside
    /// whether the table existed. So the throw is logged HERE and then re-thrown, leaving the
    /// caller's fallback behaviour unchanged.</para>
    /// </remarks>
    private async Task<T> WithDbAsync<T>(string operation, Func<CommunityHubDbContext, Task<T>> work)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
            return await work(db);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex,
                "Zoho shared token store: {Operation} FAILED — falling back to a per-instance token. "
                + "If this persists, the ZohoTokenLeases table is missing or unreachable and every "
                + "instance is minting its own token again (§1142).", operation);
            throw;
        }
    }

    public Task<ZohoSharedToken?> ReadAsync(string credentialKey, CancellationToken ct = default) =>
        WithDbAsync("read", async db =>
        {
            var row = await db.ZohoTokenLeases.AsNoTracking()
                .FirstOrDefaultAsync(r => r.CredentialKey == credentialKey, ct);
            return row is null
                ? (ZohoSharedToken?)null
                : new ZohoSharedToken(row.AccessToken, row.ExpiresAtUtc, row.RetryNotBeforeUtc);
        });

    /// <summary>
    /// 🔴 The whole point of the store, and the one operation that MUST be atomic.
    /// </summary>
    /// <remarks>
    /// <para>A read-then-write would let every instance observe "no lease" in the same instant and
    /// all refresh anyway — reproducing the stampede this class exists to stop. So the claim is a
    /// single conditional UPDATE and the row count is the answer: <b>the database decides the
    /// winner, not the application.</b></para>
    ///
    /// <para>⚠️ The lease is TIME-BOXED. An instance that dies mid-refresh (which, on a host
    /// recycling hundreds of times an hour, is a routine event rather than an edge case) must not
    /// wedge every other instance out of refreshing for ever.</para>
    /// </remarks>
    public Task<bool> TryClaimRefreshAsync(
        string credentialKey, DateTimeOffset leaseUntilUtc, DateTimeOffset nowUtc,
        CancellationToken ct = default) =>
        WithDbAsync("claim-refresh", async db =>
        {
            // Ensure a row exists before trying to claim it. A duplicate insert from a racing
            // instance violates the unique index and is treated as "someone else got there".
            var exists = await db.ZohoTokenLeases
                .AnyAsync(r => r.CredentialKey == credentialKey, ct);
            if (!exists)
            {
                db.ZohoTokenLeases.Add(new ZohoTokenLease
                {
                    CredentialKey = credentialKey,
                    AccessToken = string.Empty,
                    ExpiresAtUtc = DateTimeOffset.MinValue,
                    RefreshLeaseUntilUtc = leaseUntilUtc,
                    UpdatedAtUtc = nowUtc,
                });
                try
                {
                    await db.SaveChangesAsync(ct);
                    return true;   // we created it, so we hold the lease
                }
                catch (DbUpdateException)
                {
                    return false;  // another instance inserted first
                }
            }

            var affected = await db.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE [ZohoTokenLeases]
                      SET [RefreshLeaseUntilUtc] = {leaseUntilUtc}, [UpdatedAtUtc] = {nowUtc}
                    WHERE [CredentialKey] = {credentialKey}
                      AND ([RefreshLeaseUntilUtc] IS NULL OR [RefreshLeaseUntilUtc] < {nowUtc})", ct);

            return affected > 0;
        });

    public Task WriteTokenAsync(
        string credentialKey, string accessToken, DateTimeOffset expiresAtUtc,
        CancellationToken ct = default) =>
        WithDbAsync("write-token", async db =>
        {
            var row = await db.ZohoTokenLeases
                .FirstOrDefaultAsync(r => r.CredentialKey == credentialKey, ct);
            if (row is null) return 0;

            row.AccessToken = accessToken;
            row.ExpiresAtUtc = expiresAtUtc;
            row.RetryNotBeforeUtc = null;        // a success clears any standing cooldown
            row.RefreshLeaseUntilUtc = null;     // release
            row.RefreshCount++;
            row.UpdatedAtUtc = DateTimeOffset.UtcNow;
            var saved = await db.SaveChangesAsync(ct);

            // 🔑 §1142 — THE ONE LINE THAT MAKES THIS OBSERVABLE. Nothing logged a token grant
            // before, which is why "how often are we minting tokens?" could not be answered and the
            // bug lived for weeks. Expect roughly ONE of these per hour per credential; a run of
            // them minutes apart means instances are minting their own again and the fix has
            // regressed. Volume is bounded by design, so this is safe to log at Information.
            _log?.LogInformation(
                "Zoho token GRANT published to the shared store — grant #{Count} for this "
                + "credential, valid until {ExpiresAt:u}.", row.RefreshCount, row.ExpiresAtUtc);

            return saved;
        });

    public Task WriteCooldownAsync(
        string credentialKey, DateTimeOffset retryNotBeforeUtc, CancellationToken ct = default) =>
        WithDbAsync("write-cooldown", async db =>
        {
            var row = await db.ZohoTokenLeases
                .FirstOrDefaultAsync(r => r.CredentialKey == credentialKey, ct);
            if (row is null) return 0;

            // 🔒 The token is cleared but the COOLDOWN is what matters: it is now shared, so one
            // instance discovering a throttle stops every other instance from spending the budget
            // discovering it too. That is the §783.12 fix applied across the fleet instead of
            // within one process.
            row.AccessToken = string.Empty;
            row.ExpiresAtUtc = DateTimeOffset.MinValue;
            row.RetryNotBeforeUtc = retryNotBeforeUtc;
            row.RefreshLeaseUntilUtc = null;
            row.UpdatedAtUtc = DateTimeOffset.UtcNow;
            return await db.SaveChangesAsync(ct);
        });
}
