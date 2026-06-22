using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for attendee auto-provisioning (the <c>attendee-welcome</c> path): a
/// 2-day-ticket holder gets exactly one ACTIVE, login-capable Attendee Participant
/// at Ring1; the operation is idempotent (re-runs create nothing) and only the
/// NEW ids are returned (so the caller welcomes each holder once). Offline, EF
/// in-memory.
/// </summary>
public class AttendeeWelcomeProvisioningServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static AttendeeWelcomeProvisioningService NewService(CommunityHubDbContext db) =>
        new(db, new FixedClock(), NullLogger<AttendeeWelcomeProvisioningService>.Instance);

    private static async Task<int> SeedEventAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            CommunityName = "Test Community",
            DisplayName = "Test Community 2027",
            Code = "TC27",
            IsActive = true,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static void AddAttendee(
        CommunityHubDbContext db, int eventId, string ticketId, string email,
        string fullName, TicketStatus status)
    {
        db.Attendees.Add(new Attendee
        {
            EventId = eventId,
            BackstageTicketId = ticketId,
            Email = email,
            FullName = fullName,
            TicketStatus = status,
        });
    }

    [Fact]
    public async Task Two_day_holder_gets_one_active_login_capable_attendee_participant()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);
        AddAttendee(db, eventId, "t1", "ATTendee@Example.com", "Avery Attendee", TicketStatus.TwoDay);
        await db.SaveChangesAsync();

        var created = await NewService(db).ProvisionAsync(eventId);

        var pid = Assert.Single(created);
        var p = await db.Participants.SingleAsync(x => x.Id == pid);
        Assert.Equal(ParticipantRole.Attendee, p.Role);
        Assert.True(p.IsActive);
        Assert.Equal(ParticipantLifecycleState.Active, p.LifecycleState);   // can sign in
        Assert.Equal(Rings.Default, p.Ring);   // Broad — release safety: not auto-welcomed until email feature is promoted to Broad
        Assert.Equal("attendee@example.com", p.Email);                      // lower-cased
        Assert.Equal("Avery Attendee", p.FullName);
        Assert.Null(p.WelcomeWithLoginSentAt);                             // not welcomed yet (caller does that)
    }

    [Fact]
    public async Task Provisioning_is_idempotent_no_duplicates_on_rerun()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);
        AddAttendee(db, eventId, "t1", "a@example.com", "A", TicketStatus.TwoDay);
        AddAttendee(db, eventId, "t2", "b@example.com", "B", TicketStatus.TwoDay);
        await db.SaveChangesAsync();

        var svc = NewService(db);
        var first = await svc.ProvisionAsync(eventId);
        var second = await svc.ProvisionAsync(eventId);

        Assert.Equal(2, first.Count);
        Assert.Empty(second);                                              // nothing new
        Assert.Equal(2, await db.Participants.CountAsync(p => p.EventId == eventId));
    }

    [Fact]
    public async Task Holder_with_an_existing_participant_is_skipped()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);
        db.Participants.Add(new Participant
        {
            EventId = eventId, Email = "exists@example.com", FullName = "Already Here",
            Role = ParticipantRole.Speaker, IsActive = true,
        });
        AddAttendee(db, eventId, "t1", "exists@example.com", "Already Here", TicketStatus.TwoDay);
        await db.SaveChangesAsync();

        var created = await NewService(db).ProvisionAsync(eventId);

        Assert.Empty(created);                                             // not re-created
        Assert.Equal(1, await db.Participants.CountAsync(p => p.EventId == eventId));
    }

    [Fact]
    public async Task Non_two_day_attendees_are_not_provisioned()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);
        AddAttendee(db, eventId, "t1", "one-day@example.com", "One Day", TicketStatus.Other);
        AddAttendee(db, eventId, "t2", "none@example.com", "No Ticket", TicketStatus.None);
        await db.SaveChangesAsync();

        var created = await NewService(db).ProvisionAsync(eventId);

        Assert.Empty(created);
        Assert.Equal(0, await db.Participants.CountAsync(p => p.EventId == eventId));
    }
}
