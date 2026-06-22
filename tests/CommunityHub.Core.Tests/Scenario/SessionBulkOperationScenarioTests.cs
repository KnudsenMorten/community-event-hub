using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: an organizer clears several bad / duplicate sessions at once via the
/// multi-select bulk bar (§20 universal CRUD + bulk). The bulk service
/// (<see cref="SessionBulkOperationService"/>) applies EXACTLY the single-row
/// <see cref="SessionDeletionService"/> safety, row by row:
///   - clean sessions (no attendee engagement) are deleted with their speaker links,
///   - sessions with attendee QUESTIONS / EVALUATIONS / master-class BOOKINGS are
///     left untouched (counted as Blocked) — attendee data is never silently lost,
///   - deleted IMPORTED (non-hub-added) sessions are counted so the organizer can be
///     warned a re-import recreates them,
///   - the whole batch is edition-scoped + one transaction.
///
/// No real customer / person data — example.test addresses + generic names only.
/// </summary>
public sealed class SessionBulkOperationScenarioTests
{
    private static Session NewSession(int eventId, string title, bool hubAdded = true) =>
        new()
        {
            EventId = eventId,
            SessionizeId = hubAdded ? "hub-" + Guid.NewGuid().ToString("N") : "sz-" + Guid.NewGuid().ToString("N"),
            Title = title,
            IsHubAdded = hubAdded,
            Type = SessionType.TechnicalSession,
            Length = SessionLength.FiftyMin,
        };

    [Fact]
    public async Task Clean_sessions_are_bulk_deleted_with_their_speaker_links()
    {
        var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        using (db)
        {
            var a = NewSession(seed.EventId, "Duplicate one");
            var b = NewSession(seed.EventId, "Duplicate two");
            a.SessionSpeakers.Add(new SessionSpeaker { ParticipantId = seed.SpeakerOneId });
            db.Sessions.AddRange(a, b);
            await db.SaveChangesAsync();

            var svc = new SessionBulkOperationService(db);
            var result = await svc.DeleteAsync(seed.EventId, new[] { a.Id, b.Id });

            Assert.Equal(2, result.Matched);
            Assert.Equal(2, result.Deleted);
            Assert.Equal(0, result.Blocked);
            Assert.Equal(0, result.ImportedDeleted);   // both hub-added
            Assert.False(await db.Sessions.AnyAsync(s => s.Id == a.Id || s.Id == b.Id));
            Assert.False(await db.SessionSpeakers.AnyAsync(ss => ss.SessionId == a.Id)); // no orphan
        }
    }

    [Fact]
    public async Task Sessions_with_attendee_engagement_are_blocked_clean_ones_deleted()
    {
        var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        using (db)
        {
            var clean = NewSession(seed.EventId, "Clean");
            var withQ = NewSession(seed.EventId, "Has questions");
            var withEval = NewSession(seed.EventId, "Has evaluations");
            var withBooking = NewSession(seed.EventId, "Has bookings");
            db.Sessions.AddRange(clean, withQ, withEval, withBooking);
            await db.SaveChangesAsync();

            db.SessionQuestions.Add(new SessionQuestion
            {
                EventId = seed.EventId, SessionId = withQ.Id, QuestionText = "Slides?",
            });
            db.SessionEvaluations.Add(new SessionEvaluation
            {
                EventId = seed.EventId, SessionId = withEval.Id, Rating = 5,
            });
            db.MasterClassParticipants.Add(new MasterClassParticipant
            {
                EventId = seed.EventId, SessionId = withBooking.Id, ParticipantId = 0,
                BookingRecordId = "booking-1", BookedEmail = "guest@example.test", BookedName = "Guest",
            });
            await db.SaveChangesAsync();

            var svc = new SessionBulkOperationService(db);
            var result = await svc.DeleteAsync(
                seed.EventId, new[] { clean.Id, withQ.Id, withEval.Id, withBooking.Id });

            Assert.Equal(4, result.Matched);
            Assert.Equal(1, result.Deleted);   // only the clean one
            Assert.Equal(3, result.Blocked);   // the three engaged sessions are protected
            Assert.False(await db.Sessions.AnyAsync(s => s.Id == clean.Id));
            Assert.True(await db.Sessions.AnyAsync(s => s.Id == withQ.Id));
            Assert.True(await db.Sessions.AnyAsync(s => s.Id == withEval.Id));
            Assert.True(await db.Sessions.AnyAsync(s => s.Id == withBooking.Id));
            // Nothing attendee-supplied was destroyed.
            Assert.True(await db.SessionQuestions.AnyAsync(q => q.SessionId == withQ.Id));
        }
    }

    [Fact]
    public async Task Imported_session_deletes_are_counted_for_the_reimport_warning()
    {
        var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        using (db)
        {
            var hub = NewSession(seed.EventId, "Hub-added", hubAdded: true);
            var imported = NewSession(seed.EventId, "Imported", hubAdded: false);
            db.Sessions.AddRange(hub, imported);
            await db.SaveChangesAsync();

            var svc = new SessionBulkOperationService(db);
            var result = await svc.DeleteAsync(seed.EventId, new[] { hub.Id, imported.Id });

            Assert.Equal(2, result.Deleted);
            Assert.Equal(1, result.ImportedDeleted);   // organizer is warned a re-import recreates it
        }
    }

    [Fact]
    public async Task Bulk_delete_is_edition_scoped_and_dedupes()
    {
        var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        using (db)
        {
            var mine = NewSession(seed.EventId, "Mine");
            db.Sessions.Add(mine);
            await db.SaveChangesAsync();

            var svc = new SessionBulkOperationService(db);
            // Ask for a foreign-edition id + dupes of mine.
            var result = await svc.DeleteAsync(
                seed.EventId, new[] { mine.Id, mine.Id, seed.EventId + 99999, 0, -1 });

            Assert.Equal(1, result.Matched);   // mine de-duped to one; the rest dropped/not-found
            Assert.Equal(1, result.Deleted);
            Assert.Equal(4, result.Skipped(5));
        }
    }

    [Fact]
    public async Task Empty_selection_is_a_safe_no_op()
    {
        var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        using (db)
        {
            var svc = new SessionBulkOperationService(db);
            var result = await svc.DeleteAsync(seed.EventId, Array.Empty<int>());

            Assert.Equal(0, result.Matched);
            Assert.Equal(0, result.Deleted);
            Assert.Equal(0, result.Blocked);
        }
    }
}
