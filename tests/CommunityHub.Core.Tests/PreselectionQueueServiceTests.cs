using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="PreselectionQueueService"/> — the organizer
/// pre-selection queue advance operations (Inactive → Preselected → Active),
/// single and multi-select. Uses the EF Core InMemory provider so the real
/// DbContext mapping + queries run. Asserts the lifecycle state model, the
/// activate-single / activate-multi paths, forward-only idempotency, the
/// IsActive coupling on activation, and event-scoping.
/// </summary>
public sealed class PreselectionQueueServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"queue-{Guid.NewGuid():N}")
            .Options);

    private static Participant P(
        int eventId, string email,
        ParticipantLifecycleState state = ParticipantLifecycleState.Inactive,
        ParticipantQueueSource source = ParticipantQueueSource.VolunteerInterestForm,
        bool active = false) =>
        new()
        {
            EventId = eventId,
            Email = email,
            FullName = email.Split('@')[0],
            Role = ParticipantRole.Volunteer,
            LifecycleState = state,
            QueueSource = source,
            IsActive = active,
        };

    [Fact]
    public void Default_new_participant_is_inactive_lifecycle()
    {
        // The domain default IS the queue entry point.
        var p = new Participant();
        Assert.Equal(ParticipantLifecycleState.Inactive, p.LifecycleState);
        Assert.Equal(ParticipantQueueSource.Manual, p.QueueSource);
    }

    [Fact]
    public async Task Queue_lists_only_non_active_rows_for_the_event()
    {
        using var db = NewDb();
        var inq = P(EventId, "a@example.test", ParticipantLifecycleState.Inactive);
        var pre = P(EventId, "b@example.test", ParticipantLifecycleState.Preselected);
        var act = P(EventId, "c@example.test", ParticipantLifecycleState.Active, active: true);
        var other = P(OtherEventId, "d@example.test", ParticipantLifecycleState.Inactive);
        db.Participants.AddRange(inq, pre, act, other);
        await db.SaveChangesAsync();

        var svc = new PreselectionQueueService(db);
        var queue = await svc.GetQueueAsync(EventId);

        Assert.Equal(2, queue.Count);                 // active + other-event excluded
        Assert.Contains(queue, q => q.Id == inq.Id);
        Assert.Contains(queue, q => q.Id == pre.Id);
    }

    [Fact]
    public async Task Queue_filters_by_source()
    {
        using var db = NewDb();
        var vol = P(EventId, "v@example.test", source: ParticipantQueueSource.VolunteerInterestForm);
        var spk = P(EventId, "s@example.test", source: ParticipantQueueSource.SessionizeSync);
        db.Participants.AddRange(vol, spk);
        await db.SaveChangesAsync();

        var svc = new PreselectionQueueService(db);
        var onlyVol = await svc.GetQueueAsync(EventId, ParticipantQueueSource.VolunteerInterestForm);

        Assert.Single(onlyVol);
        Assert.Equal(vol.Id, onlyVol[0].Id);
    }

    [Fact]
    public async Task Preselect_moves_inactive_to_preselected_single()
    {
        using var db = NewDb();
        var a = P(EventId, "a@example.test", ParticipantLifecycleState.Inactive);
        db.Participants.Add(a);
        await db.SaveChangesAsync();

        var svc = new PreselectionQueueService(db);
        var result = await svc.PreselectAsync(EventId, new[] { a.Id });

        Assert.Equal(1, result.Changed);
        var reloaded = (await db.Participants.FindAsync(a.Id))!;
        Assert.Equal(ParticipantLifecycleState.Preselected, reloaded.LifecycleState);
        Assert.False(reloaded.IsActive);              // preselect does NOT flip IsActive
    }

    [Fact]
    public async Task Activate_single_sets_active_and_flips_IsActive()
    {
        using var db = NewDb();
        var a = P(EventId, "a@example.test", ParticipantLifecycleState.Inactive, active: false);
        db.Participants.Add(a);
        await db.SaveChangesAsync();

        var svc = new PreselectionQueueService(db);
        var result = await svc.ActivateAsync(EventId, new[] { a.Id });

        Assert.Equal(1, result.Changed);
        var reloaded = (await db.Participants.FindAsync(a.Id))!;
        Assert.Equal(ParticipantLifecycleState.Active, reloaded.LifecycleState);
        Assert.True(reloaded.IsActive);               // activation satisfies the login gate
    }

    [Fact]
    public async Task Activate_multi_advances_every_selected_row()
    {
        using var db = NewDb();
        var a = P(EventId, "a@example.test", ParticipantLifecycleState.Inactive);
        var b = P(EventId, "b@example.test", ParticipantLifecycleState.Preselected);
        var c = P(EventId, "c@example.test", ParticipantLifecycleState.Active, active: true); // already active
        db.Participants.AddRange(a, b, c);
        await db.SaveChangesAsync();

        var svc = new PreselectionQueueService(db);
        var result = await svc.ActivateAsync(EventId, new[] { a.Id, b.Id, c.Id });

        Assert.Equal(3, result.Matched);
        Assert.Equal(2, result.Changed);              // a + b moved; c already active
        Assert.Equal(ParticipantLifecycleState.Active, (await db.Participants.FindAsync(a.Id))!.LifecycleState);
        Assert.Equal(ParticipantLifecycleState.Active, (await db.Participants.FindAsync(b.Id))!.LifecycleState);
    }

    [Fact]
    public async Task Advance_is_forward_only_never_demotes()
    {
        using var db = NewDb();
        var a = P(EventId, "a@example.test", ParticipantLifecycleState.Active, active: true);
        db.Participants.Add(a);
        await db.SaveChangesAsync();

        var svc = new PreselectionQueueService(db);
        // Ask to "preselect" an already-active row — must be a no-op, not a demote.
        var result = await svc.PreselectAsync(EventId, new[] { a.Id });

        Assert.Equal(0, result.Changed);
        Assert.Equal(ParticipantLifecycleState.Active,
            (await db.Participants.FindAsync(a.Id))!.LifecycleState);
    }

    [Fact]
    public async Task Advance_is_idempotent_on_second_run()
    {
        using var db = NewDb();
        var a = P(EventId, "a@example.test", ParticipantLifecycleState.Inactive);
        db.Participants.Add(a);
        await db.SaveChangesAsync();

        var svc = new PreselectionQueueService(db);
        var first = await svc.ActivateAsync(EventId, new[] { a.Id });
        var second = await svc.ActivateAsync(EventId, new[] { a.Id });

        Assert.Equal(1, first.Changed);
        Assert.Equal(0, second.Changed);
    }

    [Fact]
    public async Task Advance_never_crosses_event_boundaries()
    {
        using var db = NewDb();
        var mine = P(EventId, "mine@example.test", ParticipantLifecycleState.Inactive);
        var theirs = P(OtherEventId, "theirs@example.test", ParticipantLifecycleState.Inactive);
        db.Participants.AddRange(mine, theirs);
        await db.SaveChangesAsync();

        var svc = new PreselectionQueueService(db);
        var result = await svc.ActivateAsync(EventId, new[] { mine.Id, theirs.Id });

        Assert.Equal(1, result.Matched);
        Assert.Equal(1, result.Changed);
        Assert.Equal(1, result.Skipped(2));
        Assert.Equal(ParticipantLifecycleState.Inactive,
            (await db.Participants.FindAsync(theirs.Id))!.LifecycleState); // untouched
    }

    [Fact]
    public async Task Login_gate_predicate_requires_IsActive_AND_lifecycle_Active()
    {
        // Mirrors the exact predicate used by PinLoginService / PinIdentityProvider:
        // a person can sign in only when IsActive AND LifecycleState == Active.
        using var db = NewDb();
        var canLogin   = P(EventId, "ok@example.test",      ParticipantLifecycleState.Active,      active: true);
        var notActive  = P(EventId, "queued@example.test",  ParticipantLifecycleState.Inactive,    active: true);
        var preselected= P(EventId, "pre@example.test",     ParticipantLifecycleState.Preselected, active: true);
        var withdrawn  = P(EventId, "gone@example.test",    ParticipantLifecycleState.Active,      active: false);
        db.Participants.AddRange(canLogin, notActive, preselected, withdrawn);
        await db.SaveChangesAsync();

        var allowed = await db.Participants
            .Where(p => p.EventId == EventId
                        && p.IsActive
                        && p.LifecycleState == ParticipantLifecycleState.Active)
            .Select(p => p.Email)
            .ToListAsync();

        Assert.Equal(new[] { "ok@example.test" }, allowed);
    }

    [Fact]
    public async Task Empty_or_invalid_selection_is_a_safe_no_op()
    {
        using var db = NewDb();
        var a = P(EventId, "a@example.test", ParticipantLifecycleState.Inactive);
        db.Participants.Add(a);
        await db.SaveChangesAsync();

        var svc = new PreselectionQueueService(db);
        var empty = await svc.ActivateAsync(EventId, Array.Empty<int>());
        var bogus = await svc.ActivateAsync(EventId, new[] { 0, -3, 9999 });

        Assert.Equal(0, empty.Changed);
        Assert.Equal(0, bogus.Matched);
        Assert.Equal(ParticipantLifecycleState.Inactive,
            (await db.Participants.FindAsync(a.Id))!.LifecycleState);
    }
}
