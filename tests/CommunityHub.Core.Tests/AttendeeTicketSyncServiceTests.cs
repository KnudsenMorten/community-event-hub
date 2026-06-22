using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Xunit;
using TR = CommunityHub.Core.Reminders.AttendeeTicketSyncService.TicketRow;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Ticket-id-keyed attendee sync (<see cref="AttendeeTicketSyncService"/>) — the
/// critical orphan-prevention flow: a reassigned ticket (same id, new name/email)
/// keeps the SAME attendee row so the Master Class TRANSFERS to the new holder, and
/// a cancelled ticket releases its MC seat (promoting the waitlist). EF in-memory.
/// </summary>
public class AttendeeTicketSyncServiceTests
{
    private static async Task<(int ev, int mc)> SeedAsync(CommunityHub.Core.Data.CommunityHubDbContext db, int cap)
    {
        var e = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(e); await db.SaveChangesAsync();
        var s = new Session { EventId = e.Id, Title = "MC", Type = SessionType.MasterClass, MasterClassCapacity = cap };
        db.Sessions.Add(s); await db.SaveChangesAsync();
        return (e.Id, s.Id);
    }

    private static async Task<int> SeedTicketAttendeeAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, int ev, string ticketId, string email)
    {
        var a = new Attendee { EventId = ev, BackstageTicketId = ticketId, Email = email, FirstName = "Old", LastName = "Holder", TicketStatus = TicketStatus.TwoDay };
        db.Attendees.Add(a); await db.SaveChangesAsync(); return a.Id;
    }

    [Fact]
    public async Task Reassignment_transfers_the_master_class_to_the_new_holder()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc) = await SeedAsync(db, 5);
        var aid = await SeedTicketAttendeeAsync(db, ev, "T1", "old@x.dk");
        var mcSvc = new MasterClassSignupService(db);
        await mcSvc.SignUpAsync(ev, aid, mc);                       // old holder confirmed in MC
        var sigId = (await mcSvc.SignupIdAsync(ev, aid, mc))!.Value;

        var sync = new AttendeeTicketSyncService(db, mcSvc);
        var result = await sync.SyncAsync(ev, new[] {
            new TR("T1", "New", "Person", "new@x.dk", TicketStatus.TwoDay, "2-day"),
        });

        Assert.Equal(1, result.Reassigned);
        var att = db.Attendees.Find(aid)!;
        Assert.Equal("new@x.dk", att.Email);                       // same row, new identity
        Assert.Equal("New", att.FirstName);
        Assert.NotNull(att.MasterClassInviteSentAt);              // inherited an MC -> not re-prompted to select
        // The MC is KEPT (never cancelled): the SAME signup still exists, Confirmed,
        // linked to the SAME attendee id -> transferred to the new holder.
        Assert.Single(db.MasterClassSignups);                    // not deleted/duplicated
        var sig = db.MasterClassSignups.Find(sigId)!;
        Assert.Equal(aid, sig.AttendeeId);
        Assert.Equal(MasterClassSignupStatus.Confirmed, sig.Status);
        var re = Assert.Single(result.Reassignments);
        Assert.Equal("MC", re.InheritedMcTitle);
        Assert.Equal("new@x.dk", re.NewEmail);
    }

    [Fact]
    public async Task Enriched_backstage_attendee_maps_company_country_and_custom_fields()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, 5);
        var ba = new CommunityHub.Core.Integrations.BackstageAttendee(
            TicketId: "T9", OrderId: "O9", Email: "x@x.dk", FirstName: "Test", LastName: "Testesen",
            TicketClassName: "2-day Pre-day  + Main Event", Attending: true,
            CompanyName: "Test Company", JobTitle: "CEO", Phone: "+4512345678",
            Country: "Denmark", CountryCode: "DK", City: "City", Postcode: "6000", TaxId: "DK39208032",
            CustomFieldsJson: "{\"single_choice_1\":\"Security\"}");

        var row = AttendeeTicketSyncService.FromBackstage(ba);
        Assert.Equal(TicketStatus.TwoDay, row.Status);   // "2-day ..." -> eligible

        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));
        await sync.SyncAsync(ev, new[] { row });

        var a = db.Attendees.Single(x => x.BackstageTicketId == "T9");
        Assert.Equal("Test Company", a.CompanyName);
        Assert.Equal("Denmark", a.Country);
        Assert.Equal("DK", a.CountryCode);
        Assert.Equal("CEO", a.JobTitle);
        Assert.Equal("DK39208032", a.TaxId);
        Assert.Equal("O9", a.OrderId);
        Assert.Contains("Security", a.CustomFieldsJson);
        Assert.Equal(TicketStatus.TwoDay, a.TicketStatus);
    }

    [Fact]
    public async Task New_and_plain_update_are_counted()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, 5);
        await SeedTicketAttendeeAsync(db, ev, "T1", "a@x.dk");
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        var r = await sync.SyncAsync(ev, new[] {
            new TR("T1", "A", "A", "a@x.dk", TicketStatus.TwoDay, "2-day"),   // same email -> update
            new TR("T2", "B", "B", "b@x.dk", TicketStatus.TwoDay, "2-day"),   // new
        });
        Assert.Equal(1, r.Created);
        Assert.Equal(1, r.Updated);
        Assert.Equal(0, r.Reassigned);
        Assert.Equal(2, db.Attendees.Count());
    }

    [Fact]
    public async Task Cancelled_ticket_releases_its_seat_and_promotes_the_waitlist()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc) = await SeedAsync(db, 1);                      // capacity 1
        var holder = await SeedTicketAttendeeAsync(db, ev, "T1", "h@x.dk");
        var waiter = await SeedTicketAttendeeAsync(db, ev, "T2", "w@x.dk");
        var mcSvc = new MasterClassSignupService(db);
        await mcSvc.SignUpAsync(ev, holder, mc);                    // confirmed
        await mcSvc.SignUpAsync(ev, waiter, mc);                    // waitlisted
        var sync = new AttendeeTicketSyncService(db, mcSvc);

        // Pull no longer contains T1 (cancelled); T2 still present.
        var r = await sync.SyncAsync(ev, new[] {
            new TR("T2", "W", "W", "w@x.dk", TicketStatus.TwoDay, "2-day"),
        });

        Assert.Equal(1, r.Cancelled);
        Assert.Empty(db.MasterClassSignups.Where(s => s.AttendeeId == holder)); // seat released
        // Waiter promoted into the freed seat.
        var wSig = db.MasterClassSignups.Single(s => s.AttendeeId == waiter);
        Assert.Equal(MasterClassSignupStatus.Confirmed, wSig.Status);
        Assert.NotEmpty(r.FreedPromotions);
    }
}
