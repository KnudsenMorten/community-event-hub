namespace CommunityHub.Core.Domain;

/// <summary>
/// A small per-edition marker recording the LAST SUCCESSFUL run of a one-way
/// Zoho→CEH sync (REQUIREMENTS §125/§127). One row per (<see cref="EventId"/>,
/// <see cref="Key"/>); the authoritative attendee/order sync upserts it on every
/// successful pull so telemetry/dashboards can show an "Updated &lt;t&gt; UTC"
/// footer (§127, §69) sourced from the trusted CEH mirror instead of the live
/// Zoho call's wall-clock. Also carries the active/cancelled tallies the run
/// produced, for at-a-glance freshness/health.
/// </summary>
public class SyncRun
{
    public int Id { get; set; }

    // --- Edition scope ------------------------------------------------------
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The sync this row marks (the upsert key within an edition), e.g.
    /// <see cref="AttendeeBackstageKey"/>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The well-known <see cref="Key"/> for the authoritative attendee + order
    /// Backstage mirror sync (REQUIREMENTS §125).</summary>
    public const string AttendeeBackstageKey = "attendee-backstage";

    /// <summary>When the sync last completed successfully (UTC). This is the value
    /// telemetry's "Updated &lt;t&gt;" footer reads (§127).</summary>
    public DateTimeOffset LastSuccessAt { get; set; }

    /// <summary>When a Zoho Backstage webhook was last RECEIVED + processed for this
    /// edition (UTC), or null if none has ever arrived. The real-time leg of the
    /// mirror (<see cref="AttendeeBackstageKey"/>) is fed by
    /// <c>ZohoOrderWebhook</c>; surfacing this on the §132 Sync-Health dashboard lets
    /// an organizer see whether incremental push updates are flowing or whether the
    /// edition is relying on the hourly full-sync safety-net alone.</summary>
    public DateTimeOffset? LastWebhookAt { get; set; }

    // --- Last-run tallies (active set after reconcile) ----------------------
    public int OrdersActive { get; set; }
    public int OrdersCancelled { get; set; }
    public int AttendeesActive { get; set; }
    public int AttendeesCancelled { get; set; }

    /// <summary>A short human summary of the last run (created/updated/cancelled), for display.</summary>
    public string? Summary { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
