namespace CommunityHub.Core.Integrations.Sessions;

/// <summary>
/// Picks the active <see cref="ISessionSource"/> for an edition from the persisted
/// setting (default Sessionize). Registered with all available sources injected;
/// resolves by key, falling back to Sessionize if a configured source is somehow
/// absent — so the import always has a working default.
/// </summary>
public sealed class SessionSourceResolver
{
    private readonly IReadOnlyList<ISessionSource> _sources;
    private readonly SessionSourceSettingsService _settings;

    public SessionSourceResolver(
        IEnumerable<ISessionSource> sources, SessionSourceSettingsService settings)
    {
        _sources = sources.ToList();
        _settings = settings;
    }

    public async Task<ISessionSource> ResolveAsync(int eventId, CancellationToken ct = default)
    {
        var key = await _settings.GetActiveKeyAsync(eventId, ct);
        return _sources.FirstOrDefault(s => s.Key == key)
            ?? _sources.First(s => s.Key == SessionSourceKinds.Sessionize);
    }
}
