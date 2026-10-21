using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Entitlements;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Entitlements;

/// <summary>
/// §1296 — the EF-backed side of hotel coverage: the per-edition standards, the per-person extra,
/// and resolving real people (one at a time and in bulk).
/// </summary>
public sealed class HotelCoverageServiceTests
{
    private const int EventId = 91;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-22T09:00:00Z");
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"hotel-cov-{Guid.NewGuid():N}").Options);

    private static HotelCoverageService NewSvc(CommunityHubDbContext db) => new(db, new FixedClock());

    private static void SeedEvent(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "C27", CommunityName = "C", DisplayName = "C 2027", IsActive = true,
            PreDayDate = new DateOnly(2027, 2, 8),
            StartDate = new DateOnly(2027, 2, 9),
            EndDate = new DateOnly(2027, 2, 9),
        });
        db.SaveChanges();
    }

    private static int AddPerson(
        CommunityHubDbContext db, string email, ParticipantRole role,
        SpeakerCategory? category = null)
    {
        var p = new Participant
        {
            EventId = EventId, Email = email, FullName = email.Split('@')[0],
            Role = role, IsActive = true,
        };
        db.Participants.Add(p);
        db.SaveChanges();

        if (role == ParticipantRole.Speaker)
        {
            db.SpeakerProfiles.Add(new SpeakerProfile
            {
                EventId = EventId, ParticipantId = p.Id, Category = category,
            });
            db.SaveChanges();
        }
        return p.Id;
    }

    private static void AddBooking(
        CommunityHubDbContext db, int participantId, DateOnly checkIn, DateOnly checkOut)
    {
        db.HotelBookings.Add(new HotelBooking
        {
            EventId = EventId, ParticipantId = participantId, NeedsRoom = true,
            CheckInDate = checkIn, CheckOutDate = checkOut,
        });
        db.SaveChanges();
    }

    // -----------------------------------------------------------------
    //  Standards
    // -----------------------------------------------------------------

    [Fact]
    public async Task Standards_fall_back_to_the_defaults_until_an_organizer_saves_one()
    {
        using var db = NewDb();
        SeedEvent(db);
        var svc = NewSvc(db);

        var before = await svc.GetStandardsAsync(EventId);
        Assert.Equal(HotelCoverage.All.Count, before.Count);      // every class is answered
        Assert.Equal(HotelCoverageRule.OfNights(1), before[HotelCoverageClass.Volunteer]);
        Assert.Empty(await svc.GetCustomizedClassesAsync(EventId));

        await svc.SetStandardAsync(EventId, HotelCoverageClass.Volunteer,
            HotelCoverageMode.Nights, 2, "org@example.test");

        var after = await svc.GetStandardsAsync(EventId);
        Assert.Equal(HotelCoverageRule.OfNights(2), after[HotelCoverageClass.Volunteer]);
        // The page can tell "you chose this" from "this is the built-in standard".
        Assert.Contains(HotelCoverageClass.Volunteer, await svc.GetCustomizedClassesAsync(EventId));
        // Everything else still reads its default.
        Assert.Equal(HotelCoverageRule.OfNights(6), after[HotelCoverageClass.SpeakerGuest]);
    }

    [Fact]
    public async Task Saving_a_standard_twice_updates_one_row_rather_than_adding_another()
    {
        using var db = NewDb();
        SeedEvent(db);
        var svc = NewSvc(db);

        await svc.SetStandardAsync(EventId, HotelCoverageClass.Media, HotelCoverageMode.Nights, 3, "a@x.test");
        await svc.SetStandardAsync(EventId, HotelCoverageClass.Media, HotelCoverageMode.WholeStay, 0, "b@x.test");

        Assert.Equal(1, await db.HotelCoverageStandards.CountAsync());
        var row = await db.HotelCoverageStandards.FirstAsync();
        Assert.Equal(HotelCoverageMode.WholeStay, row.Mode);
        Assert.Equal("b@x.test", row.UpdatedByEmail);
    }

    // -----------------------------------------------------------------
    //  The per-person extra
    // -----------------------------------------------------------------

    [Fact]
    public async Task Extra_nights_round_trip_and_zero_removes_the_row()
    {
        using var db = NewDb();
        SeedEvent(db);
        var svc = NewSvc(db);
        var vol = AddPerson(db, "vol@example.test", ParticipantRole.Volunteer);

        await svc.SetExtraNightsAsync(EventId, vol, 2, "two extra shifts", "org@example.test");
        Assert.Equal(2, await svc.GetExtraNightsAsync(EventId, vol));
        var row = await db.HotelCoverageExtras.FirstAsync();
        Assert.Equal("two extra shifts", row.Note);
        Assert.Equal("org@example.test", row.UpdatedByEmail);

        // 🔑 Back to zero DELETES the row, so "has a row" keeps meaning "was granted something"
        // rather than "somebody looked at them once".
        await svc.SetExtraNightsAsync(EventId, vol, 0, null, "org@example.test");
        Assert.Equal(0, await svc.GetExtraNightsAsync(EventId, vol));
        Assert.False(await db.HotelCoverageExtras.AnyAsync());
    }

    // -----------------------------------------------------------------
    //  Resolving real people
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_volunteer_on_the_standard_covers_one_night_and_pays_the_rest()
    {
        using var db = NewDb();
        SeedEvent(db);
        var vol = AddPerson(db, "vol@example.test", ParticipantRole.Volunteer);
        AddBooking(db, vol, new DateOnly(2027, 2, 8), new DateOnly(2027, 2, 11));   // 3 nights

        var r = await NewSvc(db).ResolveAsync(EventId, vol);

        Assert.Equal(HotelCoverageClass.Volunteer, r.Class);
        Assert.Equal(3, r.Result.Booked);
        Assert.Equal(1, r.Result.Covered);
        Assert.Equal(2, r.Result.OwnExpense);
        Assert.True(r.OnStandard);
    }

    [Fact]
    public async Task Extra_nights_move_one_named_person_without_touching_the_standard()
    {
        using var db = NewDb();
        SeedEvent(db);
        var svc = NewSvc(db);
        var a = AddPerson(db, "a@example.test", ParticipantRole.Volunteer);
        var b = AddPerson(db, "b@example.test", ParticipantRole.Volunteer);
        AddBooking(db, a, new DateOnly(2027, 2, 8), new DateOnly(2027, 2, 11));   // 3 nights
        AddBooking(db, b, new DateOnly(2027, 2, 8), new DateOnly(2027, 2, 11));   // 3 nights

        await svc.SetExtraNightsAsync(EventId, a, 2, "agreed", "org@example.test");

        var ra = await svc.ResolveAsync(EventId, a);
        var rb = await svc.ResolveAsync(EventId, b);

        Assert.Equal(3, ra.Result.Covered);      // 1 standard + 2 extra
        Assert.Equal(0, ra.Result.OwnExpense);
        Assert.False(ra.OnStandard);

        Assert.Equal(1, rb.Result.Covered);      // untouched
        Assert.Equal(2, rb.Result.OwnExpense);
    }

    /// <summary>
    /// The operator's own case: guest speakers are covered up to 6 nights, and the cap only bites
    /// when they book more.
    /// </summary>
    [Fact]
    public async Task A_guest_speaker_is_covered_up_to_six_nights()
    {
        using var db = NewDb();
        SeedEvent(db);
        var short_ = AddPerson(db, "g1@example.test", ParticipantRole.Speaker, SpeakerCategory.Guest);
        var long_ = AddPerson(db, "g2@example.test", ParticipantRole.Speaker, SpeakerCategory.Guest);
        AddBooking(db, short_, new DateOnly(2027, 2, 8), new DateOnly(2027, 2, 10));  // 2 nights
        AddBooking(db, long_, new DateOnly(2027, 2, 4), new DateOnly(2027, 2, 12));   // 8 nights

        var svc = NewSvc(db);
        var rs = await svc.ResolveAsync(EventId, short_);
        var rl = await svc.ResolveAsync(EventId, long_);

        Assert.Equal(2, rs.Result.Covered);
        Assert.Equal(0, rs.Result.OwnExpense);

        Assert.Equal(6, rl.Result.Covered);
        Assert.Equal(2, rl.Result.OwnExpense);
    }

    [Fact]
    public async Task An_organizer_is_covered_for_the_whole_stay_however_long()
    {
        using var db = NewDb();
        SeedEvent(db);
        var org = AddPerson(db, "org@example.test", ParticipantRole.Organizer);
        AddBooking(db, org, new DateOnly(2027, 2, 4), new DateOnly(2027, 2, 12));   // 8 nights

        var r = await NewSvc(db).ResolveAsync(EventId, org);

        Assert.Equal(HotelCoverageClass.Organizer, r.Class);
        Assert.Equal(8, r.Result.Covered);
        Assert.Equal(0, r.Result.OwnExpense);
    }

    /// <summary>
    /// A master-class speaker is the pre-day + main-day class, derived from their linked sessions —
    /// no stored flag, so a schedule change moves their coverage with it.
    /// </summary>
    [Fact]
    public async Task A_master_class_speaker_is_classified_from_their_sessions()
    {
        using var db = NewDb();
        SeedEvent(db);
        var spk = AddPerson(db, "mc@example.test", ParticipantRole.Speaker, SpeakerCategory.Community);
        AddBooking(db, spk, new DateOnly(2027, 2, 8), new DateOnly(2027, 2, 11));   // 3 nights

        var mc = new Session
        {
            EventId = EventId, Title = "Master class", Type = SessionType.MasterClass,
            StartsAt = new DateTimeOffset(2027, 2, 8, 9, 0, 0, TimeSpan.Zero),
        };
        var talk = new Session
        {
            EventId = EventId, Title = "Main talk", Type = SessionType.TechnicalSession,
            StartsAt = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero),
        };
        db.Sessions.AddRange(mc, talk);
        await db.SaveChangesAsync();
        db.SessionSpeakers.AddRange(
            new SessionSpeaker { SessionId = mc.Id, ParticipantId = spk },
            new SessionSpeaker { SessionId = talk.Id, ParticipantId = spk });
        await db.SaveChangesAsync();

        var r = await NewSvc(db).ResolveAsync(EventId, spk);

        Assert.Equal(HotelCoverageClass.SpeakerMasterClass, r.Class);
        Assert.Equal(2, r.Result.Covered);      // the agreed master-class standard
        Assert.Equal(1, r.Result.OwnExpense);
    }

    [Fact]
    public async Task Coverage_does_not_apply_to_attendees_or_uncategorized_speakers()
    {
        using var db = NewDb();
        SeedEvent(db);
        var att = AddPerson(db, "att@example.test", ParticipantRole.Attendee);
        var unk = AddPerson(db, "unk@example.test", ParticipantRole.Speaker, category: null);
        AddBooking(db, att, new DateOnly(2027, 2, 8), new DateOnly(2027, 2, 10));
        AddBooking(db, unk, new DateOnly(2027, 2, 8), new DateOnly(2027, 2, 10));

        var svc = NewSvc(db);
        Assert.Null((await svc.ResolveAsync(EventId, att)).Class);
        Assert.Null((await svc.ResolveAsync(EventId, unk)).Class);
    }

    [Fact]
    public async Task Somebody_who_needs_no_room_is_covered_for_nothing()
    {
        using var db = NewDb();
        SeedEvent(db);
        var vol = AddPerson(db, "vol@example.test", ParticipantRole.Volunteer);
        db.HotelBookings.Add(new HotelBooking
        {
            EventId = EventId, ParticipantId = vol, NeedsRoom = false,
        });
        await db.SaveChangesAsync();

        var r = await NewSvc(db).ResolveAsync(EventId, vol);
        Assert.Equal(0, r.Result.Booked);
        Assert.Equal(0, r.Result.Covered);
        Assert.Equal(0, r.Result.OwnExpense);
    }

    [Fact]
    public async Task ResolveMany_answers_for_a_whole_list_including_people_with_no_booking()
    {
        using var db = NewDb();
        SeedEvent(db);
        var vol = AddPerson(db, "vol@example.test", ParticipantRole.Volunteer);
        var org = AddPerson(db, "org@example.test", ParticipantRole.Organizer);
        var none = AddPerson(db, "none@example.test", ParticipantRole.Volunteer);
        AddBooking(db, vol, new DateOnly(2027, 2, 8), new DateOnly(2027, 2, 11));
        AddBooking(db, org, new DateOnly(2027, 2, 8), new DateOnly(2027, 2, 11));

        var all = await NewSvc(db).ResolveManyAsync(EventId, new[] { vol, org, none });

        Assert.Equal(3, all.Count);
        Assert.Equal(1, all[vol].Result.Covered);
        Assert.Equal(3, all[org].Result.Covered);
        Assert.Equal(0, all[none].Result.Booked);
    }
}
