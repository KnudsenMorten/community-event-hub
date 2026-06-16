using CommunityHub.Core.Reminders;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Unit tests for the organizer Action Queue service: the late-change window
/// gating, upsert idempotency, resolve/reopen, edition scoping and counts.
/// </summary>
public class OrganizerActionItemServiceTests
{
    private const string Hotel = OrganizerActionItemService.TypeHotelChanged;
    private const string Dinner = OrganizerActionItemService.TypeDinnerChanged;

    // --- LabelFor --------------------------------------------------------

    [Fact]
    public void LabelFor_known_type_returns_friendly_label()
    {
        Assert.Equal("Hotel changed", OrganizerActionItemService.LabelFor(Hotel));
        Assert.Equal("Dinner RSVP changed", OrganizerActionItemService.LabelFor(Dinner));
    }

    [Fact]
    public void LabelFor_unknown_type_returns_raw_code()
    {
        Assert.Equal("mystery-thing", OrganizerActionItemService.LabelFor("mystery-thing"));
    }

    // --- RaiseIfLateAsync window gating ----------------------------------

    [Fact]
    public async Task RaiseIfLate_does_nothing_before_the_window()
    {
        await using var db = TestDb.New();
        var lockDate = new DateOnly(2027, 2, 1);
        var (ev, p) = await TestDb.SeedEventAndPersonAsync(db, lockDate);

        // Well before the 14-day window (window opens 2027-01-18).
        var clock = new FixedClock(new DateTimeOffset(2027, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var svc = new OrganizerActionItemService(db, clock);

        var raised = await svc.RaiseIfLateAsync(ev, Hotel, p, "changed");

        Assert.False(raised);
        Assert.Equal(0, await svc.CountOpenAsync(ev));
    }

    [Fact]
    public async Task RaiseIfLate_raises_inside_the_window()
    {
        await using var db = TestDb.New();
        var lockDate = new DateOnly(2027, 2, 1);
        var (ev, p) = await TestDb.SeedEventAndPersonAsync(db, lockDate);

        // Inside the window (opens 2027-01-18, lock 2027-02-01).
        var clock = new FixedClock(new DateTimeOffset(2027, 1, 25, 9, 0, 0, TimeSpan.Zero));
        var svc = new OrganizerActionItemService(db, clock);

        var raised = await svc.RaiseIfLateAsync(ev, Hotel, p, "Hotel changed to 8 Feb → 10 Feb");

        Assert.True(raised);
        var open = await svc.GetOpenAsync(ev);
        var item = Assert.Single(open);
        Assert.Equal(Hotel, item.Type);
        Assert.Equal("Hotel changed to 8 Feb → 10 Feb", item.Summary);
        Assert.Equal(p, item.ParticipantId);
    }

    [Fact]
    public async Task RaiseIfLate_does_nothing_when_no_lock_date_set()
    {
        await using var db = TestDb.New();
        var (ev, p) = await TestDb.SeedEventAndPersonAsync(db, lockDate: null);
        var clock = new FixedClock(new DateTimeOffset(2027, 1, 25, 9, 0, 0, TimeSpan.Zero));
        var svc = new OrganizerActionItemService(db, clock);

        var raised = await svc.RaiseIfLateAsync(ev, Hotel, p, "changed");

        Assert.False(raised);
        Assert.Equal(0, await svc.CountOpenAsync(ev));
    }

    // --- Upsert idempotency ----------------------------------------------

    [Fact]
    public async Task RaiseIfLate_twice_keeps_one_row_and_refreshes_summary()
    {
        await using var db = TestDb.New();
        var (ev, p) = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        var clock = new FixedClock(new DateTimeOffset(2027, 1, 25, 9, 0, 0, TimeSpan.Zero));
        var svc = new OrganizerActionItemService(db, clock);

        await svc.RaiseIfLateAsync(ev, Hotel, p, "first change");
        clock.Set(new DateTimeOffset(2027, 1, 26, 9, 0, 0, TimeSpan.Zero));
        await svc.RaiseIfLateAsync(ev, Hotel, p, "second change");

        var open = await svc.GetOpenAsync(ev);
        var item = Assert.Single(open);
        Assert.Equal("second change", item.Summary);
        Assert.NotNull(item.UpdatedAt);
    }

    [Fact]
    public async Task Different_types_for_same_person_are_separate_rows()
    {
        await using var db = TestDb.New();
        var (ev, p) = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        var clock = new FixedClock(new DateTimeOffset(2027, 1, 25, 9, 0, 0, TimeSpan.Zero));
        var svc = new OrganizerActionItemService(db, clock);

        await svc.RaiseIfLateAsync(ev, Hotel, p, "hotel");
        await svc.RaiseIfLateAsync(ev, Dinner, p, "dinner");

        Assert.Equal(2, await svc.CountOpenAsync(ev));
        Assert.Single(await svc.GetOpenAsync(ev, Hotel));
        Assert.Single(await svc.GetOpenAsync(ev, Dinner));
    }

    // --- Resolve / reopen ------------------------------------------------

    [Fact]
    public async Task Resolve_then_GetOpen_excludes_it_and_GetResolved_includes_it()
    {
        await using var db = TestDb.New();
        var (ev, p) = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        var clock = new FixedClock(new DateTimeOffset(2027, 1, 25, 9, 0, 0, TimeSpan.Zero));
        var svc = new OrganizerActionItemService(db, clock);

        await svc.RaiseIfLateAsync(ev, Hotel, p, "changed");
        var id = (await svc.GetOpenAsync(ev)).Single().Id;

        var ok = await svc.ResolveAsync(ev, id, "called the hotel, confirmed");
        Assert.True(ok);
        Assert.Equal(0, await svc.CountOpenAsync(ev));

        var resolved = await svc.GetResolvedAsync(ev);
        var item = Assert.Single(resolved);
        Assert.NotNull(item.ResolvedAt);
        Assert.Equal("called the hotel, confirmed", item.ResolvedNotes);
    }

    [Fact]
    public async Task Resolve_is_idempotent_second_call_returns_false()
    {
        await using var db = TestDb.New();
        var (ev, p) = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        var clock = new FixedClock(new DateTimeOffset(2027, 1, 25, 9, 0, 0, TimeSpan.Zero));
        var svc = new OrganizerActionItemService(db, clock);
        await svc.RaiseIfLateAsync(ev, Hotel, p, "changed");
        var id = (await svc.GetOpenAsync(ev)).Single().Id;

        Assert.True(await svc.ResolveAsync(ev, id, null));
        Assert.False(await svc.ResolveAsync(ev, id, null));
    }

    [Fact]
    public async Task Reopen_moves_a_resolved_item_back_to_open()
    {
        await using var db = TestDb.New();
        var (ev, p) = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        var clock = new FixedClock(new DateTimeOffset(2027, 1, 25, 9, 0, 0, TimeSpan.Zero));
        var svc = new OrganizerActionItemService(db, clock);
        await svc.RaiseIfLateAsync(ev, Hotel, p, "changed");
        var id = (await svc.GetOpenAsync(ev)).Single().Id;
        await svc.ResolveAsync(ev, id, "done");

        var ok = await svc.ReopenAsync(ev, id);

        Assert.True(ok);
        Assert.Equal(1, await svc.CountOpenAsync(ev));
        var item = (await svc.GetOpenAsync(ev)).Single();
        Assert.Null(item.ResolvedAt);
        Assert.Null(item.ResolvedNotes);
    }

    // --- Edition scoping -------------------------------------------------

    [Fact]
    public async Task Resolve_is_scoped_to_the_edition()
    {
        await using var db = TestDb.New();
        var (evA, pA) = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        var (evB, _)  = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        var clock = new FixedClock(new DateTimeOffset(2027, 1, 25, 9, 0, 0, TimeSpan.Zero));
        var svc = new OrganizerActionItemService(db, clock);

        await svc.RaiseIfLateAsync(evA, Hotel, pA, "edition A change");
        var idInA = (await svc.GetOpenAsync(evA)).Single().Id;

        // Organizer of edition B must not be able to resolve edition A's item.
        var crossOk = await svc.ResolveAsync(evB, idInA, "should fail");
        Assert.False(crossOk);
        Assert.Equal(1, await svc.CountOpenAsync(evA));
    }

    [Fact]
    public async Task GetOpen_only_returns_the_requested_editions_items()
    {
        await using var db = TestDb.New();
        var (evA, pA) = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        var (evB, pB) = await TestDb.SeedEventAndPersonAsync(db, new DateOnly(2027, 2, 1));
        var clock = new FixedClock(new DateTimeOffset(2027, 1, 25, 9, 0, 0, TimeSpan.Zero));
        var svc = new OrganizerActionItemService(db, clock);

        await svc.RaiseIfLateAsync(evA, Hotel, pA, "A");
        await svc.RaiseIfLateAsync(evB, Hotel, pB, "B");

        Assert.Single(await svc.GetOpenAsync(evA));
        Assert.Single(await svc.GetOpenAsync(evB));
    }
}
