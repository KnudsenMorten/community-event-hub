using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1142 — the REAL <see cref="SqlZohoTokenStore"/> against a relational database.
///
/// <para>🔴 <b>Why these exist alongside <see cref="ZohoSharedTokenStoreTests"/>.</b> Those tests
/// use a fake that <i>imitates</i> the claim contract, so they prove the cache's decision-making
/// and nothing about the SQL. The atomic claim is the one piece where an imitation is worthless:
/// the whole design rests on <b>the database</b> — not the application — choosing exactly one
/// winner. A read-then-write in C# passes a fake and stampedes in production.</para>
///
/// <para>⚠️ SQLite is not SQL Server. What it does verify is that the statement is valid SQL, hits
/// the intended row, and that its WHERE clause actually excludes a held lease — the logic errors.
/// True cross-connection concurrency under load remains unproven here and is stated as such.</para>
/// </summary>
public sealed class ZohoSharedTokenStoreRelationalTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ServiceProvider _sp;

    private const string Key = "cred-A";
    private static readonly DateTimeOffset T0 = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    public ZohoSharedTokenStoreRelationalTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var services = new ServiceCollection();
        services.AddDbContext<CommunityHubDbContext>(o => o.UseSqlite(_conn));
        _sp = services.BuildServiceProvider();

        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>().Database.EnsureCreated();
    }

    public void Dispose() { _sp.Dispose(); _conn.Dispose(); }

    private SqlZohoTokenStore NewStore() =>
        new(_sp.GetRequiredService<IServiceScopeFactory>());

    [Fact]
    public async Task An_absent_row_reads_as_null()
    {
        Assert.Null(await NewStore().ReadAsync(Key));
    }

    [Fact]
    public async Task The_FIRST_claim_creates_the_row_and_the_SECOND_is_refused()
    {
        var store = NewStore();

        Assert.True(await store.TryClaimRefreshAsync(Key, T0.AddSeconds(30), T0));

        // 🔴 THE ASSERTION THE WHOLE DESIGN RESTS ON. A second instance, arriving while the first
        // holds the lease, must be told no — by the database, in one statement.
        Assert.False(await store.TryClaimRefreshAsync(Key, T0.AddSeconds(30), T0.AddSeconds(1)));
    }

    [Fact]
    public async Task A_written_token_is_visible_to_a_DIFFERENT_store_instance()
    {
        var writer = NewStore();
        await writer.TryClaimRefreshAsync(Key, T0.AddSeconds(30), T0);
        await writer.WriteTokenAsync(Key, "SHARED-TOKEN", T0.AddHours(1));

        // A separate instance — the point of the table.
        var reader = NewStore();
        var shared = await reader.ReadAsync(Key);

        Assert.NotNull(shared);
        Assert.Equal("SHARED-TOKEN", shared!.Value.AccessToken);
        Assert.Equal(T0.AddHours(1), shared.Value.ExpiresAtUtc);
        Assert.Null(shared.Value.RetryNotBeforeUtc);
    }

    [Fact]
    public async Task Writing_a_token_RELEASES_the_lease()
    {
        var store = NewStore();
        await store.TryClaimRefreshAsync(Key, T0.AddSeconds(30), T0);
        await store.WriteTokenAsync(Key, "T", T0.AddHours(1));

        // Otherwise the next legitimate refresh would be locked out for the lease duration.
        Assert.True(await store.TryClaimRefreshAsync(Key, T0.AddSeconds(60), T0.AddSeconds(2)));
    }

    [Fact]
    public async Task An_EXPIRED_lease_can_be_taken_over()
    {
        var store = NewStore();
        Assert.True(await store.TryClaimRefreshAsync(Key, T0.AddSeconds(30), T0));

        // ⚠️ An instance dying mid-refresh is routine on a host recycling hundreds of times an
        // hour. A lease that outlived its holder must not wedge the fleet out of refreshing.
        Assert.True(await store.TryClaimRefreshAsync(Key, T0.AddSeconds(90), T0.AddSeconds(31)));
    }

    [Fact]
    public async Task A_cooldown_is_shared_and_clears_the_token()
    {
        var store = NewStore();
        await store.TryClaimRefreshAsync(Key, T0.AddSeconds(30), T0);
        await store.WriteTokenAsync(Key, "T", T0.AddHours(1));
        await store.WriteCooldownAsync(Key, T0.AddMinutes(15));

        var shared = await NewStore().ReadAsync(Key);
        Assert.NotNull(shared);
        Assert.Equal(T0.AddMinutes(15), shared!.Value.RetryNotBeforeUtc);
        Assert.True(string.IsNullOrEmpty(shared.Value.AccessToken));
    }

    [Fact]
    public async Task A_successful_write_CLEARS_a_standing_cooldown()
    {
        var store = NewStore();
        await store.TryClaimRefreshAsync(Key, T0.AddSeconds(30), T0);
        await store.WriteCooldownAsync(Key, T0.AddMinutes(15));

        await store.TryClaimRefreshAsync(Key, T0.AddSeconds(30), T0.AddMinutes(20));
        await store.WriteTokenAsync(Key, "RECOVERED", T0.AddHours(1));

        var shared = await NewStore().ReadAsync(Key);
        Assert.Null(shared!.Value.RetryNotBeforeUtc);
        Assert.Equal("RECOVERED", shared.Value.AccessToken);
    }

    [Fact]
    public async Task Two_credentials_do_not_share_a_row()
    {
        var store = NewStore();
        await store.TryClaimRefreshAsync("cred-PROD", T0.AddSeconds(30), T0);
        await store.WriteTokenAsync("cred-PROD", "PROD-TOKEN", T0.AddHours(1));

        // 🔒 A rotated credential must not be served the token minted from the previous one.
        Assert.Null(await store.ReadAsync("cred-OTHER"));

        // And claiming for the other credential is independent of PROD's lease.
        Assert.True(await store.TryClaimRefreshAsync("cred-OTHER", T0.AddSeconds(30), T0));
        Assert.Equal("PROD-TOKEN", (await store.ReadAsync("cred-PROD"))!.Value.AccessToken);
    }

    [Fact]
    public async Task The_refresh_count_records_grants_actually_made()
    {
        var store = NewStore();
        await store.TryClaimRefreshAsync(Key, T0.AddSeconds(30), T0);
        await store.WriteTokenAsync(Key, "T1", T0.AddHours(1));
        await store.TryClaimRefreshAsync(Key, T0.AddSeconds(30), T0.AddMinutes(56));
        await store.WriteTokenAsync(Key, "T2", T0.AddHours(2));

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
        var row = await db.ZohoTokenLeases.AsNoTracking().FirstAsync(r => r.CredentialKey == Key);

        // Persistently high against a ~1/hour expectation is the signal that instances are still
        // minting their own — i.e. that this fix has regressed.
        Assert.Equal(2, row.RefreshCount);
    }
}
