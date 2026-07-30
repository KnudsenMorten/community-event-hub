using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §327d — the session-delete BLOCKER probe (from the §326cc destructive-operation review,
/// operator: "I don't want to end in a disaster losing data due to some unforeseen scenario").
///
/// <para>The probe decides whether a session may be hard-deleted. Anything hanging off the
/// session that it does NOT know about is either destroyed silently or blows up as an FK 500
/// mid-delete — so each dependency gets a test that proves the delete is refused and that the
/// row survives. A blocker that is only "obviously there" in the code is one rename away from
/// being gone.</para>
/// </summary>
public sealed class SessionDeletionBlockerTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"sessdel-{Guid.NewGuid():N}")
            .Options);

    private static async Task<Session> SeedSessionAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "SD27", CommunityName = "SessDel", DisplayName = "SessDel 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        var s = new Session
        {
            EventId = EventId, SessionizeId = "sez-1", Title = "A session",
            Type = SessionType.TechnicalSession,
        };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    /// <summary>Delete and assert it was REFUSED, returning the reason text.</summary>
    private static async Task<IReadOnlyList<string>> ExpectBlockedAsync(
        CommunityHubDbContext db, int sessionId)
    {
        var result = await new SessionDeletionService(db).DeleteAsync(EventId, sessionId);

        Assert.Equal(SessionDeletionService.DeletionStatus.Blocked, result.Status);
        Assert.NotEmpty(result.BlockingDependencies);

        // The session must still be there — a "blocked" that half-deleted would be worse
        // than no guard at all.
        Assert.True(await db.Sessions.AnyAsync(x => x.Id == sessionId));
        return result.BlockingDependencies;
    }

    [Fact]
    public async Task A_clean_session_is_deleted()
    {
        using var db = NewDb();
        var s = await SeedSessionAsync(db);

        var result = await new SessionDeletionService(db).DeleteAsync(EventId, s.Id);

        Assert.Equal(SessionDeletionService.DeletionStatus.Deleted, result.Status);
        Assert.False(await db.Sessions.AnyAsync(x => x.Id == s.Id));
    }

    [Fact]
    public async Task Master_class_comments_block_the_delete()
    {
        using var db = NewDb();
        var s = await SeedSessionAsync(db);
        db.MasterClassComments.Add(new MasterClassComment
        {
            EventId = EventId, SessionId = s.Id,
            AuthorDisplayName = "Attendee Alpha", Body = "Looking forward to this.",
        });
        await db.SaveChangesAsync();

        var blockers = await ExpectBlockedAsync(db, s.Id);

        Assert.Contains(blockers, b => b.Contains("comment", StringComparison.OrdinalIgnoreCase));
        Assert.True(await db.MasterClassComments.AnyAsync());   // attendee's words survive
    }

    [Fact]
    public async Task Evaluation_files_block_the_delete()
    {
        using var db = NewDb();
        var s = await SeedSessionAsync(db);
        db.SessionEvaluationFiles.Add(new SessionEvaluationFile
        {
            EventId = EventId, SessionId = s.Id, Kind = EvaluationPdfKind.Score,
            UploadedByName = "Olive Organizer", FileName = "qr.pdf",
            UploadedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var blockers = await ExpectBlockedAsync(db, s.Id);

        Assert.Contains(blockers, b => b.Contains("evaluation file", StringComparison.OrdinalIgnoreCase));
        Assert.True(await db.SessionEvaluationFiles.AnyAsync());
    }

    [Fact]
    public async Task Promo_graphics_block_the_delete()
    {
        using var db = NewDb();
        var s = await SeedSessionAsync(db);
        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = EventId, SessionId = s.Id, Type = GraphicAssetType.Session,
            StableKey = "session-1-card",
        });
        await db.SaveChangesAsync();

        var blockers = await ExpectBlockedAsync(db, s.Id);

        Assert.Contains(blockers, b => b.Contains("graphic", StringComparison.OrdinalIgnoreCase));
        Assert.True(await db.GraphicAssets.AnyAsync());
    }

    [Fact]
    public async Task Social_posts_block_the_delete()
    {
        using var db = NewDb();
        var s = await SeedSessionAsync(db);
        db.SoMePosts.Add(new SoMePost
        {
            EventId = EventId, SessionId = s.Id, Type = SoMePostType.Speaker,
            ScheduledAtUtc = DateTimeOffset.UtcNow, Status = SoMePostStatus.Published,
        });
        await db.SaveChangesAsync();

        var blockers = await ExpectBlockedAsync(db, s.Id);

        // A PUBLISHED post is already live externally — the row is the only record we posted it.
        Assert.Contains(blockers, b => b.Contains("social post", StringComparison.OrdinalIgnoreCase));
        Assert.True(await db.SoMePosts.AnyAsync());
    }

    [Fact]
    public async Task All_blocking_reasons_are_reported_together_not_one_at_a_time()
    {
        // An organizer clearing dependencies one delete-attempt at a time is a bad loop;
        // the probe reports everything that stands in the way in one pass.
        using var db = NewDb();
        var s = await SeedSessionAsync(db);
        db.MasterClassComments.Add(new MasterClassComment
        {
            EventId = EventId, SessionId = s.Id, AuthorDisplayName = "A", Body = "b",
        });
        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = EventId, SessionId = s.Id, Type = GraphicAssetType.Session, StableKey = "k",
        });
        db.SoMePosts.Add(new SoMePost
        {
            EventId = EventId, SessionId = s.Id, Type = SoMePostType.Speaker,
            ScheduledAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var blockers = await ExpectBlockedAsync(db, s.Id);

        Assert.Equal(3, blockers.Count);
    }

    [Fact]
    public async Task Speaker_links_alone_never_block_a_delete()
    {
        // Speaker links are IMPORT state, not somebody's work — they are cleaned on delete.
        // Pinning this stops a future "be safer" change from making sessions undeletable.
        using var db = NewDb();
        var s = await SeedSessionAsync(db);
        var p = new Participant
        {
            EventId = EventId, Email = "sp@example.test", FullName = "Speaker Alpha",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s.Id, ParticipantId = p.Id });
        await db.SaveChangesAsync();

        var result = await new SessionDeletionService(db).DeleteAsync(EventId, s.Id);

        Assert.Equal(SessionDeletionService.DeletionStatus.Deleted, result.Status);
        Assert.False(await db.SessionSpeakers.AnyAsync(x => x.SessionId == s.Id));
        Assert.True(await db.Participants.AnyAsync(x => x.Id == p.Id));   // the person stays
    }
}
