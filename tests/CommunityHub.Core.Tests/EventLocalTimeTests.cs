using CommunityHub.Core.Config;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="EventLocalTime"/> — the pure helper that shows a
/// moment in the EVENT's local time instead of raw UTC on the public pages
/// (REQUIREMENTS §21). Pins the IANA/Windows resolution + the never-throw UTC
/// fallback + the "timestamp carries its zone" format contract. No DB / clock /
/// I/O. Uses a winter instant (standard time) so the offset is deterministic
/// regardless of the test host's DST state.
/// </summary>
public sealed class EventLocalTimeTests
{
    // 2027-02-03 13:00Z — winter in Europe so Copenhagen is UTC+1 (CET), no DST.
    private static readonly DateTimeOffset Winter =
        new(2027, 2, 3, 13, 0, 0, TimeSpan.Zero);

    // 2027-07-03 13:00Z — summer so Copenhagen is UTC+2 (CEST), DST active.
    private static readonly DateTimeOffset Summer =
        new(2027, 7, 3, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Iana_zone_converts_to_local_wall_time_in_winter()
    {
        var local = EventLocalTime.ToLocal(Winter, "Europe/Copenhagen");

        // UTC+1 in winter ⇒ 14:00 local.
        Assert.Equal(TimeSpan.FromHours(1), local.Offset);
        Assert.Equal(14, local.Hour);
    }

    [Fact]
    public void Iana_zone_honours_dst_in_summer()
    {
        var local = EventLocalTime.ToLocal(Summer, "Europe/Copenhagen");

        // UTC+2 in summer ⇒ 15:00 local.
        Assert.Equal(TimeSpan.FromHours(2), local.Offset);
        Assert.Equal(15, local.Hour);
    }

    [Fact]
    public void Windows_zone_id_also_resolves()
    {
        // The legacy Windows id for Copenhagen — must resolve via the swap path.
        var local = EventLocalTime.ToLocal(Winter, "Romance Standard Time");

        Assert.Equal(TimeSpan.FromHours(1), local.Offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Not/A_Real_Zone")]
    public void Blank_or_unknown_zone_falls_back_to_utc_without_throwing(string? zone)
    {
        var local = EventLocalTime.ToLocal(Winter, zone);

        Assert.Equal(TimeSpan.Zero, local.Offset);
        Assert.Equal(EventLocalTime.UtcLabel, EventLocalTime.ZoneLabel(Winter, zone));
    }

    [Fact]
    public void Zone_label_shows_the_offset_for_a_real_zone()
    {
        Assert.Equal("UTC+01:00", EventLocalTime.ZoneLabel(Winter, "Europe/Copenhagen"));
        Assert.Equal("UTC+02:00", EventLocalTime.ZoneLabel(Summer, "Europe/Copenhagen"));
    }

    [Fact]
    public void Format_carries_local_time_and_its_zone_together()
    {
        var s = EventLocalTime.Format(Winter, "Europe/Copenhagen");

        // 13:00Z ⇒ 14:00 local, zone suffix appended — never the raw UTC value.
        Assert.Equal("2027-02-03 14:00 UTC+01:00", s);
    }

    [Fact]
    public void Format_falls_back_to_utc_label_when_zone_is_blank()
    {
        var s = EventLocalTime.Format(Winter, null);

        Assert.Equal("2027-02-03 13:00 UTC", s);
    }

    [Fact]
    public void Resolve_never_returns_null_for_garbage()
    {
        Assert.NotNull(EventLocalTime.Resolve("garbage-zone-id"));
        Assert.Same(TimeZoneInfo.Utc, EventLocalTime.Resolve(null));
    }
}
