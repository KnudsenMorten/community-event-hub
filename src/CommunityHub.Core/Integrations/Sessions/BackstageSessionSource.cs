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
    public string Key => SessionSourceKinds.ZohoBackstage;

    /// <summary>
    /// Live Backstage agenda access is not enabled yet (missing
    /// <c>ZohoBackstage.agenda.READ</c>). Flip to true when the OAuth client +
    /// portal/event config are wired and the scope is granted.
    /// </summary>
    public bool IsAvailable => false;

    public Task<SessionSourceResult> FetchSessionsAsync(
        int eventId,
        IReadOnlyList<SessionizeSpeaker> importedSpeakers,
        CancellationToken ct = default) =>
        Task.FromResult(SessionSourceResult.Failed(
            "Zoho Backstage session source is not enabled yet — it requires the "
            + "ZohoBackstage.agenda.READ OAuth scope on the refresh token plus the "
            + "portal/event configuration. Sessions are not changed."));
}
