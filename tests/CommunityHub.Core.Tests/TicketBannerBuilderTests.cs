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

    /// <summary>
    /// §995 — BEFORE the sale, the banner is a COUNTDOWN, not an absolute date.
    /// </summary>
    /// <remarks>
    /// 🔑 Operator 2026-08-09: the countdown *"should replace the text Tickets on sale 11 Aug 2026
    /// at 08:00 (UTC+02:00)"*. This test used to assert exactly that string — it is updated rather
    /// than deleted because the ASSERTION is what changed, not the state machine: before-open is
    /// still visible, still link-less, and still computed from config against the edition timezone.
    /// ⚠️ The absolute date/zone must be GONE: leaving it alongside the countdown was the thing he
    /// was asking to be rid of.
    /// </remarks>
    [Fact]
    public void Before_open_counts_down_instead_of_printing_the_date()
    {
        // Exactly one day before the open moment.
        var now = OpenMomentUtc.AddDays(-1);

        var view = TicketBannerBuilder.Build(Cfg(), Tz, now);

        Assert.True(view.Visible);
        Assert.False(view.Suppressed);
        Assert.Null(view.Href);                       // no link before the sale opens
        Assert.Equal("Tickets on sale in 1d 00:00:00", view.Message);

        // 🔒 The absolute date and its offset label are gone — that is the whole ask.
        Assert.DoesNotContain("11 Aug 2026", view.Message);
        Assert.DoesNotContain("UTC+", view.Message);

        // 🔑 The INSTANT rides along so the layout can tick it live. A server-rendered duration is
        // stale the second it is sent, and this banner sits on a cacheable, long-lived layout.
        Assert.Equal(OpenMomentUtc, view.OpensAt);
    }

    [Fact]
    public void One_minute_before_open_is_still_the_before_state()
    {
        var now = OpenMomentUtc.AddMinutes(-1);

        var view = TicketBannerBuilder.Build(Cfg(ticketUrl: "https://tickets.test"), Tz, now);

        Assert.True(view.Visible);
        Assert.Null(view.Href); // link only appears once open
        Assert.Equal("Tickets on sale in 00:01:00", view.Message);
    }

    /// <summary>
    /// 🔒 The countdown format itself. Seconds are ALWAYS shown because the browser re-renders this
    /// once a second — a smallest unit of minutes looks frozen for 59 seconds at a time, which
    /// reads as broken rather than as a slow clock. Days are split out rather than rolled into
    /// hours: "52:13:22" is a number the reader has to divide.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0, 5, "00:00:05")]
    [InlineData(0, 4, 13, 22, "04:13:22")]
    [InlineData(0, 23, 59, 59, "23:59:59")]
    [InlineData(1, 0, 0, 0, "1d 00:00:00")]
    [InlineData(2, 4, 13, 22, "2d 04:13:22")]
    [InlineData(367, 1, 2, 3, "367d 01:02:03")]
    public void The_countdown_format_keeps_seconds_and_splits_days(
        int d, int h, int m, int s, string expected) =>
        Assert.Equal(expected, TicketBannerBuilder.FormatCountdown(new TimeSpan(d, h, m, s)));

    /// <summary>A negative span is a guard, not a display case — the caller is in the on-sale state.</summary>
    [Fact]
    public void A_negative_remaining_span_renders_as_zero_rather_than_a_minus_sign() =>
        Assert.Equal("00:00:00", TicketBannerBuilder.FormatCountdown(TimeSpan.FromSeconds(-30)));

    /// <summary>🔒 OpensAt is set ONLY in the before-open state — nothing else may tick.</summary>
    [Fact]
    public void OpensAt_is_null_once_the_sale_is_open()
    {
        Assert.Null(TicketBannerBuilder.Build(Cfg(), Tz, OpenMomentUtc).OpensAt);
        Assert.Null(TicketBannerBuilder.Build(Cfg(afterOpen: "hide"), Tz, OpenMomentUtc).OpensAt);
        Assert.Null(TicketBannerBuilder.Fallback.OpensAt);
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
        // §995 — the winter offset is now proven by the resolved INSTANT rather than by an
        // "(UTC+01:00)" label in the copy (the countdown carries no zone, which is the point of
        // it). This is the stronger assertion of the two: it pins the value the whole state
        // machine and the browser countdown run on, not how it was rendered.
        Assert.Equal(atOpen, before.OpensAt);         // 08:00 CET == 07:00Z
        Assert.Equal("Tickets on sale in 00:00:01", before.Message);
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
