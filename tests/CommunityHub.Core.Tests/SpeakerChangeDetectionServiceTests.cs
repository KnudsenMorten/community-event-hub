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
/// §38e/§58 SPEAKER change-detection engine — 🔴 §1364: RETIRED, PERMANENTLY, IN CODE. CEH is the master for speaker
/// data; the engine had auto-applied Zoho values into CEH (§737.2), which the operator rules out entirely. These tests
/// pin that it reads nothing, writes nothing and enqueues nothing, and that an old queued Zoho→CEH row changes nothing
/// on approve. Differences are reported instead by the speaker gap mail (SpeakerZohoGapReporter).
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

    // ============ §1364: ZOHO→CEH FOR SPEAKERS IS OFF, PERMANENTLY, IN CODE ============

    /// <summary>
    /// 🔴 THE POINT OF §1364: the old switch ON, a REAL Zoho change on a linked speaker — and nothing happens. Operator
    /// 2026-10-20: "we have NO chg from zoho to hub. hub is master !!!". The engine had auto-applied 223 Zoho values.
    /// </summary>
    [Theory]
    [InlineData(SessionSyncDirection.ZohoToCeh)]
    [InlineData(SessionSyncDirection.CehToZoho)]
    public async Task A_real_Zoho_change_writes_nothing_and_enqueues_nothing(SessionSyncDirection dir)
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true, direction: dir);
        await SeedSpeakerAsync(db, "bs-1", "Sam Speaker", "Old Tagline", "Old bio.");
        var pulled = false;
        var clock = new FixedClock(Now);
        var svc = new SpeakerChangeDetectionService(db, NewZoho(), new ZohoOptions(), new FeatureGateService(db),
            clock: clock,
            pullOverride: _ => { pulled = true; return Task.FromResult(Pull(Sp("bs-1", "Samuel Speaker", "New Tagline", "New bio.", "DK"))); },
            queue: new SyncDeltaQueueService(db, clock: clock));

        var r = await svc.RunAsync(EventId);

        Assert.True(SpeakerChangeDetectionService.DisabledInCode);
        Assert.True(r.DirectionInactive);
        Assert.Contains("PERMANENTLY OFF", r.UnavailableReason);
        Assert.False(pulled);                                   // returns before reading Zoho at all
        Assert.Empty(db.SyncDeltas);
        var p = db.SpeakerProfiles.Single();
        Assert.Equal("Old Tagline", p.Tagline);                 // CEH untouched
        Assert.Equal("Old bio.", p.Biography);
        Assert.Null(p.Country);
        Assert.Equal("Old Tagline", p.BackstageTagline);        // not even the snapshot moves
        Assert.Null(p.BackstageChangeCheckedAt);
    }

    /// <summary>An old Zoho→CEH speaker row still in the queue is acknowledged on approve — and CEH is not changed.</summary>
    [Fact]
    public async Task Approving_an_old_ZohoToCeh_speaker_row_changes_nothing_in_CEH()
    {
        using var db = ScenarioFixture.NewDb();
        await EnableFeatureAsync(db, enabled: true);
        var pid = await SeedSpeakerAsync(db, "bs-1", "Sam Speaker", "Old Tagline", "Old bio.");
        var queue = new SyncDeltaQueueService(db, clock: new FixedClock(Now));
        var delta = await queue.EnqueueAsync(new SyncDelta
        {
            EventId = EventId, EntityType = SyncDeltaEntityType.Speaker,
            EntityId = pid.ToString(System.Globalization.CultureInfo.InvariantCulture), EntityLabel = "Sam Speaker",
            Source = SessionSyncDirection.ZohoToCeh, ChangeKind = SyncDeltaChangeKind.Update,
            Changes = SyncDeltaQueueService.BuildSpeakerZohoChanges(
                "Sam Speaker", "Old Tagline", "Old bio.", null, null, null,
                "Samuel Speaker", "New Tagline", "New bio.", "DK", null, null),
        });

        var result = await queue.ApproveAsync(delta.Id, "org@example.test");

        Assert.False(result.Applied);
        var p = db.SpeakerProfiles.Single();
        Assert.Equal("Old Tagline", p.Tagline);
        Assert.Equal("Old bio.", p.Biography);
        Assert.Null(p.Country);
    }
}