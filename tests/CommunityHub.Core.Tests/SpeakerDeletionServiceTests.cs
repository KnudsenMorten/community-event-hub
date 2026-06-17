using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="SpeakerDeletionService"/> — the organizer
/// "remove from speakers" action (REQUIREMENTS §22). Un-speakering deletes the
/// speaker PROFILE only (the person stays a participant) and is delete-safely:
/// a speaker still linked to a session (on the agenda) is refused. Uses the EF
/// Core InMemory provider so the real DbContext mapping + queries run, no SQL.
/// Asserts the same invariants the participant + session delete services hold:
/// event-scoping, a profile-not-found path, linked-data-safe refusal, and that
/// the participant row is never touched.
/// </summary>
public sealed class SpeakerDeletionServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"speaker-del-{Guid.NewGuid():N}")
            .Options);

    private static async Task<Participant> SeedSpeakerAsync(
        CommunityHubDbContext db, int eventId, string name, bool withProfile = true)
    {
        var p = new Participant
        {
            EventId = eventId, FullName = name,
            Email = $"{name.Replace(" ", "").ToLowerInvariant()}@example.com",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        if (withProfile)
        {
            db.SpeakerProfiles.Add(new SpeakerProfile { EventId = eventId, ParticipantId = p.Id });
            await db.SaveChangesAsync();
        }
        return p;
    }

    private static async Task LinkSessionAsync(
        CommunityHubDbContext db, int eventId, int participantId, string title)
    {
        var s = new Session
        {
            EventId = eventId, SessionizeId = Guid.NewGuid().ToString("N"), Title = title,
        };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s.Id, ParticipantId = participantId });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Delete_removes_a_clean_speakers_profile_but_keeps_the_participant()
    {
        using var db = NewDb();
        var p = await SeedSpeakerAsync(db, EventId, "Clean Speaker");

        var svc = new SpeakerDeletionService(db);
        var result = await svc.DeleteAsync(EventId, p.Id);

        Assert.Equal(SpeakerDeletionService.DeletionStatus.Deleted, result.Status);
        Assert.Equal("Clean Speaker", result.Name);
        // Profile gone …
        Assert.False(await db.SpeakerProfiles.AnyAsync(sp => sp.ParticipantId == p.Id));
        // … but the person stays a participant.
        Assert.True(await db.Participants.AnyAsync(x => x.Id == p.Id));
    }

    [Fact]
    public async Task Delete_also_cleans_the_backstage_sync_artifact()
    {
        using var db = NewDb();
        var p = await SeedSpeakerAsync(db, EventId, "Synced Speaker");
        db.SpeakerBackstageEmailSyncs.Add(new SpeakerBackstageEmailSync
        {
            EventId = EventId, ParticipantId = p.Id,
            IdentityEmail = p.Email, DesiredEmail = p.Email,
        });
        await db.SaveChangesAsync();

        var svc = new SpeakerDeletionService(db);
        var result = await svc.DeleteAsync(EventId, p.Id);

        Assert.Equal(SpeakerDeletionService.DeletionStatus.Deleted, result.Status);
        Assert.False(await db.SpeakerBackstageEmailSyncs.AnyAsync(s => s.ParticipantId == p.Id));
    }

    [Fact]
    public async Task Delete_is_blocked_when_the_speaker_is_still_linked_to_a_session()
    {
        using var db = NewDb();
        var p = await SeedSpeakerAsync(db, EventId, "On Agenda");
        await LinkSessionAsync(db, EventId, p.Id, "Keynote");

        var svc = new SpeakerDeletionService(db);
        var result = await svc.DeleteAsync(EventId, p.Id);

        Assert.Equal(SpeakerDeletionService.DeletionStatus.Blocked, result.Status);
        Assert.Equal(1, result.SessionCount);
        // Untouched.
        Assert.True(await db.SpeakerProfiles.AnyAsync(sp => sp.ParticipantId == p.Id));
    }

    [Fact]
    public async Task Delete_reports_not_found_when_there_is_no_profile()
    {
        using var db = NewDb();
        // A participant with NO speaker profile — nothing to un-speaker.
        var p = await SeedSpeakerAsync(db, EventId, "Profile Less", withProfile: false);

        var svc = new SpeakerDeletionService(db);
        var result = await svc.DeleteAsync(EventId, p.Id);

        Assert.Equal(SpeakerDeletionService.DeletionStatus.NotFound, result.Status);
        Assert.True(await db.Participants.AnyAsync(x => x.Id == p.Id)); // untouched
    }

    [Fact]
    public async Task Delete_is_edition_scoped()
    {
        using var db = NewDb();
        var theirs = await SeedSpeakerAsync(db, OtherEventId, "Theirs");

        var svc = new SpeakerDeletionService(db);
        var result = await svc.DeleteAsync(EventId, theirs.Id);

        Assert.Equal(SpeakerDeletionService.DeletionStatus.NotFound, result.Status);
        Assert.True(await db.SpeakerProfiles.AnyAsync(sp => sp.ParticipantId == theirs.Id)); // untouched
    }

    [Fact]
    public async Task GetBlockingSessionCount_is_zero_for_a_clean_speaker_and_counts_links()
    {
        using var db = NewDb();
        var clean = await SeedSpeakerAsync(db, EventId, "Clean");
        var busy = await SeedSpeakerAsync(db, EventId, "Busy");
        await LinkSessionAsync(db, EventId, busy.Id, "A");
        await LinkSessionAsync(db, EventId, busy.Id, "B");

        var svc = new SpeakerDeletionService(db);

        Assert.Equal(0, await svc.GetBlockingSessionCountAsync(EventId, clean.Id));
        Assert.Equal(2, await svc.GetBlockingSessionCountAsync(EventId, busy.Id));
    }

    [Fact]
    public async Task DeleteMany_removes_clean_speakers_keeps_linked_ones_and_counts_honestly()
    {
        using var db = NewDb();
        var clean1 = await SeedSpeakerAsync(db, EventId, "Clean One");
        var clean2 = await SeedSpeakerAsync(db, EventId, "Clean Two");
        var linked = await SeedSpeakerAsync(db, EventId, "Linked");
        await LinkSessionAsync(db, EventId, linked.Id, "Session");

        var svc = new SpeakerDeletionService(db);
        var result = await svc.DeleteManyAsync(EventId, new[] { clean1.Id, clean2.Id, linked.Id });

        Assert.Equal(3, result.Matched);
        Assert.Equal(2, result.Deleted);
        Assert.Equal(1, result.Blocked);
        Assert.False(await db.SpeakerProfiles.AnyAsync(sp => sp.ParticipantId == clean1.Id));
        Assert.False(await db.SpeakerProfiles.AnyAsync(sp => sp.ParticipantId == clean2.Id));
        Assert.True(await db.SpeakerProfiles.AnyAsync(sp => sp.ParticipantId == linked.Id)); // kept
        // All three people remain participants regardless of un-speakering.
        Assert.Equal(3, await db.Participants.CountAsync(x => x.EventId == EventId));
    }

    [Fact]
    public async Task DeleteMany_empty_invalid_and_duplicate_selections_are_safe()
    {
        using var db = NewDb();
        var p = await SeedSpeakerAsync(db, EventId, "Speaker");

        var svc = new SpeakerDeletionService(db);
        var empty = await svc.DeleteManyAsync(EventId, Array.Empty<int>());
        var bogus = await svc.DeleteManyAsync(EventId, new[] { 0, -2, 9999 });
        var dupes = await svc.DeleteManyAsync(EventId, new[] { p.Id, p.Id, p.Id });

        Assert.Equal(0, empty.Matched);
        Assert.Equal(0, bogus.Matched);
        Assert.Equal(1, dupes.Matched);   // de-duped to one
        Assert.Equal(1, dupes.Deleted);
    }
}
