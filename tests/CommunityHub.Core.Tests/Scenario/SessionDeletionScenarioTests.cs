using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: an organizer deletes a bad / duplicate session (REQUIREMENTS §21
/// organizer "Sessions delete / CRUD gap"). The safe semantics live in
/// <see cref="SessionDeletionService"/>:
///   - a clean session (no attendee engagement) is deleted, and its speaker links
///     go with it (links are import-state, never orphaned),
///   - a session with attendee QUESTIONS / EVALUATIONS / master-class BOOKINGS is
///     REFUSED with a reason, so attendee data can't be silently destroyed,
///   - delete is edition-scoped (a session in another edition is never found),
///   - an imported (non-hub-added) delete is flagged as "will be recreated by a
///     re-import" so the organizer knows.
///
/// No real customer / person data — example.test addresses + generic names only.
/// </summary>
public sealed class SessionDeletionScenarioTests
{
    private static async Task<(Data.CommunityHubDbContext Db, int EventId, Session Session)>
        SeedWithSessionAsync(bool hubAdded = true)
    {
        var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var session = new Session
        {
            EventId = seed.EventId,
            SessionizeId = hubAdded ? "hub-" + Guid.NewGuid().ToString("N") : "sz-123",
            Title = hubAdded ? "Sponsor showcase (duplicate)" : "Imported talk",
            IsHubAdded = hubAdded,
            Type = SessionType.TechnicalSession,
            Length = SessionLength.FiftyMin,
        };
        // Link a seeded speaker so we can prove the link is cleaned, not orphaned.
        session.SessionSpeakers.Add(new SessionSpeaker { ParticipantId = seed.SpeakerOneId });
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        return (db, seed.EventId, session);
    }

    [Fact]
    public async Task Clean_session_is_deleted_with_its_speaker_links()
    {
        var (db, eventId, session) = await SeedWithSessionAsync(hubAdded: true);
        using (db)
        {
            var svc = new SessionDeletionService(db);
            var result = await svc.DeleteAsync(eventId, session.Id);

            Assert.Equal(SessionDeletionService.DeletionStatus.Deleted, result.Status);
            Assert.False(result.WasImported);          // hub-added
            Assert.False(await db.Sessions.AnyAsync(s => s.Id == session.Id));
            // The speaker link went with it — no orphan.
            Assert.False(await db.SessionSpeakers.AnyAsync(ss => ss.SessionId == session.Id));
        }
    }

    [Fact]
    public async Task Imported_session_delete_is_flagged_as_recreatable()
    {
        var (db, eventId, session) = await SeedWithSessionAsync(hubAdded: false);
        using (db)
        {
            var svc = new SessionDeletionService(db);
            var result = await svc.DeleteAsync(eventId, session.Id);

            Assert.Equal(SessionDeletionService.DeletionStatus.Deleted, result.Status);
            Assert.True(result.WasImported);   // organizer is warned a re-import recreates it
        }
    }

    [Fact]
    public async Task Session_with_attendee_questions_is_blocked()
    {
        var (db, eventId, session) = await SeedWithSessionAsync();
        using (db)
        {
            db.SessionQuestions.Add(new SessionQuestion
            {
                EventId = eventId, SessionId = session.Id,
                QuestionText = "Will there be slides?",
            });
            await db.SaveChangesAsync();

            var svc = new SessionDeletionService(db);
            var result = await svc.DeleteAsync(eventId, session.Id);

            Assert.Equal(SessionDeletionService.DeletionStatus.Blocked, result.Status);
            Assert.Contains(result.BlockingDependencies, d => d.Contains("question"));
            // Nothing was destroyed.
            Assert.True(await db.Sessions.AnyAsync(s => s.Id == session.Id));
            Assert.True(await db.SessionQuestions.AnyAsync(q => q.SessionId == session.Id));
        }
    }

    [Fact]
    public async Task Session_with_attendee_evaluations_is_blocked()
    {
        var (db, eventId, session) = await SeedWithSessionAsync();
        using (db)
        {
            db.SessionEvaluations.Add(new SessionEvaluation
            {
                EventId = eventId, SessionId = session.Id, Rating = 5,
            });
            await db.SaveChangesAsync();

            var svc = new SessionDeletionService(db);
            var result = await svc.DeleteAsync(eventId, session.Id);

            Assert.Equal(SessionDeletionService.DeletionStatus.Blocked, result.Status);
            Assert.Contains(result.BlockingDependencies, d => d.Contains("evaluation"));
        }
    }

    [Fact]
    public async Task Session_with_masterclass_bookings_is_blocked()
    {
        var (db, eventId, session) = await SeedWithSessionAsync();
        using (db)
        {
            db.MasterClassParticipants.Add(new MasterClassParticipant
            {
                EventId = eventId, SessionId = session.Id,
                ParticipantId = 0,    // synthetic booking row
                BookingRecordId = "booking-1",
                BookedEmail = "guest@example.test", BookedName = "Guest",
            });
            await db.SaveChangesAsync();

            var svc = new SessionDeletionService(db);
            var result = await svc.DeleteAsync(eventId, session.Id);

            Assert.Equal(SessionDeletionService.DeletionStatus.Blocked, result.Status);
            Assert.Contains(result.BlockingDependencies, d => d.Contains("booking"));
        }
    }

    [Fact]
    public async Task Delete_is_edition_scoped()
    {
        var (db, eventId, session) = await SeedWithSessionAsync();
        using (db)
        {
            var svc = new SessionDeletionService(db);
            // A different edition never finds this session.
            var result = await svc.DeleteAsync(eventId + 9999, session.Id);

            Assert.Equal(SessionDeletionService.DeletionStatus.NotFound, result.Status);
            Assert.True(await db.Sessions.AnyAsync(s => s.Id == session.Id));
        }
    }

    [Fact]
    public async Task Blockers_probe_is_readonly_and_lists_engagement()
    {
        var (db, eventId, session) = await SeedWithSessionAsync();
        using (db)
        {
            db.SessionQuestions.Add(new SessionQuestion
            {
                EventId = eventId, SessionId = session.Id, QuestionText = "Q?",
            });
            await db.SaveChangesAsync();

            var svc = new SessionDeletionService(db);
            var blockers = await svc.GetBlockersAsync(eventId, session.Id);

            Assert.NotEmpty(blockers);
            // The probe changed nothing.
            Assert.True(await db.Sessions.AnyAsync(s => s.Id == session.Id));
        }
    }
}
