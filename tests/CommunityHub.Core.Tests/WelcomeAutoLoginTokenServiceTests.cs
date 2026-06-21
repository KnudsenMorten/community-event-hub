using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The security model of the welcome auto-login token (the one-tap sign-in CTA
/// in the welcome email). It is deliberately stronger than the reusable
/// invitation magic-link, so these tests assert each guarantee the operator's
/// rules require:
///   - <b>single-use</b>: the first redeem consumes the grant; a second is refused;
///   - <b>short-lived</b>: an expired token does not redeem;
///   - <b>signed/random</b>: a tampered / alien / empty token never redeems (and never throws);
///   - <b>no secret in the DB</b>: the grant stores only a hash of the random id;
///   - <b>scoped</b>: a role change since issue invalidates the link;
///   - <b>revocable</b>: a revoked grant does not redeem;
///   - <b>inactive</b>: a deactivated account does not redeem.
/// All offline: EF in-memory + an ephemeral DataProtection provider.
/// </summary>
public class WelcomeAutoLoginTokenServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static WelcomeAutoLoginTokenService NewService(
        CommunityHubDbContext db, DateTimeOffset? now = null) =>
        new(db,
            DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ceh-autologin-tests"))),
            new FixedClock(now ?? Now));

    private static async Task<int> SeedOneAsync(
        CommunityHubDbContext db, ParticipantRole role = ParticipantRole.Speaker,
        bool active = true)
    {
        var ev = new Event
        {
            CommunityName = "Test Community",
            DisplayName = "Test Community 2027",
            Code = "TC27",
            IsActive = true,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var p = new Participant
        {
            EventId = ev.Id,
            Email = "person@example.com",
            FullName = "Test Person",
            Role = role,
            IsActive = active,
            IsTestUser = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    [Fact]
    public async Task Mint_then_redeem_signs_in_the_participant_exactly_once()
    {
        using var db = ScenarioFixture.NewDb();
        var pid = await SeedOneAsync(db);
        var svc = NewService(db);

        var token = await svc.CreateAsync(pid);

        // First redeem succeeds.
        var first = await svc.RedeemAsync(token);
        Assert.True(first.Success);
        Assert.Equal(pid, first.ParticipantId);

        // SINGLE-USE: a second redeem of the SAME token is refused.
        var second = await svc.RedeemAsync(token);
        Assert.False(second.Success);
        Assert.Null(second.ParticipantId);
        Assert.Contains("used", second.Reason, StringComparison.OrdinalIgnoreCase);

        // The grant records the consumption (audit).
        var grant = await db.MagicLinkGrants.SingleAsync();
        Assert.NotNull(grant.ConsumedAt);
        Assert.Equal(WelcomeAutoLoginTokenService.PurposeName, grant.Purpose);
    }

    [Fact]
    public async Task The_raw_token_id_is_never_stored__only_its_hash()
    {
        using var db = ScenarioFixture.NewDb();
        var pid = await SeedOneAsync(db);
        var svc = NewService(db);

        var token = await svc.CreateAsync(pid);
        var grant = await db.MagicLinkGrants.SingleAsync();

        // The opaque URL token must not appear verbatim in the DB.
        Assert.DoesNotContain(token, grant.TokenIdHash);
        // The stored value is a SHA-256 hex digest (64 lowercase hex chars).
        Assert.Equal(64, grant.TokenIdHash.Length);
        Assert.Matches("^[0-9a-f]{64}$", grant.TokenIdHash);
    }

    [Fact]
    public async Task Expired_token_does_not_redeem()
    {
        using var db = ScenarioFixture.NewDb();
        var pid = await SeedOneAsync(db);
        var svc = NewService(db);

        // Mint with a negative TTL → already expired.
        var token = await svc.CreateAsync(pid, TimeSpan.FromMinutes(-1));

        var result = await svc.RedeemAsync(token);
        Assert.False(result.Success);
        // Expiry never consumes the grant.
        var grant = await db.MagicLinkGrants.SingleAsync();
        Assert.Null(grant.ConsumedAt);
    }

    [Fact]
    public async Task Tampered_alien_and_empty_tokens_never_redeem_and_never_throw()
    {
        using var db = ScenarioFixture.NewDb();
        var pid = await SeedOneAsync(db);
        var svc = NewService(db);

        var token = await svc.CreateAsync(pid);
        var tampered = ('A' == token[0] ? 'B' : 'A') + token[1..];

        Assert.False((await svc.RedeemAsync(tampered)).Success);
        Assert.False((await svc.RedeemAsync("not-a-real-token")).Success);
        Assert.False((await svc.RedeemAsync("")).Success);

        // None of those consumed the genuine grant.
        var grant = await db.MagicLinkGrants.SingleAsync();
        Assert.Null(grant.ConsumedAt);
        // The genuine token still works (proving the failures didn't burn it).
        Assert.True((await svc.RedeemAsync(token)).Success);
    }

    [Fact]
    public async Task Revoked_grant_does_not_redeem()
    {
        using var db = ScenarioFixture.NewDb();
        var pid = await SeedOneAsync(db);
        var svc = NewService(db);

        var token = await svc.CreateAsync(pid);
        var grant = await db.MagicLinkGrants.SingleAsync();
        grant.RevokedAt = Now;
        await db.SaveChangesAsync();

        var result = await svc.RedeemAsync(token);
        Assert.False(result.Success);
        Assert.Contains("revoked", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Role_change_since_issue_invalidates_the_link()
    {
        using var db = ScenarioFixture.NewDb();
        var pid = await SeedOneAsync(db, ParticipantRole.Speaker);
        var svc = NewService(db);

        var token = await svc.CreateAsync(pid);

        // The person is re-roled after the link was minted.
        var p = await db.Participants.FindAsync(pid);
        p!.Role = ParticipantRole.Organizer;
        await db.SaveChangesAsync();

        var result = await svc.RedeemAsync(token);
        Assert.False(result.Success);
        Assert.Contains("role", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deactivated_account_does_not_redeem()
    {
        using var db = ScenarioFixture.NewDb();
        var pid = await SeedOneAsync(db);
        var svc = NewService(db);

        var token = await svc.CreateAsync(pid);

        var p = await db.Participants.FindAsync(pid);
        p!.IsActive = false;
        await db.SaveChangesAsync();

        var result = await svc.RedeemAsync(token);
        Assert.False(result.Success);
        Assert.Contains("inactive", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
