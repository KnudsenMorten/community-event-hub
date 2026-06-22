using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for the Speaker hub "My sessions" read service
/// (<see cref="SpeakerSessionsService"/>, REQUIREMENTS §20 Speaker — "self-service
/// … 'My sessions' (room/time/master-class/attendee-questions)"). The service backs
/// the My-sessions card on <c>/Speaker</c>. Proves:
///  - OWN-ROW SCOPE: a speaker sees ONLY sessions they are linked to — never a
///    co-/other-speaker's other sessions (server-enforced),
///  - room/time + master-class flag + open-question count + co-speaker names surface,
///  - scheduled sessions sort before unscheduled (then by title),
///  - service sessions (breaks/lunch) are excluded,
///  - event scoping: another edition's sessions never leak,
///  - a non-speaker role gets an empty list.
///
/// In-memory DbContext; synthetic ids + example.test — no real names.
/// </summary>
public sealed class SpeakerSessionsServiceTests
{
    private static readonly DateTimeOffset Day1 =
        new(2027, 2, 9, 9, 0, 0, TimeSpan.Zero);

    private sealed record Seeded(
        int EventId, int AliceId, int BobId, int CarolId, int OtherEventId);

    private static async Task<Seeded> SeedAsync(CommunityHubDbContext db)
    {
        Event Evt(string code, bool active) => new()
        {
            Code = code, CommunityName = "C", DisplayName = code,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = active,
        };
        var evt = Evt("SPK27", true);
        var other = Evt("OLD26", false);
        db.Events.Add(evt);
        db.Events.Add(other);
        await db.SaveChangesAsync();

        Participant Spk(int eventId, string name, string email,
            ParticipantRole role = ParticipantRole.Speaker)
        {
            var p = new Participant
            {
                EventId = eventId, FullName = name, Email = email,
                Role = role, IsActive = true,
            };
            db.Participants.Add(p);
            return p;
        }

        var alice = Spk(evt.Id, "Alice Adams", "alice@example.test");
        var bob = Spk(evt.Id, "Bob Brown", "bob@example.test");
        var carol = Spk(evt.Id, "Carol Clark", "carol@example.test");
        // Same human, registered in the OLD edition (different participant row).
        var aliceOld = Spk(other.Id, "Alice Adams", "alice.old@example.test");
        await db.SaveChangesAsync();

        Session Sess(int eventId, string id, string title, SessionType type,
            string? room, DateTimeOffset? start, bool service, params Participant[] speakers)
        {
            var s = new Session
            {
                EventId = eventId, SessionizeId = id, Title = title,
                Type = type, Room = room, StartsAt = start,
                EndsAt = start?.AddMinutes(50), IsServiceSession = service,
            };
            foreach (var sp in speakers)
                s.SessionSpeakers.Add(new SessionSpeaker { Session = s, Participant = sp });
            db.Sessions.Add(s);
            return s;
        }

        // Alice: a scheduled master class (with Bob co-speaking) + an unscheduled tech talk.
        var mc = Sess(evt.Id, "s-mc", "Kubernetes Workshop",
            SessionType.MasterClass, "Room A", Day1, false, alice, bob);
        Sess(evt.Id, "s-unsched", "Future Talk",
            SessionType.TechnicalSession, null, null, false, alice);
        // Bob has his OWN solo session Alice must NEVER see.
        Sess(evt.Id, "s-bob", "Bob Solo", SessionType.TechnicalSession,
            "Room B", Day1.AddHours(1), false, bob);
        // A service session Alice is (oddly) linked to — must be excluded anyway.
        Sess(evt.Id, "s-break", "Lunch", SessionType.TechnicalSession,
            "Foyer", Day1.AddHours(2), true, alice);
        // Old-edition session for Alice's old row — must not leak into SPK27.
        Sess(other.Id, "s-old", "Old Talk", SessionType.TechnicalSession,
            "Room Z", Day1.AddDays(-365), false, aliceOld);
        await db.SaveChangesAsync();

        // Two open questions + one answered on Alice's master class.
        void Q(int sessionId, SessionQuestionStatus status)
            => db.SessionQuestions.Add(new SessionQuestion
            {
                EventId = evt.Id, SessionId = sessionId,
                QuestionText = "Q?", Status = status,
                CreatedAt = Day1,
            });
        Q(mc.Id, SessionQuestionStatus.Open);
        Q(mc.Id, SessionQuestionStatus.Open);
        Q(mc.Id, SessionQuestionStatus.Answered);
        await db.SaveChangesAsync();

        return new Seeded(evt.Id, alice.Id, bob.Id, carol.Id, other.Id);
    }

    [Fact]
    public async Task Own_row_scope_a_speaker_sees_only_their_own_sessions()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = new SpeakerSessionsService(db);

        var mine = await svc.GetMySessionsAsync(s.EventId, s.AliceId, ParticipantRole.Speaker);

        // Alice's two sessions (master class + unscheduled tech talk) — never Bob's solo.
        Assert.Equal(2, mine.Count);
        Assert.DoesNotContain(mine, m => m.Title == "Bob Solo");
        Assert.Contains(mine, m => m.Title == "Kubernetes Workshop");
        Assert.Contains(mine, m => m.Title == "Future Talk");
    }

    [Fact]
    public async Task Scheduled_sessions_sort_before_unscheduled()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = new SpeakerSessionsService(db);

        var mine = await svc.GetMySessionsAsync(s.EventId, s.AliceId, ParticipantRole.Speaker);

        Assert.Equal("Kubernetes Workshop", mine[0].Title); // scheduled (Day1)
        Assert.True(mine[0].IsScheduled);
        Assert.Equal("Future Talk", mine[1].Title);          // unscheduled, last
        Assert.False(mine[1].IsScheduled);
    }

    [Fact]
    public async Task Room_masterclass_flag_questions_and_co_speakers_surface()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = new SpeakerSessionsService(db);

        var mine = await svc.GetMySessionsAsync(s.EventId, s.AliceId, ParticipantRole.Speaker);
        var mc = Assert.Single(mine, m => m.Title == "Kubernetes Workshop");

        Assert.Equal("Room A", mc.Room);
        Assert.True(mc.IsMasterClass);
        Assert.Equal(2, mc.OpenQuestionCount);                 // 2 open, 1 answered excluded
        Assert.Equal(new[] { "Bob Brown" }, mc.CoSpeakerNames); // viewer excluded
    }

    [Fact]
    public async Task Service_sessions_are_excluded()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = new SpeakerSessionsService(db);

        var mine = await svc.GetMySessionsAsync(s.EventId, s.AliceId, ParticipantRole.Speaker);

        Assert.DoesNotContain(mine, m => m.Title == "Lunch");
    }

    [Fact]
    public async Task Other_editions_sessions_never_leak()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = new SpeakerSessionsService(db);

        var mine = await svc.GetMySessionsAsync(s.EventId, s.AliceId, ParticipantRole.Speaker);

        Assert.DoesNotContain(mine, m => m.Title == "Old Talk");
    }

    [Fact]
    public async Task Non_speaker_role_gets_empty_list()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = new SpeakerSessionsService(db);

        // Even passing a real speaker's id, a non-speaker role gets nothing.
        var asAttendee = await svc.GetMySessionsAsync(
            s.EventId, s.AliceId, ParticipantRole.Attendee);

        Assert.Empty(asAttendee);
    }

    [Fact]
    public async Task Speaker_with_no_linked_sessions_gets_empty_list()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = new SpeakerSessionsService(db);

        // Carol is a speaker but is linked to no (non-service) session.
        var mine = await svc.GetMySessionsAsync(s.EventId, s.CarolId, ParticipantRole.Speaker);

        Assert.Empty(mine);
    }
}
