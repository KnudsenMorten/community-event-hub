using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §499 — the two meanings of "inactive", pinned.
///
/// <para>The operator's question was <i>"how can we differentiate the 2?"</i>: someone who arrived
/// inactive from Sessionize / a volunteer form and belongs in the pending queue, versus someone who
/// WAS active and left (cancelled or reassigned ticket, organizer withdrawal) and must stop being
/// chased. Getting this wrong in either direction is a real failure — mailing someone who has left,
/// or emptying the queue an organizer works from.</para>
///
/// <para>These tests encode each PATH's actual fingerprint rather than a tidy invented one, so they
/// fail if a sync's behaviour changes underneath the predicate.</para>
/// </summary>
public sealed class ParticipantLifecycleScopeTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"lifecycle-{Guid.NewGuid():N}")
            .Options);

    /// <summary>How SessionizeImportService / the volunteer form create a row.</summary>
    private static Participant NeverActivated() => new()
    {
        EventId = 1, Email = "new@example.test", FullName = "Newcomer",
        Role = ParticipantRole.Speaker,
        IsActive = false,
        LifecycleState = ParticipantLifecycleState.Inactive,
        DeactivatedByOrganizerAt = null,
    };

    /// <summary>How AttendeeTicketSyncService leaves a cancelled / reassigned ticket holder:
    /// it flips the login gate ONLY and deliberately leaves LifecycleState alone.</summary>
    private static Participant TicketCancelled() => new()
    {
        EventId = 1, Email = "left@example.test", FullName = "Cancelled",
        Role = ParticipantRole.Attendee,
        IsActive = false,
        LifecycleState = ParticipantLifecycleState.Active,   // ← the tell: they WERE in
        DeactivatedByOrganizerAt = null,
    };

    /// <summary>How ParticipantDeactivationService leaves an organizer withdrawal (§253 G8).</summary>
    private static Participant OrganizerDeactivated() => new()
    {
        EventId = 1, Email = "withdrawn@example.test", FullName = "Withdrawn",
        Role = ParticipantRole.Volunteer,
        IsActive = false,
        LifecycleState = ParticipantLifecycleState.Inactive,
        DeactivatedByOrganizerAt = DateTimeOffset.UtcNow,     // ← the tombstone
    };

    private static Participant FullyActive() => new()
    {
        EventId = 1, Email = "in@example.test", FullName = "Active",
        Role = ParticipantRole.Attendee,
        IsActive = true,
        LifecycleState = ParticipantLifecycleState.Active,
    };

    [Fact]
    public void A_cancelled_or_reassigned_ticket_counts_as_DROPPED_OUT()
    {
        // The case that started this: the seat is gone, so reminders must stop. The ONLY thing
        // separating them from a newcomer is that LifecycleState stayed Active.
        var p = TicketCancelled();
        Assert.True(p.IsDroppedOut());
        Assert.False(p.IsAwaitingOnboarding());
        Assert.False(p.IsRemindable());
    }

    [Fact]
    public void An_organizer_withdrawal_counts_as_DROPPED_OUT()
    {
        var p = OrganizerDeactivated();
        Assert.True(p.IsDroppedOut());
        Assert.False(p.IsAwaitingOnboarding());
        Assert.False(p.IsRemindable());
    }

    [Fact]
    public void A_sessionize_or_volunteer_newcomer_is_AWAITING_ONBOARDING_not_dropped_out()
    {
        // The direction that matters most for the organizer screens: if this were treated as a
        // drop-out, the pending queue would empty out and the review work would vanish.
        var p = NeverActivated();
        Assert.True(p.IsAwaitingOnboarding());
        Assert.False(p.IsDroppedOut());
        Assert.False(p.IsRemindable());
    }

    [Fact]
    public void Only_a_fully_active_participant_is_remindable()
    {
        Assert.True(FullyActive().IsRemindable());
        // Awaiting onboarding cannot sign in either, so a "go and do this in the hub" reminder
        // would be equally useless — remindable is NOT merely "not dropped out".
        Assert.False(NeverActivated().IsRemindable());
    }

    [Fact]
    public void The_two_inactive_categories_never_overlap_and_cover_every_inactive_row()
    {
        // A row that is neither, or both, would mean a person silently missing from every screen
        // or appearing on contradictory ones.
        foreach (var p in new[] { NeverActivated(), TicketCancelled(), OrganizerDeactivated() })
        {
            Assert.True(p.IsDroppedOut() ^ p.IsAwaitingOnboarding(),
                $"{p.Email} must be exactly one of dropped-out / awaiting-onboarding");
        }
        // And an active person is in neither bucket.
        var active = FullyActive();
        Assert.False(active.IsDroppedOut());
        Assert.False(active.IsAwaitingOnboarding());
    }

    [Fact]
    public async Task The_query_form_matches_the_in_memory_form()
    {
        // The whole point of the file is ONE definition. If the EF predicate and the in-memory
        // helper disagreed, callers would get different answers depending on whether the row was
        // already materialised — which is exactly the §428 drift this exists to prevent.
        using var db = NewDb();
        db.Participants.AddRange(NeverActivated(), TicketCancelled(), OrganizerDeactivated(), FullyActive());
        await db.SaveChangesAsync();

        var remindable = await db.Participants.Remindable().Select(p => p.Email).ToListAsync();
        var dropped = await db.Participants.DroppedOut().Select(p => p.Email).ToListAsync();
        var awaiting = await db.Participants.AwaitingOnboarding().Select(p => p.Email).ToListAsync();

        Assert.Equal(new[] { "in@example.test" }, remindable);
        Assert.Equal(new[] { "left@example.test", "withdrawn@example.test" }, dropped.OrderBy(e => e));
        Assert.Equal(new[] { "new@example.test" }, awaiting);

        var all = await db.Participants.ToListAsync();
        Assert.Equal(dropped.OrderBy(e => e), all.Where(p => p.IsDroppedOut()).Select(p => p.Email).OrderBy(e => e));
        Assert.Equal(awaiting, all.Where(p => p.IsAwaitingOnboarding()).Select(p => p.Email));
        Assert.Equal(remindable, all.Where(p => p.IsRemindable()).Select(p => p.Email));
    }
}
