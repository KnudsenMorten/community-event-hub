using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// RELATIONAL concurrency + constraint tests (REQUIREMENTS §220) for the §218 OPTIMISTIC
/// master-class seat allocation in <see cref="MasterClassSignupService"/>. The in-memory
/// suite proves the LOGIC but cannot prove behaviour under a real relational provider:
/// the EF in-memory provider has no transactions, never enforces UNIQUE indexes, and
/// (crucially) never runs the atomic <c>ExecuteUpdate</c> seat-claim — it falls back to a
/// plain in-process count check. These tests run against the EF <b>SQLite</b> provider,
/// which shares the SAME query-translation + constraint pipeline as Azure SQL (prod), so:
/// <list type="bullet">
/// <item>the guarded conditional UPDATE (the §218 claim) actually translates + executes;</item>
/// <item>the <c>(EventId, AttendeeId, SessionId)</c> UNIQUE index is actually enforced;</item>
/// <item>real <c>DateTimeOffset</c> FIFO ordering of the waitlist actually translates.</item>
/// </list>
/// SQLite serializes writes on its single in-memory connection, so it cannot stage TRUE
/// wall-clock parallelism; per §220 we therefore prove the conditional-update LOGIC — that
/// N attempts on a K-seat class confirm exactly K and waitlist the rest (never an oversell
/// of the last seat), that give-up→promote never double-promotes, and that counts stay
/// exact — which is what the optimistic mechanism guarantees on real SQL. Synthetic ids +
/// example.test only; no real names.
/// </summary>
public sealed class MasterClassSignupRelationalTests : IDisposable
{
    private readonly SqliteConnection _conn;

    public MasterClassSignupRelationalTests()
    {
        // A shared in-memory SQLite db kept open for the test lifetime so the schema +
        // data persist across the DbContexts each operation opens.
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private CommunityHubDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseSqlite(_conn)
            .EnableSensitiveDataLogging()
            .Options;
        return new CommunityHubDbContext(options);
    }

    private async Task<(int ev, int mc)> SeedClassAsync(int capacity)
    {
        using var db = NewDb();
        var e = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(e);
        await db.SaveChangesAsync();
        var s = new Session
        {
            EventId = e.Id, Title = "MC A", SessionizeId = "mc-a",
            Type = SessionType.MasterClass, MasterClassCapacity = capacity,
        };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return (e.Id, s.Id);
    }

    private async Task<int> AddClassAsync(int ev, string sessionizeId, int? capacity)
    {
        using var db = NewDb();
        var s = new Session
        {
            EventId = ev, Title = sessionizeId, SessionizeId = sessionizeId,
            Type = SessionType.MasterClass, MasterClassCapacity = capacity,
        };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    private async Task<int> AddAttendeeAsync(int ev, string email)
    {
        using var db = NewDb();
        var a = new Attendee
        {
            EventId = ev, Email = email, FirstName = "F", LastName = email.Split('@')[0],
            TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active,
        };
        db.Attendees.Add(a);
        await db.SaveChangesAsync();
        return a.Id;
    }

    private int CountByStatus(int ev, int mc, MasterClassSignupStatus st)
    {
        using var db = NewDb();
        return db.MasterClassSignups.Count(x => x.EventId == ev && x.SessionId == mc && x.Status == st);
    }

    // --- §220 (a): N attempts on K seats never oversells the last seat ----------

    [Theory]
    [InlineData(1, 5)]
    [InlineData(3, 10)]
    [InlineData(7, 7)]   // exactly capacity → all confirmed, none waitlisted
    public async Task Concurrent_bookings_never_oversell_last_seat(int capacity, int contenders)
    {
        var (ev, mc) = await SeedClassAsync(capacity);
        var attendees = new List<int>();
        for (var i = 0; i < contenders; i++)
            attendees.Add(await AddAttendeeAsync(ev, $"a{i}@example.test"));

        // Each booking opens its OWN DbContext on the shared SQLite connection and runs the
        // real §218 optimistic claim (the guarded conditional UPDATE) inside a transaction.
        var results = new List<MasterClassSignupStatus>();
        foreach (var att in attendees)
        {
            using var db = NewDb();
            var svc = new MasterClassSignupService(db);
            var r = await svc.SignUpAsync(ev, att, mc);
            Assert.True(r.Ok, r.Error);
            results.Add(r.Signup!.Status);
        }

        var confirmed = CountByStatus(ev, mc, MasterClassSignupStatus.Confirmed);
        var waitlisted = CountByStatus(ev, mc, MasterClassSignupStatus.Waitlisted);

        // HARD INVARIANT (§218): confirmed seats NEVER exceed capacity.
        Assert.True(confirmed <= capacity, $"oversold: {confirmed} > cap {capacity}");
        Assert.Equal(Math.Min(capacity, contenders), confirmed);
        Assert.Equal(Math.Max(0, contenders - capacity), waitlisted);
        // Counts are exact + total is conserved (no lost / duplicated rows).
        Assert.Equal(contenders, confirmed + waitlisted);
        Assert.Equal(Math.Min(capacity, contenders),
            results.Count(s => s == MasterClassSignupStatus.Confirmed));
    }

    // --- §220 (a'): the conditional-update claim rejects the over-capacity write ----

    [Fact]
    public async Task Claim_on_a_full_class_writes_zero_rows_and_falls_back_to_waitlist()
    {
        var (ev, mc) = await SeedClassAsync(capacity: 1);
        var first = await AddAttendeeAsync(ev, "first@example.test");
        var second = await AddAttendeeAsync(ev, "second@example.test");

        using (var db = NewDb())
            Assert.Equal(MasterClassSignupStatus.Confirmed,
                (await new MasterClassSignupService(db).SignUpAsync(ev, first, mc)).Signup!.Status);

        // The class is now full: the guarded UPDATE's WHERE (count < capacity) is false, so
        // the atomic claim affects 0 rows and the booker is waitlisted — never a 2nd seat.
        using (var db = NewDb())
        {
            var r = await new MasterClassSignupService(db).SignUpAsync(ev, second, mc);
            Assert.True(r.Ok);
            Assert.Equal(MasterClassSignupStatus.Waitlisted, r.Signup!.Status);
        }

        Assert.Equal(1, CountByStatus(ev, mc, MasterClassSignupStatus.Confirmed));
        Assert.Equal(1, CountByStatus(ev, mc, MasterClassSignupStatus.Waitlisted));
    }

    // --- §220 (b): give-up + promote never double-promotes ----------------------

    [Fact]
    public async Task Give_up_then_promote_fills_each_seat_once_no_double_promote()
    {
        // Two seats, both taken, two waiters. Freeing BOTH seats must promote BOTH waiters
        // (one each) — never the same waiter twice, never a 3rd confirmed.
        var (ev, mc) = await SeedClassAsync(capacity: 2);
        var s1 = await AddAttendeeAsync(ev, "s1@example.test");
        var s2 = await AddAttendeeAsync(ev, "s2@example.test");
        var w1 = await AddAttendeeAsync(ev, "w1@example.test");
        var w2 = await AddAttendeeAsync(ev, "w2@example.test");

        async Task SignUp(int att)
        {
            using var db = NewDb();
            await new MasterClassSignupService(db).SignUpAsync(ev, att, mc);
        }
        await SignUp(s1); await SignUp(s2);   // fill the 2 seats
        await SignUp(w1); await SignUp(w2);   // waitlist (FIFO: w1 before w2)

        Assert.Equal(2, CountByStatus(ev, mc, MasterClassSignupStatus.Confirmed));
        Assert.Equal(2, CountByStatus(ev, mc, MasterClassSignupStatus.Waitlisted));

        // Free both seats (each give-up promotes exactly one waiter).
        MasterClassSignupService.PromotionResult? p1, p2;
        using (var db = NewDb()) p1 = await new MasterClassSignupService(db).RemoveAsync(ev, s1, mc);
        using (var db = NewDb()) p2 = await new MasterClassSignupService(db).RemoveAsync(ev, s2, mc);

        // Distinct waiters promoted, no double-promote.
        Assert.Equal(w1, p1!.PromotedAttendeeId);   // earliest first
        Assert.Equal(w2, p2!.PromotedAttendeeId);
        Assert.NotEqual(p1.PromotedAttendeeId, p2.PromotedAttendeeId);

        Assert.Equal(2, CountByStatus(ev, mc, MasterClassSignupStatus.Confirmed));
        Assert.Equal(0, CountByStatus(ev, mc, MasterClassSignupStatus.Waitlisted));
        // Never exceeded capacity at any point.
        Assert.True(CountByStatus(ev, mc, MasterClassSignupStatus.Confirmed) <= 2);
    }

    // --- §220 (c): the DB constraints are actually enforced ---------------------

    [Fact]
    public async Task Duplicate_signup_for_same_class_is_rejected_by_the_unique_index()
    {
        var (ev, mc) = await SeedClassAsync(capacity: 5);
        var att = await AddAttendeeAsync(ev, "dup@example.test");

        using var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        db.MasterClassSignups.Add(new MasterClassSignup
        {
            EventId = ev, SessionId = mc, AttendeeId = att,
            Status = MasterClassSignupStatus.Confirmed, CreatedAt = now, UpdatedAt = now,
        });
        db.MasterClassSignups.Add(new MasterClassSignup
        {
            EventId = ev, SessionId = mc, AttendeeId = att,   // SAME (Event,Attendee,Session)
            Status = MasterClassSignupStatus.Waitlisted, CreatedAt = now, UpdatedAt = now,
        });

        // The (EventId, AttendeeId, SessionId) UNIQUE index must reject the duplicate. The
        // in-memory provider would silently ACCEPT both rows — only a relational provider
        // enforces this.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Service_signup_to_same_class_twice_is_idempotent_not_a_duplicate_row()
    {
        var (ev, mc) = await SeedClassAsync(capacity: 5);
        var att = await AddAttendeeAsync(ev, "idem@example.test");

        using (var db = NewDb())
            Assert.True((await new MasterClassSignupService(db).SignUpAsync(ev, att, mc)).Ok);
        using (var db = NewDb())
            Assert.True((await new MasterClassSignupService(db).SignUpAsync(ev, att, mc)).Ok);

        // Exactly ONE row — the idempotent guard prevented a second (which the unique index
        // would otherwise have rejected).
        using var verify = NewDb();
        Assert.Equal(1, verify.MasterClassSignups.Count(
            x => x.EventId == ev && x.SessionId == mc && x.AttendeeId == att));
    }

    // --- §220 (d): waitlist FIFO + counts after a real relational round-trip ----

    [Fact]
    public async Task Waitlist_promotes_in_fifo_order_on_relational_provider()
    {
        var (ev, mc) = await SeedClassAsync(capacity: 1);
        var seat = await AddAttendeeAsync(ev, "seat@example.test");
        var early = await AddAttendeeAsync(ev, "early@example.test");
        var late = await AddAttendeeAsync(ev, "late@example.test");

        // Sign up in a defined order; the FIFO key is CreatedAt (a DateTimeOffset that must
        // translate + order correctly on SQLite). Distinct timestamps keep the order stable.
        using (var db = NewDb())
        {
            var s = db.MasterClassSignups;
            var baseT = new DateTimeOffset(2026, 6, 14, 9, 0, 0, TimeSpan.Zero);
            s.Add(new MasterClassSignup { EventId = ev, SessionId = mc, AttendeeId = seat, Status = MasterClassSignupStatus.Confirmed, CreatedAt = baseT, ConfirmedAt = baseT, UpdatedAt = baseT });
            s.Add(new MasterClassSignup { EventId = ev, SessionId = mc, AttendeeId = early, Status = MasterClassSignupStatus.Waitlisted, CreatedAt = baseT.AddMinutes(1), UpdatedAt = baseT });
            s.Add(new MasterClassSignup { EventId = ev, SessionId = mc, AttendeeId = late, Status = MasterClassSignupStatus.Waitlisted, CreatedAt = baseT.AddMinutes(2), UpdatedAt = baseT });
            await db.SaveChangesAsync();
        }

        MasterClassSignupService.PromotionResult? promo;
        using (var db = NewDb())
            promo = await new MasterClassSignupService(db).RemoveAsync(ev, seat, mc);

        Assert.Equal(early, promo!.PromotedAttendeeId);   // earliest CreatedAt wins
        Assert.Equal(MasterClassSignupService.PromotionKind.Confirmed, promo.Kind);
        Assert.Equal(1, CountByStatus(ev, mc, MasterClassSignupStatus.Confirmed));
        Assert.Equal(1, CountByStatus(ev, mc, MasterClassSignupStatus.Waitlisted));   // 'late' still waits
    }

    // --- §220: §94 waitlist-priority survives the relational claim --------------

    [Fact]
    public async Task Public_booker_cannot_jump_a_waitlist_on_relational_provider()
    {
        var (ev, mc) = await SeedClassAsync(capacity: 2);
        var seat = await AddAttendeeAsync(ev, "seat@example.test");
        var waiter = await AddAttendeeAsync(ev, "waiter@example.test");
        var booker = await AddAttendeeAsync(ev, "booker@example.test");

        // One confirmed (1 of 2 seats), one waitlisted (even though a seat is free).
        using (var db = NewDb())
        {
            var now = DateTimeOffset.UtcNow;
            db.MasterClassSignups.Add(new MasterClassSignup { EventId = ev, SessionId = mc, AttendeeId = seat, Status = MasterClassSignupStatus.Confirmed, CreatedAt = now, ConfirmedAt = now, UpdatedAt = now });
            db.MasterClassSignups.Add(new MasterClassSignup { EventId = ev, SessionId = mc, AttendeeId = waiter, Status = MasterClassSignupStatus.Waitlisted, CreatedAt = now.AddMinutes(1), UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        // The claim has blockWhenWaitlisted=true, so even though a seat is physically free
        // the public booker is waitlisted behind the existing waiter (§94).
        using (var db = NewDb())
        {
            var r = await new MasterClassSignupService(db).SignUpAsync(ev, booker, mc);
            Assert.True(r.Ok);
            Assert.Equal(MasterClassSignupStatus.Waitlisted, r.Signup!.Status);
        }

        Assert.Equal(1, CountByStatus(ev, mc, MasterClassSignupStatus.Confirmed));
        Assert.Equal(2, CountByStatus(ev, mc, MasterClassSignupStatus.Waitlisted));
    }

    // --- §220 (e) / §219: a MIXED seat-state workload (sign-up + switch + give-up +
    //     promotion) drives the §219 RCSI-hardened claim through the relational provider
    //     and proves it never oversells, conserves rows, and keeps each attendee to ONE
    //     active signup. (On SQL Server the claim COUNT is read under WITH (UPDLOCK,
    //     HOLDLOCK); the SQLite path here exercises the SAME guard logic + every seat-state
    //     transition that the prod-Azure load-sim then proves race-safe at 400-concurrency.)
    [Fact]
    public async Task Mixed_workload_never_oversells_and_keeps_counts_exact_relational()
    {
        var (ev, a) = await SeedClassAsync(capacity: 3);
        var b = await AddClassAsync(ev, "mc-b", capacity: 3);

        // 6 attendees sign up for A (cap 3): first 3 confirmed (FIFO), next 3 waitlisted.
        var u = new List<int>();
        for (var i = 0; i < 6; i++) u.Add(await AddAttendeeAsync(ev, $"u{i}@example.test"));
        foreach (var att in u)
            using (var db = NewDb())
                Assert.True((await new MasterClassSignupService(db).SignUpAsync(ev, att, a)).Ok);

        Assert.Equal(3, CountByStatus(ev, a, MasterClassSignupStatus.Confirmed));
        Assert.Equal(3, CountByStatus(ev, a, MasterClassSignupStatus.Waitlisted));

        // u0 switches A -> B (B free): claims B atomically, then frees its A seat which
        // promotes the A waitlist head (u3). Guard must keep BOTH classes within capacity.
        using (var db = NewDb())
        {
            var (ok, err, _) = await new MasterClassSignupService(db).SwitchAsync(ev, u[0], b);
            Assert.True(ok, err);
        }
        // u1 gives up its A seat: frees a seat -> promotes the next A waiter (u4).
        using (var db = NewDb())
            await new MasterClassSignupService(db).RemoveAsync(ev, u[1], a);

        // Invariants after the mixed workload:
        var confA = CountByStatus(ev, a, MasterClassSignupStatus.Confirmed);
        var confB = CountByStatus(ev, b, MasterClassSignupStatus.Confirmed);
        var waitA = CountByStatus(ev, a, MasterClassSignupStatus.Waitlisted);

        Assert.True(confA <= 3, $"A oversold: {confA} > 3");
        Assert.True(confB <= 3, $"B oversold: {confB} > 3");
        Assert.Equal(3, confA);   // A backfilled to its cap by promotions (u2,u3,u4)
        Assert.Equal(1, confB);   // the switcher u0
        Assert.Equal(1, waitA);   // u5 still waits

        // Rows conserved + exact: u1 left entirely (gave up, was not waitlisted), so 5
        // attendees hold exactly ONE active signup each; none lost, duplicated, or oversold.
        using var verify = NewDb();
        var rows = verify.MasterClassSignups.Where(x => x.EventId == ev).ToList();
        Assert.Equal(5, rows.Count);
        Assert.All(rows.GroupBy(x => x.AttendeeId), g => Assert.Single(g));
        Assert.Equal(confA + confB, rows.Count(r => r.Status == MasterClassSignupStatus.Confirmed));
    }

    // --- §326ba: capacity 0 must never mean "unlimited" ------------------------

    [Fact]
    public async Task Capacity_zero_is_rejected_and_never_uncaps_the_room()
    {
        // The old code did `capacity is > 0 ? capacity : null`, so a mistyped 0 became NULL
        // — the one seat-claim branch that always succeeds — and the promotion loop then
        // drained the entire waitlist into the room and mailed every one of them.
        var (ev, mc) = await SeedClassAsync(capacity: 2);
        var ids = new List<int>();
        for (var i = 0; i < 5; i++) ids.Add(await AddAttendeeAsync(ev, $"cap{i}@example.test"));
        foreach (var id in ids)
        {
            using var db = NewDb();
            await new MasterClassSignupService(db).SignUpAsync(ev, id, mc);
        }
        Assert.Equal(2, CountByStatus(ev, mc, MasterClassSignupStatus.Confirmed));
        Assert.Equal(3, CountByStatus(ev, mc, MasterClassSignupStatus.Waitlisted));

        using (var db = NewDb())
        {
            var promotions = await new MasterClassSignupService(db).SetCapacityAsync(ev, mc, 0);
            Assert.Empty(promotions);                       // nobody promoted...
        }
        using (var db = NewDb())
        {
            Assert.Equal(2, db.Sessions.Single(s => s.Id == mc).MasterClassCapacity);  // ...cap unchanged
        }
        Assert.Equal(2, CountByStatus(ev, mc, MasterClassSignupStatus.Confirmed));
        Assert.Equal(3, CountByStatus(ev, mc, MasterClassSignupStatus.Waitlisted));
    }

    [Fact]
    public async Task Clearing_capacity_to_null_is_still_how_you_say_unlimited()
    {
        // The escape hatch must keep working — it is the deliberate, now-confirmed action.
        var (ev, mc) = await SeedClassAsync(capacity: 2);
        var ids = new List<int>();
        for (var i = 0; i < 5; i++) ids.Add(await AddAttendeeAsync(ev, $"unl{i}@example.test"));
        foreach (var id in ids)
        {
            using var db = NewDb();
            await new MasterClassSignupService(db).SignUpAsync(ev, id, mc);
        }

        using (var db = NewDb())
        {
            var promotions = await new MasterClassSignupService(db).SetCapacityAsync(ev, mc, null);
            Assert.Equal(3, promotions.Count);   // the whole waitlist moves in
        }
        Assert.Equal(5, CountByStatus(ev, mc, MasterClassSignupStatus.Confirmed));
        Assert.Equal(0, CountByStatus(ev, mc, MasterClassSignupStatus.Waitlisted));
    }
}
