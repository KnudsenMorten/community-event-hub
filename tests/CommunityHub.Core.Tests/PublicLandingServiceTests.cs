using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for the PUBLIC landing read service (<see cref="PublicLandingService"/>,
/// REQUIREMENTS §21 PUBLIC). The service backs the anonymous <c>/</c> front door.
/// Proves:
///  - no active event → null (the page shows a friendly empty state),
///  - the active edition's facts (name/dates/venue) surface,
///  - the session count covers only this edition's NON-service sessions,
///  - <see cref="PublicLandingView.HasSelectedSpeakers"/> honours the SAME hard
///    gate as the public speakers page (only a selected, active, speaker-role
///    profile flips it true).
///
/// In-memory DbContext; synthetic ids + example.test — no real names.
/// </summary>
public sealed class PublicLandingServiceTests
{
    private static Event NewActive() => new()
    {
        Code = "PUB27", CommunityName = "Public Community",
        DisplayName = "Public Community 2027",
        StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        PreDayDate = new DateOnly(2027, 2, 8), VenueName = "Test Venue",
        IsActive = true,
    };

    [Fact]
    public async Task No_active_event_returns_null()
    {
        using var db = TestDb.New();
        var svc = new PublicLandingService(db);
        Assert.Null(await svc.BuildAsync());
    }

    [Fact]
    public async Task Surfaces_active_edition_facts_and_session_count()
    {
        using var db = TestDb.New();
        var evt = NewActive();
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        db.Sessions.AddRange(
            new Session { EventId = evt.Id, SessionizeId = "s1", Title = "Talk A" },
            new Session { EventId = evt.Id, SessionizeId = "s2", Title = "Talk B" },
            new Session { EventId = evt.Id, SessionizeId = "brk", Title = "Break", IsServiceSession = true });
        await db.SaveChangesAsync();

        var svc = new PublicLandingService(db);
        var view = await svc.BuildAsync();

        Assert.NotNull(view);
        Assert.Equal("Public Community", view!.CommunityName);
        Assert.Equal("Public Community 2027", view.EventDisplayName);
        Assert.Equal(new DateOnly(2027, 2, 9), view.StartDate);
        Assert.Equal(new DateOnly(2027, 2, 10), view.EndDate);
        Assert.Equal("Test Venue", view.VenueName);
        Assert.Equal(2, view.SessionCount);            // the break is excluded
        Assert.False(view.HasSelectedSpeakers);        // none selected yet
    }

    [Fact]
    public async Task HasSelectedSpeakers_only_true_when_a_speaker_is_published()
    {
        using var db = TestDb.New();
        var evt = NewActive();
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var p = new Participant
        {
            EventId = evt.Id, FullName = "Sam Speaker", Email = "sam@example.test",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        // Unselected profile → still false (the default state).
        db.SpeakerProfiles.Add(new SpeakerProfile
        { EventId = evt.Id, ParticipantId = p.Id, SelectedForPublish = false });
        await db.SaveChangesAsync();
        Assert.False((await new PublicLandingService(db).BuildAsync())!.HasSelectedSpeakers);

        // Flip the gate → true.
        var sp = await db.SpeakerProfiles.FirstAsync();
        sp.SelectedForPublish = true;
        await db.SaveChangesAsync();
        Assert.True((await new PublicLandingService(db).BuildAsync())!.HasSelectedSpeakers);
    }
}
