using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The §23 RELEASE-RING resolver + the ring-aware feature gate. The EF Core
/// InMemory provider runs the real DbContext mapping + LINQ. These prove:
///   - the effective-ring rule: contact ring SUPERSEDES company ring, company is
///     the default for its contacts, and an unassigned resource defaults to Ring3
///     (broad);
///   - a feature is active for a resource iff it is enabled (not killed) AND the
///     resource's effective ring ≤ the feature's released-to ring;
///   - the resolver/gate give the SAME result regardless of environment (the
///     resolver is environment-agnostic — a person's ring is their access level
///     everywhere).
/// </summary>
public sealed class RingResolverTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"ring-{Guid.NewGuid():N}")
            .Options);

    private static FeatureSettingsService Settings(CommunityHubDbContext db) =>
        new(db, new FixedClock(Now));

    // ---------- pure effective-ring rule ------------------------------------

    [Fact]
    public void Effective_defaults_to_broad_when_nothing_is_assigned()
    {
        // No contact ring, no company ring ⇒ Ring3 (broad).
        Assert.Equal(Ring.Broad, Rings.Effective(contactRing: null, companyRing: null));
        Assert.Equal(Ring.Broad, Rings.Effective(resourceRing: null));
    }

    [Fact]
    public void Contact_ring_supersedes_company_ring()
    {
        // The contact's own ring wins over the company default.
        Assert.Equal(Ring.Ring1, Rings.Effective(contactRing: Ring.Ring1, companyRing: Ring.Ring2));
        // No contact ring ⇒ the company default applies.
        Assert.Equal(Ring.Ring2, Rings.Effective(contactRing: null, companyRing: Ring.Ring2));
    }

    [Fact]
    public void EffectiveForContact_inherits_company_when_contact_is_on_default()
    {
        // A contact left on the platform default (Broad) inherits an earlier company ring…
        Assert.Equal(Ring.Ring1, RingResolver.EffectiveForContact(Ring.Broad, Ring.Ring1));
        // …but an explicitly-narrowed contact always wins over the company default.
        Assert.Equal(Ring.Ring0, RingResolver.EffectiveForContact(Ring.Ring0, Ring.Ring2));
        // No company row ⇒ the contact's own ring (here the default Broad).
        Assert.Equal(Ring.Broad, RingResolver.EffectiveForContact(Ring.Broad, null));
    }

    [Fact]
    public void IsActiveForRing_is_true_only_when_effective_ring_is_at_or_below_released()
    {
        Assert.True(Rings.IsActiveForRing(Ring.Ring0, Ring.Ring1));   // earlier sees it
        Assert.True(Rings.IsActiveForRing(Ring.Ring1, Ring.Ring1));   // exactly at the ring
        Assert.False(Rings.IsActiveForRing(Ring.Broad, Ring.Ring1));  // later (broad) does NOT
        Assert.True(Rings.IsActiveForRing(Ring.Broad, Ring.Broad));   // broad feature reaches all
    }

    // ---------- DB-backed resolver ------------------------------------------

    private static async Task<int> SeedSponsorContactAsync(
        CommunityHubDbContext db, string companyId, Ring contactRing)
    {
        var p = new Participant
        {
            EventId = EventId, Email = $"c{Guid.NewGuid():N}@example.test",
            FullName = "Sponsor Contact", Role = ParticipantRole.Sponsor,
            SponsorCompanyId = companyId, Ring = contactRing,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static async Task SeedCompanyAsync(
        CommunityHubDbContext db, string companyId, Ring companyRing)
    {
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = companyId, Ring = companyRing,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Resolver_unassigned_participant_is_broad()
    {
        using var db = NewDb();
        var p = new Participant
        {
            EventId = EventId, Email = "spk@example.test", FullName = "Spk",
            Role = ParticipantRole.Speaker, // Ring defaults to Broad
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        Assert.Equal(Ring.Broad, await new RingResolver(db).GetEffectiveRingAsync(p.Id));
    }

    [Fact]
    public async Task Resolver_sponsor_contact_inherits_company_default_then_own_ring_wins()
    {
        using var db = NewDb();
        await SeedCompanyAsync(db, "co-1", Ring.Ring1);
        // Contact on the platform default inherits the company's earlier ring…
        var inheriting = await SeedSponsorContactAsync(db, "co-1", Ring.Broad);
        Assert.Equal(Ring.Ring1, await new RingResolver(db).GetEffectiveRingAsync(inheriting));

        // …a contact with its own (earlier) ring supersedes the company default.
        var narrowed = await SeedSponsorContactAsync(db, "co-1", Ring.Ring0);
        Assert.Equal(Ring.Ring0, await new RingResolver(db).GetEffectiveRingAsync(narrowed));
    }

    [Fact]
    public async Task Resolver_speaker_uses_own_ring_only_no_company_default()
    {
        using var db = NewDb();
        var p = new Participant
        {
            EventId = EventId, Email = "spk2@example.test", FullName = "Spk2",
            Role = ParticipantRole.Speaker, Ring = Ring.Ring2,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        Assert.Equal(Ring.Ring2, await new RingResolver(db).GetEffectiveRingAsync(p.Id));
    }

    [Fact]
    public async Task Resolver_unknown_participant_fails_open_to_broad()
    {
        using var db = NewDb();
        Assert.Equal(Ring.Broad, await new RingResolver(db).GetEffectiveRingAsync(99999));
    }

    // ---------- ring-aware feature gate -------------------------------------

    [Fact]
    public async Task Feature_active_iff_effective_ring_le_released_ring()
    {
        using var db = NewDb();
        var settings = Settings(db);
        var gate = new FeatureGateService(db);

        // Enable an advanced feature and release it only to ring 1.
        await settings.SetEnabledAsync(EventId, "surveys", true, null);
        await settings.SetReleasedRingAsync(EventId, "surveys", Ring.Ring1, null);

        Assert.True(await gate.IsFeatureActiveForRingAsync("surveys", EventId, Ring.Ring0));
        Assert.True(await gate.IsFeatureActiveForRingAsync("surveys", EventId, Ring.Ring1));
        Assert.False(await gate.IsFeatureActiveForRingAsync("surveys", EventId, Ring.Ring2));
        Assert.False(await gate.IsFeatureActiveForRingAsync("surveys", EventId, Ring.Broad));
    }

    [Fact]
    public async Task A_killed_feature_is_never_active_even_for_ring0()
    {
        using var db = NewDb();
        var settings = Settings(db);
        var gate = new FeatureGateService(db);

        // Released to everyone (broad) BUT disabled — the kill switch wins.
        await settings.SetEnabledAsync(EventId, "surveys", false, null);
        await settings.SetReleasedRingAsync(EventId, "surveys", Ring.Broad, null);

        Assert.False(await gate.IsFeatureActiveForRingAsync("surveys", EventId, Ring.Ring0));
        Assert.False(await gate.IsFeatureActiveForRingAsync("surveys", EventId, Ring.Broad));
    }

    [Fact]
    public async Task Existing_feature_defaults_to_ring1_controlled_rollout()
    {
        using var db = NewDb();
        var settings = Settings(db);
        var gate = new FeatureGateService(db);

        // §23a (operator 2026-06-20): an EXISTING feature now starts at Ring1, so once
        // enabled a ring0/ring1 tester sees it but a Broad resource does NOT (until an
        // organizer promotes it to Broad). Controlled-rollout posture before go-live.
        await settings.SetEnabledAsync(EventId, "surveys", true, null);
        Assert.Equal(Ring.Ring1, await gate.GetReleasedRingAsync("surveys", EventId));
        Assert.True(await gate.IsFeatureActiveForRingAsync("surveys", EventId, Ring.Ring1));
        Assert.True(await gate.IsFeatureActiveForRingAsync("surveys", EventId, Ring.Ring0));
        Assert.False(await gate.IsFeatureActiveForRingAsync("surveys", EventId, Ring.Broad));

        // An advanced feature defaults OFF ⇒ inert for everyone until enabled.
        Assert.False(await gate.IsFeatureActiveForRingAsync("backstage-sync", EventId, Ring.Ring0));
    }

    // ---------- scheduler / per-resource skip (§23) -------------------------

    [Fact]
    public async Task Scheduler_skips_a_resource_whose_effective_ring_is_out_of_rollout()
    {
        // The exact decision the scheduler/job loop makes per resource (e.g. the
        // SpeakerQuestionDigest send loop): resolve the resource's effective ring,
        // then ask the ring-aware gate. An out-of-ring resource is skipped.
        using var db = NewDb();
        var settings = Settings(db);
        var gate = new FeatureGateService(db);

        // 'digest-emails' is an EMAIL feature → defaults to Ring1 (RULE 2). Enable it.
        await settings.SetEnabledAsync(EventId, "digest-emails", true, null);
        Assert.Equal(Ring.Ring1, await gate.GetReleasedRingAsync("digest-emails", EventId));

        // A ring-1 speaker is IN the rollout → processed.
        var inRing = new Participant
        {
            EventId = EventId, Email = "in@example.test", FullName = "In",
            Role = ParticipantRole.Speaker, Ring = Ring.Ring1,
        };
        // A broad (ring-3) speaker is OUT of the ring-1 rollout → skipped.
        var outRing = new Participant
        {
            EventId = EventId, Email = "out@example.test", FullName = "Out",
            Role = ParticipantRole.Speaker, Ring = Ring.Broad,
        };
        db.Participants.AddRange(inRing, outRing);
        await db.SaveChangesAsync();

        var rings = new RingResolver(db);
        Assert.True(await gate.IsFeatureActiveForParticipantAsync(
            "digest-emails", EventId, inRing.Id, rings));
        Assert.False(await gate.IsFeatureActiveForParticipantAsync(
            "digest-emails", EventId, outRing.Id, rings));
    }

    // ---------- ResolveDelivery: kill switch + redirect only (§23) ----------
    // The Email:OnlySendTo allowlist was REMOVED (rings-only, operator 2026-06-19):
    // ResolveDelivery now decides allow/deny SOLELY on the kill switch, and still
    // computes the actual recipient via RedirectAllTo. The per-recipient ring gate
    // is applied separately by the sender (see the ring tests above).

    [Fact]
    public void ResolveDelivery_allows_when_kill_switch_off_and_drops_when_on()
    {
        var live = new CommunityHub.Core.Email.EmailOptions
        {
            RedirectAllTo = string.Empty,
            KillSwitch = false,
        };

        // Kill switch off ⇒ allowed, actual recipient is the real address.
        var (toOk, okAllowed) = CommunityHub.Core.Email.BrevoEmailSender
            .ResolveDelivery(live, "anyone@example.test");
        Assert.True(okAllowed);
        Assert.Equal("anyone@example.test", toOk);

        // Kill switch on ⇒ dropped, regardless of address.
        var killed = new CommunityHub.Core.Email.EmailOptions { KillSwitch = true };
        var (_, killedAllowed) = CommunityHub.Core.Email.BrevoEmailSender
            .ResolveDelivery(killed, "anyone@example.test");
        Assert.False(killedAllowed);
    }

    [Fact]
    public void ResolveDelivery_redirect_wins_as_the_actual_recipient()
    {
        // RedirectAllTo still wins as the actual recipient (test-mode redirect intact).
        var redirect = new CommunityHub.Core.Email.EmailOptions
        {
            RedirectAllTo = "redir@example.test",
            KillSwitch = false,
        };
        var (redirTo, redirAllowed) = CommunityHub.Core.Email.BrevoEmailSender
            .ResolveDelivery(redirect, "someone-else@example.test");
        Assert.Equal("redir@example.test", redirTo);
        Assert.True(redirAllowed);
    }

    [Fact]
    public async Task Same_result_in_dev_and_prod_the_resolver_is_environment_agnostic()
    {
        // Two independent contexts standing in for "dev" and "prod": identical
        // feature + ring definitions must give identical gate results. The resolver
        // has no environment input, so the only thing that can differ is the data —
        // and we seed it identically.
        async Task<bool> EvaluateAsync(string dbName)
        {
            using var db = new CommunityHubDbContext(
                new DbContextOptionsBuilder<CommunityHubDbContext>()
                    .UseInMemoryDatabase(dbName).Options);
            var settings = new FeatureSettingsService(db, new FixedClock(Now));
            await settings.SetEnabledAsync(EventId, "surveys", true, null);
            await settings.SetReleasedRingAsync(EventId, "surveys", Ring.Ring1, null);

            var p = new Participant
            {
                EventId = EventId, Email = "u@example.test", FullName = "U",
                Role = ParticipantRole.Speaker, Ring = Ring.Ring1,
            };
            db.Participants.Add(p);
            await db.SaveChangesAsync();

            var gate = new FeatureGateService(db);
            return await gate.IsFeatureActiveForParticipantAsync(
                "surveys", EventId, p.Id, new RingResolver(db));
        }

        var dev = await EvaluateAsync($"dev-{Guid.NewGuid():N}");
        var prod = await EvaluateAsync($"prod-{Guid.NewGuid():N}");
        Assert.True(dev);
        Assert.Equal(dev, prod);
    }
}
