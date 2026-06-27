using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The CEH-owned Master Class seat + waitlist engine (<see cref="MasterClassSignupService"/>):
/// 2-day-ticket eligibility, capacity → waitlist, ≤1 confirmed + ≤1 waitlist per
/// person, and the §93/§94 automatic atomic promotion: a freed/opened seat goes to the
/// waitlist FIRST (public booking blocked while anyone waits), the highest waitlister is
/// promoted and auto-switched out of any other class (no offer/choose step), cascading
/// into the released class. EF in-memory — these cover the logic/ordering invariants;
/// the serializable-transaction / real DB-locking path can only be exercised against
/// SQL Server (the in-memory provider has no transactions, and the EF SQLite provider
/// cannot translate the engine's DateTimeOffset ordering/comparisons).
/// </summary>
public class MasterClassSignupServiceTests
{
    private static async Task<(int ev, int mcA, int mcB)> SeedAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, int capA, int capB)
    {
        var e = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(e); await db.SaveChangesAsync();
        var a = new Session { EventId = e.Id, Title = "MC A", Type = SessionType.MasterClass, MasterClassCapacity = capA };
        var b = new Session { EventId = e.Id, Title = "MC B", Type = SessionType.MasterClass, MasterClassCapacity = capB };
        db.Sessions.AddRange(a, b); await db.SaveChangesAsync();
        return (e.Id, a.Id, b.Id);
    }

    private static async Task<int> Att(CommunityHub.Core.Data.CommunityHubDbContext db, int ev, string email,
        TicketStatus t = TicketStatus.TwoDay)
    {
        var a = new Attendee { EventId = ev, Email = email, FirstName = "F", LastName = email.Split('@')[0], TicketStatus = t };
        db.Attendees.Add(a); await db.SaveChangesAsync(); return a.Id;
    }

    [Theory]
    // capacity, confirmed, offered -> expected level (>20% free = Available; <20% = FillingUp; 0 = Full)
    [InlineData(10, 5, 0, MasterClassSignupService.AvailabilityLevel.Available)]   // 50% free
    [InlineData(10, 8, 1, MasterClassSignupService.AvailabilityLevel.FillingUp)]   // 10% free
    [InlineData(10, 9, 1, MasterClassSignupService.AvailabilityLevel.Full)]        // 0 free (offer holds last seat)
    [InlineData(5, 4, 0, MasterClassSignupService.AvailabilityLevel.Available)]    // exactly 20% free = green (+20%)
    [InlineData(20, 17, 0, MasterClassSignupService.AvailabilityLevel.FillingUp)]  // 15% free -> yellow
    public void Availability_traffic_light(int cap, int confirmed, int offered,
        MasterClassSignupService.AvailabilityLevel expected)
    {
        var o = new MasterClassSignupService.McOption(1, "MC", cap, confirmed, offered, 0);
        Assert.Equal(expected, o.Availability);
        Assert.Equal(System.Math.Max(0, cap - confirmed - offered), o.Free);
    }

    [Fact]
    public void Uncapped_class_is_always_available()
    {
        var o = new MasterClassSignupService.McOption(1, "MC", null, 99, 0, 3);
        Assert.Equal(MasterClassSignupService.AvailabilityLevel.Available, o.Availability);
        Assert.Null(o.Free);
    }

    [Fact]
    public async Task Seed_creates_the_eight_master_classes_idempotently()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _, _) = await SeedAsync(db, 5, 5);   // SeedAsync already made 2 MCs (MC A/B)
        var svc = new MasterClassSignupService(db);

        var first = await svc.SeedDefaultMasterClassesAsync(ev);
        Assert.Equal(8, first);                  // none of the 8 named ones existed yet
        var titles = (await svc.ListMasterClassesAsync(ev)).Select(m => m.Title).ToList();
        Assert.Contains("Intune", titles);
        Assert.Contains("AI for Makers (Copilot & Agents)", titles);
        Assert.Contains("Azure", titles);

        var second = await svc.SeedDefaultMasterClassesAsync(ev);
        Assert.Equal(0, second);                 // idempotent
    }

    [Fact]
    public async Task Non_two_day_ticket_is_refused()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, _) = await SeedAsync(db, 10, 10);
        var one = await Att(db, ev, "one@x.dk", TicketStatus.Other);
        var svc = new MasterClassSignupService(db);
        Assert.False((await svc.SignUpAsync(ev, one, mcA)).Ok);
    }

    [Fact]
    public async Task Confirms_under_capacity_then_waitlists_when_full()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, _) = await SeedAsync(db, 1, 10);
        var a1 = await Att(db, ev, "a1@x.dk"); var a2 = await Att(db, ev, "a2@x.dk");
        var svc = new MasterClassSignupService(db);
        Assert.Equal(MasterClassSignupStatus.Confirmed, (await svc.SignUpAsync(ev, a1, mcA)).Signup!.Status);
        var r2 = await svc.SignUpAsync(ev, a2, mcA);
        Assert.Equal(MasterClassSignupStatus.Waitlisted, r2.Signup!.Status);
        Assert.Equal(1, r2.Signup.WaitlistPosition);
    }

    [Fact]
    public async Task One_confirmed_plus_one_waitlist_allowed_but_not_two_confirmed()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, mcB) = await SeedAsync(db, 1, 0);  // B has 0 capacity => always waitlist
        var a1 = await Att(db, ev, "a1@x.dk");
        var other = await Att(db, ev, "o@x.dk");
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(ev, other, mcA);              // fills A (cap 1)
        // a1: confirm... A is full, so waitlist A; then can't waitlist B too (max 1 waitlist)
        var (ev2, mcC, mcD) = await SeedAsync(db, 1, 1);
        var p = await Att(db, ev2, "p@x.dk");
        var svc2 = new MasterClassSignupService(db);
        Assert.Equal(MasterClassSignupStatus.Confirmed, (await svc2.SignUpAsync(ev2, p, mcC)).Signup!.Status);
        // Second MC with room while already confirmed -> blocked (never 2 confirmed).
        var second = await svc2.SignUpAsync(ev2, p, mcD);
        Assert.False(second.Ok);
        Assert.Contains("confirmed", second.Error!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Waitlisting_while_holding_a_seat_requires_auto_switch_consent()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, mcB) = await SeedAsync(db, 1, 1);   // default AutoSwitch
        var p = await Att(db, ev, "p@x.dk");
        var bSeat = await Att(db, ev, "b@x.dk");
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(ev, p, mcA);        // p confirmed in A
        await svc.SignUpAsync(ev, bSeat, mcB);    // B full

        var noConsent = await svc.SignUpAsync(ev, p, mcB);                       // waitlist B, no consent
        Assert.False(noConsent.Ok);
        Assert.Contains("cancel", noConsent.Error!, System.StringComparison.OrdinalIgnoreCase);

        var consented = await svc.SignUpAsync(ev, p, mcB, autoSwitchConsent: true);
        Assert.True(consented.Ok);
        Assert.NotNull(db.MasterClassSignups.Single(x => x.AttendeeId == p && x.SessionId == mcB).AutoSwitchConsentAt);
    }

    [Fact]
    public async Task Give_up_seat_instantly_promotes_seatless_next()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, _) = await SeedAsync(db, 1, 10);
        var a1 = await Att(db, ev, "a1@x.dk"); var a2 = await Att(db, ev, "a2@x.dk");
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(ev, a1, mcA);   // confirmed
        await svc.SignUpAsync(ev, a2, mcA);   // waitlisted

        var promo = await svc.RemoveAsync(ev, a1, mcA);
        Assert.Equal(a2, promo!.PromotedAttendeeId);
        Assert.Equal(MasterClassSignupService.PromotionKind.Confirmed, promo.Kind);
        var a2sig = (await svc.GetForAttendeeAsync(ev, a2)).Single();
        Assert.Equal(MasterClassSignupStatus.Confirmed, a2sig.Status);
    }

    // --- §93 / §94 : automatic atomic promotion + waitlist priority ----------

    [Fact] // §93: promotion of a seat-holder ALWAYS auto-switches — no offer/choose step.
    public async Task Promotion_of_a_seat_holder_always_auto_switches_no_offer()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, mcB) = await SeedAsync(db, 1, 1);
        var svc = new MasterClassSignupService(db);
        var p = await Att(db, ev, "p@x.dk");
        var bSeat = await Att(db, ev, "b@x.dk");
        await svc.SignUpAsync(ev, p, mcA);                              // p confirmed in A
        await svc.SignUpAsync(ev, bSeat, mcB);                         // B full
        await svc.SignUpAsync(ev, p, mcB, autoSwitchConsent: true);    // p waitlists B (holds A)

        var promo = await svc.RemoveAsync(ev, bSeat, mcB);            // B frees -> p auto-switched
        Assert.Equal(MasterClassSignupService.PromotionKind.Confirmed, promo!.Kind);
        Assert.Equal(p, promo.PromotedAttendeeId);

        // §93: p ends with EXACTLY ONE active Master Class (B), confirmed, and NO waitlist.
        var pSigs = await svc.GetForAttendeeAsync(ev, p);
        var only = Assert.Single(pSigs);
        Assert.Equal(mcB, only.SessionId);
        Assert.Equal(MasterClassSignupStatus.Confirmed, only.Status);
        Assert.DoesNotContain(pSigs, s => s.Status == MasterClassSignupStatus.Waitlisted
                                          || s.Status == MasterClassSignupStatus.Offered);
        // Nobody is ever left holding an Offered seat anymore.
        Assert.Empty(db.MasterClassSignups.Where(x => x.Status == MasterClassSignupStatus.Offered));
    }

    [Fact] // §93: cancelling the promoted person's OLD seat cascades into THAT class's waitlist.
    public async Task Auto_switch_cascades_into_the_released_classes_waitlist()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, mcB) = await SeedAsync(db, 1, 1);
        var svc = new MasterClassSignupService(db);
        var p = await Att(db, ev, "p@x.dk");
        var bSeat = await Att(db, ev, "b@x.dk");
        var aWait = await Att(db, ev, "aw@x.dk");
        await svc.SignUpAsync(ev, p, mcA);                             // p confirmed in A
        await svc.SignUpAsync(ev, aWait, mcA);                        // aWait waitlists A (A full)
        await svc.SignUpAsync(ev, bSeat, mcB);                        // B full
        await svc.SignUpAsync(ev, p, mcB, autoSwitchConsent: true);   // p waitlists B (holds A)

        await svc.RemoveAsync(ev, bSeat, mcB);   // B frees -> p switches A->B -> A frees -> aWait promoted

        var pSig = Assert.Single(await svc.GetForAttendeeAsync(ev, p));
        Assert.Equal(mcB, pSig.SessionId);
        Assert.Equal(MasterClassSignupStatus.Confirmed, pSig.Status);

        var awSig = Assert.Single(await svc.GetForAttendeeAsync(ev, aWait));
        Assert.Equal(mcA, awSig.SessionId);
        Assert.Equal(MasterClassSignupStatus.Confirmed, awSig.Status);   // A's waitlist cascaded in
    }

    [Fact] // §93: promote the HIGHEST waitlist entry, and exactly one confirmation per freed seat.
    public async Task Give_up_promotes_highest_waitlist_only_no_double_confirm()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, _) = await SeedAsync(db, 1, 10);
        var seat = await Att(db, ev, "seat@x.dk");
        var first = await Att(db, ev, "first@x.dk");
        var second = await Att(db, ev, "second@x.dk");
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(ev, seat, mcA);     // confirmed
        await svc.SignUpAsync(ev, first, mcA);    // waitlist #1 (earliest)
        await svc.SignUpAsync(ev, second, mcA);   // waitlist #2

        var promo = await svc.RemoveAsync(ev, seat, mcA);
        Assert.Equal(first, promo!.PromotedAttendeeId);          // highest (earliest) wins

        // Exactly ONE confirmed seat for the one freed seat; #2 still waiting.
        Assert.Equal(1, db.MasterClassSignups.Count(x => x.SessionId == mcA
                          && x.Status == MasterClassSignupStatus.Confirmed));
        Assert.Equal(MasterClassSignupStatus.Confirmed,
            (await svc.GetForAttendeeAsync(ev, first)).Single().Status);
        Assert.Equal(MasterClassSignupStatus.Waitlisted,
            (await svc.GetForAttendeeAsync(ev, second)).Single().Status);
    }

    [Fact] // §94: with a non-empty waitlist, a public booker is REFUSED the seat and waitlisted.
    public async Task Public_booking_refused_while_a_waitlist_exists()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, _) = await SeedAsync(db, 5, 10);   // plenty of room
        var seat = await Att(db, ev, "seat@x.dk");
        var waiter = await Att(db, ev, "wait@x.dk");
        var publicBooker = await Att(db, ev, "pub@x.dk");
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(ev, seat, mcA);   // confirmed (free seat, no waitlist)

        // Inject a waitlist entry directly (a race/leftover state the §94 guard must respect),
        // even though there are free seats.
        db.MasterClassSignups.Add(new MasterClassSignup
        {
            EventId = ev, SessionId = mcA, AttendeeId = waiter,
            Status = MasterClassSignupStatus.Waitlisted,
            CreatedAt = System.DateTimeOffset.UtcNow, UpdatedAt = System.DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        // Public booker must NOT jump the queue even though a seat is free → waitlisted.
        var r = await svc.SignUpAsync(ev, publicBooker, mcA);
        Assert.True(r.Ok);
        Assert.Equal(MasterClassSignupStatus.Waitlisted, r.Signup!.Status);
    }

    [Fact] // §93/§94: raising the cap hands the opened seats to the waitlist (not the public).
    public async Task Raising_capacity_promotes_from_the_waitlist()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, _) = await SeedAsync(db, 1, 10);
        var seat = await Att(db, ev, "seat@x.dk");
        var w1 = await Att(db, ev, "w1@x.dk");
        var w2 = await Att(db, ev, "w2@x.dk");
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(ev, seat, mcA);    // confirmed (cap 1)
        await svc.SignUpAsync(ev, w1, mcA);      // waitlist
        await svc.SignUpAsync(ev, w2, mcA);      // waitlist

        var promotions = await svc.SetCapacityAsync(ev, mcA, capacity: 3);   // +2 seats
        Assert.Equal(2, promotions.Count);
        Assert.Equal(MasterClassSignupStatus.Confirmed, (await svc.GetForAttendeeAsync(ev, w1)).Single().Status);
        Assert.Equal(MasterClassSignupStatus.Confirmed, (await svc.GetForAttendeeAsync(ev, w2)).Single().Status);
        Assert.Empty(db.MasterClassSignups.Where(x => x.SessionId == mcA
                          && x.Status == MasterClassSignupStatus.Waitlisted));
    }

    // --- Operator end-to-end walkthrough (2026-06-28) ------------------------
    // "chg max seats to 2 and make 2 sample signups; next person must get a
    //  warning so he cannot book if it becomes full; cancel; auto-cancel the MC
    //  when a seat becomes available." One narrative with named sample attendees.

    [Fact]
    public async Task Sample_cap2_fills_then_waitlists_next_then_giveup_promotes_then_autoswitch()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, mcB) = await SeedAsync(db, capA: 2, capB: 1);   // A holds 2, B holds 1
        var svc = new MasterClassSignupService(db);

        // Two sample, 2-day-ticket attendees fill MC A (capacity 2).
        var alex = await Att(db, ev, "alex.rivera@example.test");
        var sam = await Att(db, ev, "sam.taylor@example.test");
        Assert.Equal(MasterClassSignupStatus.Confirmed, (await svc.SignUpAsync(ev, alex, mcA)).Signup!.Status);
        Assert.Equal(MasterClassSignupStatus.Confirmed, (await svc.SignUpAsync(ev, sam, mcA)).Signup!.Status);

        // MC A is now FULL: 0 free, traffic light Full.
        var aFull = (await svc.ListMasterClassesAsync(ev)).Single(m => m.SessionId == mcA);
        Assert.Equal(0, aFull.Free);
        Assert.Equal(MasterClassSignupService.AvailabilityLevel.Full, aFull.Availability);

        // The NEXT person cannot take a seat — they are WAITLISTED (the warning), never
        // silently overbooked. SignupResult is OK but the status is Waitlisted, not Confirmed.
        var jordan = await Att(db, ev, "jordan.lee@example.test");
        var r3 = await svc.SignUpAsync(ev, jordan, mcA);
        Assert.True(r3.Ok);
        Assert.Equal(MasterClassSignupStatus.Waitlisted, r3.Signup!.Status);
        Assert.Equal(1, r3.Signup.WaitlistPosition);

        // HARD INVARIANT: confirmed seats NEVER exceed capacity (no overbook).
        Assert.Equal(2, db.MasterClassSignups.Count(
            x => x.SessionId == mcA && x.Status == MasterClassSignupStatus.Confirmed));

        // CANCEL: Alex gives up his seat → the freed seat goes to the waitlist FIRST,
        // promoting Jordan into a confirmed seat (and Alex's row is gone).
        var promo = await svc.RemoveAsync(ev, alex, mcA);
        Assert.Equal(jordan, promo!.PromotedAttendeeId);
        Assert.Equal(MasterClassSignupService.PromotionKind.Confirmed, promo.Kind);
        Assert.Equal(MasterClassSignupStatus.Confirmed,
            (await svc.GetForAttendeeAsync(ev, jordan)).Single().Status);
        Assert.Empty(await svc.GetForAttendeeAsync(ev, alex));
        // A is full again (Sam + Jordan).
        Assert.Equal(0, (await svc.ListMasterClassesAsync(ev)).Single(m => m.SessionId == mcA).Free);

        // AUTO-SWITCH: Priya confirms in MC B (cap 1 → B full), then waitlists the (full) MC A
        // WITH the auto-switch consent. When a seat frees in A (Sam gives up), Priya is promoted
        // into A AND her MC B seat is auto-cancelled — she ends with exactly ONE active MC.
        var priya = await Att(db, ev, "priya.nair@example.test");
        Assert.Equal(MasterClassSignupStatus.Confirmed, (await svc.SignUpAsync(ev, priya, mcB)).Signup!.Status);
        Assert.True((await svc.SignUpAsync(ev, priya, mcA, autoSwitchConsent: true)).Ok);   // waitlists A

        await svc.RemoveAsync(ev, sam, mcA);   // A frees → Priya auto-switched A, B seat cancelled

        var priyaSig = Assert.Single(await svc.GetForAttendeeAsync(ev, priya));
        Assert.Equal(mcA, priyaSig.SessionId);
        Assert.Equal(MasterClassSignupStatus.Confirmed, priyaSig.Status);
        // Her old MC B seat was auto-cancelled → B now has a free seat again.
        Assert.Equal(1, (await svc.ListMasterClassesAsync(ev)).Single(m => m.SessionId == mcB).Free);
        // No leftover Offered rows anywhere (auto-switch has no offer/hold step).
        Assert.Empty(db.MasterClassSignups.Where(x => x.Status == MasterClassSignupStatus.Offered));
    }

    [Fact] // The race the operator called out: a SWITCH into a class that filled before submit
           // must FAIL with a clear error and leave the attendee's existing seat intact.
    public async Task Switching_into_a_room_that_just_filled_errors_and_keeps_the_existing_seat()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, mcB) = await SeedAsync(db, capA: 1, capB: 1);
        var svc = new MasterClassSignupService(db);
        var morgan = await Att(db, ev, "morgan.diaz@example.test");
        var taken = await Att(db, ev, "chris.bauer@example.test");

        await svc.SignUpAsync(ev, morgan, mcB);   // Morgan confirmed in B
        await svc.SignUpAsync(ev, taken, mcA);     // A is now full (cap 1)

        // Morgan tries to switch into the now-full A → refused, A unchanged, B seat kept.
        var (ok, error, _) = await svc.SwitchAsync(ev, morgan, mcA);
        Assert.False(ok);
        Assert.Equal(MasterClassSignupService.NowFullError, error);
        var still = Assert.Single(await svc.GetForAttendeeAsync(ev, morgan));
        Assert.Equal(mcB, still.SessionId);
        Assert.Equal(MasterClassSignupStatus.Confirmed, still.Status);
    }
}
