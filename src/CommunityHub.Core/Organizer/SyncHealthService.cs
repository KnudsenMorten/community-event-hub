using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// The at-a-glance health of the CEH⇄Zoho mirror for one edition (REQUIREMENTS §132,
/// the §125 trust goal). Three states:
/// <list type="bullet">
/// <item><see cref="NeverSynced"/> — no successful Backstage sync has ever run for this
/// edition (a brand-new edition, or the job has never fired).</item>
/// <item><see cref="InSync"/> — the last successful sync is within the staleness window.</item>
/// <item><see cref="Stale"/> — the last success is older than the window: the sync may
/// have stopped and the mirror is drifting from Zoho.</item>
/// </list>
/// </summary>
public enum SyncHealthStatus
{
    NeverSynced,
    InSync,
    Stale,
}

/// <summary>
/// A read-only snapshot proving (or disproving) that the CEH mirror tracks Zoho's ACTIVE
/// set (REQUIREMENTS §132/§125). All timestamps are UTC. <see cref="GeneratedAtUtc"/> is
/// the reference "now" every age / staleness property is computed against, so the snapshot
/// is internally consistent regardless of when the view renders it.
/// </summary>
public sealed class SyncHealthSnapshot
{
    /// <summary>"Now" the snapshot was computed at (UTC) — the reference for every age.</summary>
    public DateTimeOffset GeneratedAtUtc { get; init; }

    /// <summary>How old the last successful sync may be before it is flagged stale.</summary>
    public TimeSpan StaleAfter { get; init; }

    // --- Sync marker (the §127 last-successful-sync row) ---------------------
    /// <summary>When the authoritative full Backstage sync last succeeded, or null if it
    /// never has for this edition.</summary>
    public DateTimeOffset? LastSuccessAt { get; init; }

    /// <summary>When a Zoho webhook (the real-time push leg) was last received + processed,
    /// or null if none has arrived (or webhooks aren't wired for this edition).</summary>
    public DateTimeOffset? LastWebhookAt { get; init; }

    /// <summary>The human one-line summary the last run recorded, for display.</summary>
    public string? LastRunSummary { get; init; }

    // --- Live mirror counts (Orders + Attendees) ----------------------------
    public int OrdersActive { get; init; }
    public int OrdersCancelled { get; init; }
    public int AttendeesActive { get; init; }
    public int AttendeesCancelled { get; init; }

    // --- Drift indicators (local, read-only) --------------------------------
    /// <summary>Active attendees whose ticket has no linked order row (OrderId null, or it
    /// points at an order the mirror doesn't hold) — an orphaned ticket.</summary>
    public int AttendeesWithoutOrder { get; init; }

    /// <summary>Active orders that have no active attendee — an empty order shell.</summary>
    public int OrdersWithoutAttendees { get; init; }

    // --- Derived ------------------------------------------------------------
    /// <summary>True once a full sync has succeeded at least once for the edition.</summary>
    public bool HasEverSynced => LastSuccessAt is not null;

    public int OrdersTotal => OrdersActive + OrdersCancelled;
    public int AttendeesTotal => AttendeesActive + AttendeesCancelled;

    /// <summary>Age of the last successful sync relative to <see cref="GeneratedAtUtc"/>
    /// (null if never synced). Never negative — a future stamp (clock skew) clamps to zero.</summary>
    public TimeSpan? SyncAge => Age(LastSuccessAt);

    /// <summary>Age of the last received webhook relative to <see cref="GeneratedAtUtc"/>
    /// (null if none), clamped to zero.</summary>
    public TimeSpan? WebhookAge => Age(LastWebhookAt);

    /// <summary>The three-way health state driving the status badge.</summary>
    public SyncHealthStatus Status =>
        !HasEverSynced ? SyncHealthStatus.NeverSynced
        : SyncAge!.Value > StaleAfter ? SyncHealthStatus.Stale
        : SyncHealthStatus.InSync;

    /// <summary>True when at least one drift indicator is non-zero — orphaned tickets or
    /// empty orders the organizer should look into.</summary>
    public bool HasDrift => AttendeesWithoutOrder > 0 || OrdersWithoutAttendees > 0;

    private TimeSpan? Age(DateTimeOffset? at) =>
        at is null
            ? null
            : (GeneratedAtUtc > at.Value ? GeneratedAtUtc - at.Value : TimeSpan.Zero);
}

/// <summary>
/// Builds the organizer <b>CEH⇄Zoho Sync-Health</b> snapshot (REQUIREMENTS §132): a
/// read-only aggregation over the <see cref="SyncRun"/> marker plus the
/// <see cref="Order"/> / <see cref="Attendee"/> mirror that proves CEH still tracks Zoho's
/// ACTIVE set (the §125 trust goal). It writes nothing; calling
/// <see cref="BuildAsync"/> twice yields the same view of the data.
///
/// <para>Distinct from <see cref="DataFreshnessService"/> (which reports a recency stamp
/// per feed across the whole hub): this answers the narrower "is the Zoho mirror current,
/// and is it internally consistent (no orphaned tickets / empty orders)?" question.</para>
/// </summary>
public sealed class SyncHealthService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Staleness window for the attendee-backstage sync. The authoritative full sync runs
    /// hourly (<c>0 20 * * * *</c>) and a webhook pushes in real time, so a last-success
    /// older than this means several scheduled runs have been missed and the mirror is at
    /// risk of drifting from Zoho. A display hint, not a hard SLA — a plain constant.
    /// </summary>
    public static readonly TimeSpan SyncStaleAfter = TimeSpan.FromHours(6);

    public SyncHealthService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<SyncHealthSnapshot> BuildAsync(int eventId, CancellationToken ct = default)
    {
        var marker = await _db.SyncRuns
            .Where(s => s.EventId == eventId && s.Key == SyncRun.AttendeeBackstageKey)
            .Select(s => new { s.LastSuccessAt, s.LastWebhookAt, s.Summary })
            .FirstOrDefaultAsync(ct);

        // --- Live mirror counts, by MirrorState (each resolved server-side) ----
        var ordersActive = await _db.Orders
            .CountAsync(o => o.EventId == eventId && o.MirrorState == MirrorState.Active, ct);
        var ordersCancelled = await _db.Orders
            .CountAsync(o => o.EventId == eventId && o.MirrorState == MirrorState.Cancelled, ct);
        var attendeesActive = await _db.Attendees
            .CountAsync(a => a.EventId == eventId && a.MirrorState == MirrorState.Active, ct);
        var attendeesCancelled = await _db.Attendees
            .CountAsync(a => a.EventId == eventId && a.MirrorState == MirrorState.Cancelled, ct);

        // --- Drift: active attendees with no order row they link to (orphans). ---
        //     OrderId null, OR an OrderId that no Order in this edition carries.
        var attendeesWithoutOrder = await _db.Attendees
            .CountAsync(a => a.EventId == eventId
                             && a.MirrorState == MirrorState.Active
                             && (a.OrderId == null
                                 || !_db.Orders.Any(o => o.EventId == eventId
                                                         && o.BackstageOrderId == a.OrderId)), ct);

        // --- Drift: active orders with no active attendee (empty shells). ---
        var ordersWithoutAttendees = await _db.Orders
            .CountAsync(o => o.EventId == eventId
                             && o.MirrorState == MirrorState.Active
                             && !_db.Attendees.Any(a => a.EventId == eventId
                                                        && a.MirrorState == MirrorState.Active
                                                        && a.OrderId == o.BackstageOrderId), ct);

        return new SyncHealthSnapshot
        {
            GeneratedAtUtc = _clock.GetUtcNow(),
            StaleAfter = SyncStaleAfter,
            LastSuccessAt = marker?.LastSuccessAt,
            LastWebhookAt = marker?.LastWebhookAt,
            LastRunSummary = marker?.Summary,
            OrdersActive = ordersActive,
            OrdersCancelled = ordersCancelled,
            AttendeesActive = attendeesActive,
            AttendeesCancelled = attendeesCancelled,
            AttendeesWithoutOrder = attendeesWithoutOrder,
            OrdersWithoutAttendees = ordersWithoutAttendees,
        };
    }
}
