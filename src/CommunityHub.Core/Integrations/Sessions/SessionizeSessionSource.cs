namespace CommunityHub.Core.Integrations.Sessions;

/// <summary>
/// The default session source: Sessionize's v2 view API (the <c>All</c>/<c>Sessions</c>
/// view). Delegates to the existing <see cref="SessionizeApiClient.FetchSessionsAsync"/>
/// and links sessions by the Sessionize speaker id → email → participant, so this
/// is byte-for-byte the behaviour the hub had before the source became pluggable.
/// </summary>
public sealed class SessionizeSessionSource : ISessionSource
{
    private readonly SessionizeApiClient _client;
    private readonly SessionizeApiOptions _options;
    public SessionizeSessionSource(SessionizeApiClient client, SessionizeApiOptions options)
    {
        _client = client;
        _options = options;
    }

    public string Key => SessionSourceKinds.Sessionize;

    /// <summary>Available whenever an endpoint id is configured.</summary>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_options.EndpointId);

    public async Task<SessionSourceResult> FetchSessionsAsync(
        int eventId,
        IReadOnlyList<SessionizeSpeaker> importedSpeakers,
        CancellationToken ct = default)
    {
        var fetch = await _client.FetchSessionsAsync(ct);
        // Sessions link by the Sessionize speaker id carried on each session, so
        // the speakers to link by are exactly the ones the speaker import produced.
        return new SessionSourceResult(
            fetch.Sessions, importedSpeakers, fetch.Warnings, fetch.Error);
    }
}
