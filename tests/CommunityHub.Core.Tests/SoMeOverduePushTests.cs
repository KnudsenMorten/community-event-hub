using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1144 — a post whose slot went by while it was still waiting is pushed onto the next free one.
///
/// <para>Operator 2026-08-28: <i>"planning service must adjust if a pending planned will not be met,
/// so it pushes the schedule"</i>.</para>
///
/// <para>🔴 <b>The gap.</b> The planner ADDS and never curates (§824.21a), and the dispatcher takes
/// <c>Queued &amp;&amp; ScheduledAtUtc &lt;= now</c>. A post blocked on its artwork therefore sat
/// with a date in the past — not rescheduled, not dropped — and published out of order the moment
/// the blocker cleared.</para>
///
/// <para>🔒 Two operator decisions are pinned here, both taken 2026-08-28: <b>only the slipped post
/// moves</b> (the tail is not cascaded), and <b>an accepted post is never moved</b>.</para>
/// </summary>
public sealed class SoMeOverduePushTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"some-push-{Guid.NewGuid():N}").Options);

    private static SoMeScheduleService Svc(CommunityHubDbContext db)
    {
        var clock = new FixedClock(Now);
        return new SoMeScheduleService(
            db, new SoMeTemplateService(db, clock), new SoMeVariableResolver(db), clock);
    }

    private static async Task SeedEditionAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "ELDK", DisplayName = "ELDK 2027",
            VenueName = "Bella Center", StartDate = new DateOnly(2027, 2, 9),
            EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        db.SoMeSettings.Add(new SoMeSettings
        {
            EventId = EventId, Enabled = true,
            EventSystemUrl = "https://eldk27.example.test",
            EventTags = "#ELDK27",
            OrganizerCredits = "Organizer One",
        });
        await db.SaveChangesAsync();
    }

    private static SoMePost Post(
        string subjectKey, DateTimeOffset at, SoMePostPlanState plan = SoMePostPlanState.Proposed,
        SoMePostStatus status = SoMePostStatus.Queued, DateTimeOffset? publishedAt = null,
        // §1205 — `isActive: false` is a HELD-BACK post: the approval gate refused it, so
        // auto-approve never turned it on. `manualText` is what keeps it out of §848.2's discard,
        // exactly as an edited post is in production.
        bool isActive = true, string? manualText = null,
        SoMeTemplateKind kind = SoMeTemplateKind.Sponsor) =>
        new()
        {
            EventId = EventId,
            SubjectKey = subjectKey,
            Occurrence = 1,
            ScheduledAtUtc = at,
            Status = status,
            PlanState = plan,
            IsActive = isActive,
            PublishedAtUtc = publishedAt,
            TemplateKind = kind,
            AutoText = "text",
            ManualTextOverride = manualText,
        };

    private static async Task<SoMePost> ReloadAsync(CommunityHubDbContext db, int id) =>
        await db.SoMePosts.AsNoTracking().FirstAsync(p => p.Id == id);

    [Fact]
    public async Task An_OVERDUE_proposed_post_is_moved_into_the_future()
    {
        using var db = NewDb();
        await SeedEditionAsync(db);

        var late = Post("sponsor:co-late", Now.AddDays(-6));
        db.SoMePosts.Add(late);
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);

        var after = await ReloadAsync(db, late.Id);

        // 🔴 Before §1144 this stayed six days in the past for ever, then published out of order.
        Assert.True(after.ScheduledAtUtc >= Now,
            $"overdue post was left at {after.ScheduledAtUtc:u}, before now ({Now:u})");
    }

    /// <summary>
    /// 🔒 HIS DECISION, 2026-08-28: an accepted post is never moved by the planner.
    /// </summary>
    /// <remarks>
    /// This is §848.2's rule — once he accepts a slot he owns it. An accepted post that slips keeps
    /// its date and publishes late; changing it is his call, not the engine's. Moving it would be
    /// exactly the silent re-dating §824.21a exists to prevent.
    /// </remarks>
    [Fact]
    public async Task An_ACCEPTED_post_is_NOT_moved_even_when_overdue()
    {
        using var db = NewDb();
        await SeedEditionAsync(db);

        var accepted = Post("sponsor:co-accepted", Now.AddDays(-6), SoMePostPlanState.Scheduled);
        db.SoMePosts.Add(accepted);
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);

        var after = await ReloadAsync(db, accepted.Id);
        Assert.Equal(Now.AddDays(-6), after.ScheduledAtUtc);
    }

    /// <summary>
    /// 🔒 HIS DECISION, 2026-08-28: move the slipped post only — do not cascade the tail.
    /// </summary>
    /// <remarks>
    /// The alternative (shifting everything after it to preserve the gaps) re-dates posts he has
    /// been reading all week off one missed slot. Keeping the campaign's shape was the explicit
    /// choice.
    /// </remarks>
    [Fact]
    public async Task A_FUTURE_post_keeps_its_date_when_an_earlier_one_is_pushed()
    {
        using var db = NewDb();
        await SeedEditionAsync(db);

        var late = Post("sponsor:co-late", Now.AddDays(-3));
        var future = Post("sponsor:co-future", Now.AddDays(5));
        db.SoMePosts.AddRange(late, future);
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);

        var futureAfter = await ReloadAsync(db, future.Id);
        Assert.Equal(Now.AddDays(5), futureAfter.ScheduledAtUtc);
    }

    [Fact]
    public async Task A_PUBLISHED_post_is_never_touched()
    {
        using var db = NewDb();
        await SeedEditionAsync(db);

        // §1077 stage 5 — a published time never moves. It is history.
        var published = Post(
            "sponsor:co-done", Now.AddDays(-9), SoMePostPlanState.Proposed,
            SoMePostStatus.Published, publishedAt: Now.AddDays(-9));
        db.SoMePosts.Add(published);
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);

        var after = await ReloadAsync(db, published.Id);
        Assert.Equal(Now.AddDays(-9), after.ScheduledAtUtc);
    }

    [Fact]
    public async Task A_post_that_is_still_in_the_future_is_left_alone()
    {
        using var db = NewDb();
        await SeedEditionAsync(db);

        var soon = Post("sponsor:co-soon", Now.AddDays(2));
        db.SoMePosts.Add(soon);
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);

        // Nothing has slipped, so nothing moves — the push must be a no-op on a healthy plan.
        var after = await ReloadAsync(db, soon.Id);
        Assert.Equal(Now.AddDays(2), after.ScheduledAtUtc);
    }

    [Fact]
    public async Task Pushing_is_idempotent_across_runs()
    {
        using var db = NewDb();
        await SeedEditionAsync(db);

        var late = Post("sponsor:co-late", Now.AddDays(-4));
        db.SoMePosts.Add(late);
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);
        var first = (await ReloadAsync(db, late.Id)).ScheduledAtUtc;

        await Svc(db).RunAsync(EventId);
        var second = (await ReloadAsync(db, late.Id)).ScheduledAtUtc;

        // 🔒 The scheduler runs on a timer. A push that produced a new answer every tick would walk
        // the post down the calendar for ever.
        Assert.Equal(first, second);
    }

    // ───────────────────────── §1205 — the HELD-BACK posts ─────────────────────────
    //
    // Operator 2026-09-12: *"anyone that is held-back should be planned. if they dont make the
    // planned timeslot, then the planner must push to new date until they meet the requirement. but
    // i prefer to see them inside the plan, as i can then also see capacity"*.
    //
    // ⚠️ The push had required `IsActive` — i.e. ALREADY APPROVED — which is the exact inverse of
    // its own stated purpose: a post blocked on its logo or social text never passes the approval
    // gate, so it was never active, so it was never movable. It sat in the past for ever, holding a
    // seat in a month that had already gone.

    /// <summary>
    /// 🔴 The case §1144 was written for and never covered: held back AND edited, so §848.2's
    /// discard leaves it alone and only this pass can save it.
    /// </summary>
    [Fact]
    public async Task A_HELD_BACK_post_is_pushed_when_its_slot_passes()
    {
        using var db = NewDb();
        await SeedEditionAsync(db);

        var held = Post(
            "sponsor:co-waiting", Now.AddDays(-6),
            isActive: false, manualText: "his own words, waiting on their logo");
        db.SoMePosts.Add(held);
        await db.SaveChangesAsync();

        var result = await Svc(db).RunAsync(EventId);

        var after = await ReloadAsync(db, held.Id);
        Assert.True(after.ScheduledAtUtc >= Now,
            $"held-back post was left at {after.ScheduledAtUtc:u}, before now ({Now:u})");

        // 🔒 Pushed, NOT approved. Moving the date must never be a way past the gate.
        Assert.False(after.IsActive);

        // §854 — and it says so, because a date changing under him with no explanation reads as a bug.
        Assert.Contains("still held back", result.Message);
    }

    /// <summary>
    /// 🔑 "…until they meet the requirement": one free slot per run, for as long as it is not ready.
    /// </summary>
    [Fact]
    public async Task A_HELD_BACK_post_keeps_moving_forward_run_after_run()
    {
        using var db = NewDb();
        await SeedEditionAsync(db);

        var held = Post(
            "sponsor:co-waiting", Now.AddDays(-6),
            isActive: false, manualText: "waiting on their social text");
        db.SoMePosts.Add(held);
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);
        var first = (await ReloadAsync(db, held.Id)).ScheduledAtUtc;

        // The clock moves past the new slot while the dependency still has not arrived.
        var later = first.AddDays(1);
        var svcLater = new SoMeScheduleService(
            db, new SoMeTemplateService(db, new FixedClock(later)), new SoMeVariableResolver(db),
            new FixedClock(later));
        await svcLater.RunAsync(EventId);

        var second = (await ReloadAsync(db, held.Id)).ScheduledAtUtc;

        Assert.True(second > first,
            $"the post stalled at {first:u} once its second slot passed at {later:u}");
        Assert.True(second >= later, "it was pushed, but to a slot that is already in the past again");
    }

    /// <summary>
    /// 🛑 §834.4 — a Type 5 date IS his input. §1201 has just finished undoing the cost of
    /// re-planning these; this pass must not reintroduce it.
    /// </summary>
    [Fact]
    public async Task An_overdue_EVENT_POST_keeps_the_date_from_his_deck()
    {
        using var db = NewDb();
        await SeedEditionAsync(db);

        var deckDated = Post(
            "eventpost:cfs-closing", Now.AddDays(-5),
            isActive: false, kind: SoMeTemplateKind.EventPost);
        db.SoMePosts.Add(deckDated);
        await db.SaveChangesAsync();

        var result = await Svc(db).RunAsync(EventId);

        var after = await ReloadAsync(db, deckDated.Id);
        Assert.Equal(Now.AddDays(-5), after.ScheduledAtUtc);

        // 🔑 Left alone is not the same as unnoticed — §854.
        Assert.Contains("event-post deck", result.Message);
    }

    /// <summary>
    /// §854/§1183 — what the planner may not move is NAMED, with the reason, because the remedy
    /// differs: withdraw an acceptance, or edit a deck date.
    /// </summary>
    [Fact]
    public async Task An_overdue_ACCEPTED_post_is_named_with_its_reason()
    {
        using var db = NewDb();
        await SeedEditionAsync(db);

        var accepted = Post(
            "sponsor:co-accepted", Now.AddDays(-6), SoMePostPlanState.Scheduled, isActive: false);
        db.SoMePosts.Add(accepted);
        await db.SaveChangesAsync();

        var result = await Svc(db).RunAsync(EventId);

        Assert.Equal(Now.AddDays(-6), (await ReloadAsync(db, accepted.Id)).ScheduledAtUtc);
        Assert.Contains($"#{accepted.Id}", result.Message);
        Assert.Contains("you accepted this date", result.Message);
    }
}
