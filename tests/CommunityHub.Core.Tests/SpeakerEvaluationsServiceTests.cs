using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for the speaker self-service "My session ratings" read service
/// (<see cref="SpeakerEvaluationsService"/>, REQUIREMENTS §6 / §20 Speaker). The
/// service backs the <c>/Speaker/Evaluations</c> page: a speaker sees the
/// post-session attendee evaluations (1–5 ratings + anonymous comments) for THEIR
/// OWN sessions only. Proves:
///  - OWN-ROW SCOPE: a speaker sees ONLY evaluations for sessions they are linked
///    to — never a co-/other-speaker's other session's ratings (server-enforced),
///  - per-session count + average + (non-blank) comments surface, newest comment first,
///  - the overall (cross-session) count + average roll up correctly,
///  - a session with no ratings still appears, with a null average + no comments,
///  - blank comments are excluded (a rating-only evaluation still counts),
///  - service sessions (breaks/lunch) are excluded,
///  - event scoping: another edition's evaluations never leak,
///  - a non-speaker role gets an empty result,
///  - a speaker with no linked sessions gets an empty result.
///
/// In-memory DbContext; synthetic ids + example.test — no real names.
/// </summary>
public sealed class SpeakerEvaluationsServiceTests
{
    private static readonly DateTimeOffset T0 =
        new(2027, 2, 9, 9, 0, 0, TimeSpan.Zero);

    private sealed record Seeded(
        int EventId, int AliceId, int BobId, int CarolId,
        int McSessionId, int TechSessionId, int OtherEventId);

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
        var aliceOld = Spk(other.Id, "Alice Adams", "alice.old@example.test");
        await db.SaveChangesAsync();

        Session Sess(int eventId, string id, string title, SessionType type,
            string? room, bool service, params Participant[] speakers)
        {
            var s = new Session
            {
                EventId = eventId, SessionizeId = id, Title = title,
                Type = type, Room = room, StartsAt = T0, EndsAt = T0.AddMinutes(50),
                IsServiceSession = service,
            };
            foreach (var sp in speakers)
                s.SessionSpeakers.Add(new SessionSpeaker { Session = s, Participant = sp });
            db.Sessions.Add(s);
            return s;
        }

        // Alice + Bob co-present a master class; Alice solo-presents a tech talk.
        var mc = Sess(evt.Id, "s-mc", "Kubernetes Workshop",
            SessionType.MasterClass, "Room A", false, alice, bob);
        var tech = Sess(evt.Id, "s-tech", "Future Talk",
            SessionType.TechnicalSession, "Room A", false, alice);
        // Bob has his OWN solo session whose ratings Alice must NEVER see.
        var bobSolo = Sess(evt.Id, "s-bob", "Bob Solo",
            SessionType.TechnicalSession, "Room B", false, bob);
        // A service session Alice is (oddly) linked to — excluded anyway.
        var brk = Sess(evt.Id, "s-break", "Lunch",
            SessionType.TechnicalSession, "Foyer", true, alice);
        // Old-edition session for Alice's old row — must not leak into SPK27.
        var old = Sess(other.Id, "s-old", "Old Talk",
            SessionType.TechnicalSession, "Room Z", false, aliceOld);
        await db.SaveChangesAsync();

        void Eval(int eventId, int sessionId, int rating, string? comment, DateTimeOffset when)
            => db.SessionEvaluations.Add(new SessionEvaluation
            {
                EventId = eventId, SessionId = sessionId, Rating = rating,
                Comment = comment, CreatedAt = when,
            });

        // Master class: 3 ratings (5, 4, 3 -> avg 4.0); two have comments.
        Eval(evt.Id, mc.Id, 5, "Loved it!", T0.AddMinutes(10));
        Eval(evt.Id, mc.Id, 4, "  ", T0.AddMinutes(20));        // blank comment -> counts, no comment
        Eval(evt.Id, mc.Id, 3, "Too fast", T0.AddMinutes(30));  // newest comment
        // Tech talk: 1 rating, no ratings-comment.
        Eval(evt.Id, tech.Id, 5, null, T0.AddMinutes(15));
        // Bob solo: a rating Alice must never see.
        Eval(evt.Id, bobSolo.Id, 1, "Bob only", T0.AddMinutes(5));
        // Service session rating (excluded) + old-edition rating (must not leak).
        Eval(evt.Id, brk.Id, 5, "Nice lunch", T0.AddMinutes(40));
        Eval(other.Id, old.Id, 2, "Old comment", T0.AddDays(-365));
        await db.SaveChangesAsync();

        return new Seeded(evt.Id, alice.Id, bob.Id, carol.Id, mc.Id, tech.Id, other.Id);
    }

    [Fact]
    public async Task Own_row_scope_a_speaker_sees_only_their_own_sessions()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = new SpeakerEvaluationsService(db);

        var r = await svc.GetMyEvaluationsAsync(s.EventId, s.AliceId, ParticipantRole.Speaker);

        // Alice's two sessions (master class + tech talk) — never Bob's solo.
        Assert.Equal(2, r.Sessions.Count);
        Assert.DoesNotContain(r.Sessions, x => x.Title == "Bob Solo");
        Assert.Contains(r.Sessions, x => x.Title == "Kubernetes Workshop");
        Assert.Contains(r.Sessions, x => x.Title == "Future Talk");
    }

    [Fact]
    public async Task Per_session_count_average_and_comments_surface_newest_first()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = new SpeakerEvaluationsService(db);

        var r = await svc.GetMyEvaluationsAsync(s.EventId, s.AliceId, ParticipantRole.Speaker);
        var mc = Assert.Single(r.Sessions, x => x.Title == "Kubernetes Workshop");

        Assert.Equal(3, mc.Count);                  // 3 ratings (blank-comment one still counts)
        Assert.Equal(4.0, mc.AverageRating);        // (5+4+3)/3
        Assert.True(mc.IsMasterClass);
        Assert.Equal("Room A", mc.Room);

        // Only the two non-blank comments, newest (Too fast @ +30) first.
        Assert.Equal(2, mc.Comments.Count);
        Assert.Equal("Too fast", mc.Comments[0].Comment);
        Assert.Equal(3, mc.Comments[0].Rating);
        Assert.Equal("Loved it!", mc.Comments[1].Comment);
    }

    [Fact]
    public async Task Overall_count_and_average_roll_up_across_the_speakers_sessions()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = new SpeakerEvaluationsService(db);

        var r = await svc.GetMyEvaluationsAsync(s.EventId, s.AliceId, ParticipantRole.Speaker);

        // 3 (mc) + 1 (tech) = 4 ratings; (5+4+3+5)/4 = 4.25 — never includes Bob's/service/old.
        Assert.Equal(4, r.TotalCount);
        Assert.Equal(4.25, r.OverallAverage);
    }

    [Fact]
    public async Task A_session_with_no_ratings_still_appears_with_null_average()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);

        // Give Carol a session but no ratings.
        var carolSess = new Session
        {
            EventId = s.EventId, SessionizeId = "s-carol", Title = "Carol Talk",
            Type = SessionType.TechnicalSession, Room = "Room C",
            StartsAt = T0, EndsAt = T0.AddMinutes(50),
        };
        carolSess.SessionSpeakers.Add(new SessionSpeaker
        { Session = carolSess, ParticipantId = s.CarolId });
        db.Sessions.Add(carolSess);
        await db.SaveChangesAsync();

        var svc = new SpeakerEvaluationsService(db);
        var r = await svc.GetMyEvaluationsAsync(s.EventId, s.CarolId, ParticipantRole.Speaker);

        var only = Assert.Single(r.Sessions);
        Assert.Equal("Carol Talk", only.Title);
        Assert.Equal(0, only.Count);
        Assert.Null(only.AverageRating);
        Assert.Empty(only.Comments);
        Assert.Equal(0, r.TotalCount);
        Assert.Null(r.OverallAverage);
    }

    [Fact]
    public async Task Service_sessions_and_other_editions_never_leak()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = new SpeakerEvaluationsService(db);

        var r = await svc.GetMyEvaluationsAsync(s.EventId, s.AliceId, ParticipantRole.Speaker);

        Assert.DoesNotContain(r.Sessions, x => x.Title == "Lunch");     // service session
        Assert.DoesNotContain(r.Sessions, x => x.Title == "Old Talk");  // other edition
        // The "Nice lunch"/"Old comment" texts must never surface.
        Assert.DoesNotContain(r.Sessions.SelectMany(x => x.Comments),
            c => c.Comment is "Nice lunch" or "Old comment" or "Bob only");
    }

    [Fact]
    public async Task Non_speaker_role_gets_empty_result()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = new SpeakerEvaluationsService(db);

        var r = await svc.GetMyEvaluationsAsync(s.EventId, s.AliceId, ParticipantRole.Attendee);

        Assert.Empty(r.Sessions);
        Assert.Equal(0, r.TotalCount);
        Assert.Null(r.OverallAverage);
    }

    [Fact]
    public async Task Speaker_with_no_linked_sessions_gets_empty_result()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = new SpeakerEvaluationsService(db);

        // Carol is a speaker but linked to no session in the base seed.
        var r = await svc.GetMyEvaluationsAsync(s.EventId, s.CarolId, ParticipantRole.Speaker);

        Assert.Empty(r.Sessions);
        Assert.Equal(0, r.TotalCount);
    }
}
