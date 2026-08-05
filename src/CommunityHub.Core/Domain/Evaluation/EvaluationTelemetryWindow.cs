namespace CommunityHub.Core.Domain.Evaluation;

/// <summary>
/// §753 — one FROM/TO line of the health-telemetry schedule: a period during which devices are
/// allowed to send health telemetry.
/// </summary>
/// <remarks>
/// <para>🔑 <b>The soft half of a two-part switch</b> (operator 2026-08-01: <i>"a yes/no per device
/// with a hard disable … + ability with a soft-disable which follows a schedule, where it has
/// multiple lines from/to"</i>). The hard switch lives on the device
/// (<see cref="EvaluationDevice.HealthTelemetryEnabled"/>) and always wins; these windows decide
/// WHEN an otherwise-enabled device may send.</para>
///
/// <para>🔒 <b>Times are stored in UTC and entered in Copenhagen time</b> (operator: <i>"copenhagen
/// time"</i>). This is not pedantry: Denmark is UTC+1 in February, so "midnight 9 Feb" is
/// <c>2027-02-08T23:00Z</c>. Storing what the operator typed and calling it UTC would start the
/// event window an hour late — during the hour the venue is opening.</para>
///
/// <para>⚠️ <b>Outside every window, telemetry is OFF.</b> A schedule with no line covering "now"
/// silences the fleet, which also silences battery level and cached-record count — the two figures
/// the device screen exists to catch a failure with. The operator accepted this and will cover
/// testing and install week with their own lines rather than have the heartbeat split out
/// (<i>"i would just add multiple ranges of dates including testing now and install week"</i>), so
/// the UI's job is to make "telemetry is currently OFF" impossible to miss.</para>
/// </remarks>
public class EvaluationTelemetryWindow
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>
    /// Null = applies to EVERY device on the event, which is the normal case. A device id narrows the
    /// line to one unit, for the occasion where a single suspect device needs telemetry outside the
    /// fleet's schedule.
    /// </summary>
    public int? EvaluationDeviceId { get; set; }
    public EvaluationDevice? EvaluationDevice { get; set; }

    /// <summary>Inclusive start, UTC.</summary>
    public DateTimeOffset FromUtc { get; set; }

    /// <summary>Exclusive end, UTC.</summary>
    public DateTimeOffset ToUtc { get; set; }

    /// <summary>Why this line exists — "install week", "event", "bench testing". Operator-facing.</summary>
    public string? Label { get; set; }

    /// <summary>
    /// Lets a line be parked without deleting it, so last year's window can be kept as a template.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
