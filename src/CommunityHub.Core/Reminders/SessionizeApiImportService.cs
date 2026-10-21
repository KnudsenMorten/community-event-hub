using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Sessions;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// The on-demand / scheduled Sessionize speaker+session import. A seam over
/// <see cref="SessionizeApiImportService"/> so callers (the organizer "trigger
/// import now" page button, the timer job) can run the SAME import and so the
/// page handler is unit-testable with a fake (REQUIREMENTS §198).
/// </summary>
public interface ISessionizeApiImportService
{
    /// <summary>
    /// Pull speakers (and sessions) from the Sessionize API and upsert them for
    /// the edition. Mirrors <see cref="SessionizeApiImportService.ImportAsync"/>.
    /// </summary>
    Task<SessionizeImportResult> ImportAsync(
        int eventId,
        CancellationToken ct = default,
        bool sendWelcome = false,
        SessionizeImportMode mode = SessionizeImportMode.Delta);
}

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
public sealed class SessionizeApiImportService : ISessionizeApiImportService
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
        SessionizeDisappearanceDetector? disappearance = null,
        // §999 — optional so every existing construction site (and test) still compiles; a null
        // notifier means the deviations are returned in the result and simply not mailed.
        SessionizeDeviationNotifier? deviations = null)
    {
        _client = client;
        _options = options;
        _import = import;
        _sessions = sessions;
        _sessionSource = sessionSource;
        _disappearance = disappearance;
        _deviations = deviations;
    }

    private readonly SessionizeDeviationNotifier? _deviations;

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

        // 🔴 §1243 — FETCH the sessions BEFORE importing speakers, so the speaker import can be
        // gated on confirmation too.
        //
        // Operator 2026-09-20: *"importer should NOT import sessions or speakers that have not
        // approved yet"*. §1241 gated SESSIONS; speakers came in regardless and landed in the
        // Pending-speakers queue — where approving one sets their category, which is exactly what
        // makes Zoho create and INVITE them (§326bx). So the queue was inviting him to invite four
        // people who had not agreed to speak.
        //
        // 🔑 Only the FETCH moves. The order of WRITES is unchanged — speakers are still imported
        // before sessions, so the participants exist for the session links to resolve against.
        var source = await _sessionSource.ResolveAsync(eventId, ct);
        var sessionFetch = await source.FetchSessionsAsync(eventId, fetched.Speakers, ct);

        // Sessionize has NO speaker-level confirmation flag (verified against the live v2 payload):
        // it exists only per SESSION. So "this speaker has not confirmed" has to be derived —
        // a speaker is held when EVERY session they are on is unconfirmed. One confirmed session
        // means they have committed to the event, even if another of their talks is still open.
        var speakersToImport = fetched.Speakers;
        var heldSpeakerNames = new List<string>();
        var speakerWarnings = new List<string>(fetched.Warnings);

        // 🔒 FAIL-OPEN, AND THIS IS THE IMPORTANT PART. The gate applies ONLY when the session
        // fetch actually succeeded AND returned sessions. A failed or empty session view would
        // otherwise make EVERY speaker look unconfirmed and import nobody at all — silently, with
        // no failure anywhere. That is §326ax's lesson (an empty join is not evidence) and §1241's
        // (an absent signal must never read as a negative one), and it is worse here because
        // speakers are the critical path: the whole edition would quietly stop importing.
        if (sessionFetch.Error is null)
        {
            var held = UnconfirmedSpeakers(fetched.Speakers, sessionFetch.Sessions);
            if (held.Count > 0)
            {
                var heldIds = held.Select(h => h.SessionizeId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                speakersToImport = fetched.Speakers
                    .Where(s => !heldIds.Contains(s.SessionizeId)).ToList();
                foreach (var h in held)
                {
                    var name = $"{h.FirstName} {h.LastName}".Trim();
                    heldSpeakerNames.Add(string.IsNullOrWhiteSpace(name) ? h.Email : name);
                }
                speakerWarnings.Add(
                    $"{held.Count} speaker(s) not imported — none of their sessions is confirmed "
                    + $"in Sessionize yet: {string.Join(", ", heldSpeakerNames)}. They import by "
                    + "themselves once they confirm.");
            }
        }

        // 1. Speakers, so the participants exist for the session links. §204:
        //    pass the email-less speakers too so they land in the pre-selection queue
        //    (inactive, keyed by Sessionize id) instead of being silently dropped.
        var speakerResult = await _import.ImportSpeakersAsync(
            eventId, speakersToImport, speakerWarnings, ct, sendWelcome, mode,
            emailLessSpeakers: fetched.EmailLessSpeakers);

        // 2. Then SESSIONS, from the same v2 view API, linked to the speakers we
        //    just imported (matched on the Sessionize speaker id -> email ->
        //    participant). Same upsert/never-delete delta semantics as speakers.
        //    A session fetch problem is reported but never fails the (already
        //    committed) speaker import - speakers are the critical path.
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

            // §999 — the import no longer overwrites the CEH-owned fields, so the disagreement has
            // to be TOLD to somebody or it is simply lost. Best-effort: a mail failure must never
            // fail the import, which has already written the content fields successfully.
            if (_deviations is not null && sessionResult.DeviationsOrEmpty.Count > 0)
            {
                try { await _deviations.NotifyAsync(sessionResult.DeviationsOrEmpty, ct); }
                catch { /* reported by the sender; the import stands */ }
            }
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

    /// <summary>
    /// 🔴 §1243 — the speakers to HOLD BACK: those on at least one session, none of which is
    /// confirmed. Public and static so the tests exercise THIS code rather than a copy of the rule
    /// (a duplicated derivation is a derivation that drifts).
    /// </summary>
    /// <remarks>
    /// <para>Sessionize has no speaker-level confirmation flag — verified against the live v2
    /// payload, it exists only per SESSION — so "has not confirmed" must be derived. One confirmed
    /// session is enough: a speaker with a second, still-open talk HAS committed to the event.</para>
    ///
    /// <para>🔒 <b>An EMPTY session list holds NOBODY.</b> A failed or empty session view makes
    /// every speaker look unconfirmed; acting on that would silently stop the whole edition from
    /// importing speakers, with no error anywhere. Fail open, loudly wrong rather than quietly
    /// empty — §326ax (an empty join is not evidence) and §1241 (an absent signal is not a
    /// negative one).</para>
    ///
    /// <para>⚠️ A speaker on NO session at all is NOT held: they have nothing to confirm, and
    /// treating "never asked" as "declined" would drop somebody for a state they were never in.</para>
    /// </remarks>
    public static IReadOnlyList<SessionizeSpeaker> UnconfirmedSpeakers(
        IReadOnlyList<SessionizeSpeaker> speakers,
        IReadOnlyList<SessionizeSession> sessions)
    {
        if (sessions.Count == 0) return Array.Empty<SessionizeSpeaker>();

        var confirmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sessions)
        {
            foreach (var id in s.SpeakerIds)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                seen.Add(id);
                if (s.IsConfirmed) confirmed.Add(id);
            }
        }

        return speakers
            .Where(s => !string.IsNullOrWhiteSpace(s.SessionizeId)
                        && seen.Contains(s.SessionizeId)
                        && !confirmed.Contains(s.SessionizeId))
            .ToList();
    }
}
