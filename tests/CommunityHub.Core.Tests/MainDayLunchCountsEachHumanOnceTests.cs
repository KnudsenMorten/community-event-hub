using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1258 — the main-day lunch figure counts each human ONCE, across the participant side and the
/// attendee side.
///
/// <para>🔴 Measured in PROD 2026-09-20, before this fix: the tile read
/// <c>260 crew &amp; speakers + 163 attendees = 423</c>, and 137 of those 260 were attendee LOGIN
/// rows that each also held a live ticket. ~137 lunches over-ordered. The true figure was 286.</para>
///
/// <para>🔑 The old rule was not careless — it was correct when written (<i>"crew do not have
/// tickets, so no dedup needed"</i>, 2026-08-14) and was quietly invalidated by a later feature:
/// the ticket launch provisions a participant login for every 2-day buyer. These tests pin the
/// property rather than the assumption, so the same drift cannot happen twice.</para>
/// </summary>
public class MainDayLunchCountsEachHumanOnceTests
{
    private const int EventId = 1;

    private static CommunityHub.Core.Data.CommunityHubDbContext NewDb()
    {
        var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        db.SaveChanges();
        return db;
    }

    private static void AddParticipant(
        CommunityHub.Core.Data.CommunityHubDbContext db, int id, string email, ParticipantRole role)
        => db.Participants.Add(new Participant
        {
            Id = id, EventId = EventId, Email = email, FullName = $"P{id}",
            Role = role, IsActive = true,
        });

    private static void AddTicket(
        CommunityHub.Core.Data.CommunityHubDbContext db, int id, string email,
        MirrorState state = MirrorState.Active)
        => db.Attendees.Add(new Attendee
        {
            Id = id, EventId = EventId, Email = email, BackstageTicketId = $"T{id}",
            FirstName = "T", LastName = $"{id}", TicketStatus = TicketStatus.TwoDay, MirrorState = state,
        });

    [Fact]
    public async Task An_attendee_login_holding_a_ticket_is_not_ALSO_crew()
    {
        // The live shape: a 2-day buyer gets a participant login AND has a ticket row.
        using var db = NewDb();
        AddParticipant(db, 1, "buyer@x.dk", ParticipantRole.Attendee);
        AddTicket(db, 1, "buyer@x.dk");
        await db.SaveChangesAsync();

        var crew = await LogisticsAudience.CountableCrew(db.Participants, db.Attendees, EventId).CountAsync();

        Assert.Equal(0, crew);
    }

    [Fact]
    public async Task Real_crew_still_count()
    {
        using var db = NewDb();
        AddParticipant(db, 1, "speaker@x.dk", ParticipantRole.Speaker);
        AddParticipant(db, 2, "sponsor@x.dk", ParticipantRole.Sponsor);
        AddParticipant(db, 3, "organizer@x.dk", ParticipantRole.Organizer);
        await db.SaveChangesAsync();

        Assert.Equal(3, await LogisticsAudience.CountableCrew(db.Participants, db.Attendees, EventId).CountAsync());
    }

    [Fact]
    public async Task A_SPEAKER_who_also_bought_a_ticket_is_counted_once_too()
    {
        // ⚠️ The rule is about the TICKET, not about Role == Attendee: whoever the row belongs to,
        // a live ticket already counts that human on the attendee side.
        using var db = NewDb();
        AddParticipant(db, 1, "speaks-and-buys@x.dk", ParticipantRole.Speaker);
        AddTicket(db, 1, "speaks-and-buys@x.dk");
        await db.SaveChangesAsync();

        Assert.Equal(0, await LogisticsAudience.CountableCrew(db.Participants, db.Attendees, EventId).CountAsync());
    }

    [Fact]
    public async Task A_CANCELLED_ticket_does_not_remove_someone_from_crew()
    {
        // 🔒 The dangerous direction: a cancelled ticket counts nobody on the attendee side, so
        // excluding them here would drop a real head and under-order a meal.
        using var db = NewDb();
        AddParticipant(db, 1, "was-a-buyer@x.dk", ParticipantRole.Speaker);
        AddTicket(db, 1, "was-a-buyer@x.dk", MirrorState.Cancelled);
        await db.SaveChangesAsync();

        Assert.Equal(1, await LogisticsAudience.CountableCrew(db.Participants, db.Attendees, EventId).CountAsync());
    }

    [Theory]
    [InlineData(TicketStatus.TwoDay)]
    [InlineData(TicketStatus.Other)]   // 1-day
    public async Task ANY_live_ticket_class_removes_its_holder_from_the_MAIN_DAY_crew(TicketStatus cls)
    {
        // 🔑 The main day admits every ticket holder, so both classes are a second count there.
        // (The PRE-DAY is the opposite case and deliberately does not use this rule: only a 2-day
        // ticket admits you to the Master Class day, so a 1-day holder is not counted on that
        // attendee side at all — see the note on LogisticsAudience.CountableCrew.)
        using var db = NewDb();
        AddParticipant(db, 1, "holder@x.dk", ParticipantRole.Speaker);
        db.Attendees.Add(new Attendee
        {
            Id = 1, EventId = EventId, Email = "holder@x.dk", BackstageTicketId = "T1",
            FirstName = "T", LastName = "1", TicketStatus = cls, MirrorState = MirrorState.Active,
        });
        await db.SaveChangesAsync();

        Assert.Equal(0, await LogisticsAudience.CountableCrew(db.Participants, db.Attendees, EventId).CountAsync());
    }

    [Fact]
    public async Task A_reassigned_or_refunded_ticket_row_counts_nobody_on_either_side()
    {
        // 🔒 Only MirrorState.Active is a person who eats — on the attendee side AND as the reason
        // to drop someone from the crew side. A cancelled row is history, not a head.
        using var db = NewDb();
        AddParticipant(db, 1, "crew@x.dk", ParticipantRole.Organizer);
        AddTicket(db, 1, "crew@x.dk", MirrorState.Cancelled);
        AddTicket(db, 2, "gone@x.dk", MirrorState.Cancelled);
        await db.SaveChangesAsync();

        var counts = await new LunchHeadcountService(db).ComputeAsync(EventId);

        Assert.Equal(1, counts.MainDayCrew);        // the organizer still eats
        Assert.Equal(0, counts.MainDayAttendees);   // neither cancelled ticket counts
        Assert.Equal(1, counts.MainDay);
    }

    [Fact]
    public async Task The_main_day_total_is_the_two_sides_added_with_nobody_twice()
    {
        using var db = NewDb();
        AddParticipant(db, 1, "speaker@x.dk", ParticipantRole.Speaker);        // crew only
        AddParticipant(db, 2, "buyer@x.dk", ParticipantRole.Attendee);         // login + ticket
        AddTicket(db, 1, "buyer@x.dk");
        AddTicket(db, 2, "walkup@x.dk");                                        // ticket, no login
        await db.SaveChangesAsync();

        var counts = await new LunchHeadcountService(db).ComputeAsync(EventId);

        Assert.Equal(1, counts.MainDayCrew);        // the speaker
        Assert.Equal(2, counts.MainDayAttendees);   // both tickets
        Assert.Equal(3, counts.MainDay);            // three humans, not four
    }
}
