using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: the OUTBOUND speaker sync to Zoho Backstage (hub → Backstage).
///
/// The Backstage v3 speakers API is CREATE-ONLY (verified 2026-06-25 — no update
/// endpoint), so the sync CREATES a new speaker or, if one already exists, BLOCKS +
/// emails info@ (never a duplicate). Invariants under test:
///  - HARD GATE: an unselected speaker's request carries Draft (featured=false), never Public;
///  - INACTIVE by default (Enabled=false) → does nothing;
///  - RING-GATED per speaker (feature 'backstage-speaker-sync', off by default) → a
///    speaker whose ring isn't active is held (RingGated), nothing pushed;
///  - existing-in-Backstage → BlockedNeedsManualUpdate + an info@ alert (no duplicate);
///  - no live writer wired (Null) → request built, no Zoho call faked.
///
/// NO real person data — example.test + @expertslive.dk only.
/// </summary>
public sealed class SpeakerBioBackstageSyncScenarioTests
{
    private static IOptions<BackstageSpeakerBioSyncOptions> Opts(bool enabled) =>
        Options.Create(new BackstageSpeakerBioSyncOptions { Enabled = enabled });

    private static SpeakerBioBackstageSyncService NewService(
        Data.CommunityHubDbContext db, IBackstageSpeakerBioApi api, bool enabled, CapturingEmailSender email) =>
        new(db, api, Opts(enabled), email, new FeatureGateService(db), new RingResolver(db));

    // Enable + release the ring-gate feature to Broad so a default-ring speaker passes.
    private static async Task EnableSpeakerSyncAsync(Data.CommunityHubDbContext db, int eventId)
    {
        var settings = new FeatureSettingsService(db, TimeProvider.System);
        await settings.SetEnabledAsync(eventId, "backstage-speaker-sync", true, "org@expertslive.dk");
        await settings.SetReleasedRingAsync(eventId, "backstage-speaker-sync", Ring.Broad, "org@expertslive.dk");
    }

    // ---- HARD GATE: unselected speaker is built as Draft, never Public ------

    [Fact]
    public void BuildRequest_unselected_speaker_is_draft_never_public()
    {
        var profile = new SpeakerProfile
        {
            Tagline = "Cloud engineer",
            Biography = "Builds things in the cloud.",
            SelectedForPublish = false,
        };
        var req = SpeakerBioBackstageSyncService.BuildRequest("speaker.one@example.test", profile);
        Assert.Equal(SpeakerPublishState.Draft, req.PublishState);
        Assert.Equal("speaker.one@example.test", req.IdentityEmail);
        Assert.Equal("Cloud engineer", req.Tagline);
    }

    [Fact]
    public void BuildRequest_publishes_only_when_explicitly_selected()
    {
        var profile = new SpeakerProfile { Biography = "Approved bio.", SelectedForPublish = true };
        var req = SpeakerBioBackstageSyncService.BuildRequest("speaker.one@example.test", profile);
        Assert.Equal(SpeakerPublishState.Public, req.PublishState);
    }

    [Fact]
    public void BuildRequest_maps_accreditation_to_skills()
    {
        var profile = new SpeakerProfile { Accreditation = "Microsoft MVP, Microsoft Regional Director" };
        var req = SpeakerBioBackstageSyncService.BuildRequest("s@example.test", profile);
        Assert.Equal("Microsoft MVP, Microsoft Regional Director", req.Skills);
    }

    [Fact]
    public void SelectedForPublish_defaults_false_on_a_new_profile() =>
        Assert.False(new SpeakerProfile().SelectedForPublish);

    // ---- INACTIVE BY DEFAULT -----------------------------------------------

    [Fact]
    public async Task Sync_is_disabled_by_default_and_does_nothing()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var live = new FakeLiveBackstageSpeakerBioApi();
        await SelectForPublishAsync(db, seed.SpeakerOneId, true);
        var svc = NewService(db, live, enabled: false, new CapturingEmailSender());

        Assert.False(svc.IsEnabled);
        var result = await svc.SyncOneAsync(seed.EventId, seed.SpeakerOneId);

        Assert.Equal(SpeakerBioSyncOutcome.Disabled, result.Outcome);
        Assert.Empty(live.Writes);
    }

    // ---- RING GATE: held until the speaker's ring is active -----------------

    [Fact]
    public async Task Speaker_is_ring_gated_until_the_feature_is_active_for_them()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var live = new FakeLiveBackstageSpeakerBioApi();
        // Sync enabled + a live writer, but the 'backstage-speaker-sync' feature is OFF
        // by default → the speaker is held, nothing pushed.
        var svc = NewService(db, live, enabled: true, new CapturingEmailSender());

        var result = await svc.SyncOneAsync(seed.EventId, seed.SpeakerOneId);

        Assert.Equal(SpeakerBioSyncOutcome.RingGated, result.Outcome);
        Assert.Empty(live.Writes);
    }

    // ---- CREATE in DRAFT (non-public) once enabled + ring-active ------------

    [Fact]
    public async Task New_speaker_is_created_in_draft_non_public_state()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await EnableSpeakerSyncAsync(db, seed.EventId);
        var live = new FakeLiveBackstageSpeakerBioApi();   // Action defaults to Created
        var svc = NewService(db, live, enabled: true, new CapturingEmailSender());

        var result = await svc.SyncOneAsync(seed.EventId, seed.SpeakerOneId);

        Assert.Equal(SpeakerBioSyncOutcome.PushedDraft, result.Outcome);
        var pushed = Assert.Single(live.Writes);
        Assert.Equal(SpeakerPublishState.Draft, pushed.PublishState);
        Assert.Equal(ScenarioSeed.SpeakerOneEmail, pushed.IdentityEmail);
    }

    [Fact]
    public async Task Dry_run_builds_non_public_request_without_calling_zoho()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await EnableSpeakerSyncAsync(db, seed.EventId);
        var live = new FakeLiveBackstageSpeakerBioApi();
        var svc = NewService(db, live, enabled: true, new CapturingEmailSender());

        var result = await svc.SyncOneAsync(seed.EventId, seed.SpeakerOneId, dryRun: true);

        Assert.Equal(SpeakerBioSyncOutcome.BuiltOnly, result.Outcome);
        Assert.Equal(SpeakerPublishState.Draft, result.Request.PublishState);
        Assert.Empty(live.Writes);
    }

    // ---- EXISTING in Backstage → block + email info@ (no duplicate) ---------

    [Fact]
    public async Task Existing_speaker_is_blocked_and_info_is_emailed_not_duplicated()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await EnableSpeakerSyncAsync(db, seed.EventId);
        var live = new FakeLiveBackstageSpeakerBioApi { Action = BackstageSpeakerAction.ExistsBlocked };
        var email = new CapturingEmailSender();
        var svc = NewService(db, live, enabled: true, email);

        var result = await svc.SyncOneAsync(seed.EventId, seed.SpeakerOneId);

        Assert.Equal(SpeakerBioSyncOutcome.BlockedNeedsManualUpdate, result.Outcome);
        var msg = Assert.Single(email.Messages);
        Assert.Equal(SpeakerBioBackstageSyncService.AlertEmail, msg.To);
    }

    // ---- No live writer wired (Null): build only, no faked call ------------

    [Fact]
    public async Task No_live_writer_builds_request_but_makes_no_zoho_call()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var nullApi = new NullBackstageSpeakerBioApi(); // CanWrite = false
        var svc = NewService(db, nullApi, enabled: true, new CapturingEmailSender());

        var result = await svc.SyncOneAsync(seed.EventId, seed.SpeakerOneId);

        Assert.Equal(SpeakerBioSyncOutcome.BuiltOnly, result.Outcome);
        Assert.Equal(SpeakerPublishState.Draft, result.Request.PublishState);
    }

    // ---- SyncAll: NEVER publishes an unselected speaker ---------------------

    [Fact]
    public async Task Sync_all_never_publishes_an_unselected_speaker()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await EnableSpeakerSyncAsync(db, seed.EventId);
        var live = new FakeLiveBackstageSpeakerBioApi();
        var svc = NewService(db, live, enabled: true, new CapturingEmailSender());

        var results = await svc.SyncAllAsync(seed.EventId);

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(SpeakerBioSyncOutcome.PushedDraft, r.Outcome));
        Assert.DoesNotContain(live.Writes, w => w.PublishState == SpeakerPublishState.Public);
    }

    // ---- helpers -----------------------------------------------------------

    private static async Task SelectForPublishAsync(
        Data.CommunityHubDbContext db, int participantId, bool value)
    {
        var prof = await db.SpeakerProfiles.FirstAsync(p => p.ParticipantId == participantId);
        prof.SelectedForPublish = value;
        await db.SaveChangesAsync();
    }

    /// <summary>A live Backstage speaker writer (test double) recording writes.</summary>
    private sealed class FakeLiveBackstageSpeakerBioApi : IBackstageSpeakerBioApi
    {
        public List<SpeakerBioRecord> Writes { get; } = new();
        public BackstageSpeakerAction Action { get; set; } = BackstageSpeakerAction.Created;
        public bool CanWrite => true;
        public Task<BackstageSpeakerUpsertResult> UpsertSpeakerBioAsync(SpeakerBioRecord record, CancellationToken ct)
        {
            Writes.Add(record);
            return Task.FromResult(new BackstageSpeakerUpsertResult(Action, "fake-speaker-id"));
        }
    }
}
