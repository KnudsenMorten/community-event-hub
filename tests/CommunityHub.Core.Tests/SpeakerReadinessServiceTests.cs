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
        SpeakerCategory? category = SpeakerCategory.Community,
        DateTimeOffset? bioEdited = null, string? photoUrl = null,
        DateTimeOffset? calendarSet = null)
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
            Category = category,
            BioLastEditedBySpeakerAt = bioEdited,
            PhotoUrl = photoUrl,
            CalendarEmailSetAt = calendarSet,
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
    public async Task Fresh_community_speaker_has_the_full_applicable_set_mostly_missing()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        var pid = await NewSpeakerAsync(db, eventId, "Per Son", "per@x.test");

        var r = await NewService(db).BuildForSpeakerAsync(eventId, pid);

        Assert.NotNull(r);

        // 🔴 §784.9(d) — THIS TEST USED TO ENCODE THE BUG. It asserted that a fresh speaker with no
        // tasks at all had "Other to-dos" DONE ("nothing open") — which is exactly how a speaker who
        // had NEVER LOGGED IN scored "1 of 8 done" on the organizer's roster. Something counted as
        // complete that nobody had done, and one vacuously-true item makes every number in that
        // column untrustworthy.
        //
        // Correct behaviour: with no other to-dos there is nothing to report, so the item is not
        // applicable at all — and a speaker who has done nothing scores ZERO.
        Assert.DoesNotContain(r!.Items, i => i.Key == "tasks");
        Assert.Equal(0, r.DoneCount);
        Assert.Equal(0, r.Percent);
        Assert.False(r.IsReady);

        // 11 applicable: the 6 original — details, headshot, hotel, dinner, upload-preview,
        // upload-final — PLUS the 5 Get-Started steps this speaker is entitled to (§784.9(c)).
        // masterclass is gone entirely (§784.9(a)); tasks is not applicable (above).
        Assert.Equal(11, r.ApplicableCount);
        Assert.DoesNotContain(r.Items, i => i.Key == "masterclass");
        Assert.Contains(r.MissingItems, i => i.Key == "details");
        Assert.Contains(r.MissingItems, i => i.Key == "headshot");
        Assert.Contains(r.MissingItems, i => i.Key == "hotel");
        Assert.Contains(r.MissingItems, i => i.Key == "dinner");
        Assert.Contains(r.MissingItems, i => i.Key == "upload-preview");
        Assert.Contains(r.MissingItems, i => i.Key == "upload-final");
        Assert.Equal("/Speaker/Details", r.MissingItems.First(i => i.Key == "details").FixLink);

        // 🔴 §784.9(c) — the two he named by hand: *"some are not shown like party or lunch
        // sign-up"*. They were absent from this view entirely, so an organizer could not see the
        // steps the speaker was being chased about.
        Assert.Contains(r.MissingItems, i => i.Key == "getstarted:party");
        Assert.Contains(r.MissingItems, i => i.Key == "getstarted:lunch");
        Assert.Contains(r.MissingItems, i => i.Key == "getstarted:swag");
        Assert.Contains(r.MissingItems, i => i.Key == "getstarted:calendar");
        Assert.Contains(r.MissingItems, i => i.Key == "getstarted:accept");
        Assert.Equal("/Party", r.MissingItems.First(i => i.Key == "getstarted:party").FixLink);

        // 🔒 And the ones that must NOT appear. `details`/`hotel`/`dinner` already have their own
        // signal — the wizard's copy would double-count them — and `welcome`/`deadlines` are
        // always-Done summary steps that would hand a speaker who has done nothing free progress,
        // which is §784.9(d) all over again.
        Assert.DoesNotContain(r.Items, i => i.Key == "getstarted:details");
        Assert.DoesNotContain(r.Items, i => i.Key == "getstarted:hotel");
        Assert.DoesNotContain(r.Items, i => i.Key == "getstarted:dinner");
        Assert.DoesNotContain(r.Items, i => i.Key == "getstarted:welcome");
        Assert.DoesNotContain(r.Items, i => i.Key == "getstarted:deadlines");
    }

    [Fact]
    public async Task Sponsor_category_speaker_is_not_gated_into_hotel()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        // §299 6.2: the Sponsor category entitles Dinner + lunches, NOT Hotel.
        var pid = await NewSpeakerAsync(db, eventId, "Spon Sor", "spon@x.test",
            category: SpeakerCategory.Sponsor);

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
            bioEdited: DateTimeOffset.UtcNow, photoUrl: "https://img/headshot.jpg",
            calendarSet: DateTimeOffset.UtcNow);

        db.HotelBookings.Add(new HotelBooking { EventId = eventId, ParticipantId = pid, NeedsRoom = true });
        db.DinnerSignups.Add(new DinnerSignup { EventId = eventId, ParticipantId = pid, Attending = true });

        // §784.9(c) — 100% now means the Get-Started steps too. That is the point of the change:
        // a speaker who has filled in the forms but never RSVP'd to the party is NOT ready, and
        // this view used to say they were.
        db.SwagPreferences.Add(new SwagPreference { EventId = eventId, ParticipantId = pid });
        db.LunchSignups.Add(new LunchSignup { EventId = eventId, ParticipantId = pid });
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = eventId, ParticipantId = pid, Name = "Done Speaker",
            Email = "done@x.test", Attending = true,
        });
        db.ParticipantPolicyAcceptances.Add(new ParticipantPolicyAcceptance
        {
            EventId = eventId, ParticipantId = pid, AcceptedAt = DateTimeOffset.UtcNow,
        });
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

    /// <summary>
    /// ⚰️ §784.9(a) — the inverse of the test that used to live here. Master Class prep is an
    /// OPTIONAL SERVICE, not a task (operator 2026-08-03), so it must never appear in readiness.
    /// </summary>
    /// <remarks>
    /// This is a REGRESSION GUARD, not a leftover. The old test asserted the signal was applicable
    /// when a speaker was linked to a master class and done once prep was published — which meant a
    /// master-class speaker who simply chose not to publish prep notes sat permanently below 100%
    /// and was pushed to the top of a roster sorted by "who needs chasing". Deleting the test with
    /// the feature would leave nothing to stop the next person reinstating it.
    /// </remarks>
    [Fact]
    public async Task Master_class_prep_is_NOT_a_readiness_signal_however_the_speaker_is_linked()
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

        // Linked to a master class, no prep published: still no such item, and it does not drag
        // the score down.
        var before = await NewService(db).BuildForSpeakerAsync(eventId, pid);
        Assert.DoesNotContain(before!.Items, i => i.Key == "masterclass");

        // Prep published: still no such item — readiness is unchanged either way, which is the
        // whole point of it being an option rather than an obligation.
        session.PrepContent = "Bring a laptop with Docker installed.";
        await db.SaveChangesAsync();

        var after = await NewService(db).BuildForSpeakerAsync(eventId, pid);
        Assert.DoesNotContain(after!.Items, i => i.Key == "masterclass");
        Assert.Equal(before.ApplicableCount, after.ApplicableCount);
        Assert.Equal(before.DoneCount, after.DoneCount);
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
