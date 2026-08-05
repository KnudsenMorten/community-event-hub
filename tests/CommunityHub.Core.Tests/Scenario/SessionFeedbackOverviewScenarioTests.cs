using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO §784.7(C) — the three-way merge's JOIN.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-03: *"it is confusing now"* … *"yes do the three-way merge"*. Four organizer
/// pages became one row per session. <b>The risk the merge introduced is not visual, it is the
/// join</b>: QR codes and evaluation REPORTS are keyed on the CEH <c>Session.Id</c>, while responses
/// and SCORES are keyed on <c>EvaluationSession.Id</c>, and the two meet only through
/// <c>EvaluationSession.CehSessionId</c>.</para>
///
/// <para>🔒 Getting that wrong shows a real score against the wrong session — and every row would
/// still look entirely plausible, which is why it is asserted here rather than eyeballed.</para>
/// </remarks>
public sealed class SessionFeedbackOverviewScenarioTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() => ScenarioFixture.NewDb();

    /// <remarks>
    /// The PDF store is left UNCONFIGURED on purpose: these tests are about the JOIN, and no report
    /// has been published for any of these sessions. That is also the honest default state before an
    /// event — every row reads "report not released", which is correct rather than convenient.
    /// </remarks>
    private static SessionFeedbackOverviewService NewService(CommunityHubDbContext db)
    {
        var pdf = new SessionEvalPdfService(
            new NullSharePointFileStore(),
            Options.Create(new GraphicsSharePointOptions()),
            db,
            TestDocLibrary.Resolver());
        return new SessionFeedbackOverviewService(db, new EvaluationScoreService(db), pdf);
    }

    private static async Task SeedEventAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "SF27", CommunityName = "Feedback", DisplayName = "Feedback 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Session> AddCehSessionAsync(
        CommunityHubDbContext db, string title, string? token = null, bool service = false)
    {
        var s = new Session
        {
            EventId = EventId, Title = title, PublicToken = token, IsServiceSession = service,
        };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    private static async Task<EvaluationSession> AddEvalSessionAsync(
        CommunityHubDbContext db, string title, int? cehSessionId, string? roomName)
    {
        int? roomId = null;
        if (roomName is not null)
        {
            var room = new EvaluationRoom { EventId = EventId, Name = roomName };
            db.EvaluationRooms.Add(room);
            await db.SaveChangesAsync();
            roomId = room.Id;
        }

        var ev = new EvaluationSession
        {
            EventId = EventId, Title = title, CehSessionId = cehSessionId, RoomId = roomId,
            ScheduledStart = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero),
            ScheduledEnd = new DateTimeOffset(2027, 2, 9, 11, 0, 0, TimeSpan.Zero),
        };
        db.EvaluationSessions.Add(ev);
        await db.SaveChangesAsync();
        return ev;
    }

    private static async Task AddResponsesAsync(
        CommunityHubDbContext db, int evaluationSessionId, int count, int rating)
    {
        for (var i = 0; i < count; i++)
        {
            var at = new DateTimeOffset(2027, 2, 9, 10, 30, 0, TimeSpan.Zero);
            db.EvaluationResponses.Add(new EvaluationResponse
            {
                EventId = EventId, SessionId = evaluationSessionId, Rating = rating,
                CollectionTimestamp = at, ReceivedTimestamp = at,
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 🔴 THE ONE THAT MATTERS: the score follows the CehSessionId link, not the row order.
    /// </summary>
    /// <remarks>
    /// The evaluation sessions are deliberately created in the OPPOSITE order to the CEH sessions and
    /// linked crosswise. A merge that paired them positionally — or by insertion order, or by title —
    /// would pass every "a score is shown" check and still attribute the wrong score to each session.
    /// </remarks>
    [Fact]
    public async Task The_score_follows_the_CehSessionId_link_and_not_the_row_order()
    {
        using var db = NewDb();
        await SeedEventAsync(db);

        var alpha = await AddCehSessionAsync(db, "Alpha", token: "tok-alpha");
        var beta = await AddCehSessionAsync(db, "Beta", token: "tok-beta");

        // Created in reverse, linked crosswise.
        var evBeta = await AddEvalSessionAsync(db, "Beta", beta.Id, "Room 2");
        var evAlpha = await AddEvalSessionAsync(db, "Alpha", alpha.Id, "Room 1");

        // Enough responses to clear the publish threshold, with clearly different quality.
        await AddResponsesAsync(db, evAlpha.Id, SatisfactionScore.MinimumResponses, rating: 4);
        await AddResponsesAsync(db, evBeta.Id, SatisfactionScore.MinimumResponses, rating: 1);

        var rows = await NewService(db).BuildAsync(EventId);

        var a = Assert.Single(rows, r => r.SessionId == alpha.Id);
        var b = Assert.Single(rows, r => r.SessionId == beta.Id);

        Assert.Equal(evAlpha.Id, a.EvaluationSessionId);
        Assert.Equal(evBeta.Id, b.EvaluationSessionId);
        Assert.Equal("Room 1", a.Room);
        Assert.Equal("Room 2", b.Room);

        // All-fantastic beats all-poor. If the join were positional these two would be swapped.
        Assert.NotNull(a.Score!.Score);
        Assert.NotNull(b.Score!.Score);
        Assert.True(a.Score.Score > b.Score.Score);
        Assert.Equal(SatisfactionScore.MinimumResponses, a.Responses);
        Assert.Equal(SatisfactionScore.MinimumResponses, b.Responses);
    }

    /// <summary>
    /// ⚠️ Neither leftover may vanish — losing either would make the merged page quieter, and less
    /// true, than the four pages it replaced.
    /// </summary>
    [Fact]
    public async Task A_session_with_no_mirror_and_a_mirror_with_no_session_are_both_still_listed()
    {
        using var db = NewDb();
        await SeedEventAsync(db);

        // In CEH, never mirrored: it has a printable QR code that will scan to "not open yet".
        var unmirrored = await AddCehSessionAsync(db, "Unmirrored", token: "tok-unmirrored");
        // In the evaluation model only: it holds real responses but CEH does not know it.
        var orphan = await AddEvalSessionAsync(db, "Orphan", cehSessionId: null, roomName: "Room 9");
        await AddResponsesAsync(db, orphan.Id, 3, rating: 4);

        var rows = await NewService(db).BuildAsync(EventId);

        var u = Assert.Single(rows, r => r.SessionId == unmirrored.Id);
        Assert.True(u.NotMirrored);
        Assert.False(u.CanCollect);       // no mirror ⇒ a scan resolves to nothing
        Assert.True(u.HasQrCode);         // ...and yet the code prints perfectly. That is the trap.
        Assert.Null(u.Score);

        var o = Assert.Single(rows, r => r.EvaluationSessionId == orphan.Id);
        Assert.True(o.OrphanedFromCeh);
        Assert.Null(o.SessionId);
        Assert.False(o.HasQrCode);        // no CEH session ⇒ no token can exist
        Assert.Equal(3, o.Responses);     // its feedback is real and must not disappear
    }

    /// <summary>
    /// "Collecting?" is mirror AND room — a mirrored session with no room has nowhere to put a unit.
    /// </summary>
    [Fact]
    public async Task Collecting_needs_both_a_mirror_and_a_room()
    {
        using var db = NewDb();
        await SeedEventAsync(db);

        var withRoom = await AddCehSessionAsync(db, "With room");
        var noRoom = await AddCehSessionAsync(db, "No room");
        await AddEvalSessionAsync(db, "With room", withRoom.Id, "Room 1");
        await AddEvalSessionAsync(db, "No room", noRoom.Id, roomName: null);

        var rows = await NewService(db).BuildAsync(EventId);

        Assert.True(Assert.Single(rows, r => r.SessionId == withRoom.Id).CanCollect);
        var bare = Assert.Single(rows, r => r.SessionId == noRoom.Id);
        Assert.False(bare.CanCollect);
        Assert.False(bare.NotMirrored);   // it IS mirrored — the missing thing is the room, and the
                                          // page has to say which, or the fix is a guess.
    }

    /// <summary>
    /// Service sessions (lunch, breaks) stay OUT. Nobody rates a coffee break, and the report side
    /// of this same page has always excluded them — the retired QR page did not, which is why it
    /// listed breaks as sessions missing a code.
    /// </summary>
    [Fact]
    public async Task Service_sessions_are_not_listed()
    {
        using var db = NewDb();
        await SeedEventAsync(db);

        await AddCehSessionAsync(db, "Real talk");
        await AddCehSessionAsync(db, "Lunch", service: true);

        var rows = await NewService(db).BuildAsync(EventId);

        Assert.Single(rows);
        Assert.Equal("Real talk", rows[0].Title);
    }

    /// <summary>Read-only: building the view twice changes nothing and returns the same answer.</summary>
    [Fact]
    public async Task Building_is_read_only_and_repeatable()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var s = await AddCehSessionAsync(db, "Alpha", token: "tok");
        var ev = await AddEvalSessionAsync(db, "Alpha", s.Id, "Room 1");
        await AddResponsesAsync(db, ev.Id, 4, rating: 3);

        var svc = NewService(db);
        var first = await svc.BuildAsync(EventId);
        var second = await svc.BuildAsync(EventId);

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first[0].Responses, second[0].Responses);
        Assert.Equal(1, await db.Sessions.CountAsync());
        Assert.Equal(1, await db.EvaluationSessions.CountAsync());
        Assert.Equal(4, await db.EvaluationResponses.CountAsync());
    }
}
