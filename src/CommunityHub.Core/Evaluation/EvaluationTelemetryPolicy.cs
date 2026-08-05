using CommunityHub.Core.Domain.Evaluation;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §753 — THE decision on whether a device may send health telemetry right now.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Authoritative and pure.</b> Every surface that answers "is telemetry on?" — the
/// provisioning response, the heartbeat response, and the organiser screen — must call this. Two
/// implementations of a yes/no rule is how a screen ends up saying "on" while the fleet is silent,
/// which is the §-two-switch-trap in miniature: a GUI that reports a state the device does not have.
/// No clock and no database of its own, so it is fully testable.</para>
///
/// <para><b>The rule, in order:</b>
/// <list type="number">
///   <item>Device revoked (<c>IsActive == false</c>) ⇒ off. A revoked unit sends nothing at all.</item>
///   <item>Hard switch off ⇒ off, whatever the schedule says.</item>
///   <item>No active window covers now ⇒ off.</item>
///   <item>Otherwise ⇒ on.</item>
/// </list></para>
/// </remarks>
public static class EvaluationTelemetryPolicy
{
    /// <summary>Why telemetry is off — so a screen can say which switch did it, not just "off".</summary>
    public enum Reason
    {
        /// <summary>Telemetry is on.</summary>
        Enabled = 0,

        /// <summary>The unit is revoked.</summary>
        DeviceInactive = 1,

        /// <summary>The per-device hard switch is off.</summary>
        HardDisabled = 2,

        /// <summary>The hard switch is on, but no schedule line covers this moment.</summary>
        OutsideSchedule = 3,
    }

    /// <summary>The answer, with the reason and (when off for schedule) when it next comes on.</summary>
    public sealed record Decision(bool Enabled, Reason Why, DateTimeOffset? NextOnAt, DateTimeOffset? OffAt);

    /// <summary>
    /// Evaluate the policy for one device at one instant.
    /// </summary>
    /// <param name="device">The unit. Its hard switch and revocation state are read.</param>
    /// <param name="windows">
    /// Every window in scope for this device — that is, the event-wide lines (<c>EvaluationDeviceId
    /// is null</c>) PLUS the lines naming this device. The caller does the fetching; this decides.
    /// </param>
    /// <param name="nowUtc">The instant to judge.</param>
    public static Decision Evaluate(
        EvaluationDevice device,
        IEnumerable<EvaluationTelemetryWindow> windows,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(windows);

        if (!device.IsActive) return new Decision(false, Reason.DeviceInactive, null, null);
        if (!device.HealthTelemetryEnabled) return new Decision(false, Reason.HardDisabled, null, null);

        // 🔑 Only lines that apply to THIS unit: the fleet-wide ones and its own. A window naming a
        // different device must not leak across — that would silently enable a unit the operator
        // scheduled nothing for.
        var mine = windows
            .Where(w => w.IsActive
                        && (w.EvaluationDeviceId is null || w.EvaluationDeviceId == device.Id))
            .ToList();

        // Inclusive start, EXCLUSIVE end — so two adjacent lines (…→09:00, 09:00→…) neither overlap
        // into a double-count nor leave a one-instant hole between them.
        var current = mine
            .Where(w => w.FromUtc <= nowUtc && nowUtc < w.ToUtc)
            .OrderBy(w => w.ToUtc)
            .ToList();

        if (current.Count > 0)
        {
            // 🔑 Overlapping windows extend rather than truncate: report the LATEST end among the
            // lines covering now, chained through any that start before it. Otherwise adding a second
            // overlapping line could shorten coverage, which is the opposite of what adding a line
            // means to the person who added it.
            var offAt = current.Max(w => w.ToUtc);
            bool extended;
            do
            {
                extended = false;
                foreach (var w in mine.Where(w => w.FromUtc <= offAt && w.ToUtc > offAt))
                {
                    offAt = w.ToUtc;
                    extended = true;
                }
            }
            while (extended);

            return new Decision(true, Reason.Enabled, null, offAt);
        }

        var next = mine.Where(w => w.FromUtc > nowUtc).OrderBy(w => w.FromUtc).FirstOrDefault();
        return new Decision(false, Reason.OutsideSchedule, next?.FromUtc, null);
    }
}
