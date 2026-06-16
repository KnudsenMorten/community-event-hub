using CommunityHub.Core.Integrations;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// Imports Sessionize speakers by pulling the Sessionize v2 view API (JSON)
/// instead of an uploaded Excel file. The fetch + JSON mapping lives in
/// <see cref="SessionizeApiClient"/>; the upsert semantics (match on email,
/// never overwrite roles, never delete, report skipped rows) are the SAME
/// shared core as the Excel path - <see cref="SessionizeImportService.ImportSpeakersAsync"/>.
///
/// This is schedulable from the Jobs app and runnable from the OneShot CLI; the
/// web app also exposes a button that calls it. Disabled (no-op) unless
/// <see cref="SessionizeApiOptions.Enabled"/> is true and an endpoint id is set.
/// </summary>
public sealed class SessionizeApiImportService
{
    private readonly SessionizeApiClient _client;
    private readonly SessionizeApiOptions _options;
    private readonly SessionizeImportService _import;
    private readonly SessionImportService _sessions;

    public SessionizeApiImportService(
        SessionizeApiClient client,
        SessionizeApiOptions options,
        SessionizeImportService import,
        SessionImportService sessions)
    {
        _client = client;
        _options = options;
        _import = import;
        _sessions = sessions;
    }

    /// <summary>
    /// Pull speakers from the Sessionize API and upsert them for the edition.
    /// <paramref name="sendWelcome"/> defaults to false so a scheduled pull
    /// never spams speakers; the web button / CLI can opt in.
    /// </summary>
    public async Task<SessionizeImportResult> ImportAsync(
        int eventId,
        CancellationToken ct = default,
        bool sendWelcome = false,
        SessionizeImportMode mode = SessionizeImportMode.Delta)
    {
        if (!_options.Enabled)
        {
            return new SessionizeImportResult(
                0, 0, 0, 0, Array.Empty<string>(),
                "Sessionize API integration is disabled "
                + "(Sessionize:Enabled = false).");
        }

        var fetched = await _client.FetchSpeakersAsync(ct);
        if (fetched.Error is not null)
        {
            return new SessionizeImportResult(
                0, 0, 0, 0, fetched.Warnings, fetched.Error);
        }

        // 1. Speakers first, so the participants exist for the session links.
        var speakerResult = await _import.ImportSpeakersAsync(
            eventId, fetched.Speakers, fetched.Warnings, ct, sendWelcome, mode);

        // 2. Then SESSIONS, from the same v2 view API, linked to the speakers we
        //    just imported (matched on the Sessionize speaker id -> email ->
        //    participant). Same upsert/never-delete delta semantics as speakers.
        //    A session fetch problem is reported but never fails the (already
        //    committed) speaker import - speakers are the critical path.
        var sessionFetch = await _client.FetchSessionsAsync(ct);
        SessionImportResult sessionResult;
        if (sessionFetch.Error is not null)
        {
            sessionResult = new SessionImportResult(
                0, 0, 0, 0, 0, 0, sessionFetch.Warnings, sessionFetch.Error);
        }
        else
        {
            sessionResult = await _sessions.ImportSessionsAsync(
                eventId, sessionFetch.Sessions, fetched.Speakers,
                sessionFetch.Warnings, ct);
        }

        return speakerResult with { Sessions = sessionResult };
    }
}
