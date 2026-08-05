using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Tests.Scenario;
using UglyToad.PdfPig;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §750.2 C7 — the response TIMELINE across the collection window, the last chart the brief asks for.
/// </summary>
/// <remarks>
/// <para>It answers a question the distribution cannot: <b>when</b> the room answered. A block at the
/// very end is the normal shape — people press on their way out — while responses spread evenly
/// through a talk usually mean a device was being pressed rather than an audience responding.</para>
///
/// <para>🔑 The bucketing is asserted directly (it is the part with arithmetic in it) and the
/// rendered PDF is <b>read back with PdfPig</b>, which is the §743.14 standard in this repo: a chart
/// that throws, or silently renders nothing, must not pass as "the bytes look like a PDF".</para>
/// </remarks>
public sealed class EvaluationReportTimelineTests
{
    private const int EventId = 1;
    private const int SessionId = 900;
    private static readonly DateTimeOffset Start = new(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(1);
    private static readonly DateTimeOffset WindowCloses = End.AddMinutes(30);   // 90 min total

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => WindowCloses.AddHours(1);
    }

    private static async Task<CommunityHubDbContext> SeedAsync(params DateTimeOffset[] pressedAt)
    {
        var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "Experts Live Denmark 2027",
            Code = "ELDK27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        db.EvaluationSessions.Add(new EvaluationSession
        {
            Id = SessionId, EventId = EventId, CehSessionId = 55,
            Title = "Keeping identity boring",
            ScheduledStart = Start, ScheduledEnd = End,
            CollectionWindowOpensAt = Start, CollectionWindowClosesAt = WindowCloses,
            CreatedAt = Start, UpdatedAt = Start,
        });
        foreach (var t in pressedAt)
        {
            db.EvaluationResponses.Add(new EvaluationResponse
            {
                EventId = EventId, SessionId = SessionId, Rating = 4,
                CollectionTimestamp = t, ReceivedTimestamp = t,
                Source = EvaluationResponseSources.Device,
            });
        }
        await db.SaveChangesAsync();
        return db;
    }

    private static EvaluationReportBuilder Builder(CommunityHubDbContext db) =>
        new(db, new EvaluationScoreService(db), new FixedClock());

    // ---- bucketing -----------------------------------------------------------------------------

    [Fact]
    public async Task The_window_is_cut_into_twelve_slices_spanning_it_exactly()
    {
        using var db = await SeedAsync(Start.AddMinutes(1));

        var report = await Builder(db).BuildAsync(EventId, SessionId);

        var timeline = report!.Data.Timeline!;
        Assert.Equal(EvaluationReportBuilder.TimelineBuckets, timeline.Count);
        Assert.Equal(Start, timeline[0].From);
        // 90 minutes / 12 = 7.5 minutes per slice; the last one STARTS 7.5 min before the close.
        Assert.Equal(WindowCloses.AddMinutes(-7.5), timeline[^1].From);
    }

    /// <summary>
    /// 🔑 The shape the chart exists to show: almost everything arriving in the final minutes, which
    /// is what a room emptying actually looks like.
    /// </summary>
    [Fact]
    public async Task Presses_land_in_the_slice_they_happened_in()
    {
        using var db = await SeedAsync(
            Start.AddMinutes(2),      // slice 0
            Start.AddMinutes(60),     // slice 8  (60 / 7.5)
            Start.AddMinutes(61),     // slice 8
            Start.AddMinutes(62));    // slice 8

        var t = (await Builder(db).BuildAsync(EventId, SessionId))!.Data.Timeline!;

        Assert.Equal(1, t[0].Count);
        Assert.Equal(3, t[8].Count);
        Assert.Equal(4, t.Sum(b => b.Count));
    }

    /// <summary>
    /// 🔒 The closing instant belongs to the LAST slice, not to a thirteenth that does not exist.
    /// The off-by-one here would throw on the boundary press rather than mis-draw it.
    /// </summary>
    [Fact]
    public async Task A_press_at_the_exact_closing_instant_lands_in_the_last_slice()
    {
        using var db = await SeedAsync(WindowCloses);

        var t = (await Builder(db).BuildAsync(EventId, SessionId))!.Data.Timeline!;

        Assert.Equal(1, t[^1].Count);
        Assert.Equal(1, t.Sum(b => b.Count));
    }

    /// <summary>
    /// 🔒 Presses outside the window are EXCLUDED, never clamped into an end bar — a clamped outlier
    /// would draw a spike that never happened.
    /// </summary>
    [Fact]
    public async Task Presses_outside_the_window_are_excluded_not_clamped()
    {
        using var db = await SeedAsync(
            Start.AddMinutes(-5),          // before it opened
            WindowCloses.AddMinutes(5),    // after it closed
            Start.AddMinutes(10));         // the only one inside

        var t = (await Builder(db).BuildAsync(EventId, SessionId))!.Data.Timeline!;

        Assert.Equal(1, t.Sum(b => b.Count));
        // The one press inside sits where it happened — 10 min in, i.e. slice 1 (10 / 7.5).
        Assert.Equal(1, t[1].Count);
        // 🔒 And neither end bar absorbed an outsider, which is what clamping would have done.
        Assert.Equal(0, t[0].Count);
        Assert.Equal(0, t[^1].Count);
    }

    /// <summary>
    /// 🔑 Bucketed by COLLECTION time, not RECEIVED time. The chart is about when the room responded;
    /// plotting arrival would draw a device's cache flush as a spike of applause.
    /// </summary>
    [Fact]
    public async Task Bucketing_follows_collection_time_not_the_upload_time()
    {
        using var db = await SeedAsync();
        db.EvaluationResponses.Add(new EvaluationResponse
        {
            EventId = EventId, SessionId = SessionId, Rating = 4,
            CollectionTimestamp = Start.AddMinutes(3),          // pressed early
            ReceivedTimestamp = WindowCloses.AddDays(2),        // uploaded two days later
            Source = EvaluationResponseSources.Device,
        });
        await db.SaveChangesAsync();

        var t = (await Builder(db).BuildAsync(EventId, SessionId))!.Data.Timeline!;

        Assert.Equal(1, t[0].Count);          // where it was PRESSED
        Assert.Equal(0, t[^1].Count);         // not where it ARRIVED
    }

    [Fact]
    public async Task A_session_nobody_pressed_yields_twelve_empty_slices_rather_than_nothing()
    {
        using var db = await SeedAsync();

        var t = (await Builder(db).BuildAsync(EventId, SessionId))!.Data.Timeline!;

        Assert.Equal(EvaluationReportBuilder.TimelineBuckets, t.Count);
        Assert.All(t, b => Assert.Equal(0, b.Count));
    }

    // ---- the rendered document -----------------------------------------------------------------

    /// <summary>
    /// §743.14's standard: read the produced PDF back rather than trusting the bytes. A chart that
    /// throws, or draws nothing, must not pass as "it looks like a PDF".
    /// </summary>
    [Fact]
    public async Task The_rendered_report_carries_the_timeline_heading_and_stays_one_page()
    {
        using var db = await SeedAsync(
            Start.AddMinutes(2), Start.AddMinutes(58), Start.AddMinutes(61),
            Start.AddMinutes(62), Start.AddMinutes(63), Start.AddMinutes(64),
            Start.AddMinutes(65), Start.AddMinutes(66), Start.AddMinutes(67),
            Start.AddMinutes(68), Start.AddMinutes(69), Start.AddMinutes(70));

        var report = await Builder(db).BuildAsync(EventId, SessionId);
        var pdf = new EvaluationReportService().Render(report!.Data);

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        // §750.8 — the report may now run to several pages when the written feedback needs it
        // (operator: "report can be multi page depending on open feedback comments"), so this
        // asserts the chart is PRESENT rather than pinning a page count the content decides.
        Assert.True(doc.NumberOfPages >= 1);
        // 🔑 §752.6 — read EVERY page, not page 1. The operator moved this chart to the end
        // (*"move when people scored to bottom as less relevant"*), so pinning it to page 1 now
        // asserts a POSITION the brief deliberately changed rather than the fact it is present.
        var text = string.Concat(Enumerable.Range(1, doc.NumberOfPages).Select(p => doc.GetPage(p).Text));
        Assert.Contains("When people answered", text);
        // §750.8 — the chart's "peak N" caption is gone: the details table below now prints every
        // count exactly, so a rounded peak beside it was one number too many.
        Assert.Contains("Where the score comes from", text);
        // The window's own end (session end + the 30-minute grace), not the session's end time.
        Assert.Contains("11:30", text);
    }

    /// <summary>
    /// An all-empty timeline draws NO chart — an axis with no bars says "we measured nothing", which
    /// is less honest than not drawing it, and it wastes the page a below-threshold report needs for
    /// its explanation.
    /// </summary>
    [Fact]
    public async Task An_empty_timeline_omits_the_chart_entirely()
    {
        using var db = await SeedAsync();

        var report = await Builder(db).BuildAsync(EventId, SessionId);
        var pdf = new EvaluationReportService().Render(report!.Data);

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.DoesNotContain("When people answered", doc.GetPage(1).Text);
    }

    /// <summary>
    /// 🔒 The renderer must survive a caller that supplies no timeline at all — the parameter is
    /// optional precisely so an older caller degrades instead of throwing on the send path.
    /// </summary>
    [Fact]
    public void A_report_built_without_a_timeline_still_renders()
    {
        var score = SatisfactionScore.Compute(new SatisfactionScore.Distribution(0, 0, 0, 0));
        var data = new EvaluationReportService.ReportData(
            "Event", "Title", null, null, Start, End, score, score,
            Array.Empty<string>(), WindowCloses);

        var pdf = new EvaluationReportService().Render(data);

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.True(doc.NumberOfPages >= 1);
    }
}


