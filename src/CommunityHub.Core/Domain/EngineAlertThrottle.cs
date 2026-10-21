namespace CommunityHub.Core.Domain;

/// <summary>
/// §1256b — the last time an ops alert with a given throttle key was actually sent, stored in
/// SQL so the throttle SURVIVES A HOST RESTART.
///
/// <para><b>Why this exists.</b> <c>EngineAlertSender</c> throttled on an in-memory dictionary
/// held by a singleton, which is correct only while the process lives. The Functions host
/// recycles between timer ticks, so the dictionary came back EMPTY on nearly every run and the
/// 6-hour window never applied: measured in PROD on 2026-09-20, the Sessionize-disappearance
/// alert arrived every 10 minutes — once per tick — for hours.</para>
///
/// <para>🔑 The in-memory map is kept as a first-line check (free, no round-trip); this row is
/// the authority when the map is cold. One row per key, updated in place — it is a stamp, not a
/// history, so it cannot grow without bound.</para>
///
/// <para>FLEET-WIDE, not edition-scoped: a throttle key already carries whatever scope it needs
/// (e.g. <c>sessionize-disappearance-1</c> embeds the event id), and an alert that is not about
/// one edition must still be throttleable.</para>
/// </summary>
public class EngineAlertThrottle
{
    public int Id { get; set; }

    /// <summary>The caller's throttle key (e.g. <c>sessionize-disappearance-1</c>). Unique.</summary>
    public string ThrottleKey { get; set; } = string.Empty;

    /// <summary>When an alert with this key was last SENT (never: when it was suppressed).</summary>
    public DateTimeOffset LastSentAt { get; set; }

    /// <summary>The last subject sent under this key — for reading the table during an incident.</summary>
    public string? LastSubject { get; set; }
}
