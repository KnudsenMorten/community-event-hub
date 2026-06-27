using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Sponsors;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Scenario tests for <see cref="SponsorDeliverablesService"/> (REQUIREMENTS §135) over the EF
/// in-memory provider: it proves each lifecycle stage is sourced from the RIGHT existing table
/// (SponsorInfo description/logo, booth members, booth materials, upload audits, sponsor
/// ParticipantTasks), that booth stages are exhibitor-only, that deadlines + overdue come from
/// the dated sponsor tasks, and that the organizer board sorts at-risk first. FAKE names only.
/// </summary>
public sealed class SponsorDeliverablesServiceTests
{
    private static readonly DateOnly Today = new(2026, 6, 27);

    // Slugs that back a dedicated stage (mirror SponsorOrderPullService / the JSON titles).
    private const string Onboarding = "initial-onboarding-of-sponsor";
    private const string Wall = "upload-sponsor-wall-design-in-vector-format";
    private const string Members = "register-booth-members";

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"deliverables-{Guid.NewGuid():N}")
            .Options);

    private static async Task<int> NewEventAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            Code = "ELDK27", DisplayName = "Test Edition", CommunityName = "Test Community",
            StartDate = new DateOnly(2026, 9, 1), EndDate = new DateOnly(2026, 9, 2),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static async Task AddInfoAsync(
        CommunityHubDbContext db, int eventId, string companyId,
        SponsorPackage package = SponsorPackage.Silver,
        string? description = null, string? logoRaster = null, string? logoVector = null)
    {
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = eventId, SponsorCompanyId = companyId, SponsorPackage = package,
            CompanyDescription = description, LogoRasterPath = logoRaster, LogoVectorPath = logoVector,
        });
        await db.SaveChangesAsync();
    }

    private static async Task AddSponsorTaskAsync(
        CommunityHubDbContext db, int eventId, string companyId, string slug,
        TaskState state, DateOnly? due)
    {
        db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId, SponsorCompanyId = companyId,
            SourceKey = $"sponsor:{companyId}:{slug}", Title = slug, State = state, DueDate = due,
        });
        await db.SaveChangesAsync();
    }

    private static SponsorDeliverablesService NewService(CommunityHubDbContext db) => new(db);

    [Fact]
    public async Task Digital_only_company_has_three_stages_booth_stages_not_applicable()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        await AddInfoAsync(db, eventId, "9001", SponsorPackage.Silver);

        var d = await NewService(db).BuildForCompanyAsync(eventId, "9001", Today);

        Assert.False(d.IsExhibitor);
        Assert.Equal(3, d.ApplicableCount);    // onboarding, logo, tasks
        Assert.DoesNotContain(d.Stages, s => s.Key == "booth-materials");
        Assert.DoesNotContain(d.Stages, s => s.Key == "booth-members");
        // No description / logo yet; no tasks => the catch-all "tasks" stage is done.
        Assert.Contains(d.DoneStages, s => s.Key == "tasks");
        Assert.Contains(d.MissingStages, s => s.Key == "onboarding");
        Assert.Contains(d.MissingStages, s => s.Key == "logo");
        Assert.Equal(SponsorDeliverablesService.CompanyDetailsLink,
            d.Stages.Single(s => s.Key == "onboarding").FixLink);
    }

    [Fact]
    public async Task Onboarding_done_when_company_description_saved()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        await AddInfoAsync(db, eventId, "9001", description: "We build clouds.");

        var d = await NewService(db).BuildForCompanyAsync(eventId, "9001", Today);

        Assert.Contains(d.DoneStages, s => s.Key == "onboarding");
    }

    [Fact]
    public async Task Logo_done_via_sponsorinfo_path_or_upload_audit()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        await AddInfoAsync(db, eventId, "9001", logoRaster: "uploads/logo.png");

        var viaPath = await NewService(db).BuildForCompanyAsync(eventId, "9001", Today);
        Assert.Contains(viaPath.DoneStages, s => s.Key == "logo");

        // Second company: logo only via an upload audit (no path on the row).
        await AddInfoAsync(db, eventId, "9002");
        db.SponsorUploadAudits.Add(new SponsorUploadAudit
        {
            EventId = eventId, SponsorCompanyId = "9002", Kind = "some",
            FileName = "SoMeBrandingLogo_X_v1.png", UploadedByEmail = "x@y.test",
        });
        await db.SaveChangesAsync();

        var viaAudit = await NewService(db).BuildForCompanyAsync(eventId, "9002", Today);
        Assert.Contains(viaAudit.DoneStages, s => s.Key == "logo");
    }

    [Fact]
    public async Task Exhibitor_has_booth_stages_members_and_materials()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        await AddInfoAsync(db, eventId, "9001", SponsorPackage.Gold);

        // Active booth member => members done; a tombstoned one must NOT count.
        db.SponsorBoothMembers.Add(new SponsorBoothMember
        {
            EventId = eventId, SponsorCompanyId = "9001",
            FirstName = "Bo", LastName = "Oth", Email = "bo@x.test",
        });
        db.SponsorBoothMembers.Add(new SponsorBoothMember
        {
            EventId = eventId, SponsorCompanyId = "9001",
            FirstName = "Gone", LastName = "Away", Email = "gone@x.test",
            DeletedAt = DateTimeOffset.UtcNow,
        });
        db.SponsorBoothMaterials.Add(new SponsorBoothMaterial
        {
            EventId = eventId, SponsorCompanyId = "9001",
            Kind = BoothMaterialKind.Video, Url = "https://v/1", CreatedByEmail = "x@y.test",
        });
        await db.SaveChangesAsync();

        var d = await NewService(db).BuildForCompanyAsync(eventId, "9001", Today);

        Assert.True(d.IsExhibitor);
        Assert.Equal(5, d.ApplicableCount);
        Assert.Contains(d.DoneStages, s => s.Key == "booth-members");
        Assert.Contains(d.DoneStages, s => s.Key == "booth-materials");
    }

    [Fact]
    public async Task Booth_member_data_alone_marks_company_as_exhibitor()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        // No SponsorInfo row at all — but a booth member exists.
        db.SponsorBoothMembers.Add(new SponsorBoothMember
        {
            EventId = eventId, SponsorCompanyId = "9009",
            FirstName = "Bo", LastName = "Oth", Email = "bo@x.test",
        });
        await db.SaveChangesAsync();

        var d = await NewService(db).BuildForCompanyAsync(eventId, "9009", Today);

        Assert.True(d.IsExhibitor);
        Assert.Equal(5, d.ApplicableCount);
    }

    [Fact]
    public async Task Onboarding_task_overdue_when_past_due_and_not_on_file()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        await AddInfoAsync(db, eventId, "9001");   // no description -> onboarding not done
        await AddSponsorTaskAsync(db, eventId, "9001", Onboarding, TaskState.Open, Today.AddDays(-3));

        var d = await NewService(db).BuildForCompanyAsync(eventId, "9001", Today);

        var onboarding = d.Stages.Single(s => s.Key == "onboarding");
        Assert.False(onboarding.Done);
        Assert.True(onboarding.Overdue);
        Assert.Equal(Today.AddDays(-3), onboarding.Deadline);
        Assert.True(d.AtRisk);
    }

    [Fact]
    public async Task Onboarding_deadline_present_but_not_overdue_when_data_on_file()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        await AddInfoAsync(db, eventId, "9001", description: "On file.");   // onboarding done
        await AddSponsorTaskAsync(db, eventId, "9001", Onboarding, TaskState.Open, Today.AddDays(-3));

        var d = await NewService(db).BuildForCompanyAsync(eventId, "9001", Today);

        var onboarding = d.Stages.Single(s => s.Key == "onboarding");
        Assert.True(onboarding.Done);
        Assert.False(onboarding.Overdue);   // done -> never overdue, even with a past deadline
        Assert.False(d.AtRisk);
    }

    [Fact]
    public async Task Tasks_stage_excludes_the_three_mapped_slugs_and_reflects_other_open_tasks()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        await AddInfoAsync(db, eventId, "9001", SponsorPackage.Gold);

        // The three mapped tasks are OPEN but must NOT block the catch-all "tasks" stage.
        await AddSponsorTaskAsync(db, eventId, "9001", Onboarding, TaskState.Open, Today.AddDays(5));
        await AddSponsorTaskAsync(db, eventId, "9001", Wall, TaskState.Open, Today.AddDays(5));
        await AddSponsorTaskAsync(db, eventId, "9001", Members, TaskState.Open, Today.AddDays(5));

        var doneNow = await NewService(db).BuildForCompanyAsync(eventId, "9001", Today);
        Assert.Contains(doneNow.DoneStages, s => s.Key == "tasks");  // no OTHER open task

        // Add an unrelated open task with a past due -> the "tasks" stage opens AND is overdue.
        await AddSponsorTaskAsync(db, eventId, "9001", "choose-your-booth-layout-table-chairs",
            TaskState.Open, Today.AddDays(-1));

        var blocked = await NewService(db).BuildForCompanyAsync(eventId, "9001", Today);
        var tasks = blocked.Stages.Single(s => s.Key == "tasks");
        Assert.False(tasks.Done);
        Assert.True(tasks.Overdue);
        Assert.Equal(Today.AddDays(-1), tasks.Deadline);
    }

    [Fact]
    public async Task Board_lists_every_company_and_sorts_at_risk_first()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);

        // Healthy company: everything on file, no overdue.
        await AddInfoAsync(db, eventId, "1000", description: "ok", logoRaster: "l.png");

        // At-risk company: overdue onboarding task, nothing on file.
        await AddInfoAsync(db, eventId, "2000");
        await AddSponsorTaskAsync(db, eventId, "2000", Onboarding, TaskState.Open, Today.AddDays(-2));

        var board = await NewService(db).BuildBoardAsync(eventId, Today);

        Assert.Equal(2, board.Count);
        Assert.Equal("2000", board[0].CompanyId);   // at-risk first
        Assert.True(board[0].AtRisk);
        Assert.False(board[1].AtRisk);
    }

    [Fact]
    public async Task Board_uses_resolved_company_names_when_provided()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        await AddInfoAsync(db, eventId, "1000", description: "ok");

        var names = new Dictionary<string, string> { ["1000"] = "ACME Corp" };
        var board = await NewService(db).BuildBoardAsync(eventId, Today, names);

        Assert.Single(board);
        Assert.Equal("ACME Corp", board[0].CompanyName);
    }

    [Fact]
    public async Task Board_is_empty_when_no_sponsor_data()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        var board = await NewService(db).BuildBoardAsync(eventId, Today);
        Assert.Empty(board);
    }
}
