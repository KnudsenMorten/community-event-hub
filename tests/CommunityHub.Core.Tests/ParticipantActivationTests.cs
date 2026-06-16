using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests the single lifecycle-correct "active" definition (#39):
/// <c>active = IsActive AND LifecycleState == Active</c>. Covers the in-memory
/// predicate, the EF-translatable expression (the grid's active/inactive
/// filter), and that deactivating (IsActive=false) drops a row from active.
/// </summary>
public sealed class ParticipantActivationTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"activation-{Guid.NewGuid():N}")
            .Options);

    private static Participant P(
        string email, bool isActive, ParticipantLifecycleState state) => new()
    {
        EventId = EventId,
        Email = email,
        FullName = email.Split('@')[0],
        Role = ParticipantRole.Attendee,
        IsActive = isActive,
        LifecycleState = state,
    };

    [Theory]
    [InlineData(true,  ParticipantLifecycleState.Active,      true)]
    [InlineData(false, ParticipantLifecycleState.Active,      false)] // withdrawn
    [InlineData(true,  ParticipantLifecycleState.Preselected, false)] // not yet activated
    [InlineData(true,  ParticipantLifecycleState.Inactive,    false)] // queue entry
    [InlineData(false, ParticipantLifecycleState.Inactive,    false)]
    public void IsActive_requires_both_flags(
        bool isActive, ParticipantLifecycleState state, bool expected)
    {
        var p = P("x@example.test", isActive, state);
        Assert.Equal(expected, ParticipantActivation.IsActive(p));
    }

    [Fact]
    public async Task Active_filter_returns_only_lifecycle_active_rows()
    {
        using var db = NewDb();
        db.Participants.AddRange(
            P("ok@example.test",        true,  ParticipantLifecycleState.Active),
            P("withdrawn@example.test", false, ParticipantLifecycleState.Active),
            P("queued@example.test",    true,  ParticipantLifecycleState.Preselected));
        await db.SaveChangesAsync();

        // Mirrors the grid's "active" branch.
        var active = await db.Participants
            .Where(p => p.EventId == EventId)
            .Where(ParticipantActivation.IsActiveExpr)
            .Select(p => p.Email)
            .ToListAsync();

        Assert.Equal(new[] { "ok@example.test" }, active);
    }

    [Fact]
    public async Task Inactive_filter_is_the_complement_show_inactive_toggle()
    {
        using var db = NewDb();
        db.Participants.AddRange(
            P("ok@example.test",        true,  ParticipantLifecycleState.Active),
            P("withdrawn@example.test", false, ParticipantLifecycleState.Active),
            P("queued@example.test",    true,  ParticipantLifecycleState.Preselected));
        await db.SaveChangesAsync();

        // Mirrors the grid's "inactive" branch (NOT lifecycle-active).
        var inactive = await db.Participants
            .Where(p => p.EventId == EventId)
            .Where(p => !(p.IsActive && p.LifecycleState == ParticipantLifecycleState.Active))
            .Select(p => p.Email)
            .OrderBy(e => e)
            .ToListAsync();

        Assert.Equal(new[] { "queued@example.test", "withdrawn@example.test" }, inactive);
    }

    [Fact]
    public async Task Deactivating_drops_a_row_from_the_active_set()
    {
        using var db = NewDb();
        var p = P("ok@example.test", true, ParticipantLifecycleState.Active);
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        Assert.True(ParticipantActivation.IsActive(p));

        p.IsActive = false; // the cancellation switch
        await db.SaveChangesAsync();

        var stillActive = await db.Participants
            .Where(ParticipantActivation.IsActiveExpr)
            .AnyAsync(x => x.Id == p.Id);
        Assert.False(stillActive);
    }
}
