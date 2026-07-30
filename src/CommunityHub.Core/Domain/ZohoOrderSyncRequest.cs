namespace CommunityHub.Core.Domain;

/// <summary>
/// §233 — one QUEUED request to reconcile a single Zoho Backstage order, written by the
/// order-change webhook (which now only enqueues + acks — it never calls Zoho). The
/// once-per-minute <c>ZohoWebhookDrainJob</c> coalesces everything queued in the window
/// into ONE Zoho pull, so a burst of webhooks (e.g. 5 orders in 2 minutes) can never fan
/// out into one full API pull per webhook.
/// </summary>
public class ZohoOrderSyncRequest
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The Backstage order id the webhook reported changed.</summary>
    public string OrderId { get; set; } = string.Empty;

    /// <summary>True when the webhook payload signalled a cancel/delete — lets the drain
    /// job distinguish a REAL whole-order cancellation from a transient empty pull.</summary>
    public bool CancelHint { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>Set when the drain job has reconciled (or given up on) this request.
    /// Null = still pending. Processed rows are kept briefly as an audit trail and
    /// pruned opportunistically by the drain job.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>How many drain runs have attempted this request (ambiguous pulls retry).</summary>
    public int Attempts { get; set; }

    /// <summary>Short outcome note ("reconciled", "gave-up-ambiguous", …).</summary>
    public string? Outcome { get; set; }
}
