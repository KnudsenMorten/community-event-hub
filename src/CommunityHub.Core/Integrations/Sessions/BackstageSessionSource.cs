namespace CommunityHub.Core.Integrations.Sessions;

/// <summary>
/// Session source backed by the Zoho Backstage v3 agenda API (the finalized
/// schedule). The mapping is built + offline-tested in
/// <see cref="BackstageSessionParser"/>; the LIVE pull is gated behind the
/// <c>ZohoBackstage.agenda.READ</c> OAuth scope (get-all-sessions / get-all-halls
/// both 401 without it — REQUIREMENTS §6). Until the operator extends the refresh
/// token, this source reports unavailable and never fakes data, so selecting it in
/// Settings yields a clear "not yet enabled" result rather than silent emptiness.
///
/// When the scope lands, wire the OAuth client + portal/event config here: pull
/// halls → id/name map, enumerate agenda days, pull sessions per day, then
/// <c>BackstageSessionParser.ParseSessions(json, halls)</c>; link by speaker email.
/// </summary>
public sealed class BackstageSessionSource : ISessionSource
{
    private readonly ZohoOptions _options;

    public BackstageSessionSource(ZohoOptions options) => _options = options;

    public string Key => SessionSourceKinds.ZohoBackstage;

    /// <summary>
    /// Live Backstage agenda access tracks the SAME gate as the §38e engine:
    /// <see cref="ZohoOptions.AgendaReadEnabled"/> (set once the refresh token carries
    /// the <c>ZohoBackstage.agenda.READ</c> scope). When false the Organizer Settings
    /// page won't let an operator select this source; when true the agenda pull is wired
    /// (day-enumeration + halls) and the source is selectable.
    /// </summary>
    public bool IsAvailable => _options.AgendaReadEnabled;

    public Task<SessionSourceResult> FetchSessionsAsync(
        int eventId,
        IReadOnlyList<SessionizeSpeaker> importedSpeakers,
        CancellationToken ct = default) =>
        Task.FromResult(_options.AgendaReadEnabled
            // The §38e CHANGE-DETECTION engine pulls the agenda directly (ZohoClient +
            // day-enumeration); the full IMPORT path through this source (parse → link →
            // SessionImportService) is a separate, not-yet-wired step. Until that lands,
            // selecting Backstage as the import source is a no-op rather than a silent
            // overwrite, so we surface that clearly instead of changing sessions.
            ? SessionSourceResult.Failed(
                "Zoho Backstage agenda READ is enabled and the §38e change-detection "
                + "engine uses it, but the Backstage session IMPORT path is not wired yet "
                + "— sessions are not changed. Keep Sessionize as the import source.")
            : SessionSourceResult.Failed(
                "Zoho Backstage session source is not enabled yet — it requires the "
                + "ZohoBackstage.agenda.READ OAuth scope on the refresh token "
                + "(Zoho:AgendaReadEnabled=true). Sessions are not changed."));
}
