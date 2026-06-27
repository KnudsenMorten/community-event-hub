using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §139: the atomic "switch to this Master Class" + the HARD overbooking guard on
/// <see cref="MasterClassSignupService.SwitchAsync"/>. Confirmed seats must never exceed
/// capacity; the cancel-old + book-new is one all-or-nothing unit, so a switch that loses
/// the race for the last seat fails with <see cref="MasterClassSignupService.NowFullError"/>
/// and leaves the attendee's existing seat intact.
/// <para>
/// EF in-memory exercises the guard LOGIC and ordering invariants. The in-memory provider
/// has no transactions/locking, so the "two claim the last seat" race is proven
/// deterministically by committing the claims in sequence: once the seat is gone the second
/// claimant hits the commit-time re-check and is refused — exactly what the SERIALIZABLE +
/// retry path enforces on SQL Server (where a phantom INSERT is blocked, the loser retried,
/// re-counts, and gets the same NowFullError).
/// </para>
/// FAKE names only.
/// </summary>
public class MasterClassSwitchTests
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

    [Fact] // happy path: switch A -> B cancels the old seat, confirms the new one, never 2 confirmed.
    public async Task Switch_moves_the_confirmed_seat_atomically()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, mcB) = await SeedAsync(db, 5, 5);
        var p = await Att(db, ev, "p@x.dk");
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(ev, p, mcA);   // confirmed in A

        var (ok, err, _) = await svc.SwitchAsync(ev, p, mcB);
        Assert.True(ok);
        Assert.Null(err);

        var sig = Assert.Single(await svc.GetForAttendeeAsync(ev, p));
        Assert.Equal(mcB, sig.SessionId);
        Assert.Equal(MasterClassSignupStatus.Confirmed, sig.Status);
        // No leftover seat in A, and never two confirmed.
        Assert.Equal(0, db.MasterClassSignups.Count(x => x.SessionId == mcA));
        Assert.Equal(1, db.MasterClassSignups.Count(x => x.AttendeeId == p
                          && x.Status == MasterClassSignupStatus.Confirmed));
    }

    [Fact] // an attendee with no seat: switch == a plain confirmed booking.
    public async Task Switch_with_no_current_seat_books_a_confirmed_seat()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, _) = await SeedAsync(db, 5, 5);
        var p = await Att(db, ev, "p@x.dk");
        var svc = new MasterClassSignupService(db);

        var (ok, _, _) = await svc.SwitchAsync(ev, p, mcA);
        Assert.True(ok);
        Assert.Equal(MasterClassSignupStatus.Confirmed,
            (await svc.GetForAttendeeAsync(ev, p)).Single().Status);
    }

    [Fact] // SAFETY NET: target filled mid-session -> NowFullError AND the old seat is intact.
    public async Task Switch_blocked_when_target_filled_keeps_old_seat()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, mcB) = await SeedAsync(db, 5, 1);   // B has exactly one seat
        var p = await Att(db, ev, "p@x.dk");
        var racer = await Att(db, ev, "r@x.dk");
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(ev, p, mcA);       // p confirmed in A (the seat we must not lose)

        // The page rendered B as "Available", but it fills before p confirms the switch.
        await svc.SignUpAsync(ev, racer, mcB);   // B now full (1/1)

        var (ok, err, _) = await svc.SwitchAsync(ev, p, mcB);
        Assert.False(ok);
        Assert.Equal(MasterClassSignupService.NowFullError, err);

        // p STILL holds the original A seat — the switch was all-or-nothing.
        var sig = Assert.Single(await svc.GetForAttendeeAsync(ev, p));
        Assert.Equal(mcA, sig.SessionId);
        Assert.Equal(MasterClassSignupStatus.Confirmed, sig.Status);
    }

    [Fact] // §94: target grew a waitlist -> switch refused, seat kept (seats go to the waitlist first).
    public async Task Switch_refused_when_target_has_a_waitlist()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, mcB) = await SeedAsync(db, 5, 5);
        var p = await Att(db, ev, "p@x.dk");
        var waiter = await Att(db, ev, "w@x.dk");
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(ev, p, mcA);   // p confirmed in A

        // Inject a waitlist entry on B even though it has free seats (a §94 state to respect).
        db.MasterClassSignups.Add(new MasterClassSignup
        {
            EventId = ev, SessionId = mcB, AttendeeId = waiter,
            Status = MasterClassSignupStatus.Waitlisted,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var (ok, err, _) = await svc.SwitchAsync(ev, p, mcB);
        Assert.False(ok);
        Assert.Contains("waitlist", err!, StringComparison.OrdinalIgnoreCase);
        // Seat in A kept.
        Assert.Equal(mcA, (await svc.GetForAttendeeAsync(ev, p)).Single().SessionId);
    }

    [Fact] // OVERBOOKING: two attendees claim the last seat -> exactly one confirmed, the other NowFullError.
    public async Task Two_claiming_the_last_seat_never_overbook()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, mcB) = await SeedAsync(db, 5, 1);   // ONE seat in B
        var p1 = await Att(db, ev, "p1@x.dk");
        var p2 = await Att(db, ev, "p2@x.dk");
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(ev, p1, mcA);   // both currently sit elsewhere…
        await svc.SignUpAsync(ev, p2, mcA);   // (A cap 5)

        // Both saw B as Available and try to switch in. Commit-guard re-checks at commit:
        // the first claim takes the seat, the second is refused (this is what Serializable
        // + retry guarantees against a true concurrent race on SQL Server).
        var r1 = await svc.SwitchAsync(ev, p1, mcB);
        var r2 = await svc.SwitchAsync(ev, p2, mcB);

        Assert.True(r1.Ok);
        Assert.False(r2.Ok);
        Assert.Equal(MasterClassSignupService.NowFullError, r2.Error);

        // Capacity invariant: confirmed seats in B never exceed the cap of 1.
        Assert.Equal(1, db.MasterClassSignups.Count(x => x.SessionId == mcB
                          && x.Status == MasterClassSignupStatus.Confirmed));
        // The loser kept their original seat.
        Assert.Equal(mcA, (await svc.GetForAttendeeAsync(ev, p2)).Single().SessionId);
    }

    [Fact] // releasing the switched-out seat promotes that class's waitlist (§93).
    public async Task Switch_out_promotes_the_released_classes_waitlist()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, mcB) = await SeedAsync(db, 1, 5);   // A holds exactly one seat
        var p = await Att(db, ev, "p@x.dk");
        var waiterA = await Att(db, ev, "wa@x.dk");
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(ev, p, mcA);        // p confirmed in A (A full)
        await svc.SignUpAsync(ev, waiterA, mcA);  // waiterA waitlists A

        var (ok, _, freed) = await svc.SwitchAsync(ev, p, mcB);
        Assert.True(ok);
        Assert.Equal(waiterA, freed!.PromotedAttendeeId);   // A's waitlist was promoted on release

        Assert.Equal(mcB, (await svc.GetForAttendeeAsync(ev, p)).Single().SessionId);
        Assert.Equal(MasterClassSignupStatus.Confirmed,
            (await svc.GetForAttendeeAsync(ev, waiterA)).Single().Status);
    }

    [Fact] // join then leave a waitlist (no seat lost) — the §63 join rules over the service.
    public async Task Join_then_leave_waitlist_keeps_the_existing_seat()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mcA, mcB) = await SeedAsync(db, 5, 1);
        var p = await Att(db, ev, "p@x.dk");
        var bSeat = await Att(db, ev, "b@x.dk");
        var svc = new MasterClassSignupService(db);
        await svc.SignUpAsync(ev, p, mcA);     // p confirmed in A
        await svc.SignUpAsync(ev, bSeat, mcB); // B full

        // Join B's waitlist (consented) — keeps the A seat (§63: joining alone cancels nothing).
        var joined = await svc.SignUpAsync(ev, p, mcB, autoSwitchConsent: true);
        Assert.True(joined.Ok);
        Assert.Equal(MasterClassSignupStatus.Waitlisted, joined.Signup!.Status);
        Assert.Equal(MasterClassSignupStatus.Confirmed,
            (await svc.GetForAttendeeAsync(ev, p)).Single(s => s.SessionId == mcA).Status);

        // Leave the waitlist — A seat still there.
        await svc.RemoveAsync(ev, p, mcB);
        var sig = Assert.Single(await svc.GetForAttendeeAsync(ev, p));
        Assert.Equal(mcA, sig.SessionId);
        Assert.Equal(MasterClassSignupStatus.Confirmed, sig.Status);
    }
}
