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
        var src = new BackstageSessionSource();
        Assert.False(src.IsAvailable);
        var r = src.FetchSessionsAsync(1, Array.Empty<SessionizeSpeaker>()).Result;
        Assert.NotNull(r.Error);
        Assert.Empty(r.Sessions);
    }
}
