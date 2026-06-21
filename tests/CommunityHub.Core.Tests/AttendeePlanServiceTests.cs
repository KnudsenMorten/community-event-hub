using CommunityHub.Core.Attendees;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for the attendee personal session-plan service
/// (<see cref="AttendeePlanService"/>) and its pure ordering core
/// (<see cref="AttendeePlanBuilder"/>) — the self-service "My plan" feature.
/// Proves:
///  - toggle SAVES then REMOVES the same session (idempotent), stamping the clock,
///  - saving a session from ANOTHER edition (or a service session, or an unknown id)
///    is refused (no cross-edition / forged bookmark),
///  - the saved-ids set + IsSaved are own-row scoped (one person never sees another's),
///  - BuildPlan returns the saved talks scheduled-first, start-time ordered, with
///    speaker names joined; a deleted saved session self-heals out of the plan,
///  - the pure builder orders + joins speaker names without a DbContext.
///
/// In-memory DbContext + a FixedClock; synthetic ids + example.test — no real data.
/// </summary>
public sealed class AttendeePlanServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2027, 2, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Seed(int EventId, int OtherEventId, int AliceId, int BobId,
        int TalkA, int TalkB, int Unscheduled, int Service, int OtherEdTalk);

    private static async Task<Seed> SeedAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "PLAN27", CommunityName = "Plan", DisplayName = "Plan 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        var other = new Event
        {
            Code = "OLD26", CommunityName = "Old", DisplayName = "Old 2026",
            StartDate = new DateOnly(2026, 2, 1), EndDate = new DateOnly(2026, 2, 2),
            IsActive = false,
        };
        db.Events.AddRange(evt, other);
        await db.SaveChangesAsync();

        Participant P(string name, string email, int eventId, ParticipantRole role = ParticipantRole.Attendee)
        {
            var p = new Participant { EventId = eventId, FullName = name, Email = email, Role = role, IsActive = true };
            db.Participants.Add(p);
            return p;
        }
        var alice = P("Alice Adams", "alice@example.test", evt.Id);
        var bob = P("Bob Brown", "bob@example.test", evt.Id);
        var zoe = P("Zoe Zint", "zoe@example.test", evt.Id, ParticipantRole.Speaker);
        var ann = P("Ann Apple", "ann@example.test", evt.Id, ParticipantRole.Speaker);
        await db.SaveChangesAsync();

        Session S(string id, string title, int eventId, DateTimeOffset? start,
            bool service = false, params Participant[] speakers)
        {
            var s = new Session
            {
                EventId = eventId, SessionizeId = id, Title = title,
                Type = SessionType.CommunityTechSession, Length = SessionLength.FiftyMin,
                Room = "Room A", StartsAt = start, EndsAt = start?.AddMinutes(50),
                IsServiceSession = service,
            };
            foreach (var sp in speakers)
                s.SessionSpeakers.Add(new SessionSpeaker { Session = s, Participant = sp });
            db.Sessions.Add(s);
            return s;
        }

        var talkA = S("a", "Alpha Talk", evt.Id, new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero), false, zoe, ann);
        var talkB = S("b", "Beta Talk", evt.Id, new DateTimeOffset(2027, 2, 9, 11, 0, 0, TimeSpan.Zero), false, zoe);
        var unsched = S("tba", "Gamma TBA", evt.Id, null);
        var service = S("brk", "Coffee Break", evt.Id, new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero), true);
        var otherTalk = S("old", "Last Year Talk", other.Id, new DateTimeOffset(2026, 2, 1, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        return new Seed(evt.Id, other.Id, alice.Id, bob.Id,
            talkA.Id, talkB.Id, unsched.Id, service.Id, otherTalk.Id);
    }

    private static AttendeePlanService Svc(CommunityHubDbContext db) =>
        new(db, new FixedClock(Now));

    [Fact]
    public async Task Toggle_saves_then_removes_idempotently_and_stamps_clock()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = Svc(db);

        // First toggle = saved.
        Assert.True(await svc.ToggleAsync(s.EventId, s.AliceId, s.TalkA));
        Assert.True(await svc.IsSavedAsync(s.EventId, s.AliceId, s.TalkA));
        var saved = await db.SavedSessions.SingleAsync();
        Assert.Equal(Now, saved.CreatedAt);

        // Second toggle = removed.
        Assert.False(await svc.ToggleAsync(s.EventId, s.AliceId, s.TalkA));
        Assert.False(await svc.IsSavedAsync(s.EventId, s.AliceId, s.TalkA));
        Assert.Empty(await db.SavedSessions.ToListAsync());
    }

    [Fact]
    public async Task Cannot_save_a_session_from_another_edition()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = Svc(db);

        // The other edition's talk id, saved against the active edition → refused.
        Assert.False(await svc.ToggleAsync(s.EventId, s.AliceId, s.OtherEdTalk));
        Assert.Empty(await db.SavedSessions.ToListAsync());
    }

    [Fact]
    public async Task Cannot_save_a_service_session_or_unknown_id()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = Svc(db);

        Assert.False(await svc.ToggleAsync(s.EventId, s.AliceId, s.Service));  // break
        Assert.False(await svc.ToggleAsync(s.EventId, s.AliceId, 999999));     // unknown
        Assert.Empty(await db.SavedSessions.ToListAsync());
    }

    [Fact]
    public async Task Saved_ids_are_own_row_scoped()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = Svc(db);

        await svc.ToggleAsync(s.EventId, s.AliceId, s.TalkA);
        await svc.ToggleAsync(s.EventId, s.BobId, s.TalkB);

        var aliceIds = await svc.GetSavedSessionIdsAsync(s.EventId, s.AliceId);
        var bobIds = await svc.GetSavedSessionIdsAsync(s.EventId, s.BobId);

        Assert.Equal(new[] { s.TalkA }, aliceIds.OrderBy(x => x).ToArray());
        Assert.Equal(new[] { s.TalkB }, bobIds.OrderBy(x => x).ToArray());
        Assert.False(await svc.IsSavedAsync(s.EventId, s.AliceId, s.TalkB));
    }

    [Fact]
    public async Task Remove_is_idempotent()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = Svc(db);

        await svc.ToggleAsync(s.EventId, s.AliceId, s.TalkA);
        Assert.True(await svc.RemoveAsync(s.EventId, s.AliceId, s.TalkA));   // removed
        Assert.False(await svc.RemoveAsync(s.EventId, s.AliceId, s.TalkA));  // already gone → no-op
    }

    [Fact]
    public async Task Build_plan_orders_scheduled_first_and_joins_speakers()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = Svc(db);

        // Save out of order: the later talk, the unscheduled one, then the earlier.
        await svc.ToggleAsync(s.EventId, s.AliceId, s.TalkB);
        await svc.ToggleAsync(s.EventId, s.AliceId, s.Unscheduled);
        await svc.ToggleAsync(s.EventId, s.AliceId, s.TalkA);

        var plan = await svc.BuildPlanAsync(s.EventId, s.AliceId);

        Assert.False(plan.IsEmpty);
        Assert.Equal(3, plan.Sessions.Count);
        Assert.Equal(2, plan.ScheduledCount);
        // Scheduled first (Alpha 09:00, Beta 11:00), then the unscheduled Gamma.
        Assert.Equal(new[] { "Alpha Talk", "Beta Talk", "Gamma TBA" },
            plan.Sessions.Select(x => x.Title).ToArray());
        // Speaker names joined + alphabetised on the multi-speaker talk.
        Assert.Equal("Ann Apple, Zoe Zint", plan.Sessions[0].Speakers);
        Assert.False(plan.Sessions[2].HasTime);
    }

    [Fact]
    public async Task Dangling_saved_session_self_heals_out_of_the_plan()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = Svc(db);

        await svc.ToggleAsync(s.EventId, s.AliceId, s.TalkA);
        // A SavedSession row pointing at a session that no longer exists — the state
        // left behind when an organizer deletes a session (the FK is NoAction, so no
        // cascade removes the orphan on SQL Server). The plan join must drop it.
        db.SavedSessions.Add(new SavedSession
        {
            EventId = s.EventId, ParticipantId = s.AliceId, SessionId = 987654,
            CreatedAt = Now,
        });
        await db.SaveChangesAsync();

        var plan = await svc.BuildPlanAsync(s.EventId, s.AliceId);
        Assert.Single(plan.Sessions);
        Assert.Equal("Alpha Talk", plan.Sessions[0].Title);
    }

    [Fact]
    public async Task Empty_plan_is_empty()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var plan = await Svc(db).BuildPlanAsync(s.EventId, s.AliceId);
        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Sessions);
        Assert.Equal(0, plan.ScheduledCount);
    }

    // --- Calendar export (.ics) --------------------------------------------

    [Fact]
    public async Task Plan_ics_emits_one_vevent_per_scheduled_saved_talk_with_stable_uid()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = Svc(db);

        // Save two scheduled talks + the unscheduled one (which must be excluded).
        await svc.ToggleAsync(s.EventId, s.AliceId, s.TalkA);
        await svc.ToggleAsync(s.EventId, s.AliceId, s.TalkB);
        await svc.ToggleAsync(s.EventId, s.AliceId, s.Unscheduled);

        var ics = await svc.BuildPlanIcsAsync(
            s.EventId, s.AliceId, "Alice Adams", "alice@example.test", "hub.test");

        Assert.NotNull(ics);
        Assert.StartsWith("BEGIN:VCALENDAR", ics);
        Assert.Contains("METHOD:PUBLISH", ics);
        // Exactly two events (the unscheduled talk has no time → not on the calendar).
        Assert.Equal(2, CountOccurrences(ics!, "BEGIN:VEVENT"));
        // Stable, plan-scoped UIDs so a re-download updates rather than duplicates.
        Assert.Contains($"UID:plan-session:{s.TalkA}@hub.test", ics);
        Assert.Contains($"UID:plan-session:{s.TalkB}@hub.test", ics);
        Assert.DoesNotContain($"plan-session:{s.Unscheduled}@", ics);
        // Title (summary), room (location), and a same-day pop-up alarm.
        Assert.Contains("SUMMARY:Alpha Talk", ics);
        Assert.Contains("LOCATION:Room A", ics);
        Assert.Contains("BEGIN:VALARM", ics);
        Assert.Contains("TRIGGER:-P0D", ics);
        // The owner resolves to the signed-in participant (their own plan).
        Assert.Contains("alice@example.test", ics);
    }

    [Fact]
    public async Task Plan_ics_is_null_when_no_scheduled_talks()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = Svc(db);

        // Empty plan → nothing to export.
        Assert.Null(await svc.BuildPlanIcsAsync(
            s.EventId, s.AliceId, "Alice", "alice@example.test", "hub.test"));

        // A plan with ONLY an unscheduled talk → still nothing to put on a calendar.
        await svc.ToggleAsync(s.EventId, s.AliceId, s.Unscheduled);
        Assert.Null(await svc.BuildPlanIcsAsync(
            s.EventId, s.AliceId, "Alice", "alice@example.test", "hub.test"));
    }

    [Fact]
    public async Task Plan_ics_is_own_row_scoped()
    {
        using var db = TestDb.New();
        var s = await SeedAsync(db);
        var svc = Svc(db);

        // Alice saves a scheduled talk; Bob saved nothing.
        await svc.ToggleAsync(s.EventId, s.AliceId, s.TalkA);

        var alice = await svc.BuildPlanIcsAsync(
            s.EventId, s.AliceId, "Alice", "alice@example.test", "hub.test");
        var bob = await svc.BuildPlanIcsAsync(
            s.EventId, s.BobId, "Bob", "bob@example.test", "hub.test");

        Assert.NotNull(alice);
        Assert.Contains("Alpha Talk", alice!);
        // Bob's export never carries Alice's saved talk (and is null — he saved nothing).
        Assert.Null(bob);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    // --- Pure builder (no DbContext) ---------------------------------------

    [Fact]
    public void Builder_orders_scheduled_first_then_by_time_room_title()
    {
        var t = new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero);
        var rows = new[]
        {
            new RawSavedSession(3, "Zed", SessionType.CommunityTechSession,
                null, null, null, null, Array.Empty<string>()),
            new RawSavedSession(2, "Same Time Room B", SessionType.CommunityTechSession,
                "Room B", null, t, t.AddMinutes(50), new[] { "Bob", "Ann" }),
            new RawSavedSession(1, "Same Time Room A", SessionType.CommunityTechSession,
                "Room A", null, t, t.AddMinutes(50), Array.Empty<string>()),
        };

        var plan = AttendeePlanBuilder.Build(rows);

        Assert.Equal(new[] { "Same Time Room A", "Same Time Room B", "Zed" },
            plan.Sessions.Select(x => x.Title).ToArray());
        Assert.Equal("Ann, Bob", plan.Sessions[1].Speakers);     // alphabetised
        Assert.Equal("/Sessions/1", plan.Sessions[0].DetailUrl);
        Assert.Equal(2, plan.ScheduledCount);
    }

    [Fact]
    public void Builder_empty_input_is_empty()
    {
        var plan = AttendeePlanBuilder.Build(Array.Empty<RawSavedSession>());
        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Sessions);
    }
}
