using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;
using TR = CommunityHub.Core.Reminders.AttendeeTicketSyncService.TicketRow;
using OR = CommunityHub.Core.Reminders.AttendeeTicketSyncService.OrderRow;

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

        // §326as: Zoho KEEPS a cancelled ticket in the feed and marks it "not_attending" —
        // T1 is present-but-cancelled, T2 unchanged.
        var r = await sync.SyncAsync(ev, new[] {
            new TR("T1", "H", "H", "h@x.dk", TicketStatus.TwoDay, "2-day", CancelledUpstream: true),
            new TR("T2", "W", "W", "w@x.dk", TicketStatus.TwoDay, "2-day"),
        });

        Assert.Equal(1, r.Cancelled);
        Assert.Empty(db.MasterClassSignups.Where(s => s.AttendeeId == holder)); // seat released
        // Waiter promoted into the freed seat.
        var wSig = db.MasterClassSignups.Single(s => s.AttendeeId == waiter);
        Assert.Equal(MasterClassSignupStatus.Confirmed, wSig.Status);
        Assert.NotEmpty(r.FreedPromotions);
    }

    // --- §234 5: one email may hold SEVERAL tickets (non-unique (EventId, Email)) ---

    [Fact]
    public async Task Two_tickets_sharing_one_email_sync_without_throwing()
    {
        // Zoho legitimately allows ONE email to hold TWO tickets. The old unique
        // (EventId, Email) index made SaveChanges throw here and killed the ENTIRE
        // reconcile run — the sync must complete and mirror both rows.
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, 5);
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        var r = await sync.SyncAsync(ev, new[] {
            new TR("T1", "Same", "Person", "same@x.dk", TicketStatus.TwoDay, "2-day"),
            new TR("T2", "Same", "Person", "same@x.dk", TicketStatus.Other, "1-day"),
        });

        Assert.Equal(2, r.Created);
        Assert.Equal(2, db.Attendees.Count(a => a.EventId == ev && a.Email == "same@x.dk"));
        Assert.Equal(2, r.AttendeesActive);
    }

    [Fact]
    public async Task Reassignment_onto_an_email_that_already_holds_a_ticket_completes()
    {
        // T1 stays with a@x.dk; Zoho reassigns T2 (b@x.dk) to a@x.dk — the retarget
        // lands on an email that ALREADY has a mirror row. Must not throw; both
        // rows survive under the same email and the reassignment is reported.
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, 5);
        await SeedTicketAttendeeAsync(db, ev, "T1", "a@x.dk");
        await SeedTicketAttendeeAsync(db, ev, "T2", "b@x.dk");
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        var r = await sync.SyncAsync(ev, new[] {
            new TR("T1", "A", "A", "a@x.dk", TicketStatus.TwoDay, "2-day"),
            new TR("T2", "A", "A", "a@x.dk", TicketStatus.TwoDay, "2-day"),
        });

        Assert.Equal(1, r.Reassigned);
        Assert.Equal(0, r.Cancelled);
        Assert.Equal(2, db.Attendees.Count(a => a.EventId == ev && a.Email == "a@x.dk"));
        var re = Assert.Single(r.Reassignments);
        Assert.Equal("a@x.dk", re.NewEmail);
    }

    // --- §125/§128 authoritative one-way mirror (orders + soft-cancel + reappear) ---

    private static OR Ord(string id, string? company = null) =>
        new(id, BuyerName: "Buyer", BuyerEmail: "buyer@x.dk", CompanyName: company,
            Country: "Denmark", CountryCode: "DK", City: "CPH", Postcode: "1000",
            TaxId: "DK123", OrderStatus: "completed", SourceCreatedAt: null, RawJson: "{}");

    [Fact]
    public async Task Full_dataset_upserts_orders_and_links_every_ticket_class()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, 5);
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        var orders = new[] { Ord("O1", "ACME"), Ord("O2", "Globex") };
        var tickets = new[]
        {
            new TR("T1", "A", "A", "a@x.dk", TicketStatus.TwoDay, "2-day", OrderId: "O1"),
            new TR("T2", "B", "B", "b@x.dk", TicketStatus.Other,  "1-day", OrderId: "O1"),
            new TR("T3", "C", "C", "c@x.dk", TicketStatus.Other,  "1-day", OrderId: "O2"),
        };

        var r = await sync.SyncAsync(ev, tickets, orders);

        Assert.Equal(2, r.OrdersCreated);
        Assert.Equal(2, r.OrdersActive);
        Assert.Equal(3, r.Created);
        Assert.Equal(3, r.AttendeesActive);
        // The FULL dataset is mirrored — not just 2-day (two 1-day tickets are kept).
        Assert.Equal(2, db.Attendees.Count(a => a.TicketStatus == TicketStatus.Other));
        var o1 = db.Orders.Include(o => o.Attendees).Single(o => o.BackstageOrderId == "O1");
        Assert.Equal("ACME", o1.CompanyName);
        Assert.Equal(2, o1.Attendees.Count);
    }

    [Fact]
    public async Task Not_attending_two_day_holder_is_soft_cancelled_keeps_row_and_releases_seat()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc) = await SeedAsync(db, 1);                       // capacity 1
        var holder = await SeedTicketAttendeeAsync(db, ev, "T1", "h@x.dk");
        var waiter = await SeedTicketAttendeeAsync(db, ev, "T2", "w@x.dk");
        var mcSvc = new MasterClassSignupService(db);
        await mcSvc.SignUpAsync(ev, holder, mc);                     // confirmed
        await mcSvc.SignUpAsync(ev, waiter, mc);                     // waitlisted
        var sync = new AttendeeTicketSyncService(db, mcSvc);

        // §326as: T1 is STILL IN THE PULL, flagged not_attending by Zoho itself.
        var r = await sync.SyncAsync(ev,
            new[]
            {
                new TR("T1", "H", "H", "h@x.dk", TicketStatus.TwoDay, "2-day", CancelledUpstream: true),
                new TR("T2", "W", "W", "w@x.dk", TicketStatus.TwoDay, "2-day"),
            },
            System.Array.Empty<OR>());

        Assert.Equal(1, r.Cancelled);
        var h = db.Attendees.Find(holder)!;
        // SOFT-cancel: row KEPT, MirrorState flipped + stamped, TicketStatus untouched (§128).
        Assert.Equal(MirrorState.Cancelled, h.MirrorState);
        Assert.NotNull(h.CancelledAt);
        Assert.Equal(TicketStatus.TwoDay, h.TicketStatus);
        Assert.Empty(db.MasterClassSignups.Where(s => s.AttendeeId == holder));   // seat released
        var wSig = db.MasterClassSignups.Single(s => s.AttendeeId == waiter);
        Assert.Equal(MasterClassSignupStatus.Confirmed, wSig.Status);             // waitlist promoted
        // The soft-cancelled holder is no longer Master-Class eligible.
        Assert.False(await mcSvc.IsEligibleAsync(ev, holder));
        Assert.True(await mcSvc.IsEligibleAsync(ev, waiter));
    }

    [Fact]
    public async Task Reappearing_ticket_flips_back_to_active()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, 5);
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        await sync.SyncAsync(ev,
            new[] { new TR("T1", "A", "A", "a@x.dk", TicketStatus.TwoDay, "2-day") },
            System.Array.Empty<OR>());
        // §326as: cancel by Zoho's own status, not by vanishing.
        await sync.SyncAsync(ev,
            new[] { new TR("T1", "A", "A", "a@x.dk", TicketStatus.TwoDay, "2-day", CancelledUpstream: true) },
            System.Array.Empty<OR>());
        Assert.Equal(MirrorState.Cancelled, db.Attendees.Single(a => a.BackstageTicketId == "T1").MirrorState);

        var r = await sync.SyncAsync(ev,
            new[] { new TR("T1", "A", "A", "a@x.dk", TicketStatus.TwoDay, "2-day") },
            System.Array.Empty<OR>());                                              // reappear

        Assert.Equal(1, r.Reactivated);
        var a = db.Attendees.Single(x => x.BackstageTicketId == "T1");
        Assert.Equal(MirrorState.Active, a.MirrorState);
        Assert.Null(a.CancelledAt);
    }

    [Fact]
    public async Task Order_marked_cancelled_by_zoho_is_soft_cancelled()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, 5);
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        await sync.SyncAsync(ev, System.Array.Empty<TR>(), new[] { Ord("O1") });
        Assert.Equal(MirrorState.Active, db.Orders.Single().MirrorState);

        // §326as: confirmed on real Zoho data — a cancelled order STAYS in the feed with
        // status_string "cancelled" (vs "placed"). That string is the cancellation trigger.
        var r = await sync.SyncAsync(ev, System.Array.Empty<TR>(),
            new[] { Ord("O1") with { OrderStatus = "cancelled" } });

        Assert.Equal(1, r.OrdersCancelled);
        var o = db.Orders.Single();
        Assert.Equal(MirrorState.Cancelled, o.MirrorState);
        Assert.NotNull(o.CancelledAt);
    }

    // ---- §326as: ABSENCE IS NOT CANCELLATION ------------------------------------------
    // The rule this replaces cost us the worst near-miss in the project: because
    // ZohoClient's pager looked for an e-conomic-style "nextPage" that Zoho never returns,
    // every read stopped after page 1 (500 attendees / 100 orders). Once ticket sales
    // crossed a page, everyone beyond it would have looked "absent" and been cancelled —
    // seats released, waitlist promotion mail sent, logins revoked. Verified against the
    // real ELDK26 event: Zoho KEEPS cancelled tickets in the feed as "not_attending"
    // (1158 attending / 46 not_attending of 1204), so absence NEVER meant cancellation.

    [Fact]
    public async Task Ticket_missing_from_the_pull_is_reported_but_NEVER_cancelled()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc) = await SeedAsync(db, 5);
        var holder = await SeedTicketAttendeeAsync(db, ev, "T1", "h@x.dk");
        var mcSvc = new MasterClassSignupService(db);
        await mcSvc.SignUpAsync(ev, holder, mc);
        var sync = new AttendeeTicketSyncService(db, mcSvc);

        // An EMPTY pull (a truncated read, a wrong event id, an API blip) must not touch
        // a single row — it is reported for the operator instead.
        var r = await sync.SyncAsync(ev, System.Array.Empty<TR>(), System.Array.Empty<OR>());

        Assert.Equal(0, r.Cancelled);
        Assert.Equal(1, r.AbsentNotCancelled);
        var a = db.Attendees.Single();
        Assert.Equal(MirrorState.Active, a.MirrorState);
        Assert.Null(a.CancelledAt);
        Assert.NotEmpty(db.MasterClassSignups.Where(s => s.AttendeeId == holder));   // seat KEPT
        Assert.Empty(r.FreedPromotions);                                             // no waitlist mail
    }

    [Fact]
    public async Task A_ticket_already_not_attending_when_first_seen_is_mirrored_cancelled_without_side_effects()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, 5);
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        var r = await sync.SyncAsync(ev,
            new[] { new TR("T1", "A", "A", "a@x.dk", TicketStatus.TwoDay, "2-day", CancelledUpstream: true) },
            System.Array.Empty<OR>());

        // Created straight into the Cancelled state — never Active-then-cancelled, which
        // would have released a seat it never held and mailed a waitlist it never blocked.
        Assert.Equal(1, r.Created);
        Assert.Equal(0, r.Cancelled);
        var a = db.Attendees.Single();
        Assert.Equal(MirrorState.Cancelled, a.MirrorState);
        Assert.NotNull(a.CancelledAt);
        Assert.Empty(r.FreedPromotions);
    }

    [Fact]
    public async Task A_cancelled_ticket_staying_in_the_feed_does_not_look_like_a_repurchase()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, 5);
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));
        var live = new TR("T1", "A", "A", "a@x.dk", TicketStatus.TwoDay, "2-day");
        var dead = live with { CancelledUpstream = true };

        await sync.SyncAsync(ev, new[] { live }, System.Array.Empty<OR>());
        await sync.SyncAsync(ev, new[] { dead }, System.Array.Empty<OR>());

        // A cancelled ticket is present in EVERY later pull. Presence alone must not
        // reactivate it, or the mirror would flap Cancelled→Active every 10 minutes and
        // re-welcome the person on each cycle.
        var again = await sync.SyncAsync(ev, new[] { dead }, System.Array.Empty<OR>());

        Assert.Equal(0, again.Reactivated);
        Assert.Equal(MirrorState.Cancelled, db.Attendees.Single().MirrorState);

        // ...and a REAL re-purchase (Zoho says attending again) still restores them.
        var back = await sync.SyncAsync(ev, new[] { live }, System.Array.Empty<OR>());
        Assert.Equal(1, back.Reactivated);
        Assert.Equal(MirrorState.Active, db.Attendees.Single().MirrorState);
    }

    [Fact]
    public void Only_zohos_documented_status_strings_cancel()
    {
        // A WHITELIST on purpose: an unknown or new status must never be read as "cancel
        // this person". Confirmed live — a Backstage "Refund in Progress" order still
        // reports status_string "placed" and its attendee "attending", so an
        // anything-but-placed rule would have cancelled a refund that never completed.
        Assert.True(AttendeeTicketSyncService.IsCancelledTicketStatus("not_attending"));
        Assert.True(AttendeeTicketSyncService.IsCancelledTicketStatus("  NOT_ATTENDING "));
        Assert.False(AttendeeTicketSyncService.IsCancelledTicketStatus("attending"));
        Assert.False(AttendeeTicketSyncService.IsCancelledTicketStatus("refund_in_progress"));
        Assert.False(AttendeeTicketSyncService.IsCancelledTicketStatus(null));
        Assert.False(AttendeeTicketSyncService.IsCancelledTicketStatus(""));

        Assert.True(AttendeeTicketSyncService.IsCancelledOrderStatus("cancelled"));
        Assert.True(AttendeeTicketSyncService.IsCancelledOrderStatus("Canceled"));
        Assert.False(AttendeeTicketSyncService.IsCancelledOrderStatus("placed"));
        Assert.False(AttendeeTicketSyncService.IsCancelledOrderStatus("refunded"));
        Assert.False(AttendeeTicketSyncService.IsCancelledOrderStatus(null));
    }

    [Fact]
    public void FromBackstage_reads_the_cancellation_from_zohos_status_string()
    {
        static CommunityHub.Core.Integrations.BackstageAttendee Ba(string? status) =>
            new(TicketId: "T", OrderId: "O", Email: "x@x.dk", FirstName: "X", LastName: "Y",
                TicketClassName: "2-day", Attending: status == "attending", CompanyName: null,
                JobTitle: null, Phone: null, Country: null, CountryCode: null, City: null,
                Postcode: null, TaxId: null, CustomFieldsJson: null, StatusString: status);

        Assert.True(AttendeeTicketSyncService.FromBackstage(Ba("not_attending")).CancelledUpstream);
        Assert.False(AttendeeTicketSyncService.FromBackstage(Ba("attending")).CancelledUpstream);
        Assert.False(AttendeeTicketSyncService.FromBackstage(Ba(null)).CancelledUpstream);
    }

    [Fact]
    public async Task Order_missing_from_the_pull_is_never_cancelled()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, 5);
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        await sync.SyncAsync(ev, System.Array.Empty<TR>(), new[] { Ord("O1") });
        var r = await sync.SyncAsync(ev, System.Array.Empty<TR>(), System.Array.Empty<OR>());

        Assert.Equal(0, r.OrdersCancelled);
        Assert.Equal(MirrorState.Active, db.Orders.Single().MirrorState);
    }

    [Fact]
    public async Task Order_set_null_does_not_touch_orders_legacy_ticket_only_path()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, 5);
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        await sync.SyncAsync(ev, System.Array.Empty<TR>(), new[] { Ord("O1") });
        // orders == null ⇒ the order half is skipped entirely (no order reconcile/cancel).
        var r = await sync.SyncAsync(ev, System.Array.Empty<TR>(), orders: null);

        Assert.Equal(0, r.OrdersCancelled);
        Assert.Equal(MirrorState.Active, db.Orders.Single().MirrorState);
    }

    [Theory]
    [InlineData("2-day Pre-day + Main Event", true)]
    [InlineData("2 Day Combo", true)]
    [InlineData("1-day Main Event", false)]
    [InlineData("Pre-day Workshop", false)]    // the OLD telemetry regex matched "pre.?day" — policy does NOT
    [InlineData("Master Class add-on", false)] // the OLD telemetry regex matched "master" — policy does NOT
    public void Two_day_eligibility_is_decided_by_policy_only(string ticketClass, bool expectTwoDay)
    {
        var ba = new CommunityHub.Core.Integrations.BackstageAttendee(
            TicketId: "T", OrderId: "O", Email: "x@x.dk", FirstName: "X", LastName: "Y",
            TicketClassName: ticketClass, Attending: true, CompanyName: null, JobTitle: null,
            Phone: null, Country: null, CountryCode: null, City: null, Postcode: null,
            TaxId: null, CustomFieldsJson: null);

        var row = AttendeeTicketSyncService.FromBackstage(ba);
        Assert.Equal(expectTwoDay ? TicketStatus.TwoDay : TicketStatus.Other, row.Status);
    }

    // --- §128 INCREMENTAL single-order reconcile (the webhook path) ---

    [Fact]
    public async Task SyncOrder_creates_the_order_and_its_attendees()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, 5);
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        var r = await sync.SyncOrderAsync(ev, "O1", Ord("O1", "ACME"), new[]
        {
            new TR("T1", "A", "A", "a@x.dk", TicketStatus.TwoDay, "2-day", OrderId: "O1"),
            new TR("T2", "B", "B", "b@x.dk", TicketStatus.Other,  "1-day", OrderId: "O1"),
        });

        Assert.True(r.OrderCreated);
        Assert.Equal(2, r.Created);
        var o = db.Orders.Include(x => x.Attendees).Single(x => x.BackstageOrderId == "O1");
        Assert.Equal("ACME", o.CompanyName);
        Assert.Equal(2, o.Attendees.Count);
    }

    [Fact]
    public async Task SyncOrder_only_reconciles_its_own_order_leaving_others_untouched()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, 5);
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        // Two orders seeded via the full sync.
        await sync.SyncAsync(ev, new[]
        {
            new TR("T1", "A", "A", "a@x.dk", TicketStatus.TwoDay, "2-day", OrderId: "O1"),
            new TR("T2", "B", "B", "b@x.dk", TicketStatus.TwoDay, "2-day", OrderId: "O2"),
        }, new[] { Ord("O1"), Ord("O2") });

        // Incremental reconcile of ONLY O1, with O2 absent from the call — O2 must NOT cancel.
        var r = await sync.SyncOrderAsync(ev, "O1", Ord("O1"), new[]
        {
            new TR("T1", "A", "A", "a@x.dk", TicketStatus.TwoDay, "2-day", OrderId: "O1"),
        });

        Assert.Equal(0, r.Cancelled);
        Assert.Equal(MirrorState.Active, db.Attendees.Single(a => a.BackstageTicketId == "T2").MirrorState);
        Assert.Equal(MirrorState.Active, db.Orders.Single(o => o.BackstageOrderId == "O2").MirrorState);
    }

    [Fact]
    public async Task SyncOrder_soft_cancels_a_single_dropped_ticket_and_releases_its_seat()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc) = await SeedAsync(db, 1);                       // capacity 1
        var mcSvc = new MasterClassSignupService(db);
        var sync = new AttendeeTicketSyncService(db, mcSvc);

        // One order, two 2-day holders; T1 confirmed in the MC, T2 waitlisted.
        await sync.SyncOrderAsync(ev, "O1", Ord("O1"), new[]
        {
            new TR("T1", "H", "H", "h@x.dk", TicketStatus.TwoDay, "2-day", OrderId: "O1"),
            new TR("T2", "W", "W", "w@x.dk", TicketStatus.TwoDay, "2-day", OrderId: "O1"),
        });
        var holder = db.Attendees.Single(a => a.BackstageTicketId == "T1").Id;
        var waiter = db.Attendees.Single(a => a.BackstageTicketId == "T2").Id;
        await mcSvc.SignUpAsync(ev, holder, mc);                     // confirmed
        await mcSvc.SignUpAsync(ev, waiter, mc);                     // waitlisted

        // The webhook fires: the order still has T2 only (T1 was cancelled in Zoho).
        var r = await sync.SyncOrderAsync(ev, "O1", Ord("O1"), new[]
        {
            new TR("T2", "W", "W", "w@x.dk", TicketStatus.TwoDay, "2-day", OrderId: "O1"),
        });

        Assert.Equal(1, r.Cancelled);
        var h = db.Attendees.Find(holder)!;
        Assert.Equal(MirrorState.Cancelled, h.MirrorState);          // soft-cancel: row kept
        Assert.NotNull(h.CancelledAt);
        Assert.Empty(db.MasterClassSignups.Where(s => s.AttendeeId == holder));   // seat released
        var wSig = db.MasterClassSignups.Single(s => s.AttendeeId == waiter);
        Assert.Equal(MasterClassSignupStatus.Confirmed, wSig.Status);             // waitlist promoted
        Assert.NotEmpty(r.FreedPromotions);
        Assert.Equal(MirrorState.Active, db.Orders.Single().MirrorState);         // order itself still active
    }

    [Fact]
    public async Task SyncOrder_whole_order_cancellation_soft_cancels_order_and_all_its_attendees()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, 5);
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        await sync.SyncOrderAsync(ev, "O1", Ord("O1"), new[]
        {
            new TR("T1", "A", "A", "a@x.dk", TicketStatus.TwoDay, "2-day", OrderId: "O1"),
            new TR("T2", "B", "B", "b@x.dk", TicketStatus.Other,  "1-day", OrderId: "O1"),
        });

        // Event Order Cancel/Delete: order gone from Zoho ⇒ orderRemoved.
        var r = await sync.SyncOrderAsync(ev, "O1", order: null, ticketsForOrder: System.Array.Empty<TR>(),
            orderRemoved: true);

        Assert.True(r.OrderCancelled);
        Assert.Equal(2, r.Cancelled);
        Assert.Equal(MirrorState.Cancelled, db.Orders.Single().MirrorState);
        Assert.All(db.Attendees.ToList(), a => Assert.Equal(MirrorState.Cancelled, a.MirrorState));
    }

    [Fact]
    public async Task SyncOrder_reassignment_transfers_the_master_class()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc) = await SeedAsync(db, 5);
        var mcSvc = new MasterClassSignupService(db);
        var sync = new AttendeeTicketSyncService(db, mcSvc);

        await sync.SyncOrderAsync(ev, "O1", Ord("O1"), new[]
        {
            new TR("T1", "Old", "Holder", "old@x.dk", TicketStatus.TwoDay, "2-day", OrderId: "O1"),
        });
        var aid = db.Attendees.Single(a => a.BackstageTicketId == "T1").Id;
        await mcSvc.SignUpAsync(ev, aid, mc);

        // Same ticket id, new holder email ⇒ reassignment.
        var r = await sync.SyncOrderAsync(ev, "O1", Ord("O1"), new[]
        {
            new TR("T1", "New", "Person", "new@x.dk", TicketStatus.TwoDay, "2-day", OrderId: "O1"),
        });

        Assert.Equal(1, r.Reassigned);
        var att = db.Attendees.Find(aid)!;
        Assert.Equal("new@x.dk", att.Email);                        // same row, new identity
        Assert.Single(db.MasterClassSignups);                       // MC transferred, not duplicated
        Assert.Equal(aid, db.MasterClassSignups.Single().AttendeeId);
        var re = Assert.Single(r.Reassignments);
        Assert.Equal("MC", re.InheritedMcTitle);
    }

    [Fact]
    public async Task SyncOrder_reappearing_ticket_flips_back_to_active()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, 5);
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        await sync.SyncOrderAsync(ev, "O1", Ord("O1"), new[]
        {
            new TR("T1", "A", "A", "a@x.dk", TicketStatus.TwoDay, "2-day", OrderId: "O1"),
        });
        await sync.SyncOrderAsync(ev, "O1", order: null, ticketsForOrder: System.Array.Empty<TR>(), orderRemoved: true);
        Assert.Equal(MirrorState.Cancelled, db.Attendees.Single().MirrorState);

        var r = await sync.SyncOrderAsync(ev, "O1", Ord("O1"), new[]
        {
            new TR("T1", "A", "A", "a@x.dk", TicketStatus.TwoDay, "2-day", OrderId: "O1"),
        });

        Assert.Equal(1, r.Reactivated);
        Assert.True(r.OrderReactivated);
        Assert.Equal(MirrorState.Active, db.Attendees.Single().MirrorState);
        Assert.Equal(MirrorState.Active, db.Orders.Single().MirrorState);
    }
}
