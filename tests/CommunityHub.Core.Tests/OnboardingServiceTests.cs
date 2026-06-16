using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="OnboardingService"/> — the per-step completion
/// flags the wizard sets, the admin overview aggregation, and the organizer
/// "flip a flag back to 0" hook that hands off to the email system (raises an
/// <see cref="OrganizerActionItem"/>). EF Core InMemory provider + a fixed clock.
/// </summary>
public sealed class OnboardingServiceTests
{
    private const int EventId = 1;

    private static readonly DateTimeOffset Now =
        new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"onboard-{Guid.NewGuid():N}")
            .Options);

    private static OnboardingService NewSvc(CommunityHubDbContext db) =>
        new(db, new OrganizerActionItemService(db, new FixedClock(Now)), new FixedClock(Now));

    private static async Task<Participant> SeedActiveAsync(
        CommunityHubDbContext db, string email = "p@example.test")
    {
        db.Events.Add(new Event
        {
            Id = EventId,
            Code = "T27",
            CommunityName = "T",
            DisplayName = "T 2027",
            StartDate = new DateOnly(2027, 2, 9),
            EndDate = new DateOnly(2027, 2, 10),
            LockDate = new DateOnly(2027, 1, 1),
            IsActive = true,
        });
        var p = new Participant
        {
            EventId = EventId,
            Email = email,
            FullName = email.Split('@')[0],
            Role = ParticipantRole.Speaker,
            IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    [Fact]
    public async Task MarkStepComplete_sets_the_flag_and_is_idempotent()
    {
        using var db = NewDb();
        var p = await SeedActiveAsync(db);
        var svc = NewSvc(db);

        var first = await svc.MarkStepCompleteAsync(EventId, p.Id, OnboardingStep.Hotel);
        var second = await svc.MarkStepCompleteAsync(EventId, p.Id, OnboardingStep.Hotel);

        Assert.True(first);
        Assert.False(second);                          // already done → no change
        Assert.True((await db.Participants.FindAsync(p.Id))!.OnboardingCompleted_Hotel);
    }

    [Fact]
    public async Task MarkStepComplete_each_step_maps_to_its_own_flag()
    {
        using var db = NewDb();
        var p = await SeedActiveAsync(db);
        var svc = NewSvc(db);

        await svc.MarkStepCompleteAsync(EventId, p.Id, OnboardingStep.Bio);
        await svc.MarkStepCompleteAsync(EventId, p.Id, OnboardingStep.Picture);
        await svc.MarkStepCompleteAsync(EventId, p.Id, OnboardingStep.Hotel);
        await svc.MarkStepCompleteAsync(EventId, p.Id, OnboardingStep.Appreciation);
        await svc.MarkStepCompleteAsync(EventId, p.Id, OnboardingStep.Swag);

        var r = (await db.Participants.FindAsync(p.Id))!;
        Assert.True(r.OnboardingCompleted_Bio);
        Assert.True(r.OnboardingCompleted_Picture);
        Assert.True(r.OnboardingCompleted_Hotel);
        Assert.True(r.OnboardingCompleted_Appreciation);
        Assert.True(r.OnboardingCompleted_Swag);
        Assert.True(r.IsFullyOnboarded);
    }

    [Fact]
    public async Task ResetStep_flips_flag_to_zero_and_raises_email_handoff_action()
    {
        using var db = NewDb();
        var p = await SeedActiveAsync(db);
        var svc = NewSvc(db);
        await svc.MarkStepCompleteAsync(EventId, p.Id, OnboardingStep.Hotel);

        var reset = await svc.ResetStepAsync(EventId, p.Id, OnboardingStep.Hotel);

        Assert.True(reset);
        Assert.False((await db.Participants.FindAsync(p.Id))!.OnboardingCompleted_Hotel);

        // The email-system hand-off: exactly one open action item of the reset type.
        var actions = await db.OrganizerActionItems
            .Where(a => a.EventId == EventId
                        && a.Type == OrganizerActionItemService.TypeOnboardingStepReset
                        && a.ResolvedAt == null)
            .ToListAsync();
        Assert.Single(actions);
        Assert.Equal(p.Id, actions[0].ParticipantId);
    }

    [Fact]
    public async Task ResetStep_on_an_incomplete_step_is_a_no_op_and_raises_nothing()
    {
        using var db = NewDb();
        var p = await SeedActiveAsync(db);
        var svc = NewSvc(db);

        var reset = await svc.ResetStepAsync(EventId, p.Id, OnboardingStep.Swag);

        Assert.False(reset);
        Assert.Empty(await db.OrganizerActionItems.ToListAsync());
    }

    [Fact]
    public async Task ResetStep_twice_keeps_one_open_action_item()
    {
        using var db = NewDb();
        var p = await SeedActiveAsync(db);
        var svc = NewSvc(db);
        await svc.MarkStepCompleteAsync(EventId, p.Id, OnboardingStep.Hotel);

        await svc.ResetStepAsync(EventId, p.Id, OnboardingStep.Hotel);
        // Re-complete then re-reset: the upsert keeps a single open row per pair.
        await svc.MarkStepCompleteAsync(EventId, p.Id, OnboardingStep.Hotel);
        await svc.ResetStepAsync(EventId, p.Id, OnboardingStep.Hotel);

        var open = await db.OrganizerActionItems
            .CountAsync(a => a.Type == OrganizerActionItemService.TypeOnboardingStepReset
                             && a.ResolvedAt == null);
        Assert.Equal(1, open);
    }

    [Fact]
    public async Task BuildOverview_reports_per_step_done_total_and_grid()
    {
        using var db = NewDb();
        var a = await SeedActiveAsync(db, "a@example.test");   // Speaker (all 5 required)
        var b = new Participant
        {
            EventId = EventId, Email = "b@example.test", FullName = "b",
            Role = ParticipantRole.Volunteer, IsActive = true,  // crew: hotel/appr/swag only
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(b);
        await db.SaveChangesAsync();

        var svc = NewSvc(db);
        await svc.MarkStepCompleteAsync(EventId, a.Id, OnboardingStep.Bio);
        await svc.MarkStepCompleteAsync(EventId, a.Id, OnboardingStep.Picture);
        await svc.MarkStepCompleteAsync(EventId, a.Id, OnboardingStep.Hotel);
        await svc.MarkStepCompleteAsync(EventId, a.Id, OnboardingStep.Appreciation);
        await svc.MarkStepCompleteAsync(EventId, a.Id, OnboardingStep.Swag); // speaker fully onboarded
        await svc.MarkStepCompleteAsync(EventId, b.Id, OnboardingStep.Swag); // volunteer: only swag

        var overview = await svc.BuildOverviewAsync(EventId);

        Assert.Equal(2, overview.TotalParticipants);
        Assert.Equal(1, overview.FullyOnboarded);      // only the speaker finished its set

        // Bio is required for the speaker only → total=1, done=1.
        var bioStat = overview.StepStats.Single(s => s.Step == OnboardingStep.Bio);
        Assert.Equal(1, bioStat.Done);
        Assert.Equal(1, bioStat.Total);

        // Swag is required for BOTH personas → total=2, both did it → done=2.
        var swagStat = overview.StepStats.Single(s => s.Step == OnboardingStep.Swag);
        Assert.Equal(2, swagStat.Done);
        Assert.Equal(2, swagStat.Total);

        Assert.Equal(2, overview.Rows.Count);
    }

    [Fact]
    public async Task BuildOverview_excludes_queue_rows_not_yet_active()
    {
        using var db = NewDb();
        await SeedActiveAsync(db, "active@example.test");
        db.Participants.Add(new Participant
        {
            EventId = EventId, Email = "queued@example.test", FullName = "q",
            Role = ParticipantRole.Volunteer, IsActive = false,
            LifecycleState = ParticipantLifecycleState.Inactive,
        });
        await db.SaveChangesAsync();

        var overview = await NewSvc(db).BuildOverviewAsync(EventId);

        Assert.Equal(1, overview.TotalParticipants);   // queued row excluded
    }
}
