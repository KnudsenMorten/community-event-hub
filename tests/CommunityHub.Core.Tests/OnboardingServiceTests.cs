using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
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

    private static async Task<Participant> SeedActiveRoleAsync(
        CommunityHubDbContext db, ParticipantRole role, string email,
        ParticipantLifecycleState lifecycle = ParticipantLifecycleState.Active)
    {
        var p = new Participant
        {
            EventId = EventId, Email = email, FullName = email.Split('@')[0],
            Role = role, IsActive = true, LifecycleState = lifecycle,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    [Fact]
    public async Task ResetStepForPersona_reopens_only_those_who_completed_it_and_raises_one_action_each()
    {
        using var db = NewDb();
        // Two speakers who finished Hotel + one speaker who did NOT.
        var a = await SeedActiveAsync(db, "a@example.test");                       // Speaker
        var b = await SeedActiveRoleAsync(db, ParticipantRole.Speaker, "b@example.test");
        var c = await SeedActiveRoleAsync(db, ParticipantRole.Speaker, "c@example.test");
        // A volunteer who finished Hotel must NOT be touched (different persona).
        var v = await SeedActiveRoleAsync(db, ParticipantRole.Volunteer, "v@example.test");
        var svc = NewSvc(db);

        await svc.MarkStepCompleteAsync(EventId, a.Id, OnboardingStep.Hotel);
        await svc.MarkStepCompleteAsync(EventId, b.Id, OnboardingStep.Hotel);
        await svc.MarkStepCompleteAsync(EventId, v.Id, OnboardingStep.Hotel);
        // c never did Hotel.

        var result = await svc.ResetStepForPersonaAsync(
            EventId, PersonaGroup.Speaker, OnboardingStep.Hotel);

        Assert.Equal(2, result.Reopened);        // a + b
        Assert.Equal(2, result.Candidates);
        Assert.False(result.IsNoOp);

        // a + b flipped back to 0; c was already 0; the volunteer is untouched.
        Assert.False((await db.Participants.FindAsync(a.Id))!.OnboardingCompleted_Hotel);
        Assert.False((await db.Participants.FindAsync(b.Id))!.OnboardingCompleted_Hotel);
        Assert.False((await db.Participants.FindAsync(c.Id))!.OnboardingCompleted_Hotel);
        Assert.True((await db.Participants.FindAsync(v.Id))!.OnboardingCompleted_Hotel);

        // One remind hand-off per re-opened person (a + b), none for the volunteer.
        var actions = await db.OrganizerActionItems
            .Where(x => x.EventId == EventId
                        && x.Type == OrganizerActionItemService.TypeOnboardingStepReset
                        && x.ResolvedAt == null)
            .ToListAsync();
        Assert.Equal(2, actions.Count);
        Assert.Contains(actions, x => x.ParticipantId == a.Id);
        Assert.Contains(actions, x => x.ParticipantId == b.Id);
        Assert.DoesNotContain(actions, x => x.ParticipantId == v.Id);
    }

    [Fact]
    public async Task ResetStepForPersona_is_a_noop_when_nobody_completed_the_step()
    {
        using var db = NewDb();
        await SeedActiveAsync(db, "a@example.test");   // Speaker, nothing done
        var svc = NewSvc(db);

        var result = await svc.ResetStepForPersonaAsync(
            EventId, PersonaGroup.Speaker, OnboardingStep.Hotel);

        Assert.True(result.IsNoOp);
        Assert.Equal(0, result.Reopened);
        Assert.Empty(await db.OrganizerActionItems.ToListAsync());
    }

    [Fact]
    public async Task ResetStepForPersona_is_a_noop_when_step_not_required_by_persona()
    {
        using var db = NewDb();
        // Bio is NOT a sponsor-required step. Even if the bit is somehow set, the
        // persona-not-required guard makes this a clean no-op.
        var s = await SeedActiveRoleAsync(db, ParticipantRole.Sponsor, "s@example.test");
        s.OnboardingCompleted_Bio = true;
        await db.SaveChangesAsync();
        var svc = NewSvc(db);

        var result = await svc.ResetStepForPersonaAsync(
            EventId, PersonaGroup.Sponsor, OnboardingStep.Bio);

        Assert.True(result.IsNoOp);
        Assert.Equal(0, result.Candidates);
        Assert.True((await db.Participants.FindAsync(s.Id))!.OnboardingCompleted_Bio); // untouched
        Assert.Empty(await db.OrganizerActionItems.ToListAsync());
    }

    [Fact]
    public async Task ResetStepForPersona_is_idempotent_on_a_second_run()
    {
        using var db = NewDb();
        var a = await SeedActiveAsync(db, "a@example.test");   // Speaker
        var svc = NewSvc(db);
        await svc.MarkStepCompleteAsync(EventId, a.Id, OnboardingStep.Swag);

        var first = await svc.ResetStepForPersonaAsync(EventId, PersonaGroup.Speaker, OnboardingStep.Swag);
        var second = await svc.ResetStepForPersonaAsync(EventId, PersonaGroup.Speaker, OnboardingStep.Swag);

        Assert.Equal(1, first.Reopened);
        Assert.True(second.IsNoOp);                            // already re-opened → no change
        // The upsert keeps a single open action item across both runs.
        var open = await db.OrganizerActionItems
            .CountAsync(x => x.Type == OrganizerActionItemService.TypeOnboardingStepReset
                             && x.ResolvedAt == null);
        Assert.Equal(1, open);
    }

    [Fact]
    public async Task ResetStepForPersona_covers_masterclass_speakers_and_excludes_other_editions()
    {
        using var db = NewDb();
        var s1 = await SeedActiveAsync(db, "s1@example.test");                                  // Speaker
        var mc = await SeedActiveRoleAsync(db, ParticipantRole.MasterclassSpeaker, "mc@example.test"); // same persona
        // Another edition's speaker with the step done must be untouched (scoping).
        db.Events.Add(new Event
        {
            Id = 2, Code = "T28", CommunityName = "T", DisplayName = "T 2028",
            StartDate = new DateOnly(2028, 2, 9), EndDate = new DateOnly(2028, 2, 10),
            LockDate = new DateOnly(2028, 1, 1), IsActive = true,
        });
        var other = new Participant
        {
            EventId = 2, Email = "other@example.test", FullName = "other",
            Role = ParticipantRole.Speaker, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
            OnboardingCompleted_Hotel = true,
        };
        db.Participants.Add(other);
        await db.SaveChangesAsync();
        var svc = NewSvc(db);
        await svc.MarkStepCompleteAsync(EventId, s1.Id, OnboardingStep.Hotel);
        await svc.MarkStepCompleteAsync(EventId, mc.Id, OnboardingStep.Hotel);

        var result = await svc.ResetStepForPersonaAsync(EventId, PersonaGroup.Speaker, OnboardingStep.Hotel);

        Assert.Equal(2, result.Reopened);   // the speaker + the masterclass speaker (same persona)
        // The other edition's speaker is untouched.
        Assert.True((await db.Participants.FindAsync(other.Id))!.OnboardingCompleted_Hotel);
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
    public async Task BuildPending_lists_only_people_not_fully_onboarded_with_missing_steps()
    {
        using var db = NewDb();
        // Speaker requires all 5 steps; finish only 2 → still pending, 3 missing.
        var pending = await SeedActiveAsync(db, "pending@example.test");
        // A second speaker who finishes EVERY step → fully onboarded, excluded.
        var done = new Participant
        {
            EventId = EventId, Email = "done@example.test", FullName = "done",
            Role = ParticipantRole.Speaker, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(done);
        await db.SaveChangesAsync();

        var svc = NewSvc(db);
        await svc.MarkStepCompleteAsync(EventId, pending.Id, OnboardingStep.Bio);
        await svc.MarkStepCompleteAsync(EventId, pending.Id, OnboardingStep.Picture);
        foreach (var step in new[]
        {
            OnboardingStep.Bio, OnboardingStep.Picture, OnboardingStep.Hotel,
            OnboardingStep.Appreciation, OnboardingStep.Swag,
        })
        {
            await svc.MarkStepCompleteAsync(EventId, done.Id, step);
        }

        var rows = await svc.BuildPendingAsync(EventId);

        var row = Assert.Single(rows);                       // only the pending speaker
        Assert.Equal(pending.Id, row.ParticipantId);
        Assert.Equal(2, row.DoneCount);
        Assert.Equal(5, row.RequiredCount);
        Assert.Equal(3, row.MissingSteps.Count);
        Assert.Contains(OnboardingStep.Hotel, row.MissingSteps);
        Assert.Contains(OnboardingStep.Appreciation, row.MissingSteps);
        Assert.Contains(OnboardingStep.Swag, row.MissingSteps);
        Assert.DoesNotContain(OnboardingStep.Bio, row.MissingSteps);
    }

    [Fact]
    public async Task BuildPending_includes_preselected_and_honours_the_persona_filter()
    {
        using var db = NewDb();
        await SeedActiveAsync(db, "speaker@example.test");          // Speaker, 0 done → pending
        // A pre-selected volunteer (queue shortlist) is still "pending".
        db.Participants.Add(new Participant
        {
            EventId = EventId, Email = "vol@example.test", FullName = "vol",
            Role = ParticipantRole.Volunteer, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Preselected,
        });
        await db.SaveChangesAsync();
        var svc = NewSvc(db);

        var all = await svc.BuildPendingAsync(EventId);
        Assert.Equal(2, all.Count);                                // both pending
        Assert.Contains(all, r => r.Stage == OnboardingStage.Preselected);

        var speakersOnly = await svc.BuildPendingAsync(EventId, PersonaGroup.Speaker);
        var only = Assert.Single(speakersOnly);
        Assert.Equal(PersonaGroup.Speaker, only.Persona);
    }

    [Fact]
    public async Task BuildPendingCsv_has_header_and_one_data_row_per_pending_person()
    {
        using var db = NewDb();
        await SeedActiveAsync(db, "pending@example.test");          // Speaker, 0 done
        var svc = NewSvc(db);

        var csv = await svc.BuildPendingCsvAsync(EventId);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("Name,Email,Persona,Stage,Done,Required,Percent,MissingSteps", lines[0]);
        Assert.Equal(2, lines.Length);                             // header + one pending row
        Assert.Contains("pending@example.test", lines[1]);
        Assert.Contains("0%", lines[1]);                           // nothing done yet
    }

    [Fact]
    public async Task BuildPendingCsv_is_empty_of_rows_when_everyone_is_onboarded()
    {
        using var db = NewDb();
        var p = await SeedActiveAsync(db, "done@example.test");     // Speaker
        var svc = NewSvc(db);
        foreach (var step in new[]
        {
            OnboardingStep.Bio, OnboardingStep.Picture, OnboardingStep.Hotel,
            OnboardingStep.Appreciation, OnboardingStep.Swag,
        })
        {
            await svc.MarkStepCompleteAsync(EventId, p.Id, step);
        }

        var csv = await svc.BuildPendingCsvAsync(EventId);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Single(lines);                                       // header only, no data rows
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
