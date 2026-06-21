using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The CEH-owned Master Class seat + waitlist engine (<see cref="MasterClassSignupService"/>):
/// 2-day-ticket eligibility, capacity → waitlist, ≤1 confirmed + ≤1 waitlist per
/// person, instant promotion on a freed seat, and the offer/auto-switch decision
/// modes (held seats count as full). EF in-memory.
/// </summary>
public class MasterClassSignupServiceTests
{
    private static async Task<(int ev, int mcA, int mcB)> SeedAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, int capA, int capB)
    {
        var e = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(e); await db.SaveChangesAsync();
        var a = new Session { EventId = e.Id, Title = "MC A", Type = SessionType.CommunityMasterClass, MasterClassCapacity = capA };
        var b = new Session { EventId = e.Id, Title = "MC B", Type = SessionType.CommunityMasterClass, MasterClassCapacity = capB };
        db.Sessions.AddRange(a, b); await db.SaveChangesAsync();
        return (e.Id, a.Id, b.Id);
    }

    private static async Task<int> Att(CommunityHub.Core.Data.CommunityHubDbContext db, int ev, string email,
        TicketStatus t = TicketStatus.TwoDay)
    {
        var a = new Attendee { EventId = ev, Email = email, FirstName = "F", LastName = email.Split('@')[0], TicketStatus = t };
        db.Attendees.Add(a); await db.SaveChangesAsync(); return a.Id;
    }

    private static async Task SetModeAsync(MasterClassSignupService svc, int ev,
        MasterClassPromotionMode mode, int hold = 12) => await svc.SaveSettingsAsync(ev, hold, mode, "org@x");

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

    [Fact]
    public async Task Offer_mode_holds_a_seat_for_a_seat_holder_and_counts_as_full()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, mcB) = await SeedAsync(db, 1, 1);
        var svc = new MasterClassSignupService(db);
        await SetModeAsync(svc, ev, MasterClassPromotionMode.OfferAndDecide, hold: 12);

        var p = await Att(db, ev, "p@x.dk");      // will hold A + waitlist B
        var bSeat = await Att(db, ev, "b@x.dk");  // holds B's only seat
        await svc.SignUpAsync(ev, p, mcA);        // p confirmed in A
        await svc.SignUpAsync(ev, bSeat, mcB);    // B full
        await svc.SignUpAsync(ev, p, mcB, autoSwitchConsent: true);  // p waitlists B (consents to auto-switch)

        var promo = await svc.RemoveAsync(ev, bSeat, mcB);  // B seat frees -> p already holds A
        Assert.Equal(MasterClassSignupService.PromotionKind.Offered, promo!.Kind);
        var pB = (await svc.GetForAttendeeAsync(ev, p)).Single(s => s.SessionId == mcB);
        Assert.Equal(MasterClassSignupStatus.Offered, pB.Status);
        // The held offer occupies the seat -> B shows full.
        var bOpt = (await svc.ListMasterClassesAsync(ev)).Single(m => m.SessionId == mcB);
        Assert.True(bOpt.IsFull);
    }

    [Fact]
    public async Task Accept_offer_switches_and_releases_the_old_seat()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, mcB) = await SeedAsync(db, 1, 1);
        var svc = new MasterClassSignupService(db);
        await SetModeAsync(svc, ev, MasterClassPromotionMode.OfferAndDecide);
        var p = await Att(db, ev, "p@x.dk");
        var bSeat = await Att(db, ev, "b@x.dk");
        var aWait = await Att(db, ev, "aw@x.dk");
        await svc.SignUpAsync(ev, p, mcA);
        await svc.SignUpAsync(ev, aWait, mcA);   // waits on A (A full)
        await svc.SignUpAsync(ev, bSeat, mcB);
        await svc.SignUpAsync(ev, p, mcB, autoSwitchConsent: true);  // p waitlists B (consents to auto-switch)
        await svc.RemoveAsync(ev, bSeat, mcB);    // p offered B

        var (ok, _, freed) = await svc.AcceptOfferAsync(ev, p);
        Assert.True(ok);
        Assert.Equal(MasterClassSignupStatus.Confirmed, (await svc.GetForAttendeeAsync(ev, p)).Single().Status);
        Assert.Equal(mcB, (await svc.GetForAttendeeAsync(ev, p)).Single().SessionId);   // now in B
        Assert.Equal(aWait, freed!.PromotedAttendeeId);                                  // A's waitlist promoted
    }

    [Fact]
    public async Task Default_mode_auto_switches_a_seat_holder()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, mcB) = await SeedAsync(db, 1, 1);   // default mode = AutoSwitch
        var svc = new MasterClassSignupService(db);
        var p = await Att(db, ev, "p@x.dk");
        var bSeat = await Att(db, ev, "b@x.dk");
        await svc.SignUpAsync(ev, p, mcA);
        await svc.SignUpAsync(ev, bSeat, mcB);
        await svc.SignUpAsync(ev, p, mcB, autoSwitchConsent: true);  // p waitlists B (consents to auto-switch) while confirmed in A

        var promo = await svc.RemoveAsync(ev, bSeat, mcB);  // auto-switch p into B
        Assert.Equal(MasterClassSignupService.PromotionKind.Confirmed, promo!.Kind);
        var pSig = (await svc.GetForAttendeeAsync(ev, p)).Single();
        Assert.Equal(mcB, pSig.SessionId);
        Assert.Equal(MasterClassSignupStatus.Confirmed, pSig.Status);  // old A released
    }

    [Fact]
    public async Task Expired_offer_auto_switches_by_default()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, mcB) = await SeedAsync(db, 1, 1);
        var svc = new MasterClassSignupService(db);
        await SetModeAsync(svc, ev, MasterClassPromotionMode.OfferAndDecide, hold: 12);
        var p = await Att(db, ev, "p@x.dk");
        var bSeat = await Att(db, ev, "b@x.dk");
        await svc.SignUpAsync(ev, p, mcA);
        await svc.SignUpAsync(ev, bSeat, mcB);
        await svc.SignUpAsync(ev, p, mcB, autoSwitchConsent: true);
        await svc.RemoveAsync(ev, bSeat, mcB);   // p offered B

        // Force the offer past its window, then expire.
        var off = db.MasterClassSignups.Single(x => x.AttendeeId == p && x.SessionId == mcB);
        off.OfferExpiresAt = System.DateTimeOffset.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();
        await svc.ExpireOffersAsync(System.DateTimeOffset.UtcNow, ev);

        var pSig = (await svc.GetForAttendeeAsync(ev, p)).Single();
        Assert.Equal(mcB, pSig.SessionId);                              // auto-switched to B
        Assert.Equal(MasterClassSignupStatus.Confirmed, pSig.Status);
    }
}
