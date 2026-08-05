using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §753 — the health-telemetry switch: a per-device HARD yes/no, narrowed by a schedule of FROM/TO
/// lines (operator 2026-08-01).
/// </summary>
/// <remarks>
/// <para>🔒 The property that carries the most weight here is <b>hard beats soft</b>. The hard switch
/// is what an operator reaches for when one unit is misbehaving mid-event; a schedule that could
/// re-enable it would make the control useless exactly when it is needed.</para>
///
/// <para>🔑 The Copenhagen boundary is tested with real offsets rather than a comment. Denmark is
/// UTC+1 in February, so the operator's <i>"midnight 9 Feb"</i> is <c>2027-02-08T23:00Z</c> — storing
/// what he typed and calling it UTC would start the event window an hour late, during the hour the
/// venue opens.</para>
/// </remarks>
public sealed class EvaluationTelemetryPolicyTests
{
    private static EvaluationDevice Device(bool hard = true, bool active = true, int id = 1) =>
        new() { Id = id, SerialNumber = "AA:BB", IsActive = active, HealthTelemetryEnabled = hard };

    private static EvaluationTelemetryWindow Window(
        string fromUtc, string toUtc, int? serialNumber = null, bool active = true) =>
        new()
        {
            FromUtc = DateTimeOffset.Parse(fromUtc, System.Globalization.CultureInfo.InvariantCulture),
            ToUtc = DateTimeOffset.Parse(toUtc, System.Globalization.CultureInfo.InvariantCulture),
            EvaluationDeviceId = serialNumber,
            IsActive = active,
        };

    private static DateTimeOffset At(string utc) =>
        DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture);

    // -----------------------------------------------------------------------------
    //  Hard switch
    // -----------------------------------------------------------------------------

    /// <summary>🔒 The whole point of a HARD switch: no schedule can talk it round.</summary>
    [Fact]
    public void The_hard_switch_beats_any_window_that_covers_now()
    {
        var d = Device(hard: false);
        var windows = new[] { Window("2027-02-08T23:00Z", "2027-02-10T23:00Z") };

        var r = EvaluationTelemetryPolicy.Evaluate(d, windows, At("2027-02-09T12:00Z"));

        Assert.False(r.Enabled);
        Assert.Equal(EvaluationTelemetryPolicy.Reason.HardDisabled, r.Why);
    }

    /// <summary>A revoked unit sends nothing at all — checked before the telemetry switch.</summary>
    [Fact]
    public void A_revoked_device_never_sends_telemetry()
    {
        var r = EvaluationTelemetryPolicy.Evaluate(
            Device(active: false), new[] { Window("2027-02-08T23:00Z", "2027-02-10T23:00Z") },
            At("2027-02-09T12:00Z"));

        Assert.False(r.Enabled);
        Assert.Equal(EvaluationTelemetryPolicy.Reason.DeviceInactive, r.Why);
    }

    // -----------------------------------------------------------------------------
    //  The schedule, in Copenhagen time
    // -----------------------------------------------------------------------------

    /// <summary>
    /// 🔑 The operator's default: midnight 9 Feb → midnight 11 Feb, COPENHAGEN. February is UTC+1, so
    /// the window is 08T23:00Z → 10T23:00Z. These three instants are the ones a UTC/local mix-up
    /// would get wrong, and only these.
    /// </summary>
    [Theory]
    // 22:59Z on the 8th = 23:59 Copenhagen on the 8th — one minute BEFORE the window opens.
    [InlineData("2027-02-08T22:59Z", false)]
    // 23:00Z on the 8th = midnight Copenhagen on the 9th — the window opens exactly here.
    [InlineData("2027-02-08T23:00Z", true)]
    [InlineData("2027-02-09T12:00Z", true)]
    // 22:59Z on the 10th = 23:59 Copenhagen on the 10th — still inside.
    [InlineData("2027-02-10T22:59Z", true)]
    // 23:00Z on the 10th = midnight Copenhagen on the 11th — exclusive end, so already off.
    [InlineData("2027-02-10T23:00Z", false)]
    public void The_default_event_window_is_Copenhagen_midnight_to_midnight(string nowUtc, bool expected)
    {
        var windows = new[] { Window("2027-02-08T23:00Z", "2027-02-10T23:00Z") };

        var r = EvaluationTelemetryPolicy.Evaluate(Device(), windows, At(nowUtc));

        Assert.Equal(expected, r.Enabled);
    }

    /// <summary>
    /// ⚠️ Outside every line the fleet is SILENT — which also silences battery and cached-record
    /// count. The operator accepted that and covers testing and install week with his own lines, so
    /// this pins the consequence honestly rather than hiding it.
    /// </summary>
    [Fact]
    public void With_no_window_covering_now_telemetry_is_off_and_says_when_it_returns()
    {
        var windows = new[] { Window("2027-02-08T23:00Z", "2027-02-10T23:00Z") };

        var r = EvaluationTelemetryPolicy.Evaluate(Device(), windows, At("2027-01-15T09:00Z"));

        Assert.False(r.Enabled);
        Assert.Equal(EvaluationTelemetryPolicy.Reason.OutsideSchedule, r.Why);
        Assert.Equal(At("2027-02-08T23:00Z"), r.NextOnAt);
    }

    /// <summary>
    /// The operator's stated way of covering install week: <i>"just add multiple ranges"</i>. Several
    /// disjoint lines all count.
    /// </summary>
    [Fact]
    public void Multiple_ranges_each_enable_their_own_period()
    {
        var windows = new[]
        {
            Window("2026-08-01T00:00Z", "2026-08-08T00:00Z"),   // bench testing, now
            Window("2027-02-02T00:00Z", "2027-02-06T00:00Z"),   // install week
            Window("2027-02-08T23:00Z", "2027-02-10T23:00Z"),   // the event
        };

        Assert.True(EvaluationTelemetryPolicy.Evaluate(Device(), windows, At("2026-08-03T10:00Z")).Enabled);
        Assert.True(EvaluationTelemetryPolicy.Evaluate(Device(), windows, At("2027-02-03T10:00Z")).Enabled);
        Assert.True(EvaluationTelemetryPolicy.Evaluate(Device(), windows, At("2027-02-09T10:00Z")).Enabled);
        // …and the gaps between them are off.
        Assert.False(EvaluationTelemetryPolicy.Evaluate(Device(), windows, At("2027-02-07T10:00Z")).Enabled);
    }

    /// <summary>
    /// 🔒 Adding an overlapping line must EXTEND coverage, never shorten it. Reporting the earliest
    /// end would mean a second line made things worse, which is the opposite of what adding one means.
    /// </summary>
    [Fact]
    public void Overlapping_windows_extend_rather_than_truncate()
    {
        var windows = new[]
        {
            Window("2027-02-08T23:00Z", "2027-02-09T12:00Z"),
            Window("2027-02-09T06:00Z", "2027-02-10T23:00Z"),
        };

        var r = EvaluationTelemetryPolicy.Evaluate(Device(), windows, At("2027-02-09T08:00Z"));

        Assert.True(r.Enabled);
        Assert.Equal(At("2027-02-10T23:00Z"), r.OffAt);
    }

    /// <summary>Two adjacent lines leave no gap at the seam — inclusive start, exclusive end.</summary>
    [Fact]
    public void Adjacent_windows_have_no_hole_at_the_seam()
    {
        var windows = new[]
        {
            Window("2027-02-08T23:00Z", "2027-02-09T12:00Z"),
            Window("2027-02-09T12:00Z", "2027-02-10T23:00Z"),
        };

        Assert.True(EvaluationTelemetryPolicy.Evaluate(Device(), windows, At("2027-02-09T12:00Z")).Enabled);
    }

    /// <summary>An inactive (parked) line does not count — it is kept as a template, not applied.</summary>
    [Fact]
    public void A_parked_window_does_not_enable_anything()
    {
        var windows = new[] { Window("2027-02-08T23:00Z", "2027-02-10T23:00Z", active: false) };

        Assert.False(EvaluationTelemetryPolicy.Evaluate(Device(), windows, At("2027-02-09T12:00Z")).Enabled);
    }

    // -----------------------------------------------------------------------------
    //  Scoping
    // -----------------------------------------------------------------------------

    /// <summary>
    /// 🔒 A line naming ANOTHER device must not leak across. Otherwise scheduling one suspect unit
    /// would quietly switch the whole fleet on.
    /// </summary>
    [Fact]
    public void A_window_for_another_device_does_not_enable_this_one()
    {
        var windows = new[] { Window("2027-02-08T23:00Z", "2027-02-10T23:00Z", serialNumber: 99) };

        var r = EvaluationTelemetryPolicy.Evaluate(Device(id: 1), windows, At("2027-02-09T12:00Z"));

        Assert.False(r.Enabled);
        Assert.Equal(EvaluationTelemetryPolicy.Reason.OutsideSchedule, r.Why);
    }

    /// <summary>A line naming THIS device counts, alongside the fleet-wide ones.</summary>
    [Fact]
    public void A_window_naming_this_device_enables_it_outside_the_fleet_schedule()
    {
        var windows = new[]
        {
            Window("2027-02-08T23:00Z", "2027-02-10T23:00Z"),                 // fleet
            Window("2026-08-01T00:00Z", "2026-08-02T00:00Z", serialNumber: 1),    // this unit only
        };

        Assert.True(EvaluationTelemetryPolicy.Evaluate(Device(id: 1), windows, At("2026-08-01T10:00Z")).Enabled);
        Assert.False(EvaluationTelemetryPolicy.Evaluate(Device(id: 2), windows, At("2026-08-01T10:00Z")).Enabled);
    }

    /// <summary>No windows configured at all ⇒ off, not "on by default".</summary>
    [Fact]
    public void An_empty_schedule_means_off()
    {
        var r = EvaluationTelemetryPolicy.Evaluate(
            Device(), Array.Empty<EvaluationTelemetryWindow>(), At("2027-02-09T12:00Z"));

        Assert.False(r.Enabled);
        Assert.Equal(EvaluationTelemetryPolicy.Reason.OutsideSchedule, r.Why);
    }
}
