using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Xunit;
using TR = CommunityHub.Core.Reminders.AttendeeTicketSyncService.TicketRow;

namespace CommunityHub.Core.Tests;

/// <summary>
/// REQUIREMENTS §230 (operator 2026-07-07) — "verify Magiclink works when an attendee is
/// renamed": a ticket REASSIGNMENT must reconcile BOTH holders' hub logins. The previous
/// holder (who no longer holds an active ticket) is LOCKED OUT — their Attendee-role
/// Participant flips IsActive=false, which the §169 magic-link resolver refuses — and the
/// new holder's login is restored if a previously-deactivated Participant exists for
/// their email (a brand-new holder is provisioned + welcomed by the sync job instead).
/// </summary>
public class ReassignmentLoginLifecycleTests
{
    private static async Task<int> SeedEventAsync(Data.CommunityHubDbContext db)
    {
        var e = new Event
        {
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(e); await db.SaveChangesAsync(); return e.Id;
    }

    private static async Task<int> SeedTicketAsync(
        Data.CommunityHubDbContext db, int ev, string ticketId, string email, TicketStatus status)
    {
        var a = new Attendee
        {
            EventId = ev, BackstageTicketId = ticketId, Email = email,
            FirstName = "Old", LastName = "Holder", TicketStatus = status,
            MirrorState = MirrorState.Active,
        };
        db.Attendees.Add(a); await db.SaveChangesAsync(); return a.Id;
    }

    private static async Task<int> SeedLoginAsync(
        Data.CommunityHubDbContext db, int ev, string email, bool isActive = true)
    {
        var p = new Participant
        {
            EventId = ev, Email = email, FullName = "Login " + email,
            Role = ParticipantRole.Attendee, IsActive = isActive,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p); await db.SaveChangesAsync(); return p.Id;
    }

    [Fact]
    public async Task Reassignment_locks_out_the_previous_holder()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedTicketAsync(db, ev, "T1", "old@x.dk", TicketStatus.TwoDay);
        var oldLogin = await SeedLoginAsync(db, ev, "old@x.dk");

        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));
        var r = await sync.SyncAsync(ev, new[]
        {
            new TR("T1", "New", "Person", "new@x.dk", TicketStatus.TwoDay, "2-day"),
        });

        Assert.Equal(1, r.Reassigned);
        // §230: the old holder's login is DEACTIVATED — their magic link stops resolving
        // (EmailMagicLinkService refuses inactive participants).
        Assert.False(db.Participants.Find(oldLogin)!.IsActive);
    }

    [Fact]
    public async Task Reassignment_restores_a_previously_deactivated_new_holder_login()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedTicketAsync(db, ev, "T1", "old@x.dk", TicketStatus.TwoDay);
        var oldLogin = await SeedLoginAsync(db, ev, "old@x.dk");
        // The new holder cancelled a ticket earlier this edition → deactivated login (§216).
        var newLogin = await SeedLoginAsync(db, ev, "new@x.dk", isActive: false);

        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));
        await sync.SyncAsync(ev, new[]
        {
            new TR("T1", "New", "Person", "new@x.dk", TicketStatus.TwoDay, "2-day"),
        });

        Assert.False(db.Participants.Find(oldLogin)!.IsActive);   // old locked out
        Assert.True(db.Participants.Find(newLogin)!.IsActive);    // new restored
    }

    [Fact]
    public async Task Reassignment_does_not_lock_out_an_old_holder_who_still_has_another_active_ticket()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedTicketAsync(db, ev, "T1", "old@x.dk", TicketStatus.TwoDay);
        await SeedTicketAsync(db, ev, "T2", "old@x.dk", TicketStatus.Other);   // second ticket stays theirs
        var oldLogin = await SeedLoginAsync(db, ev, "old@x.dk");

        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));
        await sync.SyncAsync(ev, new[]
        {
            new TR("T1", "New", "Person", "new@x.dk", TicketStatus.TwoDay, "2-day"),
            new TR("T2", "Old", "Holder", "old@x.dk", TicketStatus.Other, "1-day"),
        });

        // Entitlement is computed from the committed mirror: T2 is still active → keep login.
        Assert.True(db.Participants.Find(oldLogin)!.IsActive);
    }

    [Fact]
    public async Task Webhook_scoped_reassignment_also_locks_out_the_previous_holder()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var a = new Attendee
        {
            EventId = ev, BackstageTicketId = "T1", OrderId = "O1", Email = "old@x.dk",
            FirstName = "Old", LastName = "Holder", TicketStatus = TicketStatus.TwoDay,
            MirrorState = MirrorState.Active,
        };
        db.Attendees.Add(a); await db.SaveChangesAsync();
        var oldLogin = await SeedLoginAsync(db, ev, "old@x.dk");

        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));
        var r = await sync.SyncOrderAsync(ev, "O1",
            new AttendeeTicketSyncService.OrderRow("O1", null, null, null, null, null, null, null, null, "completed", null, null),
            new[] { new TR("T1", "New", "Person", "new@x.dk", TicketStatus.TwoDay, "2-day", OrderId: "O1") });

        Assert.Equal(1, r.Reassigned);
        Assert.False(db.Participants.Find(oldLogin)!.IsActive);
    }
}
