using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The pluggable session-source plumbing (REQUIREMENTS §6): per-edition setting
/// (default Sessionize, unknown falls back), and the resolver that picks the
/// active <see cref="ISessionSource"/> by key with a Sessionize fallback.
/// </summary>
public class SessionSourceTests
{
    /// <summary>
    /// §551 — the service now writes an audit entry on every stage change, so the tests use the
    /// REAL <see cref="AuditTrailService"/> against the same in-memory context. That lets a test
    /// assert what actually landed on the trail rather than that a mock was called.
    /// </summary>
    private static SessionSourceSettingsService NewSettings(CommunityHubDbContext db) =>
        new(db, new AuditTrailService(db, TimeProvider.System));

    private sealed class FakeSource : ISessionSource
    {
        public FakeSource(string key, bool available = true) { Key = key; IsAvailable = available; }
        public string Key { get; }
        public bool IsAvailable { get; }
        public Task<SessionSourceResult> FetchSessionsAsync(
            int eventId, IReadOnlyList<SessionizeSpeaker> importedSpeakers,
            CancellationToken ct = default) =>
            Task.FromResult(new SessionSourceResult(
                Array.Empty<SessionizeSession>(), importedSpeakers,
                Array.Empty<string>(), null));
    }

    [Fact]
    public async Task Settings_default_is_sessionize_and_set_is_persisted()
    {
        using var db = ScenarioFixture.NewDb();
        var svc = NewSettings(db);

        Assert.Equal(SessionSourceKinds.Sessionize, await svc.GetActiveKeyAsync(1));

        await svc.SetAsync(1, SessionSourceKinds.ZohoBackstage, "mok@expertslive.dk");
        Assert.Equal(SessionSourceKinds.ZohoBackstage, await svc.GetActiveKeyAsync(1));

        // Upsert (not duplicate) on a second set.
        await svc.SetAsync(1, SessionSourceKinds.Sessionize, "mok@expertslive.dk");
        Assert.Equal(SessionSourceKinds.Sessionize, await svc.GetActiveKeyAsync(1));
        Assert.Single(db.SessionSourceSettings);
    }

    [Fact]
    public async Task SyncDirection_default_is_stage1_and_set_is_persisted()
    {
        using var db = ScenarioFixture.NewDb();
        var svc = NewSettings(db);

        // §57: no row ⇒ stage 1 (SessionizeToCeh).
        Assert.Equal(CommunityHub.Core.Domain.SessionSyncDirection.SessionizeToCeh,
            await svc.GetSyncDirectionAsync(1));

        await svc.SetSyncDirectionAsync(
            1, CommunityHub.Core.Domain.SessionSyncDirection.ZohoToCeh, "mok@expertslive.dk");
        Assert.Equal(CommunityHub.Core.Domain.SessionSyncDirection.ZohoToCeh,
            await svc.GetSyncDirectionAsync(1));

        // Upsert (not a duplicate row) on a second set; Source stays valid.
        await svc.SetSyncDirectionAsync(
            1, CommunityHub.Core.Domain.SessionSyncDirection.CehToZoho, "mok@expertslive.dk");
        Assert.Equal(CommunityHub.Core.Domain.SessionSyncDirection.CehToZoho,
            await svc.GetSyncDirectionAsync(1));
        Assert.Single(db.SessionSourceSettings);
        Assert.Equal(SessionSourceKinds.Default, db.SessionSourceSettings.Single().Source);
    }

    [Fact]
    public async Task SpeakerSyncDirection_default_is_stage1_and_is_independent_of_session_direction()
    {
        using var db = ScenarioFixture.NewDb();
        var svc = NewSettings(db);

        // §58: no row ⇒ speaker stage 1 (SessionizeToCeh); the Zoho→CEH gate is INACTIVE.
        Assert.Equal(CommunityHub.Core.Domain.SessionSyncDirection.SessionizeToCeh,
            await svc.GetSpeakerSyncDirectionAsync(1));
        Assert.False(await svc.IsSpeakerZohoToCehActiveAsync(1));

        // Flipping the SESSION direction must NOT move the speaker direction (independent).
        await svc.SetSyncDirectionAsync(
            1, CommunityHub.Core.Domain.SessionSyncDirection.ZohoToCeh, "mok@expertslive.dk");
        Assert.Equal(CommunityHub.Core.Domain.SessionSyncDirection.SessionizeToCeh,
            await svc.GetSpeakerSyncDirectionAsync(1));
        Assert.False(await svc.IsSpeakerZohoToCehActiveAsync(1));

        // Set the SPEAKER direction to stage 3 ⇒ the gate arms; the session direction is unchanged.
        await svc.SetSpeakerSyncDirectionAsync(
            1, CommunityHub.Core.Domain.SessionSyncDirection.ZohoToCeh, "mok@expertslive.dk");
        Assert.Equal(CommunityHub.Core.Domain.SessionSyncDirection.ZohoToCeh,
            await svc.GetSpeakerSyncDirectionAsync(1));
        Assert.True(await svc.IsSpeakerZohoToCehActiveAsync(1));
        Assert.Equal(CommunityHub.Core.Domain.SessionSyncDirection.ZohoToCeh,
            await svc.GetSyncDirectionAsync(1));

        // Upsert (single row); Source stays valid after speaker-only writes.
        await svc.SetSpeakerSyncDirectionAsync(
            1, CommunityHub.Core.Domain.SessionSyncDirection.CehToZoho, "mok@expertslive.dk");
        Assert.Equal(CommunityHub.Core.Domain.SessionSyncDirection.CehToZoho,
            await svc.GetSpeakerSyncDirectionAsync(1));
        Assert.False(await svc.IsSpeakerZohoToCehActiveAsync(1));
        Assert.Single(db.SessionSourceSettings);
        Assert.Equal(SessionSourceKinds.Default, db.SessionSourceSettings.Single().Source);
    }

    [Fact]
    public async Task SpeakerSyncDirection_set_first_seeds_a_valid_source_row()
    {
        // §58: setting the speaker direction with NO existing row must seed a valid Source
        // (NOT NULL) and leave the session direction at its stage-1 default.
        using var db = ScenarioFixture.NewDb();
        var svc = NewSettings(db);

        await svc.SetSpeakerSyncDirectionAsync(
            1, CommunityHub.Core.Domain.SessionSyncDirection.CehToZoho, null);

        var row = db.SessionSourceSettings.Single();
        Assert.Equal(SessionSourceKinds.Default, row.Source);
        Assert.Equal(CommunityHub.Core.Domain.SessionSyncDirection.CehToZoho, row.SpeakerSyncDirection);
        Assert.Equal(CommunityHub.Core.Domain.SessionSyncDirection.SessionizeToCeh, row.SyncDirection);
    }

    // ---- §551: "not configured" must be distinguishable from a deliberate stage 1 ----

    [Fact]
    public async Task Stage_state_reports_NOT_CONFIGURED_when_no_row_exists()
    {
        // The whole §551 defect: with no settings row the engines read stage 1 and the UI said
        // "stage 1", so a push that had silently switched itself off looked like a choice.
        using var db = ScenarioFixture.NewDb();
        var svc = NewSettings(db);

        var session = await svc.GetSyncDirectionStateAsync(1);
        Assert.False(session.IsConfigured);
        Assert.Equal(SessionSyncDirection.SessionizeToCeh, session.Effective);
        // Never audited yet ⇒ reported as unknown, NOT guessed from the row's shared stamp.
        Assert.Null(session.LastChangeBy);
        Assert.Null(session.LastChangeAt);

        var speaker = await svc.GetSpeakerSyncDirectionStateAsync(1);
        Assert.False(speaker.IsConfigured);
        Assert.Equal(SessionSyncDirection.SessionizeToCeh, speaker.Effective);
    }

    [Fact]
    public async Task Stage_state_reports_CONFIGURED_for_a_deliberate_stage_1()
    {
        // The other half: choosing stage 1 on purpose must NOT look like "never configured".
        using var db = ScenarioFixture.NewDb();
        var svc = NewSettings(db);

        await svc.SetSyncDirectionAsync(1, SessionSyncDirection.SessionizeToCeh, "mok@expertslive.dk");

        var state = await svc.GetSyncDirectionStateAsync(1);
        Assert.True(state.IsConfigured);
        Assert.Equal(SessionSyncDirection.SessionizeToCeh, state.Effective);
    }

    [Fact]
    public async Task Stage_state_is_per_edition_so_a_new_edition_reads_as_unconfigured()
    {
        // §551's leading hypothesis: the row does not match the CURRENT active EventId (a new
        // edition, a restore, a re-seed) and the push goes dark with nobody having edited it.
        using var db = ScenarioFixture.NewDb();
        var svc = NewSettings(db);

        await svc.SetSyncDirectionAsync(1, SessionSyncDirection.CehToZoho, "mok@expertslive.dk");

        Assert.True((await svc.GetSyncDirectionStateAsync(1)).IsConfigured);

        var other = await svc.GetSyncDirectionStateAsync(2);
        Assert.False(other.IsConfigured);
        Assert.Equal(SessionSyncDirection.SessionizeToCeh, other.Effective);
    }

    [Fact]
    public async Task Stage_change_is_audited_with_who_and_from_to()
    {
        // "it is NOT audited ... there is no history of who moved the stage or when — the exact
        // question he is now asking, and it cannot be answered." Now it can.
        using var db = ScenarioFixture.NewDb();
        var svc = NewSettings(db);

        await svc.SetSyncDirectionAsync(1, SessionSyncDirection.CehToZoho, "mok@expertslive.dk");
        await svc.SetSyncDirectionAsync(1, SessionSyncDirection.SessionizeToCeh, "someone@expertslive.dk");

        var entries = await db.AuditEntries
            .Where(e => e.Action == AuditActions.SessionSyncDirectionChanged)
            .OrderBy(e => e.Id)
            .ToListAsync();

        Assert.Equal(2, entries.Count);

        // First write: never configured before ⇒ says so, rather than claiming "was stage 1".
        Assert.Contains("NOT CONFIGURED", entries[0].Summary);
        Assert.Equal("mok@expertslive.dk", entries[0].ActorEmail);
        Assert.Equal(AuditCategory.Admin, entries[0].Category);

        // Second write: the from → to that answers "something took it back to stage 1".
        Assert.Contains("from stage 2", entries[1].Summary);
        Assert.Contains("to stage 1", entries[1].Summary);
        Assert.Equal("someone@expertslive.dk", entries[1].ActorEmail);

        // And the state read surfaces the most recent one.
        var state = await svc.GetSyncDirectionStateAsync(1);
        Assert.Equal("someone@expertslive.dk", state.LastChangeBy);
        Assert.Contains("to stage 1", state.LastChangeSummary!);
    }

    [Fact]
    public async Task Speaker_stage_change_is_audited_under_its_OWN_action_code()
    {
        // The speaker stage has its own identical setting and default, so it goes dark the same
        // way. Separate codes keep "who moved the speaker stage" answerable on its own.
        using var db = ScenarioFixture.NewDb();
        var svc = NewSettings(db);

        await svc.SetSyncDirectionAsync(1, SessionSyncDirection.CehToZoho, "mok@expertslive.dk");
        await svc.SetSpeakerSyncDirectionAsync(1, SessionSyncDirection.CehToZoho, "mok@expertslive.dk");

        Assert.Single(await db.AuditEntries
            .Where(e => e.Action == AuditActions.SessionSyncDirectionChanged).ToListAsync());
        Assert.Single(await db.AuditEntries
            .Where(e => e.Action == AuditActions.SpeakerSyncDirectionChanged).ToListAsync());

        // The session read must not pick up the SPEAKER change as its own last change.
        var session = await svc.GetSyncDirectionStateAsync(1);
        Assert.Contains("Session sync stage", session.LastChangeSummary!);

        var speaker = await svc.GetSpeakerSyncDirectionStateAsync(1);
        Assert.Contains("Speaker sync stage", speaker.LastChangeSummary!);
    }

    [Fact]
    public async Task An_unchanged_re_save_is_audited_as_unchanged_not_as_a_move()
    {
        // A re-save must not read as "someone moved the stage" when chasing an incident.
        using var db = ScenarioFixture.NewDb();
        var svc = NewSettings(db);

        await svc.SetSyncDirectionAsync(1, SessionSyncDirection.CehToZoho, "mok@expertslive.dk");
        await svc.SetSyncDirectionAsync(1, SessionSyncDirection.CehToZoho, "mok@expertslive.dk");

        var last = await db.AuditEntries
            .Where(e => e.Action == AuditActions.SessionSyncDirectionChanged)
            .OrderByDescending(e => e.Id).FirstAsync();
        Assert.Contains("unchanged", last.Summary);
    }

    [Fact]
    public async Task Settings_set_rejects_an_unknown_key()
    {
        using var db = ScenarioFixture.NewDb();
        var svc = NewSettings(db);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SetAsync(1, "nope", null));
    }

    [Fact]
    public async Task Resolver_picks_active_source_then_falls_back_to_sessionize()
    {
        using var db = ScenarioFixture.NewDb();
        var settings = NewSettings(db);
        var sessionize = new FakeSource(SessionSourceKinds.Sessionize);
        var backstage = new FakeSource(SessionSourceKinds.ZohoBackstage);
        var resolver = new SessionSourceResolver(new ISessionSource[] { sessionize, backstage }, settings);

        // Default -> sessionize.
        Assert.Same(sessionize, await resolver.ResolveAsync(1));

        // Switched -> backstage.
        await settings.SetAsync(1, SessionSourceKinds.ZohoBackstage, null);
        Assert.Same(backstage, await resolver.ResolveAsync(1));

        // Active key present but that source missing from the list -> sessionize fallback.
        var resolverNoBackstage = new SessionSourceResolver(new ISessionSource[] { sessionize }, settings);
        Assert.Same(sessionize, await resolverNoBackstage.ResolveAsync(1));
    }

    /// <summary>
    /// 🗑 §754.5 — the Backstage source is SELECTABLE (reading the agenda works), but importing is
    /// not built, so it changes nothing and says so.
    /// </summary>
    /// <remarks>
    /// This replaces two tests that pinned a config flag: one asserting the source was unavailable
    /// by default, one asserting it became available when the flag was flipped. The flag claimed a
    /// <c>ZohoBackstage.agenda.READ</c> scope was missing, which was never true — so the "clear
    /// error" it produced blamed a permission the operator had already granted, on his own settings
    /// page. What is genuinely missing is the import wiring, and the refusal now says that.
    /// </remarks>
    [Fact]
    public async Task Backstage_source_is_selectable_but_importing_is_not_built_and_it_says_so()
    {
        var src = new BackstageSessionSource(new ZohoOptions());

        Assert.True(src.IsAvailable);

        var r = await src.FetchSessionsAsync(1, Array.Empty<SessionizeSpeaker>());

        // A no-op with a reason — never a silent overwrite of the session list.
        Assert.NotNull(r.Error);
        Assert.Empty(r.Sessions);
        Assert.Contains("IMPORT", r.Error, StringComparison.OrdinalIgnoreCase);
        // 🔒 And it must NOT blame a missing scope — that is the sentence that cost ten sessions.
        Assert.DoesNotContain("scope", r.Error, StringComparison.OrdinalIgnoreCase);
    }
}
