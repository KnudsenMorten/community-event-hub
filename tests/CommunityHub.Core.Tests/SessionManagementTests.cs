using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for the session-management feature set (REQUIREMENTS § session management):
///  - <b>Type + Length defaults</b> for imported sessions (SessionDefaultsMapper),
///  - <b>Type/Length filters</b> over the session list,
///  - <b>hub-only sessions</b> (add a non-Sessionize session; import never touches it),
///  - <b>room + per-room QR</b> stamping via the QR seam (Null vs wired provider),
///  - the <b>evaluation mail hook</b> emailing HappyOrNot results to speakers.
///
/// No real Sessionize ids / customer / person data — synthetic ids + example.test.
/// </summary>
public sealed class SessionManagementTests
{
    private static readonly DateTimeOffset Now =
        new(2027, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // ---- A QR provider that "stores" a deterministic URL, for stamping tests ----
    private sealed class FakeRoomQrProvider : IRoomQrProvider
    {
        public bool CanProvision => true;
        public Task<RoomQr> EnsureRoomQrAsync(
            string eventCode, string room, string targetUrl, CancellationToken ct = default) =>
            Task.FromResult(new RoomQr(
                TargetUrl: targetUrl,
                ImageUrl: $"https://sharepoint.example.test/{eventCode}/{room}/qr.png"));
    }

    private static async Task<(int eventId, int speakerAId, int speakerBId)> SeedAsync(
        CommunityHubDbContext db)
    {
        var (eventId, speakerAId) = await TestDb.SeedEventAndPersonAsync(db, lockDate: null);
        var speakerB = new Participant
        {
            EventId = eventId,
            FullName = "Second Speaker",
            Email = "second@example.test",
            Role = ParticipantRole.Speaker,
            IsActive = true,
        };
        db.Participants.Add(speakerB);
        await db.SaveChangesAsync();
        return (eventId, speakerAId, speakerB.Id);
    }

    // ---------------------------------------------------------------- mapping ----

    [Theory]
    [InlineData(20, SessionLength.TwentyMin)]
    [InlineData(25, SessionLength.TwentyMin)]
    [InlineData(50, SessionLength.FiftyMin)]
    [InlineData(60, SessionLength.SixtyMin)]
    [InlineData(56, SessionLength.SixtyMin)]      // nearest-bucket: 56 → 60
    [InlineData(8 * 60, SessionLength.FullDay)]   // 8h → full day
    public void MapLength_rounds_to_nearest_bucket(int minutes, SessionLength expected)
    {
        var start = Now;
        var end = Now.AddMinutes(minutes);
        Assert.Equal(expected, SessionDefaultsMapper.MapLength(start, end));
    }

    [Fact]
    public void MapLength_defaults_to_sixty_when_untimed()
    {
        Assert.Equal(SessionLength.SixtyMin, SessionDefaultsMapper.MapLength(null, null));
        Assert.Equal(SessionLength.SixtyMin, SessionDefaultsMapper.MapLength(Now, Now)); // zero duration
    }

    [Fact]
    public void MapType_fullday_is_masterclass_else_techsession()
    {
        Assert.Equal(SessionType.MasterClass, SessionDefaultsMapper.MapType(SessionLength.FullDay));
        Assert.Equal(SessionType.TechnicalSession, SessionDefaultsMapper.MapType(SessionLength.FiftyMin));
    }

    // ------------------------------------------------------------- import default ----

    [Fact]
    public async Task Import_stamps_type_and_length_defaults_from_duration()
    {
        using var db = TestDb.New();
        var (eventId, _, _) = await SeedAsync(db);
        var svc = new SessionImportService(db, new FixedClock(Now));

        var sessions = new[]
        {
            new SessionizeSession("sess-talk", "A Talk", null, "Room A", null,
                Now, Now.AddMinutes(50), false, Array.Empty<string>()),
            new SessionizeSession("sess-ws", "A Workshop", null, "Room B", null,
                Now, Now.AddHours(8), false, Array.Empty<string>()),
        };

        await svc.ImportSessionsAsync(eventId, sessions,
            Array.Empty<SessionizeSpeaker>(), Array.Empty<string>());

        var talk = await db.Sessions.SingleAsync(s => s.SessionizeId == "sess-talk");
        Assert.Equal(SessionLength.FiftyMin, talk.Length);
        Assert.Equal(SessionType.TechnicalSession, talk.Type);
        Assert.False(talk.IsHubAdded);

        var ws = await db.Sessions.SingleAsync(s => s.SessionizeId == "sess-ws");
        Assert.Equal(SessionLength.FullDay, ws.Length);
        Assert.Equal(SessionType.MasterClass, ws.Type);
    }

    // ----------------------------------------------------------------- hub-add ----

    [Fact]
    public async Task AddHubSession_creates_import_safe_session()
    {
        using var db = TestDb.New();
        var (eventId, speakerAId, _) = await SeedAsync(db);
        var svc = new SessionManagementService(db, new NullRoomQrProvider(), new FixedClock(Now));

        var s = await svc.AddHubSessionAsync(
            eventId, "Sponsor Showcase", SessionType.TechnicalSession, SessionLength.TwentyMin,
            room: "Expo", @abstract: "By a sponsor.",
            speakerParticipantIds: new[] { speakerAId });

        Assert.True(s.IsHubAdded);
        Assert.StartsWith(SessionManagementService.HubSessionizeIdPrefix, s.SessionizeId);
        Assert.Equal(SessionType.TechnicalSession, s.Type);
        Assert.Equal(SessionLength.TwentyMin, s.Length);
        Assert.Equal("Expo", s.Room);
        Assert.Single(s.SessionSpeakers);
    }

    [Fact]
    public async Task Import_never_touches_or_deletes_a_hub_session()
    {
        using var db = TestDb.New();
        var (eventId, _, _) = await SeedAsync(db);
        var mgmt = new SessionManagementService(db, new NullRoomQrProvider(), new FixedClock(Now));
        var hub = await mgmt.AddHubSessionAsync(
            eventId, "Hub Only", SessionType.TechnicalSession, SessionLength.FiftyMin, room: "Expo");

        // A full import that does NOT include the hub session must leave it intact.
        var import = new SessionImportService(db, new FixedClock(Now));
        await import.ImportSessionsAsync(eventId,
            new[] { new SessionizeSession("sess-x", "Imported", null, "Room A", null,
                Now, Now.AddMinutes(60), false, Array.Empty<string>()) },
            Array.Empty<SessionizeSpeaker>(), Array.Empty<string>());

        var stillThere = await db.Sessions.SingleAsync(s => s.Id == hub.Id);
        Assert.Equal("Hub Only", stillThere.Title);
        Assert.Equal(SessionType.TechnicalSession, stillThere.Type);
        Assert.True(stillThere.IsHubAdded);
        Assert.Equal(2, await db.Sessions.CountAsync(s => s.EventId == eventId));
    }

    // ------------------------------------------------------------------ filter ----

    [Fact]
    public async Task Type_and_length_filters_narrow_the_list()
    {
        using var db = TestDb.New();
        var (eventId, _, _) = await SeedAsync(db);
        var mgmt = new SessionManagementService(db, new NullRoomQrProvider(), new FixedClock(Now));
        await mgmt.AddHubSessionAsync(eventId, "Keynote 20", SessionType.Keynote, SessionLength.TwentyMin);
        await mgmt.AddHubSessionAsync(eventId, "Tech 60", SessionType.TechnicalSession, SessionLength.SixtyMin);
        await mgmt.AddHubSessionAsync(eventId, "Master Full", SessionType.MasterClass, SessionLength.FullDay);

        var keynotes = await db.Sessions
            .Where(s => s.EventId == eventId && s.Type == SessionType.Keynote)
            .ToListAsync();
        Assert.Single(keynotes);
        Assert.Equal("Keynote 20", keynotes[0].Title);

        var sixties = await db.Sessions
            .Where(s => s.EventId == eventId && s.Length == SessionLength.SixtyMin)
            .ToListAsync();
        Assert.Single(sixties);
        Assert.Equal("Tech 60", sixties[0].Title);
    }

    // -------------------------------------------------------------------- QR ----

    [Fact]
    public async Task ProvisionRoomQr_without_wiring_does_not_stamp()
    {
        using var db = TestDb.New();
        var (eventId, _, _) = await SeedAsync(db);
        var mgmt = new SessionManagementService(db, new NullRoomQrProvider(), new FixedClock(Now));
        await mgmt.AddHubSessionAsync(eventId, "In Room A", SessionType.TechnicalSession, SessionLength.FiftyMin, room: "Room A");

        var result = await mgmt.ProvisionRoomQrAsync(eventId, "Room A", "https://example.test/room-a");

        Assert.False(result.Provisioned);
        Assert.Equal(0, result.SessionsUpdated);
        var s = await db.Sessions.SingleAsync(x => x.EventId == eventId);
        Assert.Null(s.RoomQrUrl); // nothing faked
    }

    [Fact]
    public async Task ProvisionRoomQr_with_wiring_stamps_all_sessions_in_room()
    {
        using var db = TestDb.New();
        var (eventId, _, _) = await SeedAsync(db);
        var mgmt = new SessionManagementService(db, new FakeRoomQrProvider(), new FixedClock(Now));
        await mgmt.AddHubSessionAsync(eventId, "A1", SessionType.TechnicalSession, SessionLength.FiftyMin, room: "Room A");
        await mgmt.AddHubSessionAsync(eventId, "A2", SessionType.TechnicalSession, SessionLength.SixtyMin, room: "Room A");
        await mgmt.AddHubSessionAsync(eventId, "B1", SessionType.TechnicalSession, SessionLength.SixtyMin, room: "Room B");

        var result = await mgmt.ProvisionRoomQrAsync(eventId, "Room A", "https://example.test/room-a");

        Assert.True(result.Provisioned);
        Assert.Equal(2, result.SessionsUpdated);
        var roomA = await db.Sessions.Where(s => s.Room == "Room A").ToListAsync();
        Assert.All(roomA, s => Assert.False(string.IsNullOrEmpty(s.RoomQrUrl)));
        Assert.All(roomA, s => Assert.NotNull(s.RoomQrGeneratedAt));
        var roomB = await db.Sessions.SingleAsync(s => s.Room == "Room B");
        Assert.Null(roomB.RoomQrUrl); // untouched
    }

    // ------------------------------------------------------------- eval hook ----

    [Fact]
    public async Task EmailEvaluation_sends_results_to_all_speakers_and_stamps()
    {
        using var db = TestDb.New();
        var (eventId, speakerAId, speakerBId) = await SeedAsync(db);
        var mgmt = new SessionManagementService(db, new NullRoomQrProvider(), new FixedClock(Now));
        var session = await mgmt.AddHubSessionAsync(
            eventId, "Joint", SessionType.TechnicalSession, SessionLength.SixtyMin,
            speakerParticipantIds: new[] { speakerAId, speakerBId });

        var mailer = new CapturingEmailSender();
        var evalSvc = new SessionEvaluationMailService(db, mailer, new FixedClock(Now));

        var result = await evalSvc.EmailResultsToSpeakersAsync(session.Id, "86% happy (42 votes)");

        Assert.True(result.Sent);
        Assert.Equal(2, mailer.Sent.Count);
        Assert.Contains(mailer.Sent, m => m.To == "person@example.test");
        Assert.Contains(mailer.Sent, m => m.To == "second@example.test");
        Assert.Contains("86% happy", mailer.Messages[0].Html);

        var stamped = await db.Sessions.SingleAsync(s => s.Id == session.Id);
        Assert.NotNull(stamped.EvaluationEmailedAt);
    }

    [Fact]
    public async Task EmailEvaluation_with_blank_results_sends_nothing()
    {
        using var db = TestDb.New();
        var (eventId, speakerAId, _) = await SeedAsync(db);
        var mgmt = new SessionManagementService(db, new NullRoomQrProvider(), new FixedClock(Now));
        var session = await mgmt.AddHubSessionAsync(
            eventId, "Solo", SessionType.TechnicalSession, SessionLength.SixtyMin,
            speakerParticipantIds: new[] { speakerAId });

        var mailer = new CapturingEmailSender();
        var evalSvc = new SessionEvaluationMailService(db, mailer, new FixedClock(Now));

        var result = await evalSvc.EmailResultsToSpeakersAsync(session.Id, "   ");

        Assert.False(result.Sent);
        Assert.Empty(mailer.Sent);
    }
}
