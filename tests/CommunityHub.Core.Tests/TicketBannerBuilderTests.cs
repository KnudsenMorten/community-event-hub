using CommunityHub.Core.Config;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="TicketBannerBuilder"/> — the pure builder behind
/// the site-wide topbar ticket banner (REQUIREMENTS §10). It replaces the old
/// hardcoded <c>Layout.TicketInfo</c> resx literal that silently went stale (read
/// "2028" once). These tests pin the SHOW-BEFORE / SWITCH-OR-HIDE-AFTER-open rule
/// against a fixed clock, the timezone interpretation of the configured wall time,
/// and the additive fallback contract (absent/disabled/garbage ⇒ caller keeps its
/// literal). No DB / no I/O; "now" + config are inputs. FAKE values only.
/// </summary>
public sealed class TicketBannerBuilderTests
{
    private const string Tz = "Europe/Copenhagen";

    // The authoritative ELDK27 open moment: 11 Aug 2026 08:00 Danish wall time.
    // August ⇒ CEST (UTC+2), so the absolute moment is 06:00Z.
    private const string OpensAt = "2026-08-11T08:00:00";
    private static readonly DateTimeOffset OpenMomentUtc =
        new(2026, 8, 11, 6, 0, 0, TimeSpan.Zero);

    private static TicketSaleConfig Cfg(
        bool enabled = true,
        string opensAt = OpensAt,
        string ticketUrl = "",
        string afterOpen = "onsale") =>
        new()
        {
            Enabled = enabled,
            OpensAtLocal = opensAt,
            TicketUrl = ticketUrl,
            AfterOpen = afterOpen,
        };

    [Fact]
    public void Before_open_shows_the_sale_date_and_time_in_edition_local_wall_time()
    {
        // One day before the open moment.
        var now = OpenMomentUtc.AddDays(-1);

        var view = TicketBannerBuilder.Build(Cfg(), Tz, now);

        Assert.True(view.Visible);
        Assert.False(view.Suppressed);
        Assert.Null(view.Href); // no link before the sale opens
        // Danish wall time = 08:00 (UTC+2 in August). The date/time read from
        // config, never a hardcoded literal.
        Assert.Contains("11 Aug 2026", view.Message);
        Assert.Contains("08:00", view.Message);
        Assert.Contains("UTC+02:00", view.Message);
    }

    [Fact]
    public void One_minute_before_open_is_still_the_before_state()
    {
        var now = OpenMomentUtc.AddMinutes(-1);

        var view = TicketBannerBuilder.Build(Cfg(ticketUrl: "https://tickets.test"), Tz, now);

        Assert.True(view.Visible);
        Assert.Null(view.Href); // link only appears once open
        Assert.Contains("08:00", view.Message);
    }

    [Fact]
    public void At_the_open_moment_switches_to_on_sale_with_a_link_when_url_set()
    {
        var view = TicketBannerBuilder.Build(
            Cfg(ticketUrl: "https://tickets.test/buy"), Tz, OpenMomentUtc);

        Assert.True(view.Visible);
        Assert.False(view.Suppressed);
        Assert.Equal("https://tickets.test/buy", view.Href);
        Assert.Equal(TicketBannerBuilder.OnSaleMessage, view.Message);
    }

    [Fact]
    public void After_open_shows_on_sale_text_without_a_link_when_no_url()
    {
        var now = OpenMomentUtc.AddHours(1);

        var view = TicketBannerBuilder.Build(Cfg(ticketUrl: ""), Tz, now);

        Assert.True(view.Visible);
        Assert.Null(view.Href);
        Assert.Equal(TicketBannerBuilder.OnSaleMessage, view.Message);
    }

    [Fact]
    public void After_open_with_afterOpen_hide_suppresses_the_banner()
    {
        var now = OpenMomentUtc.AddHours(1);

        var view = TicketBannerBuilder.Build(
            Cfg(ticketUrl: "https://tickets.test", afterOpen: "hide"), Tz, now);

        Assert.False(view.Visible);
        Assert.True(view.Suppressed); // render NOTHING, not the fallback literal
        Assert.Equal(string.Empty, view.Message);
    }

    [Fact]
    public void Before_open_ignores_afterOpen_hide_and_still_announces_the_sale()
    {
        var now = OpenMomentUtc.AddDays(-3);

        var view = TicketBannerBuilder.Build(Cfg(afterOpen: "hide"), Tz, now);

        // hide only applies AFTER the open moment.
        Assert.True(view.Visible);
        Assert.False(view.Suppressed);
    }

    [Fact]
    public void Unknown_afterOpen_value_is_treated_as_on_sale()
    {
        var now = OpenMomentUtc.AddHours(1);

        var view = TicketBannerBuilder.Build(
            Cfg(ticketUrl: "https://tickets.test", afterOpen: "whatever"), Tz, now);

        Assert.True(view.Visible);
        Assert.Equal("https://tickets.test", view.Href);
    }

    [Fact]
    public void Null_config_falls_back_to_the_static_literal()
    {
        var view = TicketBannerBuilder.Build(null, Tz, OpenMomentUtc);

        Assert.False(view.Visible);
        Assert.False(view.Suppressed); // NOT suppressed ⇒ caller keeps its literal
    }

    [Fact]
    public void Disabled_config_falls_back_to_the_static_literal()
    {
        var view = TicketBannerBuilder.Build(Cfg(enabled: false), Tz, OpenMomentUtc.AddDays(-1));

        Assert.False(view.Visible);
        Assert.False(view.Suppressed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-date")]
    public void Blank_or_unparseable_open_datetime_falls_back(string opensAt)
    {
        var view = TicketBannerBuilder.Build(Cfg(opensAt: opensAt), Tz, OpenMomentUtc);

        Assert.False(view.Visible);
        Assert.False(view.Suppressed);
    }

    [Fact]
    public void Winter_open_time_is_interpreted_at_the_winter_offset()
    {
        // A February open moment ⇒ CET (UTC+1). 08:00 local = 07:00Z.
        var cfg = Cfg(opensAt: "2027-02-09T08:00:00");
        var oneSecondBefore = new DateTimeOffset(2027, 2, 9, 6, 59, 59, TimeSpan.Zero);
        var atOpen = new DateTimeOffset(2027, 2, 9, 7, 0, 0, TimeSpan.Zero);

        var before = TicketBannerBuilder.Build(cfg, Tz, oneSecondBefore);
        var open = TicketBannerBuilder.Build(cfg, Tz, atOpen);

        Assert.True(before.Visible);
        Assert.Null(before.Href);
        Assert.Contains("UTC+01:00", before.Message); // winter offset proven
        // The boundary is the winter-offset moment, so 07:00Z is "open".
        Assert.Equal(TicketBannerBuilder.OnSaleMessage, open.Message);
    }

    [Fact]
    public void Ticket_url_is_trimmed()
    {
        var view = TicketBannerBuilder.Build(
            Cfg(ticketUrl: "  https://tickets.test/x  "), Tz, OpenMomentUtc);

        Assert.Equal("https://tickets.test/x", view.Href);
    }
}
