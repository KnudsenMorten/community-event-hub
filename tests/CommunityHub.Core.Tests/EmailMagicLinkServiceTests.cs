using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The §169 personal email magic-link: the long-lived (365-day), REUSABLE,
/// per-participant + per-edition auto-login link every in-hub email CTA routes
/// through. These tests pin each guarantee the operator's rules require:
///   - <b>hashed at rest</b>: the grant stores only a SHA-256 of the token; the raw never lands in the DB;
///   - <b>resolve</b>: a live token signs in (returns the participant) + records usage;
///   - <b>fail-safe</b>: expired / revoked / unknown / empty tokens fail (never throw), with recovery email when known;
///   - <b>reusable, one per participant</b>: two builds reuse the SAME token + one grant row;
///   - <b>revocable</b>: a revoked link no longer resolves;
///   - <b>rotatable</b>: rotate kills the old link + issues a new working one;
///   - <b>bound to participant + edition</b>: an edition mismatch is refused;
///   - <b>link building</b>: the URL embeds the token + a safe deep-link target.
/// All offline: EF in-memory + an ephemeral DataProtection provider. FAKE names only.
/// </summary>
public class EmailMagicLinkServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 28, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static EmailMagicLinkService NewService(
        CommunityHubDbContext db, DateTimeOffset? now = null) =>
        new(db,
            DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ceh-emailmagic-tests"))),
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
    public async Task The_raw_token_is_never_stored__only_its_hash()
    {
        using var db = ScenarioFixture.NewDb();
        var pid = await SeedOneAsync(db);
        var svc = NewService(db);

        var token = await svc.GetOrCreateTokenAsync(pid);
        var grant = await db.MagicLinkGrants.SingleAsync();

        // Stored value is a SHA-256 hex digest of the token; the raw never appears.
        Assert.Equal(EmailMagicLinkService.HashToken(token), grant.TokenIdHash);
        Assert.Equal(64, grant.TokenIdHash.Length);
        Assert.Matches("^[0-9a-f]{64}$", grant.TokenIdHash);
        Assert.NotEqual(token, grant.TokenIdHash);
        // The reuse copy is ciphertext, not the raw token.
        Assert.NotNull(grant.TokenProtected);
        Assert.NotEqual(token, grant.TokenProtected);
        Assert.DoesNotContain(token, grant.TokenProtected!);
        // It is a 365-day, multi-use, "email"-purpose grant.
        Assert.True(grant.MultiUse);
        Assert.Equal(EmailMagicLinkService.PurposeName, grant.Purpose);
        Assert.Equal(Now.AddDays(365), grant.ExpiresAt);
    }

    [Fact]
    public async Task A_live_token_resolves_to_the_participant_and_records_usage()
    {
        using var db = ScenarioFixture.NewDb();
        var pid = await SeedOneAsync(db);
        var svc = NewService(db);

        var token = await svc.GetOrCreateTokenAsync(pid);

        var first = await svc.ResolveAsync(token);
        Assert.True(first.Success);
        Assert.Equal(pid, first.ParticipantId);

        // REUSABLE: a SECOND resolve of the SAME token still works (not single-use).
        var second = await svc.ResolveAsync(token);
        Assert.True(second.Success);
        Assert.Equal(pid, second.ParticipantId);

        var grant = await db.MagicLinkGrants.SingleAsync();
        Assert.Null(grant.ConsumedAt);            // never consumed
        Assert.Equal(2, grant.UseCount);          // both uses logged
        Assert.Equal(Now, grant.LastUsedAt);
    }

    [Fact]
    public async Task Expired_revoked_unknown_and_empty_tokens_fail_safe_and_never_throw()
    {
        using var db = ScenarioFixture.NewDb();
        var pid = await SeedOneAsync(db);
        var svc = NewService(db);
        var token = await svc.GetOrCreateTokenAsync(pid);

        // Unknown / garbage / empty → fail, no recovery email, no throw.
        foreach (var bad in new[] { "not-a-real-token", "", "   " })
        {
            var r = await svc.ResolveAsync(bad);
            Assert.False(r.Success);
            Assert.Null(r.ParticipantId);
            Assert.Null(r.RecoveryEmail);
        }

        // Expired → fail, but the recovery email is recovered for pre-staging.
        var expiredSvc = NewService(db, Now.AddDays(366));
        var expired = await expiredSvc.ResolveAsync(token);
        Assert.False(expired.Success);
        Assert.Equal("person@example.com", expired.RecoveryEmail);

        // The genuine token still works at the right time (failures didn't burn it).
        Assert.True((await svc.ResolveAsync(token)).Success);
    }

    [Fact]
    public async Task One_token_per_participant__reused_across_builds()
    {
        using var db = ScenarioFixture.NewDb();
        var pid = await SeedOneAsync(db);
        var svc = NewService(db);

        var t1 = await svc.GetOrCreateTokenAsync(pid);
        var t2 = await svc.GetOrCreateTokenAsync(pid);

        Assert.Equal(t1, t2);                                   // same token reused
        Assert.Equal(1, await db.MagicLinkGrants.CountAsync()); // one grant row, not minted per call

        // And the link builder embeds that same token both times.
        var u1 = await svc.BuildUrlForParticipantAsync(pid, "https://hub.example/");
        var u2 = await svc.BuildUrlForParticipantAsync(pid, "https://hub.example/");
        Assert.Equal(u1, u2);
        Assert.Contains($"/go/{t1}", u1);
        Assert.Equal(1, await db.MagicLinkGrants.CountAsync());
    }

    [Fact]
    public async Task Revoke_then_resolve_fails()
    {
        using var db = ScenarioFixture.NewDb();
        var pid = await SeedOneAsync(db);
        var svc = NewService(db);
        var token = await svc.GetOrCreateTokenAsync(pid);
        var grant = await db.MagicLinkGrants.SingleAsync();

        var ok = await svc.RevokeAsync(grant.EventId, grant.Id, Now);
        Assert.True(ok);

        var r = await svc.ResolveAsync(token);
        Assert.False(r.Success);
        Assert.Equal("person@example.com", r.RecoveryEmail);   // genuine-but-dead → pre-stage
    }

    [Fact]
    public async Task Rotate_issues_a_new_working_token_and_invalidates_the_old()
    {
        using var db = ScenarioFixture.NewDb();
        var pid = await SeedOneAsync(db);
        var svc = NewService(db);
        var oldToken = await svc.GetOrCreateTokenAsync(pid);
        var seed = await db.MagicLinkGrants.SingleAsync();

        var newToken = await svc.RotateAsync(seed.EventId, seed.Id, Now);

        Assert.NotNull(newToken);
        Assert.NotEqual(oldToken, newToken);
        // Old link no longer resolves; the new one does.
        Assert.False((await svc.ResolveAsync(oldToken)).Success);
        Assert.True((await svc.ResolveAsync(newToken!)).Success);
        // GetOrCreate now hands out the NEW token (the rotated-in live grant).
        Assert.Equal(newToken, await svc.GetOrCreateTokenAsync(pid));
    }

    [Fact]
    public async Task Edition_mismatch_is_refused()
    {
        using var db = ScenarioFixture.NewDb();
        var pid = await SeedOneAsync(db);
        var svc = NewService(db);
        var token = await svc.GetOrCreateTokenAsync(pid);

        // The participant moves to a different edition after the link was minted.
        var p = await db.Participants.FindAsync(pid);
        p!.EventId = p.EventId + 999;
        await db.SaveChangesAsync();

        var r = await svc.ResolveAsync(token);
        Assert.False(r.Success);
        Assert.Contains("edition", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deactivated_account_does_not_resolve()
    {
        using var db = ScenarioFixture.NewDb();
        var pid = await SeedOneAsync(db);
        var svc = NewService(db);
        var token = await svc.GetOrCreateTokenAsync(pid);

        var p = await db.Participants.FindAsync(pid);
        p!.IsActive = false;
        await db.SaveChangesAsync();

        var r = await svc.ResolveAsync(token);
        Assert.False(r.Success);
        Assert.Contains("inactive", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildUrl_embeds_the_token_and_only_safe_local_targets()
    {
        using var db = ScenarioFixture.NewDb();
        var svc = NewService(db);

        Assert.Equal("https://hub.example/go/TOK", svc.BuildUrl("https://hub.example/", "TOK"));
        Assert.Equal("https://hub.example/go/TOK?r=%2FTasks", svc.BuildUrl("https://hub.example", "TOK", "/Tasks"));
        // Cross-site / protocol-relative / non-local targets are dropped.
        Assert.Equal("https://hub.example/go/TOK", svc.BuildUrl("https://hub.example", "TOK", "//evil.example.com/phish"));
        Assert.Equal("https://hub.example/go/TOK", svc.BuildUrl("https://hub.example", "TOK", "https://evil.example.com"));
    }
}
