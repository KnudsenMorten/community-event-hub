using System.Net.Http;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §38e/§58 SPEAKER change-detection engine (the speaker analogue of §38e sessions): a real
/// Backstage name/tagline/bio/country/social change on a LINKED speaker ENQUEUES a Speaker
/// ZohoToCeh Update delta (never emails inline, never auto-applies). First-populate seeds the
/// Backstage* baseline silently; the engine is inert unless the SPEAKER sync direction is
/// stage 3; no source ⇒ graceful no-op; nothing is ever deleted. EF in-memory + a canned pull.
/// </summary>
public sealed class SpeakerChangeDetectionServiceTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2026, 6, 26, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static ZohoClient NewZoho() =>
        new(new HttpClient(), new ZohoOptions()); // never called (pull is overridden)

    private static SpeakerChangeDetectionService NewService(
        CommunityHubDbContext db, BackstageSpeakersResult pull)
    {
        var clock = new FixedClock(Now);
        var queue = new SyncDeltaQueueService(db, clock: clock);
        return new(db, NewZoho(), new ZohoOptions(), new FeatureGateService(db),
            clock: clock,
            pullOverride: _ => Task.FromResult(pull),
            queue: queue);
    }

    private static async Task<int> SeedSpeakerAsync(
        CommunityHubDbContext db, string backstageId,
        string? storedName, string? storedTagline, string? storedBio,
        string? storedCountry = null, string? storedLinkedIn = null, string? storedTwitter = null)
    {
        var p = new Participant
        {
            EventId = EventId, Email = $"{backstageId}@x.dk", FullName = "Sam Speaker",
            Role = ParticipantRole.Speaker,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        var profile = new SpeakerProfile
        {
            EventId = EventId,
            ParticipantId = p.Id,
            BackstageSpeakerId = backstageId,
            BackstageName = storedName,
            BackstageTagline = storedTagline,
            BackstageBio = storedBio,
            BackstageCountry = storedCountry,
            BackstageLinkedIn = storedLinkedIn,
            BackstageTwitter = storedTwitter,
            // CEH-owned bio fields seeded to the stored baseline so an apply-test can observe a diff.
            Tagline = storedTagline,
            Biography = storedBio,
        };
        db.SpeakerProfiles.Add(profile);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static async Task EnableFeatureAsync(
        CommunityHubDbContext db, bool enabled,
        SessionSyncDirection direction = SessionSyncDirection.ZohoToCeh)
    {
        db.Events.Add(new Event
        {
            Id = EventId,
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        db.FeatureSettings.Add(new FeatureSetting
        {
            EventId = EventId,
            FeatureKey = SpeakerChangeDetectionService.FeatureKey,
            Enabled = enabled,
        });
        db.SessionSourceSettings.Add(new SessionSourceSetting
        {
            EventId = EventId,
            Source = SessionSourceKinds.Default,
            SpeakerSyncDirection = direction,
        });
        await db.SaveChangesAsync();
    }

    private static BackstageSpeakersResult Pull(params BackstageSpeaker[] speakers) =>
        BackstageSpeakersResult.Available(speakers);

    private static BackstageSpeaker Sp(
        string id, string? name, string? tagline, string? bio,
        string? country = null, string? linkedIn = null, string? twitter = null) =>
        new(id, name, tagline, bio, country, linkedIn, twitter);

    // -------------------------------------------------------------------------

    [Fact]
    public async Task First_populate_seeds_silently_and_never_enqueues()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true);
        // Nothing stored yet (all Backstage* null) ⇒ first populate.
        await SeedSpeakerAsync(db, "bs-1", null, null, null);

        var svc = NewService(db, Pull(
            Sp("bs-1", "Sam Speaker", "Cloud Architect", "A bio.", "DK", "https://li/sam", "@sam")));

        var r = await svc.RunAsync(EventId);

        Assert.True(r.SourceAvailable);
        Assert.Equal(1, r.Matched);
        Assert.Equal(1, r.Seeded);
        Assert.Equal(0, r.Changed);
        Assert.Equal(0, r.Enqueued);
        Assert.Empty(db.SyncDeltas);          // seed silently — no queue item

        var p = db.SpeakerProfiles.Single();
        Assert.Equal("Cloud Architect", p.BackstageTagline);
        Assert.Equal("A bio.", p.BackstageBio);
        Assert.Equal("DK", p.BackstageCountry);
        Assert.NotNull(p.BackstageChangeCheckedAt);
    }

    [Fact]
    public async Task Real_bio_or_tagline_change_enqueues_a_speaker_zohotoceh_update()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true);
        var pid = await SeedSpeakerAsync(db, "bs-1", "Sam Speaker", "Old Tagline", "Old bio.");

        var svc = NewService(db, Pull(
            Sp("bs-1", "Sam Speaker", "New Tagline", "New bio.")));   // tagline + bio changed

        var r = await svc.RunAsync(EventId);

        Assert.Equal(1, r.Changed);
        Assert.Equal(1, r.Enqueued);

        // Stored baseline UNTOUCHED until an operator approves (old→new diff must survive).
        var p = db.SpeakerProfiles.Single();
        Assert.Equal("Old Tagline", p.BackstageTagline);
        Assert.Equal("Old bio.", p.BackstageBio);

        var delta = Assert.Single(db.SyncDeltas);
        Assert.Equal(SyncDeltaStatus.Pending, delta.Status);
        Assert.Equal(SyncDeltaEntityType.Speaker, delta.EntityType);
        Assert.Equal(SyncDeltaChangeKind.Update, delta.ChangeKind);
        Assert.Equal(SessionSyncDirection.ZohoToCeh, delta.Source);
        Assert.Equal(pid.ToString(), delta.EntityId);
        Assert.Contains(delta.Changes, c => c.Field == SyncDeltaQueueService.FieldTagline && c.NewValue == "New Tagline");
        Assert.Contains(delta.Changes, c => c.Field == SyncDeltaQueueService.FieldBio && c.NewValue == "New bio.");
    }

    [Fact]
    public async Task No_change_does_not_enqueue_but_stamps_check_time()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true);
        await SeedSpeakerAsync(db, "bs-1", "Sam Speaker", "Tagline", "Bio.", "DK");

        var svc = NewService(db, Pull(
            Sp("bs-1", "Sam Speaker", "Tagline", "Bio.", "DK")));   // identical

        var r = await svc.RunAsync(EventId);

        Assert.Equal(0, r.Changed);
        Assert.Empty(db.SyncDeltas);
        Assert.NotNull(db.SpeakerProfiles.Single().BackstageChangeCheckedAt);
    }

    [Fact]
    public async Task Kill_switch_off_means_no_enqueue_even_on_change()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: false); // killed
        await SeedSpeakerAsync(db, "bs-1", "Sam", "Old", "Old bio.");

        var svc = NewService(db, Pull(Sp("bs-1", "Sam", "New", "New bio.")));

        var r = await svc.RunAsync(EventId);

        // The change is still detected, but the kill switch suppresses enqueue.
        Assert.Equal(1, r.Changed);
        Assert.Equal(0, r.Enqueued);
        Assert.Empty(db.SyncDeltas);
    }

    [Theory]
    [InlineData(SessionSyncDirection.SessionizeToCeh)] // stage 1 = default
    [InlineData(SessionSyncDirection.CehToZoho)]        // stage 2
    public async Task Engine_is_inert_unless_speaker_direction_is_stage3(SessionSyncDirection dir)
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true, direction: dir);
        await SeedSpeakerAsync(db, "bs-1", "Sam", "Old", "Old bio.");

        var svc = NewService(db, Pull(Sp("bs-1", "Sam", "New", "New bio.")));

        var r = await svc.RunAsync(EventId);

        Assert.True(r.DirectionInactive);
        Assert.False(r.SourceAvailable);
        Assert.Contains($"stage {(int)dir}", r.UnavailableReason);
        Assert.Equal(0, r.Matched);
        Assert.Equal(0, r.Changed);
        Assert.Empty(db.SyncDeltas);
        // Nothing written back — stored baseline untouched.
        Assert.Equal("Old", db.SpeakerProfiles.Single().BackstageTagline);
    }

    [Fact]
    public async Task Default_edition_with_no_setting_row_is_inert()
    {
        // No SessionSourceSetting row at all ⇒ speaker direction defaults to stage 1 ⇒ inert.
        using var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId,
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        db.FeatureSettings.Add(new FeatureSetting
        {
            EventId = EventId,
            FeatureKey = SpeakerChangeDetectionService.FeatureKey,
            Enabled = true,
        });
        await db.SaveChangesAsync();
        await SeedSpeakerAsync(db, "bs-1", "Sam", "Old", "Old bio.");

        var svc = NewService(db, Pull(Sp("bs-1", "Sam", "New", "New bio.")));

        var r = await svc.RunAsync(EventId);

        Assert.True(r.DirectionInactive);
        Assert.Contains("stage 1", r.UnavailableReason);
        Assert.Empty(db.SyncDeltas);
        Assert.Equal("Old", db.SpeakerProfiles.Single().BackstageTagline); // untouched
    }

    [Fact]
    public async Task Unavailable_source_is_a_graceful_no_op()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true);
        await SeedSpeakerAsync(db, "bs-1", null, null, null);

        var svc = NewService(db, BackstageSpeakersResult.Unavailable("speaker scope not granted"));

        var r = await svc.RunAsync(EventId);

        Assert.False(r.SourceAvailable);
        Assert.Equal("speaker scope not granted", r.UnavailableReason);
        Assert.Empty(db.SyncDeltas);
        // No write happened — not even the first-populate seed.
        Assert.Null(db.SpeakerProfiles.Single().BackstageChangeCheckedAt);
    }

    [Fact]
    public async Task Zoho_speaker_not_linked_to_ceh_is_skipped_never_created_or_deleted()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true);
        // CEH has ONE linked speaker (bs-1); Zoho returns a DIFFERENT speaker id (bs-999).
        await SeedSpeakerAsync(db, "bs-1", "Sam", "Tagline", "Bio.");

        var svc = NewService(db, Pull(Sp("bs-999", "Stranger", "x", "y")));

        var r = await svc.RunAsync(EventId);

        Assert.Equal(0, r.Matched);
        Assert.Equal(1, r.Unmatched);
        Assert.Empty(db.SyncDeltas);
        // The CEH speaker still exists and is untouched (never auto-deleted).
        Assert.Single(db.SpeakerProfiles);
        Assert.Equal("Tagline", db.SpeakerProfiles.Single().BackstageTagline);
    }

    [Fact]
    public async Task After_first_populate_a_later_change_enqueues()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true);
        await SeedSpeakerAsync(db, "bs-1", null, null, null); // unseeded

        // PASS 1: first populate — silent, seeds baseline, NO queue item.
        var svc1 = NewService(db, Pull(Sp("bs-1", "Sam", "Tagline", "Bio.")));
        var r1 = await svc1.RunAsync(EventId);
        Assert.Equal(1, r1.Seeded);
        Assert.Empty(db.SyncDeltas);
        Assert.Equal("Tagline", db.SpeakerProfiles.Single().BackstageTagline);

        // PASS 2: bio moves → a real CHANGE → ENQUEUED.
        var svc2 = NewService(db, Pull(Sp("bs-1", "Sam", "Tagline", "Updated bio.")));
        var r2 = await svc2.RunAsync(EventId);

        Assert.Equal(0, r2.Seeded);
        Assert.Equal(1, r2.Changed);
        Assert.Equal(1, r2.Enqueued);
        Assert.Single(db.SyncDeltas);
        // Baseline kept at the seeded value until approval.
        Assert.Equal("Bio.", db.SpeakerProfiles.Single().BackstageBio);
    }
}
