using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The pluggable session-source plumbing (REQUIREMENTS §6): per-edition setting
/// (default Sessionize, unknown falls back), and the resolver that picks the
/// active <see cref="ISessionSource"/> by key with a Sessionize fallback.
/// </summary>
public class SessionSourceTests
{
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
        var svc = new SessionSourceSettingsService(db);

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
        var svc = new SessionSourceSettingsService(db);

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
        var svc = new SessionSourceSettingsService(db);

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
        var svc = new SessionSourceSettingsService(db);

        await svc.SetSpeakerSyncDirectionAsync(
            1, CommunityHub.Core.Domain.SessionSyncDirection.CehToZoho, null);

        var row = db.SessionSourceSettings.Single();
        Assert.Equal(SessionSourceKinds.Default, row.Source);
        Assert.Equal(CommunityHub.Core.Domain.SessionSyncDirection.CehToZoho, row.SpeakerSyncDirection);
        Assert.Equal(CommunityHub.Core.Domain.SessionSyncDirection.SessionizeToCeh, row.SyncDirection);
    }

    [Fact]
    public async Task Settings_set_rejects_an_unknown_key()
    {
        using var db = ScenarioFixture.NewDb();
        var svc = new SessionSourceSettingsService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SetAsync(1, "nope", null));
    }

    [Fact]
    public async Task Resolver_picks_active_source_then_falls_back_to_sessionize()
    {
        using var db = ScenarioFixture.NewDb();
        var settings = new SessionSourceSettingsService(db);
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

    [Fact]
    public void Backstage_source_is_not_available_and_returns_a_clear_error()
    {
        // Default ZohoOptions ⇒ AgendaReadEnabled=false ⇒ source not selectable.
        var src = new BackstageSessionSource(new ZohoOptions());
        Assert.False(src.IsAvailable);
        var r = src.FetchSessionsAsync(1, Array.Empty<SessionizeSpeaker>()).Result;
        Assert.NotNull(r.Error);
        Assert.Empty(r.Sessions);
    }

    [Fact]
    public void Backstage_source_becomes_selectable_when_agenda_read_enabled()
    {
        // Flipping the §38e gate makes the Organizer source selector offer Backstage.
        var src = new BackstageSessionSource(new ZohoOptions { AgendaReadEnabled = true });
        Assert.True(src.IsAvailable);
    }
}
