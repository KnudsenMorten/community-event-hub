using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Unit tests for the unified participant checklist (REQUIREMENTS Top-8 #7 / §21
/// Participant [H]): ONE shared builder feeds the Hub home, the Tasks page and
/// attendee My-event with the same "what's still needed" view — pending/completed
/// split, overdue flagging, sponsor company-scoped tasks, and the SourceKey →
/// form deep-link mapping. FAKE names only.
/// </summary>
public sealed class ParticipantChecklistBuilderTests
{
    private const string SponsorCompanyId = "9001";

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"checklist-{Guid.NewGuid():N}")
            .Options);

    // Fixed "today" so overdue maths is deterministic.
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Seed(int EventId, int ParticipantId);

    private static async Task<Seed> SeedAsync(
        CommunityHubDbContext db, string? sponsorCompanyId = null)
    {
        var ev = new Event
        {
            Code = "ELDK27", DisplayName = "Test Edition", CommunityName = "Test Community",
            StartDate = new DateOnly(2026, 9, 1), EndDate = new DateOnly(2026, 9, 2),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var p = new Participant
        {
            EventId = ev.Id, Email = "person@example.test", FullName = "Per Son",
            Role = sponsorCompanyId is null ? ParticipantRole.Volunteer : ParticipantRole.Sponsor,
            IsActive = true, SponsorCompanyId = sponsorCompanyId,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return new Seed(ev.Id, p.Id);
    }

    private static ParticipantChecklistBuilder NewBuilder(CommunityHubDbContext db) =>
        new(db, new FixedClock(), new CommunityHub.Core.Participants.FormTaskReconciler(db, new FixedClock()));

    [Fact]
    public async Task Splits_pending_and_completed()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);
        db.Tasks.AddRange(
            new ParticipantTask { EventId = seed.EventId, AssignedParticipantId = seed.ParticipantId, Title = "Do A", State = TaskState.Open },
            new ParticipantTask { EventId = seed.EventId, AssignedParticipantId = seed.ParticipantId, Title = "Do B", State = TaskState.Done });
        await db.SaveChangesAsync();

        var cl = await NewBuilder(db).BuildAsync(seed.EventId, seed.ParticipantId);

        Assert.Equal(1, cl.OpenCount);
        Assert.False(cl.AllComplete);
        Assert.Equal("Do A", Assert.Single(cl.Pending).Title);
        Assert.Equal("Do B", Assert.Single(cl.Completed).Title);
    }

    [Fact]
    public async Task Flags_overdue_open_task_with_day_count()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);
        db.Tasks.Add(new ParticipantTask
        {
            EventId = seed.EventId, AssignedParticipantId = seed.ParticipantId,
            Title = "Late", State = TaskState.Open, DueDate = new DateOnly(2026, 6, 10), // 5 days before "today"
        });
        await db.SaveChangesAsync();

        var cl = await NewBuilder(db).BuildAsync(seed.EventId, seed.ParticipantId);

        var row = Assert.Single(cl.Pending);
        Assert.Equal(5, row.DaysOverdue);
        Assert.Equal(1, cl.OverdueCount);
    }

    [Fact]
    public async Task Completed_task_is_never_overdue()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);
        db.Tasks.Add(new ParticipantTask
        {
            EventId = seed.EventId, AssignedParticipantId = seed.ParticipantId,
            Title = "Done late", State = TaskState.Done, DueDate = new DateOnly(2026, 6, 10),
        });
        await db.SaveChangesAsync();

        var cl = await NewBuilder(db).BuildAsync(seed.EventId, seed.ParticipantId);

        Assert.Equal(0, cl.OverdueCount);
        Assert.Null(Assert.Single(cl.Completed).DaysOverdue);
    }

    [Fact]
    public async Task Includes_sponsor_company_scoped_tasks()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db, SponsorCompanyId);
        // Company-scoped task: no assigned participant, just the company id.
        db.Tasks.Add(new ParticipantTask
        {
            EventId = seed.EventId, AssignedParticipantId = null, SponsorCompanyId = SponsorCompanyId,
            Title = "Upload booth logo", State = TaskState.Open, SourceKey = "sponsor:logo",
        });
        await db.SaveChangesAsync();

        var cl = await NewBuilder(db).BuildAsync(seed.EventId, seed.ParticipantId);

        var row = Assert.Single(cl.Pending);
        Assert.Equal("Upload booth logo", row.Title);
        // P6: the sponsor: checklist deep-link now points at the Company Details
        // form page (was /Sponsor/Tasks).
        Assert.Equal("/Sponsor/CompanyDetails", row.Link);   // SourceKey deep-link mapping
    }

    [Fact]
    public async Task All_complete_when_no_pending()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);
        var cl = await NewBuilder(db).BuildAsync(seed.EventId, seed.ParticipantId);
        Assert.True(cl.AllComplete);
    }

    [Theory]
    [InlineData("hotel-form:7", "/Forms/Hotel")]
    [InlineData("dinner-form:7", "/Forms/Dinner")]
    [InlineData("volunteer-form:7", "/volunteer/availability")] // B8: retired /Forms/VolunteerWizard
    [InlineData("swag-form:7", "/Forms/Swag")]
    [InlineData("lunch-form:7", "/Forms/Lunch")]
    // §161: manual mark-done steps now deep-link to the same page their Get-Started card opens.
    [InlineData("signal:7", "/Forms/Signal")]
    [InlineData("promote:7", "/Speaker/Promote")]
    [InlineData("party-form:7", "/Party")]
    [InlineData(null, null)]
    [InlineData("unknown:7", null)]
    public void SourceKey_maps_to_form_page(string? key, string? expected)
    {
        Assert.Equal(expected, ParticipantChecklistBuilder.LinkForSourceKey(key));
    }
}
