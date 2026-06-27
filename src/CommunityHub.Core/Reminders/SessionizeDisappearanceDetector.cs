using System.Globalization;
using System.Net;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Sessions;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// REQUIREMENTS §58 / §56 — the NEVER-AUTO-DELETE disappearance alert for Sessionize-linked
/// speakers and sessions.
///
/// <para>The Sessionize import UPSERTS speakers + sessions and never deletes (see
/// <see cref="SessionizeImportService"/> / <see cref="SessionImportService"/>). So when an
/// entity is removed in Sessionize, its CEH row is simply LEFT in place. That is correct
/// (we never auto-delete), but it means the operator has no way of knowing a speaker/session
/// vanished upstream.</para>
///
/// <para>This detector closes that gap WITHOUT deleting anything: after an import pass it
/// compares the CEH entities that are LINKED to Sessionize (they carry a
/// <c>SessionizeSpeakerId</c> / <c>SessionizeId</c>) against the set of ids that were present
/// in the latest pull. Any linked CEH entity NOT in the latest pull has "disappeared" from
/// Sessionize — we EMAIL the operator a short list and let them decide manually. We never
/// delete or deactivate the row.</para>
///
/// <para>Idempotency/non-spam: we only email when the disappeared set is non-empty, and the
/// alert goes through <see cref="EngineAlertSender"/> which throttles per key (6h window), so
/// a run that keeps seeing the same missing entities won't flood the inbox. Hub-added sessions
/// (synthetic <c>hub-*</c> Sessionize ids) are never "from Sessionize" and so are excluded.</para>
///
/// <para>Delivery: <see cref="EngineAlertSender"/> is the RING-EXEMPT ops path (the operator
/// mailbox is not a ring-gated participant, so a normal participant send would be dropped).
/// The detector is best-effort: a mail failure never throws back into the import.</para>
/// </summary>
public sealed class SessionizeDisappearanceDetector
{
    private readonly CommunityHubDbContext _db;
    // Optional: when the ops alert sender is not registered (e.g. a minimal web wiring or a
    // unit test that doesn't care about the alert), the detector still runs its READ-ONLY
    // comparison and simply skips the email — exactly like the best-effort picture store in
    // the speaker import. It NEVER deletes regardless.
    private readonly EngineAlertSender? _alerts;
    // §59: each disappeared entity is ALSO enqueued as a Pending Disappeared SyncDelta so it
    // shows in /Organizer/SyncQueue (Approve = acknowledge only, NEVER deletes — the queue's
    // apply arm already enforces that). Optional: a null queue (minimal test wiring) just skips
    // the enqueue; the summary email is unchanged. Dedupe in EnqueueAsync handles repeated runs.
    private readonly SyncDeltaQueueService? _queue;

    public SessionizeDisappearanceDetector(
        CommunityHubDbContext db,
        EngineAlertSender? alerts = null,
        SyncDeltaQueueService? queue = null)
    {
        _db = db;
        _alerts = alerts;
        _queue = queue;
    }

    /// <summary>The outcome of a disappearance scan, for the import result / tests.</summary>
    public sealed record Result(
        IReadOnlyList<(int Id, string Name, string SessionizeId)> DisappearedSpeakers,
        IReadOnlyList<(int Id, string Title, string SessionizeId)> DisappearedSessions,
        bool Emailed)
    {
        public bool Any => DisappearedSpeakers.Count > 0 || DisappearedSessions.Count > 0;
    }

    /// <summary>
    /// Scan an edition for Sessionize-linked speakers/sessions that were NOT in the latest
    /// pull and, if any, email the operator. Pass the ids that the import just saw:
    /// <paramref name="presentSpeakerIds"/> are the Sessionize speaker ids and
    /// <paramref name="presentSessionIds"/> the Sessionize session ids from this pull. Pass
    /// null/empty present-sets ONLY when the pull genuinely returned nothing — a caller that
    /// could not fetch (an error) must NOT call this, or every linked entity would look
    /// "disappeared". NEVER deletes. Never throws (best-effort).
    /// </summary>
    public async Task<Result> ScanAsync(
        int eventId,
        IReadOnlyCollection<string> presentSpeakerIds,
        IReadOnlyCollection<string> presentSessionIds,
        CancellationToken ct = default)
    {
        var presentSpeakers = new HashSet<string>(presentSpeakerIds, StringComparer.OrdinalIgnoreCase);
        var presentSessions = new HashSet<string>(presentSessionIds, StringComparer.OrdinalIgnoreCase);

        // SPEAKERS: profiles linked to Sessionize (carry a SessionizeSpeakerId) whose id is
        // absent from the latest pull. Join Participant for a human-readable name.
        var linkedSpeakers = await _db.SpeakerProfiles
            .AsNoTracking()
            .Where(sp => sp.EventId == eventId && sp.SessionizeSpeakerId != null && sp.SessionizeSpeakerId != "")
            .Select(sp => new
            {
                sp.Id,
                SessionizeId = sp.SessionizeSpeakerId!,
                Name = sp.Participant.FullName,
            })
            .ToListAsync(ct);

        var disappearedSpeakers = linkedSpeakers
            .Where(s => !presentSpeakers.Contains(s.SessionizeId))
            .Select(s => (s.Id, Name: string.IsNullOrWhiteSpace(s.Name) ? "(no name)" : s.Name, s.SessionizeId))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // SESSIONS: imported sessions (NOT hub-added) whose SessionizeId is absent from the
        // latest pull. Hub-added sessions carry a synthetic hub-* id and are never "from
        // Sessionize", so they can never disappear from it.
        var linkedSessions = await _db.Sessions
            .AsNoTracking()
            .Where(s => s.EventId == eventId && !s.IsHubAdded
                        && s.SessionizeId != "" && !s.SessionizeId.StartsWith("hub-"))
            .Select(s => new { s.Id, s.SessionizeId, s.Title })
            .ToListAsync(ct);

        var disappearedSessions = linkedSessions
            .Where(s => !presentSessions.Contains(s.SessionizeId))
            .Select(s => (s.Id, Title: string.IsNullOrWhiteSpace(s.Title) ? "(untitled)" : s.Title, s.SessionizeId))
            .OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new Result(disappearedSpeakers, disappearedSessions, Emailed: false);
        if (!result.Any) return result;          // nothing missing ⇒ never email/enqueue (non-spam)

        // §59: ENQUEUE each disappeared entity as a Pending Disappeared delta so the operator
        // sees it in /Organizer/SyncQueue. Approve there = acknowledge only (NEVER deletes —
        // the queue's apply arm enforces that). EnqueueAsync dedupes per (event, type, id, kind),
        // so a repeated import run collapses to one queue item rather than stacking duplicates.
        if (_queue is not null)
        {
            foreach (var sp in disappearedSpeakers)
            {
                await _queue.EnqueueDisappearanceAsync(
                    eventId, SyncDeltaEntityType.Speaker,
                    sp.Id.ToString(CultureInfo.InvariantCulture), sp.Name,
                    SessionSyncDirection.SessionizeToCeh, ct);
            }
            foreach (var s in disappearedSessions)
            {
                await _queue.EnqueueDisappearanceAsync(
                    eventId, SyncDeltaEntityType.Session,
                    s.Id.ToString(CultureInfo.InvariantCulture), s.Title,
                    SessionSyncDirection.SessionizeToCeh, ct);
            }
        }

        if (_alerts is null) return result;       // no ops sender wired ⇒ skip the email only

        var emailed = await TryEmailAsync(eventId, disappearedSpeakers, disappearedSessions, ct);
        return result with { Emailed = emailed };
    }

    /// <summary>
    /// The Sessionize ids of the edition's CURRENTLY-linked, non-hub sessions. Passed as the
    /// "present" set when a SESSION fetch FAILED, so the scan can't flag any session as gone
    /// just because the pull errored (a fetch error ≠ a disappearance). Speakers are scanned
    /// normally in that case (their fetch succeeded).
    /// </summary>
    public async Task<IReadOnlyCollection<string>> CurrentLinkedSessionIdsAsync(
        int eventId, CancellationToken ct = default) =>
        await _db.Sessions
            .AsNoTracking()
            .Where(s => s.EventId == eventId && !s.IsHubAdded
                        && s.SessionizeId != "" && !s.SessionizeId.StartsWith("hub-"))
            .Select(s => s.SessionizeId)
            .ToListAsync(ct);

    private async Task<bool> TryEmailAsync(
        int eventId,
        IReadOnlyList<(int Id, string Name, string SessionizeId)> speakers,
        IReadOnlyList<(int Id, string Title, string SessionizeId)> sessions,
        CancellationToken ct)
    {
        // §108: show the edition Code/DisplayName, never the raw numeric event id.
        var ev = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.Code, e.DisplayName })
            .FirstOrDefaultAsync(ct);
        var label =
            !string.IsNullOrWhiteSpace(ev?.Code) ? ev!.Code
            : !string.IsNullOrWhiteSpace(ev?.DisplayName) ? ev!.DisplayName
            : $"event {eventId}";

        var subject =
            $"[CEH] Sessionize entities disappeared — manual review ({label}): "
            + $"{speakers.Count} speaker(s), {sessions.Count} session(s)";

        var html = BuildHtml(eventId, speakers, sessions);

        // Throttle per edition so a still-missing set won't email on every import tick.
        var throttleKey = $"sessionize-disappearance-{eventId}";
        await _alerts!.AlertAsync(subject, html, ct, throttleKey);
        return true;
    }

    private static string BuildHtml(
        int eventId,
        IReadOnlyList<(int Id, string Name, string SessionizeId)> speakers,
        IReadOnlyList<(int Id, string Title, string SessionizeId)> sessions)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(
            "<p>These CEH speakers/sessions are linked to Sessionize but were "
            + "<strong>no longer present in the latest Sessionize pull</strong>. They have "
            + "<strong>NOT</strong> been deleted — decide manually whether to remove them in "
            + "CEH (and/or Zoho Backstage).</p>");

        if (speakers.Count > 0)
        {
            sb.Append("<h3>Speakers</h3><ul>");
            foreach (var s in speakers)
            {
                sb.Append(
                    $"<li>{Enc(s.Name)} — CEH id {s.Id}, Sessionize id {Enc(s.SessionizeId)}</li>");
            }
            sb.Append("</ul>");
        }

        if (sessions.Count > 0)
        {
            sb.Append("<h3>Sessions</h3><ul>");
            foreach (var s in sessions)
            {
                sb.Append(
                    $"<li>{Enc(s.Title)} — CEH id {s.Id}, Sessionize id {Enc(s.SessionizeId)}</li>");
            }
            sb.Append("</ul>");
        }

        sb.Append("<p>(CEH never auto-deletes a speaker or session — REQUIREMENTS §58/§56.)</p>");
        return sb.ToString();
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
