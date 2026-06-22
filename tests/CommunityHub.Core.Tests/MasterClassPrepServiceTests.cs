using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// FEATURE 2: the master-class attendee LANDING PAGE authority
/// (<see cref="MasterClassPrepService"/>) — prep editing (linked speaker / organizer
/// only), the Q&amp;A comment thread (confirmed attendee + the MC's speakers), and the
/// 1:1 private question that reaches the MC's speakers and whose answer returns to the
/// attendee.
/// </summary>
public sealed class MasterClassPrepServiceTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 1, 9, 0, 0, TimeSpan.Zero);

    private sealed record Seed(
        int EventId, int OrganizerId, int LinkedSpeakerId, int OtherSpeakerId,
        int McId, int ConfirmedAttendeeId, int NoSeatAttendeeId);

    private static MasterClassPrepService NewSvc(CommunityHubDbContext db) =>
        new(db, new FixedClock(Now));

    private static async Task<Seed> SeedAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "PREP27", CommunityName = "Prep", DisplayName = "Prep 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var org = new Participant { EventId = evt.Id, FullName = "Org", Email = "org@example.test", Role = ParticipantRole.Organizer, IsActive = true };
        var linked = new Participant { EventId = evt.Id, FullName = "Linked Speaker", Email = "linked@example.test", Role = ParticipantRole.Speaker, IsActive = true };
        var other = new Participant { EventId = evt.Id, FullName = "Other Speaker", Email = "other@example.test", Role = ParticipantRole.Speaker, IsActive = true };
        db.Participants.AddRange(org, linked, other);
        await db.SaveChangesAsync();

        var mgmt = new SessionManagementService(db, new NullRoomQrProvider(), new FixedClock(Now));
        var mc = await mgmt.AddHubSessionAsync(
            evt.Id, "Hands-on MC", SessionType.MasterClass, SessionLength.FullDay,
            room: "Lab", speakerParticipantIds: new[] { linked.Id });
        mc.MasterClassCapacity = 10;
        await db.SaveChangesAsync();

        var confirmed = new Attendee { EventId = evt.Id, Email = "seat@example.test", FirstName = "Seat", LastName = "Holder", TicketStatus = TicketStatus.TwoDay };
        var noSeat = new Attendee { EventId = evt.Id, Email = "noseat@example.test", FirstName = "No", LastName = "Seat", TicketStatus = TicketStatus.TwoDay };
        db.Attendees.AddRange(confirmed, noSeat);
        await db.SaveChangesAsync();

        // The confirmed attendee holds a confirmed seat in the MC.
        var signups = new MasterClassSignupService(db);
        var r = await signups.SignUpAsync(evt.Id, confirmed.Id, mc.Id);
        Assert.True(r.Ok);
        Assert.Equal(MasterClassSignupStatus.Confirmed, r.Signup!.Status);

        return new Seed(evt.Id, org.Id, linked.Id, other.Id, mc.Id, confirmed.Id, noSeat.Id);
    }

    // ----------------------------------------------------------- prep editing ---

    [Fact]
    public async Task Linked_speaker_can_edit_prep_and_it_is_stamped()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = NewSvc(db);

        Assert.True(await svc.CanEditAsync(s.EventId, s.McId, s.LinkedSpeakerId, ParticipantRole.Speaker));
        var session = await svc.UpdatePrepAsync(
            s.EventId, s.McId, s.LinkedSpeakerId, ParticipantRole.Speaker,
            "Bring a charged laptop and install the SDK.");

        Assert.Equal("Bring a charged laptop and install the SDK.", session.PrepContent);
        Assert.NotNull(session.PrepUpdatedAt);
        Assert.Equal(s.LinkedSpeakerId, session.PrepUpdatedByParticipantId);
    }

    [Fact]
    public async Task Organizer_can_edit_prep()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = NewSvc(db);
        Assert.True(await svc.CanEditAsync(s.EventId, s.McId, s.OrganizerId, ParticipantRole.Organizer));
        var session = await svc.UpdatePrepAsync(s.EventId, s.McId, s.OrganizerId, ParticipantRole.Organizer, "Notes.");
        Assert.Equal("Notes.", session.PrepContent);
    }

    [Fact]
    public async Task Non_linked_speaker_cannot_edit_prep()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = NewSvc(db);

        Assert.False(await svc.CanEditAsync(s.EventId, s.McId, s.OtherSpeakerId, ParticipantRole.Speaker));
        await Assert.ThrowsAsync<MasterClassPrepAccessDeniedException>(
            () => svc.UpdatePrepAsync(s.EventId, s.McId, s.OtherSpeakerId, ParticipantRole.Speaker, "Sneaky edit"));
    }

    // ----------------------------------------------------------- view + comment -

    [Fact]
    public async Task Confirmed_attendee_can_view_and_comment_but_unconfirmed_cannot()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = NewSvc(db);

        Assert.True(await svc.AttendeeHasConfirmedSeatAsync(s.EventId, s.McId, s.ConfirmedAttendeeId));
        Assert.False(await svc.AttendeeHasConfirmedSeatAsync(s.EventId, s.McId, s.NoSeatAttendeeId));

        var c = await svc.AddAttendeeCommentAsync(s.EventId, s.McId, s.ConfirmedAttendeeId, "Looking forward to it!");
        Assert.Equal("Looking forward to it!", c.Body);
        Assert.Equal(s.ConfirmedAttendeeId, c.AuthorAttendeeId);
        Assert.Equal("Seat Holder", c.AuthorDisplayName);

        // The MC's speaker can comment too (always a participant on their own MC).
        await svc.AddParticipantCommentAsync(s.EventId, s.McId, s.LinkedSpeakerId, ParticipantRole.Speaker, "See you there.");

        var thread = await svc.LoadCommentsAsync(s.EventId, s.McId);
        Assert.Equal(2, thread.Count);

        // A non-confirmed attendee cannot comment.
        await Assert.ThrowsAsync<MasterClassPrepAccessDeniedException>(
            () => svc.AddAttendeeCommentAsync(s.EventId, s.McId, s.NoSeatAttendeeId, "Let me in"));
    }

    // ----------------------------------------------------------- 1:1 question ---

    [Fact]
    public async Task Private_question_reaches_speakers_and_answer_returns_to_attendee()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = NewSvc(db);

        // Confirmed attendee asks a 1:1 private question.
        var q = await svc.AskPrivateQuestionAsync(
            s.EventId, s.McId, s.ConfirmedAttendeeId, "Can I run this on an ARM laptop?");
        Assert.True(q.IsPrivate);
        Assert.Equal(s.ConfirmedAttendeeId, q.AskerAttendeeId);
        Assert.Equal(SessionQuestionStatus.Open, q.Status);

        // The MC's speaker SEES it via the SessionQuestionService (the speaker hub path).
        var qsvc = new SessionQuestionService(db, new FixedClock(Now));
        var speakerActor = new SessionQuestionService.ActorContext(
            s.LinkedSpeakerId, "linked@example.test", ParticipantRole.Speaker, s.EventId);
        var forSpeaker = await qsvc.LoadForSessionAsync(speakerActor, s.McId);
        Assert.Contains(forSpeaker, x => x.Id == q.Id && x.IsPrivate);

        // A speaker NOT on the MC cannot see / answer it.
        var otherActor = new SessionQuestionService.ActorContext(
            s.OtherSpeakerId, "other@example.test", ParticipantRole.Speaker, s.EventId);
        await Assert.ThrowsAsync<SessionQuestionAccessDeniedException>(
            () => qsvc.LoadForSessionAsync(otherActor, s.McId));

        // The MC's speaker answers.
        Assert.True(await qsvc.RespondAsync(speakerActor, q.Id, "Yes — ARM is supported."));

        // The answer returns to the attendee on the landing page.
        var mine = await svc.LoadMyPrivateQuestionsAsync(s.EventId, s.McId, s.ConfirmedAttendeeId);
        var answered = Assert.Single(mine);
        Assert.Equal("Yes — ARM is supported.", answered.ResponseText);
        Assert.Equal(SessionQuestionStatus.Answered, answered.Status);

        // A non-confirmed attendee cannot ask.
        await Assert.ThrowsAsync<MasterClassPrepAccessDeniedException>(
            () => svc.AskPrivateQuestionAsync(s.EventId, s.McId, s.NoSeatAttendeeId, "let me in"));
    }

    [Fact]
    public async Task Landing_view_loads_for_master_class_only()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = NewSvc(db);

        var view = await svc.GetLandingAsync(s.EventId, s.McId);
        Assert.NotNull(view);
        Assert.Equal("Hands-on MC", view!.Title);
        Assert.Contains("Linked Speaker", view.Speakers);

        // A non-master-class session has no landing view.
        var mgmt = new SessionManagementService(db, new NullRoomQrProvider(), new FixedClock(Now));
        var tech = await mgmt.AddHubSessionAsync(s.EventId, "A Talk", SessionType.TechnicalSession, SessionLength.SixtyMin);
        Assert.Null(await svc.GetLandingAsync(s.EventId, tech.Id));
    }
}
