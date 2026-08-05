namespace CommunityHub.Core.Domain.Evaluation;

/// <summary>
/// §743 §8.1 — ONE attendee rating. The only high-volume table in Session Evaluation, and the only
/// one that must never be updated in place.
/// </summary>
/// <remarks>
/// <para>🔒 <b>APPEND-ONLY. Corrections are new rows or flags, never edits.</b> That is what makes
/// the §5.4 recomputation safe to run repeatedly, and it is why no computed score is stored here as
/// authoritative — every metric is derived at query time. Baking a percentage into the record would
/// lock the product into today's formula permanently.</para>
///
/// <para>🔒 <b>Both channels are ONE instrument.</b> A QR submission and a button press are the same
/// measurement — same four ratings, same weights, same threshold, same score.
/// <see cref="Source"/> exists for diagnostics, NOT for segmenting the headline figure, and there is
/// deliberately no separate QR table.</para>
///
/// <para>🔒 <b>The two timestamps are not interchangeable.</b>
/// <see cref="CollectionTimestamp"/> is when the attendee actually pressed, and it drives session
/// attribution and every metric. <see cref="ReceivedTimestamp"/> is when we accepted the record and
/// is for diagnostics, duplicate detection and audit ONLY — it must never drive attribution. A
/// press is attributed to the session running at its collection time no matter how much later it
/// arrives, because offline caching makes late data normal operation rather than an error.</para>
/// </remarks>
public class EvaluationResponse
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>
    /// The unit that captured it, or NULL for a QR submission. Opaque string, matched exactly.
    /// </summary>
    public string? SerialNumber { get; set; }

    /// <summary>
    /// The session this press belongs to — resolved from the device's room and
    /// <see cref="CollectionTimestamp"/> (device) or known from the token (QR).
    /// </summary>
    /// <remarks>
    /// 🔑 <b>NULL is a legitimate, documented state, not a failure</b> (§5.3): a response whose
    /// collection time falls in no session window is stored and attributed to the venue and date
    /// only. It is excluded from every session-scoped metric but STILL COUNTS toward the event-level
    /// pooled score — it is real feedback from a real attendee on a real day; only the attribution
    /// is missing. A rising count of these is the earliest signal of a drifted clock or a schedule
    /// that no longer matches reality, which is why it is surfaced per device on the health board.
    /// </remarks>
    public int? SessionId { get; set; }

    /// <summary>1–4. The SOURCE OF TRUTH. Colour, label text and weight are never stored here —
    /// they are presentation mappings resolved at render time, so relabelling or re-weighting can
    /// never invalidate history.</summary>
    public int Rating { get; set; }

    /// <summary>UTC. When the button was pressed. Drives attribution and every metric.</summary>
    public DateTimeOffset CollectionTimestamp { get; set; }

    /// <summary>UTC. When we accepted it. Diagnostics, dedupe and audit only — NEVER attribution.</summary>
    public DateTimeOffset ReceivedTimestamp { get; set; }

    /// <summary>
    /// Device-generated. 🔒 Ingestion is IDEMPOTENT on (SerialNumber, DeviceRecordId) — a unique index
    /// enforces it — so a unit retrying a cached batch cannot inflate the counts. Null for QR.
    /// </summary>
    public string? DeviceRecordId { get; set; }

    /// <summary>"device" or "qr". Diagnostics only; both score identically.</summary>
    public string Source { get; set; } = EvaluationResponseSources.Device;

    /// <summary>As reported by the unit. Fielded devices run mixed versions during a staggered OTA.</summary>
    public string? FirmwareVersion { get; set; }

    /// <summary>The device's last successful clock sync, as reported. Basis for the skew check.</summary>
    public DateTimeOffset? ClockSyncedAt { get; set; }

    /// <summary>
    /// Set when <see cref="CollectionTimestamp"/> failed the skew tolerance.
    /// </summary>
    /// <remarks>
    /// 🔒 The record is still COUNTED — flagging is not filtering. The flag travels with the row so
    /// an implausible session result can be traced to a drifting unit rather than blamed on the
    /// speaker. And we never silently rewrite a device timestamp to "correct" drift: any correction
    /// may only ever be a separate, clearly-labelled derived value.
    /// </remarks>
    public bool TimestampSuspect { get; set; }

    /// <summary>
    /// QR submissions only. NEVER used in any calculation, and the most sensitive data in the
    /// system: visible to the session's own speakers and to organisers only — never on the
    /// scoreboard, the signage feed, or any aggregate endpoint.
    /// </summary>
    public string? FreeText { get; set; }
}

/// <summary>The two collection channels. One instrument, two delivery paths.</summary>
public static class EvaluationResponseSources
{
    public const string Device = "device";
    public const string Qr = "qr";
}
