using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Sessions;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// Imports Sessionize speakers by pulling the Sessionize v2 view API (JSON) —
/// the only import source (§82, Excel upload removed). The fetch + JSON mapping
/// lives in <see cref="SessionizeApiClient"/>; the upsert semantics (match on
/// email, never overwrite roles, never delete, report skipped rows) live in the
/// shared core <see cref="SessionizeImportService.ImportSpeakersAsync"/>.
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
    private readonly SessionSourceResolver _sessionSource;
    // §58: after the upsert, detect Sessionize-linked CEH speakers/sessions that vanished from
    // the pull and EMAIL the operator (never delete). Optional: a null detector simply skips
    // the check (e.g. minimal test wiring); the import behaviour is otherwise unchanged.
    private readonly SessionizeDisappearanceDetector? _disappearance;

    public SessionizeApiImportService(
        SessionizeApiClient client,
        SessionizeApiOptions options,
        SessionizeImportService import,
        SessionImportService sessions,
        SessionSourceResolver sessionSource,
        SessionizeDisappearanceDetector? disappearance = null)
    {
        _client = client;
        _options = options;
        _import = import;
        _sessions = sessions;
        _sessionSource = sessionSource;
        _disappearance = disappearance;
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
        // SESSIONS come from the edition's ACTIVE session source (default
        // Sessionize; switchable to Zoho Backstage per REQUIREMENTS §6). The
        // source returns the sessions + the speaker list to link them by.
        var source = await _sessionSource.ResolveAsync(eventId, ct);
        var sessionFetch = await source.FetchSessionsAsync(eventId, fetched.Speakers, ct);
        SessionImportResult sessionResult;
        if (sessionFetch.Error is not null)
        {
            sessionResult = new SessionImportResult(
                0, 0, 0, 0, 0, 0, sessionFetch.Warnings, sessionFetch.Error);
        }
        else
        {
            sessionResult = await _sessions.ImportSessionsAsync(
                eventId, sessionFetch.Sessions, sessionFetch.LinkSpeakers,
                sessionFetch.Warnings, ct);
        }

        // §58 NEVER-AUTO-DELETE disappearance alert. Compare the CEH entities LINKED to
        // Sessionize against the ids actually present in THIS pull and email the operator
        // for any that vanished — but only when the relevant fetch SUCCEEDED, so a fetch
        // error never makes everything look "disappeared". Speakers always fetched here
        // (speakerResult.Error would have returned earlier); sessions only when the source
        // fetch had no error. Best-effort — never throws back into the import.
        if (_disappearance is not null)
        {
            var presentSpeakerIds = fetched.Speakers
                .Select(s => s.SessionizeId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToList();
            // Only treat sessions as "scanned" when the session fetch succeeded; otherwise we
            // can't tell present-from-missing, so leave sessions out of the scan (pass the
            // current CEH-linked ids so nothing is flagged as gone).
            IReadOnlyCollection<string> presentSessionIds;
            if (sessionFetch.Error is null)
            {
                presentSessionIds = sessionFetch.Sessions
                    .Select(s => s.SessionizeId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!)
                    .ToList();
            }
            else
            {
                presentSessionIds = await _disappearance.CurrentLinkedSessionIdsAsync(eventId, ct);
            }

            try
            {
                await _disappearance.ScanAsync(eventId, presentSpeakerIds, presentSessionIds, ct);
            }
            catch { /* best-effort: a disappearance-alert failure must not fail the import */ }
        }

        return speakerResult with { Sessions = sessionResult };
    }
}
