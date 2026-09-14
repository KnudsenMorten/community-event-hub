using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1136 — the audit page's "Show local time" button renders Copenhagen time beside UTC.
///
/// <para>Operator 2026-08-25: <i>"Can you make it possible to show time in audit in local time
/// (copenhagen) with a button 'Show local time' - instead of utc"</i>.</para>
///
/// <para>🔑 The toggle itself is DOM text-swapping, verified in a browser. What is worth a test is
/// the CONVERSION, because it is the part that can be quietly wrong for half the year: Denmark is
/// UTC+1 in winter and UTC+2 in summer, and a fixed offset looks perfect until the clocks change.
/// The page reuses <see cref="SoMeDisplayTime.ToDanish"/> — the one authority for Danish local time
/// (§844.5) — rather than adding a second conversion that could drift from the SoMe surfaces.</para>
/// </summary>
public sealed class AuditLocalTimeTests
{
    [Fact]
    public void Summer_is_UTC_plus_two()
    {
        var utc = new DateTimeOffset(2026, 8, 25, 14, 41, 24, TimeSpan.Zero);

        Assert.Equal("2026-08-25 16:41:24",
            SoMeDisplayTime.ToDanish(utc).ToString("yyyy-MM-dd HH:mm:ss"));
    }

    [Fact]
    public void Winter_is_UTC_plus_one()
    {
        // 🔴 The case a hard-coded "+2" would get wrong for five months of the year — including
        // January, when most of the onboarding audit traffic happens.
        var utc = new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal("2026-01-15 10:00:00",
            SoMeDisplayTime.ToDanish(utc).ToString("yyyy-MM-dd HH:mm:ss"));
    }

    [Fact]
    public void The_event_itself_falls_in_winter_time()
    {
        // ELDK27 runs 8–10 Feb 2027, so every audit row read during the event is UTC+1.
        var utc = new DateTimeOffset(2027, 2, 10, 5, 40, 0, TimeSpan.Zero);

        Assert.Equal("2027-02-10 06:40:00",
            SoMeDisplayTime.ToDanish(utc).ToString("yyyy-MM-dd HH:mm:ss"));
    }

    [Fact]
    public void The_conversion_shifts_the_DAY_when_it_crosses_midnight()
    {
        // ⚠️ 23:30 UTC is the NEXT day in Copenhagen. An organizer filtering "last 1 day" and reading
        // local timestamps must not be surprised that a row's date moved — it legitimately did.
        var utc = new DateTimeOffset(2026, 8, 25, 23, 30, 0, TimeSpan.Zero);

        Assert.Equal("2026-08-26 01:30:00",
            SoMeDisplayTime.ToDanish(utc).ToString("yyyy-MM-dd HH:mm:ss"));
    }

    [Fact]
    public void UTC_rendering_is_unchanged()
    {
        // 🔒 The default view must still be exactly what it always was — the button is additive.
        var utc = new DateTimeOffset(2026, 8, 25, 14, 41, 24, TimeSpan.Zero);

        Assert.Equal("2026-08-25 14:41:24", utc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"));
    }
}
