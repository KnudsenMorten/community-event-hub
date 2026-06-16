using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: the OUTBOUND speaker-bio sync to Zoho Backstage (hub -> Backstage).
///
/// THE LINEUP IS NOT SELECTED YET, so the overriding rule under test is: NO
/// speaker may be made PUBLIC in Backstage. The sync is gated on a per-speaker
/// <see cref="SpeakerProfile.SelectedForPublish"/> flag that DEFAULTS FALSE — a
/// speaker is only ever pushed to the PUBLIC/visible state when that flag is
/// explicitly true; otherwise the bio goes out DRAFT/hidden only.
///
/// This proves the contract:
///  - HARD GATE: with the flag false (the default for everyone) the built request
///    carries Draft, never Public — an unselected speaker is never published;
///  - the gate flips to Public ONLY when the organizer explicitly approves;
///  - the sync is INACTIVE by default (Enabled=false) — it does nothing until an
///    operator opts in;
///  - the 1-speaker test path pushes exactly one speaker in DRAFT/non-public mode
///    (and a dry-run builds the request asserting the not-public state) — NO test
///    makes any speaker public against a live writer;
///  - with no live writer wired (the Null default) the gated request is still
///    built, but no Zoho call is faked.
///
/// NO real customer / person data — example.test + @expertslive.dk only.
/// </summary>
public sealed class SpeakerBioBackstageSyncScenarioTests
{
    private static IOptions<BackstageSpeakerBioSyncOptions> Opts(bool enabled) =>
        Options.Create(new BackstageSpeakerBioSyncOptions { Enabled = enabled });

    // ---- HARD GATE: unselected speaker is built as Draft, never Public ------

    [Fact]
    public void BuildRequest_unselected_speaker_is_draft_never_public()
    {
        var profile = new SpeakerProfile
        {
            Tagline = "Cloud engineer",
            Biography = "Builds things in the cloud.",
            SelectedForPublish = false, // the default for everyone today
        };

        var req = SpeakerBioBackstageSyncService.BuildRequest("speaker.one@example.test", profile);

        Assert.Equal(SpeakerPublishState.Draft, req.PublishState);
        Assert.NotEqual(SpeakerPublishState.Public, req.PublishState);
        Assert.Equal("speaker.one@example.test", req.IdentityEmail);
        Assert.Equal("Cloud engineer", req.Tagline);
        Assert.Equal("Builds things in the cloud.", req.Biography);
    }

    [Fact]
    public void BuildRequest_publishes_only_when_explicitly_selected()
    {
        var profile = new SpeakerProfile { Biography = "Approved bio.", SelectedForPublish = true };

        var req = SpeakerBioBackstageSyncService.BuildRequest("speaker.one@example.test", profile);

        Assert.Equal(SpeakerPublishState.Public, req.PublishState);
    }

    [Fact]
    public void SelectedForPublish_defaults_false_on_a_new_profile()
    {
        // The hard-gate flag must be off the moment a profile is created.
        Assert.False(new SpeakerProfile().SelectedForPublish);
    }

    // ---- INACTIVE BY DEFAULT -----------------------------------------------

    [Fact]
    public async Task Sync_is_disabled_by_default_and_does_nothing()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var live = new FakeLiveBackstageSpeakerBioApi();
        // Even with a live writer present AND a speaker selected, a disabled sync
        // must make NO call.
        await SelectForPublishAsync(db, seed.SpeakerOneId, true);
        var svc = new SpeakerBioBackstageSyncService(db, live, Opts(enabled: false));

        Assert.False(svc.IsEnabled);
        var result = await svc.SyncOneAsync(seed.EventId, seed.SpeakerOneId);

        Assert.Equal(SpeakerBioSyncOutcome.Disabled, result.Outcome);
        Assert.Empty(live.Writes); // nothing pushed, nothing published
    }

    // ---- 1-SPEAKER push in DRAFT (non-public) ------------------------------

    [Fact]
    public async Task One_speaker_push_carries_draft_non_public_state()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var live = new FakeLiveBackstageSpeakerBioApi();
        // Sync ENABLED, but the speaker is NOT selected (the default) -> Draft.
        var svc = new SpeakerBioBackstageSyncService(db, live, Opts(enabled: true));

        var result = await svc.SyncOneAsync(seed.EventId, seed.SpeakerOneId);

        // Exactly one speaker pushed, in DRAFT — never public.
        Assert.Equal(SpeakerBioSyncOutcome.PushedDraft, result.Outcome);
        var pushed = Assert.Single(live.Writes);
        Assert.Equal(SpeakerPublishState.Draft, pushed.PublishState);
        Assert.NotEqual(SpeakerPublishState.Public, pushed.PublishState);
        Assert.Equal(ScenarioSeed.SpeakerOneEmail, pushed.IdentityEmail);
    }

    [Fact]
    public async Task One_speaker_dry_run_builds_non_public_request_without_calling_zoho()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var live = new FakeLiveBackstageSpeakerBioApi();
        var svc = new SpeakerBioBackstageSyncService(db, live, Opts(enabled: true));

        var result = await svc.SyncOneAsync(seed.EventId, seed.SpeakerOneId, dryRun: true);

        // Dry-run builds the request asserting the draft/non-public state, but
        // makes NO live call even though a writer is wired.
        Assert.Equal(SpeakerBioSyncOutcome.BuiltOnly, result.Outcome);
        Assert.Equal(SpeakerPublishState.Draft, result.Request.PublishState);
        Assert.Empty(live.Writes);
    }

    // ---- No live writer wired (Null default): build only, no faked call ----

    [Fact]
    public async Task No_live_writer_builds_request_but_makes_no_zoho_call()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var nullApi = new NullBackstageSpeakerBioApi(); // CanWrite = false (the default)
        var svc = new SpeakerBioBackstageSyncService(db, nullApi, Opts(enabled: true));

        var result = await svc.SyncOneAsync(seed.EventId, seed.SpeakerOneId);

        Assert.Equal(SpeakerBioSyncOutcome.BuiltOnly, result.Outcome);
        Assert.Equal(SpeakerPublishState.Draft, result.Request.PublishState);
        // The Null writer throws if ever called -> proves no call was made.
    }

    // ---- SyncAll: NEVER publishes an unselected speaker ---------------------

    [Fact]
    public async Task Sync_all_never_publishes_an_unselected_speaker()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var live = new FakeLiveBackstageSpeakerBioApi();
        var svc = new SpeakerBioBackstageSyncService(db, live, Opts(enabled: true));

        // The seed has speaker profiles for the masterclass speaker + speaker one,
        // all with SelectedForPublish defaulting false (lineup not selected).
        var results = await svc.SyncAllAsync(seed.EventId);

        Assert.NotEmpty(results);
        // Every push went out as Draft; NOTHING was made public.
        Assert.All(results, r => Assert.Equal(SpeakerBioSyncOutcome.PushedDraft, r.Outcome));
        Assert.All(live.Writes, w => Assert.Equal(SpeakerPublishState.Draft, w.PublishState));
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

    /// <summary>A live Backstage speaker-bio writer (test double) recording writes.</summary>
    private sealed class FakeLiveBackstageSpeakerBioApi : IBackstageSpeakerBioApi
    {
        public List<SpeakerBioRecord> Writes { get; } = new();
        public bool CanWrite => true;
        public Task UpsertSpeakerBioAsync(SpeakerBioRecord record, CancellationToken ct)
        {
            // A live writer would honour the gate; this double records what it was
            // asked to write so a test can assert the state it carried.
            Writes.Add(record);
            return Task.CompletedTask;
        }
    }
}
