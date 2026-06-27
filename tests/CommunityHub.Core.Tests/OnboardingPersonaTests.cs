using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for the PERSONA-aware onboarding: each persona's required-step
/// set (<see cref="OnboardingStepSets"/>), persona-aware completion + timestamps
/// in <see cref="OnboardingService"/>, the stage derivation
/// (Pre-selected / Invited / In-progress / Completed) and the dashboard
/// by-stage counts + persona filter. EF Core InMemory + a fixed clock.
/// </summary>
public sealed class OnboardingPersonaTests
{
    private const int EventId = 1;

    private static readonly DateTimeOffset Now =
        new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"persona-{Guid.NewGuid():N}")
            .Options);

    private static OnboardingService NewSvc(CommunityHubDbContext db) =>
        new(db, new OrganizerActionItemService(db, new FixedClock(Now)), new FixedClock(Now));

    private static async Task SeedEventAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "T27", CommunityName = "T", DisplayName = "T 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            LockDate = new DateOnly(2027, 1, 1), IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Participant> AddAsync(
        CommunityHubDbContext db, string email, ParticipantRole role,
        ParticipantLifecycleState state = ParticipantLifecycleState.Active)
    {
        var p = new Participant
        {
            EventId = EventId, Email = email, FullName = email.Split('@')[0],
            Role = role,
            IsActive = state == ParticipantLifecycleState.Active,
            LifecycleState = state,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    // ----- required-step sets ----------------------------------------------

    [Fact]
    public void Speaker_requires_all_five_steps()
    {
        var steps = OnboardingStepSets.For(ParticipantRole.Speaker);
        Assert.Equal(5, steps.Count);
        Assert.Contains(OnboardingStep.Bio, steps);
        Assert.Contains(OnboardingStep.Picture, steps);
    }

    [Fact]
    public void Sponsor_only_requires_appreciation_and_swag_not_bio_or_hotel()
    {
        // OnboardingStepSets still drives the GENERIC onboarding wizard for sponsors.
        // The ORGANIZER dashboard, however, no longer derives sponsor completion from
        // this set — it tracks the sponsor wizard's real data (company info / logos /
        // booth members); see Sponsor_completes_from_wizard_data_not_appreciation_or_swag.
        var steps = OnboardingStepSets.For(ParticipantRole.Sponsor);
        Assert.Equal(new[] { OnboardingStep.Appreciation, OnboardingStep.Swag }, steps);
        Assert.DoesNotContain(OnboardingStep.Bio, steps);
        Assert.DoesNotContain(OnboardingStep.Hotel, steps);
    }

    [Fact]
    public void Volunteer_and_media_require_crew_steps_no_bio()
    {
        var vol = OnboardingStepSets.For(ParticipantRole.Volunteer);
        var media = OnboardingStepSets.For(ParticipantRole.Media);
        Assert.Equal(vol, media);  // media-team == volunteer crew set
        Assert.DoesNotContain(OnboardingStep.Bio, vol);
        Assert.Contains(OnboardingStep.Hotel, vol);
    }

    // ----- persona-aware completion + timestamps ---------------------------

    [Fact]
    public async Task Sponsor_completes_from_wizard_data_not_appreciation_or_swag()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var sponsor = await AddAsync(db, "s@example.test", ParticipantRole.Sponsor);
        sponsor.SponsorCompanyId = "1001";
        // Sponsor onboarding tracks the sponsor WIZARD's real data: company info +
        // logos (no booth ⇒ those two are the whole set). Appreciation/Swag are
        // irrelevant to a sponsor and must NOT move the dashboard.
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "1001",
            WebsiteUrl = "https://example.test",
            LogoRasterPath = "uploads/sponsors/1001/logo.png",
            SponsorPackage = SponsorPackage.Silver,   // digital, no booth
        });
        await db.SaveChangesAsync();
        var svc = NewSvc(db);

        // Even with Appreciation + Swag marked, that does not contribute.
        await svc.MarkStepCompleteAsync(EventId, sponsor.Id, OnboardingStep.Appreciation);
        await svc.MarkStepCompleteAsync(EventId, sponsor.Id, OnboardingStep.Swag);

        var overview = await svc.BuildOverviewAsync(EventId);
        var row = overview.Rows.Single(r => r.ParticipantId == sponsor.Id);
        Assert.True(row.IsComplete);                       // company info + logos done
        Assert.Equal(2, row.RequiredCount);                // not the Appreciation/Swag set
        Assert.Equal(OnboardingStage.Completed, row.Stage);
        Assert.Equal(100, row.Percent);
        Assert.Equal(1, overview.FullyOnboarded);
    }

    [Fact]
    public async Task Sponsor_with_no_wizard_data_is_invited_not_complete()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var sponsor = await AddAsync(db, "s@example.test", ParticipantRole.Sponsor);
        var svc = NewSvc(db);

        // Appreciation/Swag are the unreachable steps that used to pin sponsors at
        // 0% — they must NOT make a sponsor "complete" now.
        await svc.MarkStepCompleteAsync(EventId, sponsor.Id, OnboardingStep.Appreciation);
        await svc.MarkStepCompleteAsync(EventId, sponsor.Id, OnboardingStep.Swag);

        var overview = await svc.BuildOverviewAsync(EventId);
        var row = overview.Rows.Single(r => r.ParticipantId == sponsor.Id);
        Assert.False(row.IsComplete);
        Assert.Equal(OnboardingStage.Invited, row.Stage);
        Assert.Equal(0, row.DoneCount);
        Assert.Equal(2, row.RequiredCount);                // company info + logos pending
    }

    [Fact]
    public async Task Exhibitor_sponsor_also_requires_booth_members()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var sponsor = await AddAsync(db, "ex@example.test", ParticipantRole.Sponsor);
        sponsor.SponsorCompanyId = "2002";
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "2002",
            CompanyDescription = "We make widgets.",
            LogoVectorPath = "uploads/sponsors/2002/logo.eps",
            SponsorPackage = SponsorPackage.Gold,     // booth ⇒ booth members also required
        });
        await db.SaveChangesAsync();
        var svc = NewSvc(db);

        // Company info + logos done, but no booth members yet ⇒ 2 of 3.
        var before = await svc.BuildOverviewAsync(EventId);
        var rowBefore = before.Rows.Single(r => r.ParticipantId == sponsor.Id);
        Assert.False(rowBefore.IsComplete);
        Assert.Equal(2, rowBefore.DoneCount);
        Assert.Equal(3, rowBefore.RequiredCount);
        Assert.Equal(OnboardingStage.InProgress, rowBefore.Stage);

        db.SponsorBoothMembers.Add(new SponsorBoothMember
        {
            EventId = EventId, SponsorCompanyId = "2002",
            FirstName = "Booth", LastName = "Staff", Email = "booth@example.test",
        });
        await db.SaveChangesAsync();

        var after = await svc.BuildOverviewAsync(EventId);
        var rowAfter = after.Rows.Single(r => r.ParticipantId == sponsor.Id);
        Assert.True(rowAfter.IsComplete);
        Assert.Equal(3, rowAfter.DoneCount);
        Assert.Equal(OnboardingStage.Completed, rowAfter.Stage);
    }

    [Fact]
    public async Task MarkStepComplete_stamps_a_timestamp_and_reset_clears_it()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var p = await AddAsync(db, "p@example.test", ParticipantRole.Speaker);
        var svc = NewSvc(db);

        await svc.MarkStepCompleteAsync(EventId, p.Id, OnboardingStep.Hotel);
        var afterMark = await db.Participants.AsNoTracking().FirstAsync(x => x.Id == p.Id);
        Assert.Equal(Now, afterMark.OnboardingCompleted_HotelAt);

        await svc.ResetStepAsync(EventId, p.Id, OnboardingStep.Hotel);
        var afterReset = await db.Participants.AsNoTracking().FirstAsync(x => x.Id == p.Id);
        Assert.False(afterReset.OnboardingCompleted_Hotel);
        Assert.Null(afterReset.OnboardingCompleted_HotelAt);   // timestamp cleared with the bit
    }

    // ----- stage derivation -------------------------------------------------

    [Fact]
    public async Task Stages_derive_from_lifecycle_and_required_progress()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var pre = await AddAsync(db, "pre@example.test", ParticipantRole.Volunteer,
            ParticipantLifecycleState.Preselected);
        var invited = await AddAsync(db, "inv@example.test", ParticipantRole.Volunteer);
        var inprog = await AddAsync(db, "ip@example.test", ParticipantRole.Volunteer);
        var done = await AddAsync(db, "done@example.test", ParticipantRole.Volunteer);
        var svc = NewSvc(db);

        // invited: activated, zero steps done (default).
        // in-progress: one of the 3 crew steps.
        await svc.MarkStepCompleteAsync(EventId, inprog.Id, OnboardingStep.Hotel);
        // done: all 3 crew steps.
        await svc.MarkStepCompleteAsync(EventId, done.Id, OnboardingStep.Hotel);
        await svc.MarkStepCompleteAsync(EventId, done.Id, OnboardingStep.Appreciation);
        await svc.MarkStepCompleteAsync(EventId, done.Id, OnboardingStep.Swag);

        var ov = await svc.BuildOverviewAsync(EventId);
        Assert.Equal(OnboardingStage.Preselected, ov.Rows.Single(r => r.ParticipantId == pre.Id).Stage);
        Assert.Equal(OnboardingStage.Invited, ov.Rows.Single(r => r.ParticipantId == invited.Id).Stage);
        Assert.Equal(OnboardingStage.InProgress, ov.Rows.Single(r => r.ParticipantId == inprog.Id).Stage);
        Assert.Equal(OnboardingStage.Completed, ov.Rows.Single(r => r.ParticipantId == done.Id).Stage);
    }

    [Fact]
    public async Task Inactive_queue_rows_are_excluded_but_preselected_are_included()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await AddAsync(db, "inact@example.test", ParticipantRole.Volunteer,
            ParticipantLifecycleState.Inactive);
        await AddAsync(db, "pre@example.test", ParticipantRole.Volunteer,
            ParticipantLifecycleState.Preselected);

        var ov = await NewSvc(db).BuildOverviewAsync(EventId);
        Assert.Equal(1, ov.TotalParticipants);             // only the preselected row
        Assert.Equal(1, ov.StageStats.Single(s => s.Stage == OnboardingStage.Preselected).Count);
    }

    // ----- dashboard counts + persona filter --------------------------------

    [Fact]
    public async Task StageStats_count_each_stage()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await AddAsync(db, "p1@example.test", ParticipantRole.Volunteer,
            ParticipantLifecycleState.Preselected);
        await AddAsync(db, "i1@example.test", ParticipantRole.Volunteer);  // invited
        await AddAsync(db, "i2@example.test", ParticipantRole.Volunteer);  // invited
        var svc = NewSvc(db);

        var ov = await svc.BuildOverviewAsync(EventId);
        Assert.Equal(1, ov.StageStats.Single(s => s.Stage == OnboardingStage.Preselected).Count);
        Assert.Equal(2, ov.StageStats.Single(s => s.Stage == OnboardingStage.Invited).Count);
        Assert.Equal(0, ov.StageStats.Single(s => s.Stage == OnboardingStage.Completed).Count);
    }

    [Fact]
    public async Task Persona_filter_restricts_to_one_group()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await AddAsync(db, "spk@example.test", ParticipantRole.Speaker);
        await AddAsync(db, "vol@example.test", ParticipantRole.Volunteer);
        await AddAsync(db, "spo@example.test", ParticipantRole.Sponsor);
        var svc = NewSvc(db);

        var speakers = await svc.BuildOverviewAsync(EventId, PersonaGroup.Speaker);
        Assert.Equal(1, speakers.TotalParticipants);
        Assert.All(speakers.Rows, r => Assert.Equal(PersonaGroup.Speaker, r.Persona));
        Assert.Equal(PersonaGroup.Speaker, speakers.PersonaFilter);

        var all = await svc.BuildOverviewAsync(EventId);
        Assert.Equal(3, all.TotalParticipants);
        Assert.Null(all.PersonaFilter);
    }

    [Fact]
    public async Task OverallPercent_reflects_fully_onboarded_share()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var a = await AddAsync(db, "a@example.test", ParticipantRole.Sponsor);  // will finish
        a.SponsorCompanyId = "3003";
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "3003",
            WebsiteUrl = "https://a.test",
            LogoVectorPath = "uploads/sponsors/3003/logo.eps",
            SponsorPackage = SponsorPackage.Silver,
        });
        await AddAsync(db, "b@example.test", ParticipantRole.Sponsor);          // won't
        await db.SaveChangesAsync();
        var svc = NewSvc(db);

        var ov = await svc.BuildOverviewAsync(EventId);
        Assert.Equal(2, ov.TotalParticipants);
        Assert.Equal(1, ov.FullyOnboarded);
        Assert.Equal(50, ov.OverallPercent);
    }
}
