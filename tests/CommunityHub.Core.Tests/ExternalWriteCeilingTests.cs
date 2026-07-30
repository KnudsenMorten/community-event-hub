using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §612 — the environment config is a CEILING on external writes, not a default an organizer can
/// raise. Operator 2026-07-28: <i>"in dev we want to enforce BLOCK WRITES !!!"</i>.
///
/// <para><b>The hole this closes.</b> The guard used to resolve
/// <c>over ?? _options.AllowExternalWrites</c>, so pressing "Allow writes" on the DEV Settings page
/// would have enabled REAL writes to Zoho Backstage, e-conomic, LinkedIn and SharePoint from DEV —
/// one click, silently. His words for the consequence: <i>"as i will then have 2 set of everything
/// in zoho"</i>. Duplicate Zoho records are manual to undo, and §326bx showed a write can e-mail
/// real people (19 speakers invited prematurely).</para>
/// </summary>
public class ExternalWriteCeilingTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"xw-{Guid.NewGuid():N}").Options);

    private static async Task SeedAsync(CommunityHubDbContext db, bool? overrideValue)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
        });
        if (overrideValue is bool v)
        {
            db.FeatureSettings.Add(new FeatureSetting
            {
                EventId = EventId, FeatureKey = ExternalWriteGuard.OverrideKey, Enabled = v,
            });
        }
        await db.SaveChangesAsync();
    }

    private static ExternalWriteGuard Guard(CommunityHubDbContext db, bool envAllows) =>
        new(db, new ExternalWriteOptions { AllowExternalWrites = envAllows }, null);

    // ---------- DEV: blocked means blocked ----------

    [Theory]
    [InlineData(null)]   // no override at all
    [InlineData(false)]  // organizer explicitly blocked
    [InlineData(true)]   // organizer pressed "Allow writes" — THE case that used to open the hole
    public async Task A_blocked_environment_can_never_be_unblocked_by_an_override(bool? over)
    {
        using var db = NewDb();
        await SeedAsync(db, over);

        Assert.False(await Guard(db, envAllows: false).IsAllowedAsync());
    }

    [Fact]
    public async Task The_allow_call_also_refuses_on_a_blocked_environment()
    {
        // The per-operation gate every integration calls must agree with IsAllowedAsync — a guard
        // that says "blocked" while AllowAsync says "go ahead" would be worse than no guard.
        using var db = NewDb();
        await SeedAsync(db, overrideValue: true);

        Assert.False(await Guard(db, envAllows: false).AllowAsync("Zoho", "CreateSession"));
    }

    // ---------- PROD: the override still RESTRICTS ----------

    [Fact]
    public async Task An_allowed_environment_still_writes_when_there_is_no_override()
    {
        using var db = NewDb();
        await SeedAsync(db, overrideValue: null);

        Assert.True(await Guard(db, envAllows: true).IsAllowedAsync());
    }

    [Fact]
    public async Task An_organizer_can_still_STOP_writes_on_an_allowed_environment()
    {
        // The control must keep working in the restrictive direction — that is its whole purpose in
        // PROD ("stop everything now"). Only WIDENING was removed.
        using var db = NewDb();
        await SeedAsync(db, overrideValue: false);

        Assert.False(await Guard(db, envAllows: true).IsAllowedAsync());
    }

    [Fact]
    public async Task An_allowed_environment_with_an_allow_override_still_writes()
    {
        using var db = NewDb();
        await SeedAsync(db, overrideValue: true);

        Assert.True(await Guard(db, envAllows: true).IsAllowedAsync());
    }

    // ---------- the banner must not promise the old behaviour ----------

    [Fact]
    public void The_blocked_banner_states_that_no_override_can_enable_writes()
    {
        var blocked = ExternalWriteGuard.StartupBanner(envDefault: false);

        Assert.Contains("ENFORCED", blocked);
        // It used to end "unless an organizer explicitly overrides it on the Settings page" — the
        // exact promise §612 removed. A banner that describes the old rule is a trap.
        Assert.DoesNotContain("unless an organizer", blocked);
    }
}
