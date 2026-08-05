namespace CommunityHub.Core.Integrations.Sessions;

/// <summary>
/// Session source backed by the Zoho Backstage v3 agenda API (the finalized schedule). The mapping
/// is built + offline-tested in <see cref="BackstageSessionParser"/>.
/// </summary>
/// <remarks>
/// <para>🗑 <b>§754.5 — this is NOT blocked on an OAuth scope.</b> It used to say the live pull was
/// gated behind <c>ZohoBackstage.agenda.READ</c>, "until the operator extends the refresh token".
/// That was never true: the Backstage credentials carry every permission CEH needs, and the §38e
/// change-detection engine reads the agenda in production today.</para>
///
/// <para>🔑 <b>What is genuinely missing is the IMPORT path</b>, and only that: parse → link →
/// <c>SessionImportService</c>. To wire it, pull halls → id/name map, enumerate agenda days, pull
/// sessions per day, then <c>BackstageSessionParser.ParseSessions(json, halls)</c> and link by
/// speaker e-mail. Until then this source reports its limitation honestly rather than silently
/// overwriting the session list.</para>
/// </remarks>
public sealed class BackstageSessionSource : ISessionSource
{
    public BackstageSessionSource(ZohoOptions options) { }

    public string Key => SessionSourceKinds.ZohoBackstage;

    /// <summary>
    /// The agenda API is reachable, so the source is offered in Organizer Settings — but see
    /// <see cref="FetchSessionsAsync"/>: selecting it is a NO-OP until the import path is wired,
    /// and it says so.
    /// </summary>
    public bool IsAvailable => true;

    public Task<SessionSourceResult> FetchSessionsAsync(
        int eventId,
        IReadOnlyList<SessionizeSpeaker> importedSpeakers,
        CancellationToken ct = default) =>
        // 🔒 A no-op with a clear reason, never a silent overwrite. The §38e CHANGE-DETECTION engine
        // pulls the agenda directly (ZohoClient + day enumeration); the full IMPORT path through
        // this source is a separate step that has not been built.
        Task.FromResult(SessionSourceResult.Failed(
            "The Zoho Backstage session IMPORT path is not built yet — sessions are NOT changed. "
            + "(Reading the Backstage agenda works: the session change-detection engine uses it. "
            + "It is the import that is missing.) Keep Sessionize as the import source."));
}
