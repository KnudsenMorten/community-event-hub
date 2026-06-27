using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Sessions;

/// <summary>
/// The §57 STAGE 2 (CehToZoho) SESSION PUSH engine. For each CEH session it CREATES the
/// session in Zoho Backstage when it has no <see cref="Session.BackstageSessionId"/> (then
/// stores the returned id), or UPDATES the existing Backstage session when it already has
/// one — keeping the two 1:1 by the stored id (idempotent). It NEVER deletes anything.
///
/// <b>§57 DIRECTION GATE.</b> This engine is only active when the edition's session sync
/// direction is stage 2 (<see cref="SessionSyncDirection.CehToZoho"/>). At the default
/// stage 1 (Sessionize→CEH) and at stage 3 (Zoho→CEH, the §38e read engine) it is INERT —
/// it pushes nothing. The two directions are mutually exclusive by design: the push (§57
/// stage 2) and the change-detection read (§38e / stage 3) never run for the same edition
/// at the same time.
///
/// <b>What is pushed.</b> Title (required), abstract→description, start_time, duration
/// (derived from end−start, falling back to the <see cref="Session.Length"/> bucket),
/// track, and the 1-based agenda day (derived from the session's start date relative to the
/// edition's first agenda day). Service sessions (breaks/lunch) are skipped.
/// </summary>
public sealed class SessionBackstagePushService
{
    private readonly CommunityHubDbContext _db;
    private readonly ZohoClient _zoho;
    private readonly ZohoOptions _zohoOptions;

    // Overridable token source (default = the real ZohoClient refresh). Tests inject a
    // canned token so the gate + push-decision logic is exercised without a token refresh.
    private readonly Func<CancellationToken, Task<string?>>? _tokenOverride;

    // §59: when an ALREADY-LINKED session would be UPDATED, ENQUEUE a CehToZoho Update delta
    // for operator approval instead of pushing inline (NEW sessions still create directly).
    // LAZY (Func) so DI builds the push service WITHOUT eagerly constructing the queue — the
    // queue itself depends on this push service for apply-on-approve, and a lazy factory breaks
    // that otherwise-circular graph. Null ⇒ no queue wired (legacy inline-update behaviour).
    private readonly Func<SyncDeltaQueueService>? _queueFactory;

    public SessionBackstagePushService(
        CommunityHubDbContext db, ZohoClient zoho, ZohoOptions zohoOptions,
        Func<CancellationToken, Task<string?>>? tokenOverride = null,
        Func<SyncDeltaQueueService>? queueFactory = null)
    {
        _db = db; _zoho = zoho; _zohoOptions = zohoOptions;
        _tokenOverride = tokenOverride; _queueFactory = queueFactory;
    }

    /// <summary>What happened to one session in a push pass (for the job log + tests).</summary>
    public enum PushAction { Skipped = 0, Created = 1, Updated = 2, Failed = 3, Enqueued = 4 }

    /// <summary>Per-session outcome.</summary>
    public sealed record SessionPushResult(
        int SessionId, string Title, PushAction Action, string? BackstageId = null, string? Error = null);

    /// <summary>The outcome of one push pass.</summary>
    public sealed record Result(
        bool DirectionActive,
        string? InactiveReason,
        bool SourceAvailable,
        string? UnavailableReason,
        int Created,
        int Updated,
        int Failed,
        int Skipped,
        IReadOnlyList<SessionPushResult> Items,
        int Enqueued = 0)
    {
        public static Result Inactive(string reason) =>
            new(false, reason, false, null, 0, 0, 0, 0, Array.Empty<SessionPushResult>());

        public static Result Unavailable(string reason) =>
            new(true, null, false, reason, 0, 0, 0, 0, Array.Empty<SessionPushResult>());
    }

    /// <summary>
    /// Run one push pass for an edition. Gated on §57 stage 2 (CehToZoho). Pushes every
    /// non-service CEH session: create-if-unlinked / update-if-linked, idempotent by the
    /// stored Backstage id. Never deletes.
    /// </summary>
    public async Task<Result> RunAsync(int eventId, CancellationToken ct = default)
    {
        // §57 DIRECTION GATE — only stage 2 (CehToZoho) is active for the push.
        var direction = await _db.SessionSourceSettings.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .Select(s => (SessionSyncDirection?)s.SyncDirection)
            .FirstOrDefaultAsync(ct) ?? SessionSyncDirection.SessionizeToCeh;
        if (direction != SessionSyncDirection.CehToZoho)
        {
            return Result.Inactive(
                $"session sync direction is stage {(int)direction} ({direction}) — CEH→Zoho push inactive");
        }

        var token = await GetTokenAsync(ct);
        if (token is null)
            return Result.Unavailable("No Zoho access token (token refresh failed).");

        // The first agenda day anchor: the edition's pre-day (master classes) when set,
        // else its start date. day index is 1-based (agenda day 0 is empty).
        var ev = await _db.Events.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new { e.StartDate, e.PreDayDate })
            .FirstOrDefaultAsync(ct);
        var firstDay = ev?.PreDayDate ?? ev?.StartDate;

        var sessions = await _db.Sessions
            .Where(s => s.EventId == eventId && !s.IsServiceSession)
            .ToListAsync(ct);

        // Resolve track NAME → Backstage track ID once per pass (live-verified: the create
        // endpoint requires the track id, not the name). Case-insensitive; an unresolvable
        // CEH track name simply omits the track (it never blocks the push).
        var tracks = await _zoho.GetTracksAsync(token, ct);
        var trackIdByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks) trackIdByName[t.Name] = t.Id;

        // The required, event-specific session type (operator config; no enumerable list).
        var sessionType = _zohoOptions.PushSessionType;

        int created = 0, updated = 0, failed = 0, skipped = 0, enqueued = 0;
        var items = new List<SessionPushResult>(sessions.Count);
        var queue = _queueFactory?.Invoke();
        var prevPending = queue is not null ? await queue.CountPendingAsync(eventId, ct) : 0;

        foreach (var s in sessions)
        {
            if (string.IsNullOrWhiteSpace(s.Title))
            {
                skipped++;
                items.Add(new SessionPushResult(s.Id, s.Title, PushAction.Skipped, s.BackstageSessionId,
                    "session has no title — not pushed"));
                continue;
            }

            var duration = DurationMinutes(s);
            var day = DayIndex(firstDay, s.StartsAt);
            var trackId = !string.IsNullOrWhiteSpace(s.Track)
                          && trackIdByName.TryGetValue(s.Track!, out var tid) ? tid : null;

            if (string.IsNullOrWhiteSpace(s.BackstageSessionId))
            {
                // CREATE — not yet in Zoho.
                var res = await _zoho.CreateSessionAsync(
                    token, day, s.Title, s.Abstract, s.StartsAt, duration, trackId, sessionType, ct);
                if (res.Ok)
                {
                    s.BackstageSessionId = res.Id;
                    s.UpdatedAt = DateTimeOffset.UtcNow;
                    created++;
                    items.Add(new SessionPushResult(s.Id, s.Title, PushAction.Created, res.Id));
                }
                else
                {
                    failed++;
                    items.Add(new SessionPushResult(s.Id, s.Title, PushAction.Failed, null, res.Error));
                }
            }
            else if (queue is not null)
            {
                // UPDATE of an ALREADY-LINKED session (§59): do NOT push inline. ENQUEUE a
                // CehToZoho Update delta carrying the current CEH values; the operator approves
                // it in /Organizer/SyncQueue, and the queue's apply arm pushes to Zoho on
                // approve. EnqueueAsync dedupes per (event, Session, id, Update).
                await queue.EnqueueSessionUpdateAsync(
                    eventId, s.Id, s.Title, SessionSyncDirection.CehToZoho,
                    BuildPushChanges(s), ct);
                enqueued++;
                items.Add(new SessionPushResult(s.Id, s.Title, PushAction.Enqueued, s.BackstageSessionId,
                    "update enqueued for operator approval"));
            }
            else
            {
                // No queue wired (legacy/minimal): UPDATE inline, already linked 1:1 by the id.
                var ok = await _zoho.UpdateSessionAsync(
                    token, s.BackstageSessionId!, s.Title, s.Abstract, s.StartsAt, duration, trackId, sessionType, ct);
                if (ok)
                {
                    s.UpdatedAt = DateTimeOffset.UtcNow;
                    updated++;
                    items.Add(new SessionPushResult(s.Id, s.Title, PushAction.Updated, s.BackstageSessionId));
                }
                else
                {
                    failed++;
                    items.Add(new SessionPushResult(s.Id, s.Title, PushAction.Failed, s.BackstageSessionId,
                        "Zoho session update failed (see logs)"));
                }
            }
        }

        if (created > 0 || updated > 0) await _db.SaveChangesAsync(ct);

        // §59: if any linked-session update was enqueued, notify the operator that new pending
        // deltas need approval (throttled; only fires when the pending count actually rose).
        if (queue is not null && enqueued > 0)
            await queue.NotifyNewAsync(eventId, prevPending, ct);

        return new Result(true, null, true, null, created, updated, failed, skipped, items, enqueued);
    }

    /// <summary>
    /// Build the {Field, Old, New} diff list for a CehToZoho session UPDATE delta. CEH is the
    /// source of truth here, so NewValue carries the current CEH value the operator's approve
    /// will push; OldValue is left null (the queue UI shows it as the value being pushed). Only
    /// non-blank fields are included so a blank CEH value never appears as a "change".
    /// </summary>
    private static IReadOnlyList<SyncFieldChange> BuildPushChanges(Session s)
    {
        var list = new List<SyncFieldChange>();
        if (!string.IsNullOrWhiteSpace(s.Title))
            list.Add(new SyncFieldChange(SyncDeltaQueueService.FieldTitle, null, s.Title));
        if (s.StartsAt is { } st)
            list.Add(new SyncFieldChange(SyncDeltaQueueService.FieldStartsAt, null,
                st.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
        if (s.EndsAt is { } en)
            list.Add(new SyncFieldChange(SyncDeltaQueueService.FieldEndsAt, null,
                en.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(s.Track))
            list.Add(new SyncFieldChange(SyncDeltaQueueService.FieldTrack, null, s.Track));
        if (!string.IsNullOrWhiteSpace(s.Abstract))
            list.Add(new SyncFieldChange(SyncDeltaQueueService.FieldAbstract, null, s.Abstract));
        return list;
    }

    /// <summary>
    /// Push the CURRENT CEH values of ONE already-linked session to Zoho (§57 stage-2 UPDATE
    /// on approve, REQUIREMENTS §59). Used by the delta-approval queue: a stage-2 update of a
    /// linked record is ENQUEUED (not pushed inline) and only pushed here when the operator
    /// approves it. Resolves the track name → Backstage track id exactly like
    /// <see cref="RunAsync"/>. Returns (ok, message). Does NOT re-check the direction gate —
    /// the enqueue side already did — and NEVER creates (a session with no Backstage id is a
    /// caller error here, reported as a failure).
    /// </summary>
    public async Task<(bool Ok, string Message)> UpdateLinkedSessionAsync(
        int eventId, int sessionId, CancellationToken ct = default)
    {
        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.EventId == eventId, ct);
        if (session is null)
            return (false, "The session no longer exists in this edition.");
        if (string.IsNullOrWhiteSpace(session.BackstageSessionId))
            return (false, "The session is not linked to a Zoho session (nothing to update).");
        if (string.IsNullOrWhiteSpace(session.Title))
            return (false, "The session has no title — not pushed.");

        var token = await GetTokenAsync(ct);
        if (token is null) return (false, "No Zoho access token (token refresh failed).");

        var ev = await _db.Events.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new { e.StartDate, e.PreDayDate })
            .FirstOrDefaultAsync(ct);
        var firstDay = ev?.PreDayDate ?? ev?.StartDate;

        string? trackId = null;
        if (!string.IsNullOrWhiteSpace(session.Track))
        {
            var tracks = await _zoho.GetTracksAsync(token, ct);
            trackId = tracks.FirstOrDefault(t =>
                string.Equals(t.Name, session.Track, StringComparison.OrdinalIgnoreCase))?.Id;
        }

        var ok = await _zoho.UpdateSessionAsync(
            token, session.BackstageSessionId!, session.Title, session.Abstract,
            session.StartsAt, DurationMinutes(session), trackId, _zohoOptions.PushSessionType, ct);
        if (!ok) return (false, "Zoho session update failed (see logs).");

        session.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return (true, "Pushed the session's current values to Zoho.");
    }

    private async Task<string?> GetTokenAsync(CancellationToken ct) =>
        _tokenOverride is not null ? await _tokenOverride(ct) : await _zoho.GetAccessTokenAsync(ct);

    /// <summary>
    /// The session's duration in whole minutes: end−start when both are scheduled, else the
    /// <see cref="Session.Length"/> bucket (FullDay → 480 min). Null when nothing is known.
    /// </summary>
    public static int? DurationMinutes(Session s)
    {
        if (s.StartsAt is { } start && s.EndsAt is { } end && end > start)
            return (int)Math.Round((end - start).TotalMinutes);
        return s.Length switch
        {
            SessionLength.FullDay => 480,
            SessionLength.TwentyMin => 20,
            SessionLength.FiftyMin => 50,
            SessionLength.SixtyMin => 60,
            _ => null,
        };
    }

    /// <summary>
    /// The 1-based agenda day index for a session: 1 + (session start date − first agenda
    /// day). Falls back to day 1 when either the anchor or the session start is unknown, or
    /// when the computed offset is negative (a session dated before the anchor).
    /// </summary>
    public static int DayIndex(DateOnly? firstAgendaDay, DateTimeOffset? sessionStart)
    {
        if (firstAgendaDay is not { } anchor || sessionStart is not { } start) return 1;
        var sessionDay = DateOnly.FromDateTime(start.UtcDateTime);
        var offset = sessionDay.DayNumber - anchor.DayNumber;
        return offset >= 0 ? offset + 1 : 1;
    }
}
