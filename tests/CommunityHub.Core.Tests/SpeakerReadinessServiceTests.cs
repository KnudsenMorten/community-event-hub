using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Scenario tests for <see cref="SpeakerReadinessService"/> (REQUIREMENTS §134) over the
/// EF in-memory provider: it proves each signal is sourced from the RIGHT existing table
/// (profile bio-edit marker, headshot, entitlement-gated hotel/dinner, the §120 upload
/// tasks, Master Class prep, remaining to-dos) and the organizer roster sorts lowest
/// readiness first. FAKE names only.
/// </summary>
public sealed class SpeakerReadinessServiceTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"readiness-{Guid.NewGuid():N}")
            .Options);

    private static async Task<int> NewEventAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            Code = "ELDK27", DisplayName = "Test Edition", CommunityName = "Test Community",
            StartDate = new DateOnly(2026, 9, 1), EndDate = new DateOnly(2026, 9, 2),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static async Task<int> NewSpeakerAsync(
        CommunityHubDbContext db, int eventId, string name, string email,
        SpeakerFunding funding = SpeakerFunding.Supported,
        DateTimeOffset? bioEdited = null, string? photoUrl = null)
    {
        var p = new Participant
        {
            EventId = eventId, Email = email, FullName = name,
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = eventId, ParticipantId = p.Id,
            SpeakerFunding = funding,
            BioLastEditedBySpeakerAt = bioEdited,
            PhotoUrl = photoUrl,
        });
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static SpeakerReadinessService NewService(CommunityHubDbContext db) => new(db);

    [Fact]
    public async Task Non_speaker_returns_null()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        var p = new Participant
        {
            EventId = eventId, Email = "vol@x.test", FullName = "Vol Un Teer",
            Role = ParticipantRole.Volunteer, IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        var r = await NewService(db).BuildForSpeakerAsync(eventId, p.Id);
        Assert.Null(r);
    }

    [Fact]
    public async Task Fresh_supported_speaker_has_the_full_applicable_set_mostly_missing()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        var pid = await NewSpeakerAsync(db, eventId, "Per Son", "per@x.test");

        var r = await NewService(db).BuildForSpeakerAsync(eventId, pid);

        Assert.NotNull(r);
        // Master Class is NOT applicable (not linked) -> 7 applicable items.
        Assert.Equal(7, r!.ApplicableCount);
        Assert.DoesNotContain(r.Items, i => i.Key == "masterclass");
        // A fresh speaker with no tasks at all has "Other to-dos" satisfied (nothing open),
        // but everything else is missing.
        Assert.Contains(r.DoneItems, i => i.Key == "tasks");
        Assert.Contains(r.MissingItems, i => i.Key == "details");
        Assert.Contains(r.MissingItems, i => i.Key == "headshot");
        Assert.Contains(r.MissingItems, i => i.Key == "hotel");
        Assert.Contains(r.MissingItems, i => i.Key == "dinner");
        Assert.Contains(r.MissingItems, i => i.Key == "upload-preview");
        Assert.Contains(r.MissingItems, i => i.Key == "upload-final");
        Assert.Equal("/Speaker/Details", r.MissingItems.First(i => i.Key == "details").FixLink);
    }

    [Fact]
    public async Task Sponsor_self_funded_speaker_is_not_gated_into_hotel()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        // SponsorSelfFunded entitles AppreciationDinner + LunchMainDay, NOT Hotel.
        var pid = await NewSpeakerAsync(db, eventId, "Spon Sor", "spon@x.test",
            funding: SpeakerFunding.SponsorSelfFunded);

        var r = await NewService(db).BuildForSpeakerAsync(eventId, pid);

        Assert.NotNull(r);
        Assert.DoesNotContain(r!.Items, i => i.Key == "hotel");   // not entitled
        Assert.Contains(r.Items, i => i.Key == "dinner");          // entitled
    }

    [Fact]
    public async Task Completed_signals_are_counted_done()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        var pid = await NewSpeakerAsync(db, eventId, "Done Speaker", "done@x.test",
            bioEdited: DateTimeOffset.UtcNow, photoUrl: "https://img/headshot.jpg");

        db.HotelBookings.Add(new HotelBooking { EventId = eventId, ParticipantId = pid, NeedsRoom = true });
        db.DinnerSignups.Add(new DinnerSignup { EventId = eventId, ParticipantId = pid, Attending = true });
        db.Tasks.AddRange(
            new ParticipantTask
            {
                EventId = eventId, AssignedParticipantId = pid, Title = "Upload preview presentation",
                State = TaskState.Done, SourceKey = $"speakerdl:{pid}:{SpeakerReadinessService.PreviewTaskSlug}",
            },
            new ParticipantTask
            {
                EventId = eventId, AssignedParticipantId = pid, Title = "Upload final presentation",
                State = TaskState.Done, SourceKey = $"speakerdl:{pid}:{SpeakerReadinessService.FinalTaskSlug}",
            });
        await db.SaveChangesAsync();

        var r = await NewService(db).BuildForSpeakerAsync(eventId, pid);

        Assert.NotNull(r);
        Assert.True(r!.IsReady);
        Assert.Equal(100, r.Percent);
        Assert.Empty(r.MissingItems);
    }

    [Fact]
    public async Task Open_non_upload_task_blocks_the_other_todos_item()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        var pid = await NewSpeakerAsync(db, eventId, "Busy Speaker", "busy@x.test");

        db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId, AssignedParticipantId = pid, Title = "Join Signal groups",
            State = TaskState.Open, SourceKey = $"signal:{pid}",
        });
        await db.SaveChangesAsync();

        var r = await NewService(db).BuildForSpeakerAsync(eventId, pid);

        Assert.NotNull(r);
        Assert.Contains(r!.MissingItems, i => i.Key == "tasks");
    }

    [Fact]
    public async Task Master_class_prep_is_applicable_when_linked_and_done_when_prep_published()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        var pid = await NewSpeakerAsync(db, eventId, "MC Speaker", "mc@x.test");

        var session = new Session
        {
            EventId = eventId, Title = "Deep Dive Workshop", Type = SessionType.MasterClass,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = session.Id, ParticipantId = pid });
        await db.SaveChangesAsync();

        // Linked but no prep yet -> applicable + missing.
        var before = await NewService(db).BuildForSpeakerAsync(eventId, pid);
        Assert.Contains(before!.MissingItems, i => i.Key == "masterclass");

        // Publish prep -> done.
        session.PrepContent = "Bring a laptop with Docker installed.";
        await db.SaveChangesAsync();

        var after = await NewService(db).BuildForSpeakerAsync(eventId, pid);
        Assert.Contains(after!.DoneItems, i => i.Key == "masterclass");
    }

    [Fact]
    public async Task Roster_sorts_lowest_readiness_first()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);

        // "Ahead" speaker: bio edited + headshot -> higher score.
        var ahead = await NewSpeakerAsync(db, eventId, "Ada Head", "ada@x.test",
            bioEdited: DateTimeOffset.UtcNow, photoUrl: "https://img/a.jpg");
        // "Behind" speaker: nothing -> lower score.
        var behind = await NewSpeakerAsync(db, eventId, "Ben Hind", "ben@x.test");

        var roster = await NewService(db).BuildRosterAsync(eventId);

        Assert.Equal(2, roster.Count);
        Assert.Equal(behind, roster[0].ParticipantId);   // lowest readiness first
        Assert.Equal(ahead, roster[1].ParticipantId);
        Assert.True(roster[0].Percent <= roster[1].Percent);
    }

    [Fact]
    public async Task Roster_is_empty_when_no_speakers()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        var roster = await NewService(db).BuildRosterAsync(eventId);
        Assert.Empty(roster);
    }
}
