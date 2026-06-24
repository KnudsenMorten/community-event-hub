using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="ParticipantBulkOperationService"/> — the
/// organizer multi-select bulk actions (deactivate / reactivate / change-role)
/// over a selected set of participants. Uses the EF Core InMemory provider so
/// the real DbContext mapping + queries run, no SQL. Asserts the three
/// invariants the page relies on: event-scoping, idempotency, and an accurate
/// changed-count.
/// </summary>
public sealed class ParticipantBulkOperationServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"bulkops-{Guid.NewGuid():N}")
            .Options);

    private static Participant P(
        int eventId, string email, ParticipantRole role = ParticipantRole.Attendee,
        bool active = true) =>
        new()
        {
            EventId = eventId,
            Email = email,
            FullName = email.Split('@')[0],
            Role = role,
            // "active" means the LIFECYCLE-CORRECT state (IsActive AND
            // LifecycleState == Active), so seed both consistently.
            IsActive = active,
            LifecycleState = active
                ? ParticipantLifecycleState.Active
                : ParticipantLifecycleState.Inactive,
        };

    [Fact]
    public async Task Reactivate_activates_a_synced_row_with_IsActive_true_but_lifecycle_not_active()
    {
        // The real bug (operator 2026-06-23): synced sponsors had IsActive=true but
        // LifecycleState != Active, so they read as "Inactive" yet reactivate said
        // "already active" and never cleared the lifecycle gate.
        using var db = NewDb();
        var synced = new Participant
        {
            EventId = EventId, Email = "synced@example.test", FullName = "Synced",
            Role = ParticipantRole.Sponsor,
            IsActive = true,
            LifecycleState = ParticipantLifecycleState.Inactive,
        };
        db.Participants.Add(synced);
        await db.SaveChangesAsync();

        var svc = new ParticipantBulkOperationService(db);
        var result = await svc.ReactivateAsync(EventId, new[] { synced.Id }, default);

        Assert.Equal(1, result.Matched);
        Assert.Equal(1, result.Changed);
        var reloaded = (await db.Participants.FindAsync(synced.Id))!;
        Assert.True(ParticipantActivation.IsActive(reloaded));
        Assert.Equal(ParticipantLifecycleState.Active, reloaded.LifecycleState);
    }

    [Fact]
    public async Task Deactivate_flips_only_active_rows_and_counts_real_changes()
    {
        using var db = NewDb();
        var a = P(EventId, "a@example.test", active: true);
        var b = P(EventId, "b@example.test", active: true);
        var c = P(EventId, "c@example.test", active: false); // already inactive
        db.Participants.AddRange(a, b, c);
        await db.SaveChangesAsync();

        var svc = new ParticipantBulkOperationService(db);
        var result = await svc.DeactivateAsync(EventId, new[] { a.Id, b.Id, c.Id }, default);

        Assert.Equal(3, result.Matched);
        Assert.Equal(2, result.Changed);           // only a + b moved; c was already inactive
        Assert.False((await db.Participants.FindAsync(a.Id))!.IsActive);
        Assert.False((await db.Participants.FindAsync(b.Id))!.IsActive);
        Assert.False((await db.Participants.FindAsync(c.Id))!.IsActive);
    }

    [Fact]
    public async Task Deactivate_is_idempotent_on_second_run()
    {
        using var db = NewDb();
        var a = P(EventId, "a@example.test", active: true);
        db.Participants.Add(a);
        await db.SaveChangesAsync();

        var svc = new ParticipantBulkOperationService(db);
        var first = await svc.DeactivateAsync(EventId, new[] { a.Id }, default);
        var second = await svc.DeactivateAsync(EventId, new[] { a.Id }, default);

        Assert.Equal(1, first.Changed);
        Assert.Equal(0, second.Changed);           // nothing left to change
        Assert.Equal(1, second.Matched);
    }

    [Fact]
    public async Task Reactivate_flips_only_inactive_rows()
    {
        using var db = NewDb();
        var a = P(EventId, "a@example.test", active: false);
        var b = P(EventId, "b@example.test", active: true);  // already active
        db.Participants.AddRange(a, b);
        await db.SaveChangesAsync();

        var svc = new ParticipantBulkOperationService(db);
        var result = await svc.ReactivateAsync(EventId, new[] { a.Id, b.Id }, default);

        Assert.Equal(2, result.Matched);
        Assert.Equal(1, result.Changed);
        Assert.True((await db.Participants.FindAsync(a.Id))!.IsActive);
    }

    [Fact]
    public async Task SetRing_assigns_ring_and_skips_rows_already_on_that_ring()
    {
        using var db = NewDb();
        var a = P(EventId, "a@example.test");           // default ring
        var b = P(EventId, "b@example.test");
        b.Ring = Ring.Ring1;                              // already on the target
        db.Participants.AddRange(a, b);
        await db.SaveChangesAsync();

        var svc = new ParticipantBulkOperationService(db);
        var result = await svc.SetRingAsync(EventId, new[] { a.Id, b.Id }, Ring.Ring1, default);

        Assert.Equal(2, result.Matched);
        Assert.Equal(1, result.Changed);                 // only a moved
        Assert.Equal(Ring.Ring1, (await db.Participants.FindAsync(a.Id))!.Ring);
        Assert.Equal(Ring.Ring1, (await db.Participants.FindAsync(b.Id))!.Ring);
    }

    [Fact]
    public async Task SetRing_is_event_scoped()
    {
        using var db = NewDb();
        var mine = P(EventId, "mine@example.test");
        var theirs = P(OtherEventId, "theirs@example.test");
        db.Participants.AddRange(mine, theirs);
        await db.SaveChangesAsync();

        var svc = new ParticipantBulkOperationService(db);
        var result = await svc.SetRingAsync(EventId, new[] { mine.Id, theirs.Id }, Ring.Ring2, default);

        Assert.Equal(1, result.Matched);
        Assert.Equal(Ring.Ring2, (await db.Participants.FindAsync(mine.Id))!.Ring);
        Assert.NotEqual(Ring.Ring2, (await db.Participants.FindAsync(theirs.Id))!.Ring);
    }

    [Fact]
    public async Task ChangeRole_assigns_role_and_skips_rows_already_in_role()
    {
        using var db = NewDb();
        var a = P(EventId, "a@example.test", role: ParticipantRole.Attendee);
        var b = P(EventId, "b@example.test", role: ParticipantRole.Volunteer); // already target
        db.Participants.AddRange(a, b);
        await db.SaveChangesAsync();

        var svc = new ParticipantBulkOperationService(db);
        var result = await svc.ChangeRoleAsync(
            EventId, new[] { a.Id, b.Id }, ParticipantRole.Volunteer, default);

        Assert.Equal(2, result.Matched);
        Assert.Equal(1, result.Changed);
        Assert.Equal(ParticipantRole.Volunteer, (await db.Participants.FindAsync(a.Id))!.Role);
    }

    [Fact]
    public async Task ChangeRole_does_not_touch_IsActive()
    {
        using var db = NewDb();
        var a = P(EventId, "a@example.test", role: ParticipantRole.Attendee, active: false);
        db.Participants.Add(a);
        await db.SaveChangesAsync();

        var svc = new ParticipantBulkOperationService(db);
        await svc.ChangeRoleAsync(EventId, new[] { a.Id }, ParticipantRole.Speaker, default);

        var reloaded = (await db.Participants.FindAsync(a.Id))!;
        Assert.Equal(ParticipantRole.Speaker, reloaded.Role);
        Assert.False(reloaded.IsActive);           // role change left active flag alone
    }

    [Fact]
    public async Task Operations_never_cross_event_boundaries()
    {
        using var db = NewDb();
        var mine = P(EventId, "mine@example.test", active: true);
        var theirs = P(OtherEventId, "theirs@example.test", active: true);
        db.Participants.AddRange(mine, theirs);
        await db.SaveChangesAsync();

        var svc = new ParticipantBulkOperationService(db);
        // Ask to deactivate BOTH ids, but scoped to EventId.
        var result = await svc.DeactivateAsync(EventId, new[] { mine.Id, theirs.Id }, default);

        Assert.Equal(1, result.Matched);           // only mine resolved in this event
        Assert.Equal(1, result.Changed);
        Assert.Equal(1, result.Skipped(2));        // theirs counted as not-found
        Assert.False((await db.Participants.FindAsync(mine.Id))!.IsActive);
        Assert.True((await db.Participants.FindAsync(theirs.Id))!.IsActive); // untouched
    }

    [Fact]
    public async Task Empty_or_invalid_selection_is_a_safe_no_op()
    {
        using var db = NewDb();
        var a = P(EventId, "a@example.test", active: true);
        db.Participants.Add(a);
        await db.SaveChangesAsync();

        var svc = new ParticipantBulkOperationService(db);
        var empty = await svc.DeactivateAsync(EventId, Array.Empty<int>(), default);
        var bogus = await svc.DeactivateAsync(EventId, new[] { 0, -5, 9999 }, default);

        Assert.Equal(0, empty.Matched);
        Assert.Equal(0, empty.Changed);
        Assert.Equal(0, bogus.Matched);            // 0/-5 dropped, 9999 doesn't exist
        Assert.Equal(0, bogus.Changed);
        Assert.True((await db.Participants.FindAsync(a.Id))!.IsActive); // untouched
    }

    [Fact]
    public async Task Duplicate_ids_in_selection_are_collapsed()
    {
        using var db = NewDb();
        var a = P(EventId, "a@example.test", active: true);
        db.Participants.Add(a);
        await db.SaveChangesAsync();

        var svc = new ParticipantBulkOperationService(db);
        var result = await svc.DeactivateAsync(EventId, new[] { a.Id, a.Id, a.Id }, default);

        Assert.Equal(1, result.Matched);           // de-duped to one
        Assert.Equal(1, result.Changed);
    }
}
