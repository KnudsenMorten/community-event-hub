using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §928 — THE MASTER CLASSES GO OUT TOGETHER, IN ONE NAMED WEEK.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-06: <i>"reschedule master classes to last week august"</i>, following
/// <i>"i expect some master classes to be moved up earlier, but havent decided yet"</i>.</para>
///
/// <para>🔴 <b>It is two pieces of work, and a test for only one of them would pass while the
/// feature did nothing.</b> §918's auto-approval had already accepted the whole open queue, so the
/// nine master classes he wanted moved were exactly the posts re-planning is forbidden to touch
/// (§848.2 discards only what he has NOT accepted). Steering new posts is the easy half; moving the
/// frozen rows is the half that makes the queue agree with the rule (§901).</para>
/// </remarks>
public sealed class SoMeMasterClassWindowTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The Monday of the last full week of August 2026 — his week.</summary>
    private static readonly DateOnly WindowFrom = new(2026, 8, 24);

    private static readonly DateTimeOffset WindowOpens =
        new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The Monday after his week — nothing should reach this if the window is honoured.</summary>
    private static readonly DateTimeOffset WindowOverflows =
        new(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset EventStart =
        new(2027, 2, 9, 0, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------------------------------
    // The pure re-time, tested without a database.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// 🔴 THE POINT OF THE WHOLE FEATURE: posts move BACKWARDS as well as forwards.
    /// </summary>
    /// <remarks>
    /// A floor ("not before X") could never have delivered this. The §848.1 spread had scattered the
    /// master classes as far out as February, so the ones that needed moving needed to come EARLIER —
    /// which is why this is §908's "the date is the instruction" applied to stored rows rather than
    /// another gate.
    /// </remarks>
    [Fact]
    public void Retime_pulls_posts_back_into_the_window_from_months_away()
    {
        var movable = new[]
        {
            (1, "session:1", new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero)),
            (2, "session:2", new DateTimeOffset(2026, 11, 3, 11, 0, 0, TimeSpan.Zero)),
            (3, "session:3", new DateTimeOffset(2027, 1, 20, 13, 0, 0, TimeSpan.Zero)),
        };

        var moved = SoMeSchedulePlanner.RetimeIntoWindow(
            movable, Array.Empty<DateTimeOffset>(), WindowOpens, EventStart, maxPerDay: 2);

        Assert.Equal(3, moved.Count);
        Assert.All(moved, m => Assert.True(
            m.ScheduledAtUtc >= WindowOpens && m.ScheduledAtUtc < WindowOverflows,
            $"post {m.PostId} landed {m.ScheduledAtUtc:u}, outside the window week"));
    }

    /// <summary>
    /// 🔒 IDEMPOTENT, and it MUST be — the scheduler runs on a timer.
    /// </summary>
    /// <remarks>
    /// A re-time that produced a different answer each run would move a post he had read and approved
    /// yesterday to somewhere else today, which is the exact reshuffling
    /// <see cref="SoMeSchedulePlanner"/> exists to avoid. The order is taken from the posts' CURRENT
    /// slots, so the second run sees the order the first run produced and reports nothing to do — a
    /// fixed point, not a shuffle that eventually settles.
    /// </remarks>
    [Fact]
    public void Retime_run_twice_changes_nothing_the_second_time()
    {
        var movable = new List<(int, string, DateTimeOffset)>
        {
            (1, "session:1", new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero)),
            (2, "session:2", new DateTimeOffset(2026, 11, 3, 11, 0, 0, TimeSpan.Zero)),
            (3, "session:3", new DateTimeOffset(2027, 1, 20, 13, 0, 0, TimeSpan.Zero)),
            (4, "session:4", new DateTimeOffset(2026, 12, 8, 14, 30, 0, TimeSpan.Zero)),
        };

        var first = SoMeSchedulePlanner.RetimeIntoWindow(
            movable, Array.Empty<DateTimeOffset>(), WindowOpens, EventStart, maxPerDay: 2);
        Assert.NotEmpty(first);

        // Apply the moves, exactly as the service does, and run again.
        var byId = first.ToDictionary(m => m.PostId, m => m.ScheduledAtUtc);
        var after = movable
            .Select(m => (m.Item1, m.Item2, byId.TryGetValue(m.Item1, out var s) ? s : m.Item3))
            .ToList();

        var second = SoMeSchedulePlanner.RetimeIntoWindow(
            after, Array.Empty<DateTimeOffset>(), WindowOpens, EventStart, maxPerDay: 2);

        Assert.Empty(second);
    }

    /// <summary>
    /// ⚠️ THE DAY CEILING STILL WINS — master classes fill AROUND what is already booked.
    /// </summary>
    /// <remarks>
    /// §843.3's "normal is 2" is an attendee-experience rule, not a capacity tweak. A window that
    /// packed announcements four to a day would honour his instruction by breaking a rule he cared
    /// about more; the tail moves to the following days instead.
    /// </remarks>
    [Fact]
    public void Retime_never_exceeds_the_day_ceiling_and_never_evicts_another_post()
    {
        // Monday and Tuesday of his week are already full: two other posts each.
        var occupied = new[]
        {
            SoMeSchedulePlanner.ToUtc(new DateOnly(2026, 8, 24), new TimeOnly(9, 0)),
            SoMeSchedulePlanner.ToUtc(new DateOnly(2026, 8, 24), new TimeOnly(11, 0)),
            SoMeSchedulePlanner.ToUtc(new DateOnly(2026, 8, 25), new TimeOnly(9, 0)),
            SoMeSchedulePlanner.ToUtc(new DateOnly(2026, 8, 25), new TimeOnly(13, 0)),
        };

        var movable = Enumerable.Range(1, 4)
            .Select(i => (i, $"session:{i}",
                new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero).AddDays(i)))
            .ToList();

        var moved = SoMeSchedulePlanner.RetimeIntoWindow(
            movable, occupied, WindowOpens, EventStart, maxPerDay: 2);

        Assert.Equal(4, moved.Count);

        // Nothing landed on a slot another post already held…
        Assert.All(moved, m => Assert.DoesNotContain(m.ScheduledAtUtc, occupied));

        // …and the two full days stayed at two.
        var full = new[] { new DateOnly(2026, 8, 24), new DateOnly(2026, 8, 25) };
        Assert.All(moved, m => Assert.DoesNotContain(
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
                m.ScheduledAtUtc, SoMeSchedulePlanner.DanishTime).DateTime),
            full));
    }

    /// <summary>
    /// 🔒 NO ROOM ⇒ THE POST KEEPS THE SLOT IT HAS. Never dropped.
    /// </summary>
    /// <remarks>
    /// Being announced on the wrong day is a preference missed; vanishing out of the queue is a post
    /// that never goes out. Same reasoning as the planner's pass 3 (§842.5): late beats never.
    /// </remarks>
    [Fact]
    public void Retime_leaves_a_post_where_it_is_when_the_window_has_no_room_before_the_event()
    {
        var itsSlot = new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero);
        var movable = new[] { (1, "session:1", itsSlot) };

        // A window that opens AFTER the event: there is nowhere legal to put it.
        var moved = SoMeSchedulePlanner.RetimeIntoWindow(
            movable, Array.Empty<DateTimeOffset>(),
            new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.Zero), EventStart, maxPerDay: 2);

        Assert.Empty(moved);
    }

    // ---------------------------------------------------------------------------------------
    // The service, end to end.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// §928 half one — a NEW master-class post is planned into the window, not spread.
    /// </summary>
    /// <remarks>
    /// 🔑 The window is an INSTRUCTION, so it uses §908's explicit-round path. A plain floor would
    /// only have said "not before 24 Aug" and left the §848.1 spread free to scatter the nine master
    /// classes across the six months to the event all over again — which is the state he asked to
    /// have fixed.
    /// </remarks>
    [Fact]
    public async Task New_master_class_posts_are_planned_into_the_window()
    {
        using var db = NewDb();
        await SeedAsync(db);
        await AddMasterClassesAsync(db, 4);
        await SetWindowAsync(db, WindowFrom);

        await Svc(db).RunAsync(EventId);

        var mc = await MasterClassPostsAsync(db);
        Assert.Equal(4, mc.Count);
        Assert.All(mc, p => Assert.True(
            p.ScheduledAtUtc >= WindowOpens && p.ScheduledAtUtc < WindowOverflows,
            $"a master class landed {p.ScheduledAtUtc:u}, outside the window week"));
    }

    /// <summary>
    /// 🔴 §928 half two — A POST HE HAS ALREADY APPROVED IS MOVED. This is the one that matters.
    /// </summary>
    /// <remarks>
    /// <para>§918's auto-approval was ON and he had approved the open queue, so every master-class
    /// post was <c>Accepted</c> and frozen against re-planning. A feature that only steered NEW posts
    /// would have passed its own tests, changed nothing he could see, and left the planner and the
    /// queue telling different stories — §901's shape exactly.</para>
    ///
    /// <para>🔒 Only the date changes: the words, the approval and the plan state survive, because a
    /// re-time is not a re-plan.</para>
    /// </remarks>
    [Fact]
    public async Task An_already_approved_master_class_post_is_moved_into_the_window()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ids = await AddMasterClassesAsync(db, 2);
        await SetWindowAsync(db, WindowFrom);

        // As PROD has them: accepted, approved, sitting months away.
        foreach (var (id, i) in ids.Select((x, i) => (x, i)))
        {
            db.SoMePosts.Add(new SoMePost
            {
                EventId = EventId, Type = SoMePostType.Speaker,
                TemplateKind = SoMeTemplateKind.Session,
                SubjectKey = $"session:{id}", Occurrence = 1,
                ScheduledAtUtc = new DateTimeOffset(2026, 12, 8, 9, 0, 0, TimeSpan.Zero).AddDays(i),
                Status = SoMePostStatus.Queued,
                IsActive = true,
                PlanState = SoMePostPlanState.Scheduled,
                AutoText = "The words he approved.",
            });
        }
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);

        var mc = await MasterClassPostsAsync(db);
        Assert.Equal(2, mc.Count);

        Assert.All(mc, p => Assert.True(
            p.ScheduledAtUtc >= WindowOpens && p.ScheduledAtUtc < WindowOverflows,
            $"an approved master class stayed at {p.ScheduledAtUtc:u} — the rows were not moved"));

        // 🔒 Nothing but the date. His approval and his words are untouched.
        Assert.All(mc, p =>
        {
            Assert.True(p.IsActive);
            Assert.Equal(SoMePostPlanState.Scheduled, p.PlanState);
            Assert.Equal("The words he approved.", p.AutoText);
        });
    }

    /// <summary>
    /// 🔒 A PUBLISHED POST IS HISTORY. It is never moved, whatever the window says.
    /// </summary>
    [Fact]
    public async Task A_published_master_class_post_is_never_moved()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ids = await AddMasterClassesAsync(db, 1);
        await SetWindowAsync(db, WindowFrom);

        var sent = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        db.SoMePosts.Add(new SoMePost
        {
            EventId = EventId, Type = SoMePostType.Speaker,
            TemplateKind = SoMeTemplateKind.Session,
            SubjectKey = $"session:{ids[0]}", Occurrence = 1,
            ScheduledAtUtc = sent, Status = SoMePostStatus.Published,
            PublishedAtUtc = sent, IsActive = true,
            PlanState = SoMePostPlanState.Scheduled, AutoText = "Already out.",
        });
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);

        var post = await db.SoMePosts.FirstAsync(p => p.SubjectKey == $"session:{ids[0]}");
        Assert.Equal(sent, post.ScheduledAtUtc);
    }

    /// <summary>
    /// ⚠️ NO WINDOW SET ⇒ NOTHING CHANGES. An edition that has not chosen a week keeps §848.1's spread.
    /// </summary>
    /// <remarks>
    /// The §927 trap in miniature: a feature whose default silently rewrote the campaign would be far
    /// worse than one that waits to be switched on. Null means "no opinion", not "today".
    /// </remarks>
    [Fact]
    public async Task With_no_window_set_master_class_posts_are_left_alone()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var ids = await AddMasterClassesAsync(db, 1);
        // Deliberately NOT setting the window.

        var far = new DateTimeOffset(2026, 12, 8, 9, 0, 0, TimeSpan.Zero);
        db.SoMePosts.Add(new SoMePost
        {
            EventId = EventId, Type = SoMePostType.Speaker,
            TemplateKind = SoMeTemplateKind.Session,
            SubjectKey = $"session:{ids[0]}", Occurrence = 1,
            ScheduledAtUtc = far, Status = SoMePostStatus.Queued, IsActive = true,
            PlanState = SoMePostPlanState.Scheduled, AutoText = "Left alone.",
        });
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId);

        var post = await db.SoMePosts.FirstAsync(p => p.SubjectKey == $"session:{ids[0]}");
        Assert.Equal(far, post.ScheduledAtUtc);
    }

    /// <summary>
    /// 🔒 THE WINDOW IS FOR MASTER CLASSES ONLY — a technical session is not swept up with them.
    /// </summary>
    /// <remarks>
    /// The subjects share a <c>session:</c> key space and a template kind, so "master class" has to
    /// be read off the session's TYPE. Getting that wrong would pull the entire session campaign into
    /// one week, which is a far bigger change than the one he asked for.
    /// </remarks>
    [Fact]
    public async Task An_ordinary_session_is_not_pulled_into_the_master_class_window()
    {
        using var db = NewDb();
        await SeedAsync(db, sessions: 3);
        await AddMasterClassesAsync(db, 2);
        await SetWindowAsync(db, WindowFrom);

        await Svc(db).RunAsync(EventId);

        var mcKeys = (await db.Sessions
                .Where(s => s.EventId == EventId && s.Type == SessionType.MasterClass)
                .Select(s => s.Id).ToListAsync())
            .Select(id => $"session:{id}")
            .ToHashSet();

        var others = await db.SoMePosts
            .Where(p => p.TemplateKind == SoMeTemplateKind.Session)
            .ToListAsync();

        var ordinary = others.Where(p => !mcKeys.Contains(p.SubjectKey!)).ToList();
        Assert.NotEmpty(ordinary);
        Assert.Contains(ordinary, p => p.ScheduledAtUtc >= WindowOverflows);
    }

    // ---------------------------------------------------------------------------------------

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"some-mcwindow-{Guid.NewGuid():N}").Options);

    private static SoMeScheduleService Svc(CommunityHubDbContext db)
    {
        var clock = new FixedClock(Now);
        return new SoMeScheduleService(
            db, new SoMeTemplateService(db, clock), new SoMeVariableResolver(db), clock);
    }

    private static async Task SeedAsync(CommunityHubDbContext db, int sessions = 0)
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
            EventSystemUrl = "https://eldk27.expertslive.dk",
            EventTags = "#ELDK27 #ExpertsLiveDK",
            OrganizerCredits = "Organizer One | Organizer Two",
            // 🔒 The track floor stays set in these fixtures (§925.1): without it every track is
            // announced immediately and the campaign under test is not the one he has.
            SpeakerAnnouncementFrom = new DateOnly(2026, 9, 7),
        });
        for (var i = 1; i <= sessions; i++)
        {
            db.Sessions.Add(new Session
            {
                EventId = EventId, SessionizeId = $"s{i}", Title = $"Session {i}",
                Track = "AI", Type = SessionType.TechnicalSession,
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task<List<int>> AddMasterClassesAsync(CommunityHubDbContext db, int count)
    {
        var added = new List<Session>();
        for (var i = 1; i <= count; i++)
        {
            var s = new Session
            {
                EventId = EventId, SessionizeId = $"mc{i}", Title = $"Master Class {i}",
                Track = $"Track {i}", Type = SessionType.MasterClass,
                CreatedAt = new DateTimeOffset(2026, 6, 25, 0, 0, 0, TimeSpan.Zero),
            };
            db.Sessions.Add(s);
            added.Add(s);
        }
        await db.SaveChangesAsync();
        return added.Select(s => s.Id).ToList();
    }

    private static async Task SetWindowAsync(CommunityHubDbContext db, DateOnly from)
    {
        var s = await db.SoMeSettings.FirstAsync();
        s.MasterClassAnnouncementFrom = from;
        await db.SaveChangesAsync();
    }

    private static async Task<List<SoMePost>> MasterClassPostsAsync(CommunityHubDbContext db)
    {
        var ids = await db.Sessions
            .Where(s => s.EventId == EventId && s.Type == SessionType.MasterClass)
            .Select(s => s.Id)
            .ToListAsync();
        var keys = ids.Select(id => $"session:{id}").ToHashSet();

        return (await db.SoMePosts.Where(p => p.EventId == EventId).ToListAsync())
            .Where(p => p.SubjectKey != null && keys.Contains(p.SubjectKey))
            .ToList();
    }
}
