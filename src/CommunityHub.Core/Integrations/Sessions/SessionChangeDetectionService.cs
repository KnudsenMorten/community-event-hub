using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Sessions;

/// <summary>
/// The §38e SESSION CHANGE DETECTION engine (operator 2026-06-25). It pulls the CURRENT
/// Zoho Backstage agenda, matches each CEH session by <see cref="Session.BackstageSessionId"/>,
/// and — when the Backstage start/end/room differs from the value CEH last stored AND
/// that stored value was already non-null (a real CHANGE, not the first populate) — emails
/// every speaker on the session, subject to the feature gates below. On every pass it
/// refreshes the stored <c>Backstage*</c> snapshot + stamps
/// <see cref="Session.BackstageChangeCheckedAt"/>.
///
/// <b>FIRST POPULATE is silent.</b> When the stored Backstage* values are all null the
/// engine SEEDS them and NEVER emails (there is no "previous" to have changed from).
///
/// <b>GATES per speaker (all must pass to email).</b>
///   1. KILL SWITCH — the <c>session-change-alerts</c> feature is enabled for the edition.
///   2. RELEASED RING — the speaker's effective ring ≤ the feature's released ring
///      (<see cref="FeatureGateService.GetReleasedRingAsync"/>).
///   3. DATE GATE — ring 0 / ring 1 are NEVER date-limited (so a ring-1 tester gets the
///      alert immediately); ring 2 / ring 3 (Broad) are held until
///      <c>now &gt;= FeatureSetting.ActiveFromForBroadRings</c> (1 Dec 2026 by config).
/// The send itself goes through the normal RING-GATED participant email path
/// (<see cref="IEmailSender"/> tagged with the feature key), so the sender re-checks the
/// recipient ring as a backstop — this IS a participant email; it is NOT ring-exempt.
///
/// <b>SOURCE AVAILABILITY.</b> The Backstage agenda API needs the
/// <c>ZohoBackstage.agenda.READ</c> scope. Until granted (and
/// <see cref="ZohoOptions.AgendaReadEnabled"/> set) the pull returns IsAvailable=false and
/// this engine NO-OPS with a clear result — it never treats an empty pull as "everything
/// changed".
/// </summary>
public sealed class SessionChangeDetectionService
{
    /// <summary>The §38e feature key (kill switch + released-ring + date gate).</summary>
    public const string FeatureKey = "session-change-alerts";

    /// <summary>
    /// REQUIREMENTS §38e / §52 — the DEFAULT broad-rings auto-enable date for the
    /// session-change-alerts feature. When an organizer has NOT persisted
    /// <see cref="FeatureSetting.ActiveFromForBroadRings"/> (it is null), the date gate
    /// falls back to this constant so ring 2 / ring 3 (Broad) speakers are still date-held
    /// until <b>1 Dec 2026 (UTC)</b> by default — they are NOT silently let through. An
    /// organizer who sets the field still overrides this default. Ring 0 / ring 1 remain
    /// unrestricted regardless (testing rings get the alert immediately).
    /// </summary>
    public static readonly DateTimeOffset DefaultBroadRingsActiveFrom =
        new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Category = "session-change";
    private const string TemplateName = "session-time-location-changed";

    private readonly CommunityHubDbContext _db;
    private readonly ZohoClient _zoho;
    private readonly ZohoOptions _zohoOptions;
    private readonly FeatureGateService _gate;
    private readonly RingResolver _rings;
    private readonly IEmailSender _sender;
    private readonly IEmailContextAccessor _context;
    private readonly EmailTemplateProvider? _templates;
    private readonly TimeProvider _clock;

    // §59 DELTA-APPROVAL QUEUE. A detected change is ENQUEUED here (Pending) for the
    // operator to approve/reject — it is NO LONGER auto-applied + emailed inline. When the
    // queue service is null (a minimal test wiring) the engine still detects + seeds and
    // simply records the change count without enqueuing.
    private readonly SyncDeltaQueueService? _queue;

    // Overridable pull seam (default = the real ZohoClient agenda call). Tests inject a
    // canned BackstageSessionsResult so the engine is exercised without HTTP. Production
    // wiring leaves this null ⇒ the default below calls Zoho.
    private readonly Func<CancellationToken, Task<BackstageSessionsResult>>? _pullOverride;

    public SessionChangeDetectionService(
        CommunityHubDbContext db, ZohoClient zoho, ZohoOptions zohoOptions,
        FeatureGateService gate, RingResolver rings, IEmailSender sender,
        IEmailContextAccessor context, EmailTemplateProvider? templates = null,
        TimeProvider? clock = null,
        Func<CancellationToken, Task<BackstageSessionsResult>>? pullOverride = null,
        SyncDeltaQueueService? queue = null)
    {
        _db = db; _zoho = zoho; _zohoOptions = zohoOptions; _gate = gate;
        _rings = rings; _sender = sender; _context = context; _templates = templates;
        _clock = clock ?? TimeProvider.System;
        _pullOverride = pullOverride;
        _queue = queue;
    }

    /// <summary>The outcome of one detection pass, for the job log + tests.</summary>
    /// <remarks>
    /// §59: a real change is now ENQUEUED to the delta-approval queue (<see cref="Enqueued"/>)
    /// instead of auto-applied + emailed. <see cref="Emailed"/> stays for back-compat but is
    /// always 0 now — the speaker email is sent on APPROVE by
    /// <see cref="SyncDeltaQueueService"/>, not here.
    /// </remarks>
    public sealed record Result(
        bool SourceAvailable,
        string? UnavailableReason,
        int Matched,
        int Seeded,
        int Changed,
        int Emailed,
        int EmailsSkippedByGate,
        int Unmatched = 0,
        bool DirectionInactive = false,
        int Enqueued = 0)
    {
        public static Result Unavailable(string reason) =>
            new(false, reason, 0, 0, 0, 0, 0);

        /// <summary>
        /// §57 gate: the edition's sync direction is not stage 3 (Zoho→CEH), so this
        /// engine is INACTIVE — it pulls nothing, matches nothing, and writes nothing.
        /// </summary>
        public static Result Inactive(string reason) =>
            new(false, reason, 0, 0, 0, 0, 0, DirectionInactive: true);
    }

    /// <summary>
    /// Run one detection pass for an edition. Pulls the current Backstage agenda, diffs
    /// each matched session, seeds first-populates silently, and emails speakers for real
    /// changes that pass the per-speaker gates.
    /// </summary>
    public async Task<Result> RunAsync(int eventId, CancellationToken ct = default)
    {
        // §57 DIRECTION GATE. This Zoho→CEH engine is only active at stage 3
        // (SessionSyncDirection.ZohoToCeh). At the default stage 1 (Sessionize→CEH) — and at
        // stage 2 (CEH→Zoho) — it is INERT: it pulls nothing from Backstage and writes
        // nothing back, so a first-populate can't silently happen before the operator opts in.
        var direction = await _db.SessionSourceSettings.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .Select(s => (SessionSyncDirection?)s.SyncDirection)
            .FirstOrDefaultAsync(ct) ?? SessionSyncDirection.SessionizeToCeh;
        if (direction != SessionSyncDirection.ZohoToCeh)
        {
            return Result.Inactive(
                $"session sync direction is stage {(int)direction} ({direction}) — Zoho→CEH change detection inactive");
        }

        // Pull the current agenda. Unavailable ⇒ no-op (never fake / never email).
        var pull = await PullAsync(ct);
        if (!pull.IsAvailable)
        {
            return Result.Unavailable(pull.UnavailableReason ?? "Backstage agenda unavailable.");
        }

        // ALL sessions for this edition — we match BOTH already-linked sessions (by their
        // stored BackstageSessionId) AND not-yet-linked ones (by normalized title), so the
        // engine actually populates BackstageSessionId on the first confident match instead
        // of being a permanent no-op (it previously only loaded BackstageSessionId != null).
        var sessions = await _db.Sessions
            .Where(s => s.EventId == eventId)
            .ToListAsync(ct);

        // CEH sessions already linked to a Backstage id, keyed by that id.
        var cehById = new Dictionary<string, Session>(StringComparer.OrdinalIgnoreCase);
        // Unlinked CEH sessions keyed by normalized title (for first-populate matching). A
        // title that is blank or collides across >1 unlinked session is dropped from the map
        // so we never CONFIDENTLY match an ambiguous title (we skip + log those instead).
        var cehByTitle = new Dictionary<string, Session?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sessions)
        {
            if (!string.IsNullOrWhiteSpace(s.BackstageSessionId))
            {
                cehById[s.BackstageSessionId!] = s;
                continue;
            }
            var key = NormalizeTitle(s.Title);
            if (key.Length == 0) continue;
            if (cehByTitle.ContainsKey(key)) cehByTitle[key] = null; // ambiguous → unusable
            else cehByTitle[key] = s;
        }

        // §59: the kill switch still gates whether we enqueue a change at all. The
        // per-speaker released-ring + date gates that previously decided who got the inline
        // speaker email no longer apply here — the speaker email is sent on APPROVE by the
        // queue's apply step (an operator-initiated action), so detection only needs the
        // feature to be enabled to enqueue.
        var featureEnabled = await _gate.IsFeatureEnabledAsync(FeatureKey, eventId, ct);
        var now = _clock.GetUtcNow();

        // §59: snapshot the pending-queue count BEFORE this pass so NotifyNew only emails the
        // operator when this run actually adds new pending items (a quiet re-detection that
        // re-finds the same already-queued change won't re-notify).
        var pendingBefore = _queue is null ? 0 : await _queue.CountPendingAsync(eventId, ct);

        int matched = 0, seeded = 0, changed = 0, emailed = 0, skipped = 0, unmatched = 0, enqueued = 0;

        foreach (var current in pull.Sessions)
        {
            if (string.IsNullOrWhiteSpace(current.SessionId)) { unmatched++; continue; }

            // Resolve the CEH session for this Backstage session. Prefer the exact id link
            // (already populated on a prior pass); else fall back to a confident, unambiguous
            // NORMALIZED-TITLE match on an unlinked CEH session. Anything else is skipped
            // (logged) rather than mis-assigned — be conservative.
            Session? session;
            var firstLink = false;
            if (cehById.TryGetValue(current.SessionId, out var linked))
            {
                session = linked;
            }
            else if (cehByTitle.TryGetValue(NormalizeTitle(current.Title), out var byTitle)
                     && byTitle is not null)
            {
                session = byTitle;
                firstLink = true;
                // Claim it so a second Backstage session with the same title can't re-match it.
                cehByTitle[NormalizeTitle(current.Title)] = null;
            }
            else
            {
                unmatched++;
                continue;
            }

            matched++;

            // FIRST LINK by title: set the id now. A title-matched session has never been
            // seeded (BackstageSessionId was null), so this always goes down the silent
            // first-populate path below.
            if (firstLink) session.BackstageSessionId = current.SessionId;

            // A fresh title link (BackstageSessionId was null) is ALWAYS a first-populate:
            // seed silently, never email — there is no trusted "previous" to diff against.
            var hadAnyStored = !firstLink && (session.BackstageStartsAt is not null
                || session.BackstageEndsAt is not null
                || session.BackstageRoom is not null);

            var timeChanged = session.BackstageStartsAt != current.StartsAt
                || session.BackstageEndsAt != current.EndsAt;
            var roomChanged = !RoomEquals(session.BackstageRoom, current.Room);

            // Snapshot the OLD (currently-stored) values — these are the CEH-side "old"
            // that a queued Update diffs against.
            var oldStart = session.BackstageStartsAt;
            var oldEnd = session.BackstageEndsAt;
            var oldRoom = session.BackstageRoom;

            if (!hadAnyStored)
            {
                // FIRST populate: seed the baseline silently (auto-write is correct here —
                // there is no "previous" to approve). Stamp the check time.
                session.BackstageSessionId ??= current.SessionId;
                session.BackstageStartsAt = current.StartsAt;
                session.BackstageEndsAt = current.EndsAt;
                session.BackstageRoom = current.Room;
                session.BackstageChangeCheckedAt = now;
                seeded++;
                continue;
            }

            // Even when nothing changed we still stamp the check time (proof the engine ran)
            // — this is NOT a content write, so it's safe to do without approval.
            session.BackstageChangeCheckedAt = now;

            if (!timeChanged && !roomChanged) continue;

            // §59: a REAL change is NO LONGER auto-applied + emailed inline. We DO NOT
            // overwrite the stored Backstage* snapshot here (so the old→new diff survives
            // until a decision); instead we ENQUEUE a Pending Update delta for the operator
            // to approve/reject. The speaker email is sent on APPROVE by the queue's apply
            // step. Kept gated on stage 3 (the §57 direction gate above) + the feature kill
            // switch below.
            changed++;
            if (_queue is null || !featureEnabled) continue;

            var fieldChanges = SyncDeltaQueueService.BuildSessionChanges(
                oldStart, oldEnd, oldRoom, current.StartsAt, current.EndsAt, current.Room);

            await _queue.EnqueueSessionUpdateAsync(
                eventId, session.Id, session.Title, SessionSyncDirection.ZohoToCeh,
                fieldChanges, ct);
            enqueued++;
        }

        if (matched > 0) await _db.SaveChangesAsync(ct);

        // §59: notify the operator if this pass newly increased the pending queue.
        if (_queue is not null && enqueued > 0)
        {
            await _queue.NotifyNewAsync(eventId, pendingBefore, ct);
        }

        return new Result(true, null, matched, seeded, changed, emailed, skipped, unmatched, Enqueued: enqueued);
    }

    /// <summary>
    /// Normalize a session title for the first-populate match: trim + collapse internal
    /// whitespace + case-insensitive (the dictionary uses OrdinalIgnoreCase). Blank when
    /// the title is null/empty so it never becomes a match key.
    /// </summary>
    private static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        return string.Join(' ', title.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Pull the current Backstage agenda (test seam overrides the live call).</summary>
    private async Task<BackstageSessionsResult> PullAsync(CancellationToken ct)
    {
        if (_pullOverride is not null) return await _pullOverride(ct);

        var token = await _zoho.GetAccessTokenAsync(ct);
        if (token is null)
        {
            return BackstageSessionsResult.Unavailable("No Zoho access token (token refresh failed).");
        }
        return await _zoho.GetBackstageSessionsAsync(token, ct);
    }

    /// <summary>
    /// The DATE gate (§38e, testable in isolation): ring 0 / ring 1 are never
    /// date-limited; ring 2 / ring 3 (Broad) are gated until <paramref name="now"/> ≥ the
    /// effective broad-rings date. When <paramref name="activeFromBroad"/> is null we fall
    /// back to <see cref="DefaultBroadRingsActiveFrom"/> (1 Dec 2026 UTC) per REQUIREMENTS
    /// §38e/§52 — so broad rings are date-held by default rather than let through.
    /// </summary>
    public static bool IsWithinDateGate(
        Ring effectiveRing, DateTimeOffset? activeFromBroad, DateTimeOffset now)
    {
        // Ring 0 + ring 1 ignore the date gate entirely.
        if ((int)effectiveRing <= (int)Ring.Ring1) return true;
        // Ring 2 + ring 3: no configured date ⇒ use the seeded default (don't let through);
        // an organizer-set date overrides it. Hold until the effective date.
        var effectiveDate = activeFromBroad ?? DefaultBroadRingsActiveFrom;
        return now >= effectiveDate;
    }

    private static bool RoomEquals(string? a, string? b) =>
        string.Equals(
            string.IsNullOrWhiteSpace(a) ? null : a.Trim(),
            string.IsNullOrWhiteSpace(b) ? null : b.Trim(),
            StringComparison.OrdinalIgnoreCase);
}
