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
        // 🔒 §708.4 — A TASK LINKS TO THE TASK, anchored at its own row.
        //
        // This used to assert "/Sponsor/CompanyDetails#logos" (P6/§297), i.e. that the destination
        // was GUESSED from the word "logo" in the title. That guessing produced a wrong destination
        // three separate times (§674, §679, §708.4 — "Validate booth members have lead scan app +
        // exhibitor guide" landing on the booth-MEMBER editor because its title contains "member"),
        // and the page it guessed is the one §707.44 removed from the nav and §689.1 is retiring.
        // The row now carries the body, the buttons and the upload control, so the row IS the
        // destination.
        Assert.Equal($"/Sponsor/Tasks{ParticipantChecklistBuilder.TaskAnchor(row.Id)}", row.Link);
    }

    /// <summary>
    /// 🔒 §708.4 — the regression that keeps coming back: a task title whose words happen to name
    /// another task's page. Both of these are real PROD rows from the operator's screenshot.
    /// </summary>
    [Theory]
    // Contains "member" — used to land on /Sponsor/CompanyDetails#booth-members, an editor with
    // nothing to do with a lead-scan app. This is the row he arrowed.
    [InlineData("sponsor:acme:validate-booth-members-have-lead-scan-app--exhibitor-guide")]
    // Contains "onboard" — used to land on /Sponsor/CompanyDetails#company.
    [InlineData("sponsor:acme:initial-onboarding-of-sponsor")]
    // Contains "member" — the one he named: "i see links to old company details booth member".
    [InlineData("sponsor:acme:register-booth-members")]
    public void Sponsor_task_links_to_its_own_row_never_to_company_details(string sourceKey)
    {
        var link = ParticipantChecklistBuilder.LinkForTask(42, sourceKey);

        Assert.Equal("/Sponsor/Tasks#task-42", link);
        Assert.DoesNotContain("CompanyDetails", link);
    }

    // ── §708.10 — THE THIRD ROW-OWNING FAMILY ───────────────────────────────────────────────
    //
    // The generic roles' step tasks now embed their form on their own row on /Tasks, so the row is
    // the complete destination and "a task links to the TASK" (§708.4) applies to them too.

    [Theory]
    [InlineData("hotel-form:7")]
    [InlineData("dinner-form:7")]
    [InlineData("lunch-form:7")]
    [InlineData("swag-form:7")]
    [InlineData("signal:7")]
    [InlineData("profile:7")]
    [InlineData("accept:7")]
    [InlineData("availability:7")]
    public void Embedded_step_task_links_to_its_own_row_on_the_shared_tasks_page(string sourceKey)
    {
        var link = ParticipantChecklistBuilder.LinkForTask(42, sourceKey);

        Assert.Equal("/Tasks#task-42", link);
        // It must NOT reopen the whole Get-Started journey to change ONE answer — §708.2a decided
        // the wizard is the first-run flow and a direct edit is a different thing.
        Assert.DoesNotContain("Wizard", link);
    }

    [Theory]
    // 🔒 §707.57b keeps party and master class on their own surfaces, and §351-6/§365 point these
    // two at NAMED wizard steps for reasons the operator gave by name. If §708.10 ever swallows
    // them, this fails loudly rather than the decision being lost quietly.
    [InlineData("party-form:7", "/Forms/Wizard?step=party")]
    [InlineData("masterclass-form:7", "/Forms/Wizard?step=masterclass")]
    public void Party_and_master_class_keep_their_own_destinations(string sourceKey, string expected)
    {
        Assert.Equal(expected, ParticipantChecklistBuilder.LinkForTask(42, sourceKey));
    }

    [Fact]
    public async Task All_complete_when_no_pending()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);
        var cl = await NewBuilder(db).BuildAsync(seed.EventId, seed.ParticipantId);
        Assert.True(cl.AllComplete);
    }

    // §352 (operator 2026-07-26: "i bet we have other similar overlaps") — every task whose form
    // has an INLINE wizard step now deep-links to that step, the way §351-6/§365 already did for
    // party and master class. One canonical surface, with hub chrome and a way back.
    [Theory]
    [InlineData("hotel-form:7", "/Forms/Wizard?step=hotel")]
    [InlineData("dinner-form:7", "/Forms/Wizard?step=dinner")]
    [InlineData("volunteer-form:7", "/Forms/VolunteerWizard")] // §234 (7a): the SHIFTS wizard's own form (availability is a different live form)
    [InlineData("swag-form:7", "/Forms/Wizard?step=swag")]
    [InlineData("lunch-form:7", "/Forms/Wizard?step=lunch")]
    // §161: manual mark-done steps now deep-link to the same page their Get-Started card opens.
    [InlineData("signal:7", "/Forms/Wizard?step=signal")]
    // §314: the Promote step page is retired — a legacy promote: row still lands on Help Promote.
    [InlineData("promote:7", "/Speaker/Graphics")]
    // 🔒 §708.4 — the speakerdl: family owns a rendered ROW, so it links to the row's page rather
    // than to a page guessed from a word in its title. These three used to assert
    // "/Speaker/Graphics", "/Speaker" and "/Speaker" respectively (§314/§322i), matched by
    // `Contains("promote")` / `Contains("presentation")` — the same guessing that sent the sponsor
    // rows to a retired page. §708 embeds the upload control IN the task, so "/Speaker" is no
    // longer even where the work happens.
    [InlineData("speakerdl:7:help-to-promote-your-sessions", "/Speaker/Tasks")]
    [InlineData("speakerdl:7:upload-preview-presentation", "/Speaker/Tasks")]
    [InlineData("speakerdl:7:upload-final-presentation", "/Speaker/Tasks")]
    [InlineData("party-form:7", "/Forms/Wizard?step=party")]
    [InlineData(null, null)]
    [InlineData("unknown:7", null)]
    public void SourceKey_maps_to_form_page(string? key, string? expected)
    {
        Assert.Equal(expected, ParticipantChecklistBuilder.LinkForSourceKey(key));
    }
}
