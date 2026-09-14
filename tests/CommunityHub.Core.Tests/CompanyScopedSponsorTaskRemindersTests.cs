using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1081 — A SPONSOR COMPANY TASK IS CHASED, AND IT IS CHASED PER COMPANY.
/// </summary>
/// <remarks>
/// <para>🔴 <b>The gap.</b> <see cref="TaskReminderBuilder"/>'s main query requires
/// <c>AssignedParticipantId != null</c>, and a sponsor task deliberately has NO assignee — one row
/// per company, so any coordinator's completion closes it for the team. Those rows therefore could
/// never match. Measured on prod 2026-08-13: <b>131</b> open dated sponsor company tasks across
/// <b>14</b> companies, and <b>zero</b> assigned ones — so the coordinator fan-out written for
/// exactly this had never run once. Correct code behind a filter that excluded everything it was
/// for, which is §968's shape.</para>
///
/// <para>🔑 <b>Operator 2026-08-13:</b> <i>"the logic must be linked to company, not people when it
/// is about sponsors"</i>, and on why nothing looked broken: <i>"they are future tasks with due date
/// in future. reason they havent been chased yet"</i> — true of 123 of the 131.</para>
///
/// <para>⚠️ <b>Nothing here existed before, and that is why the gap survived.</b> Every task-reminder
/// test seeded an ASSIGNED task, so the whole company-scoped path was untested.</para>
///
/// <para>FAKE names, companies and addresses only.</para>
/// </remarks>
public sealed class CompanyScopedSponsorTaskRemindersTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);
    private const string CompanyId = "4242";

    private static EmailTemplateProvider Templates() =>
        new(Options.Create(new EmailTemplateOptions { TemplateDirectory = RepoPaths.EmailTemplates() }));

    private static TaskReminderBuilder NewBuilder(CommunityHubDbContext db) =>
        new(db, Templates(), new FixedClock(Now), new SponsorRecipientResolver(db));

    private static async Task<int> SeedEventAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            CommunityName = "C", DisplayName = "T", Code = "T27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static Participant Contact(
        int eventId, string email, string name, bool coordinator, bool signer = false) =>
        new()
        {
            EventId = eventId, Email = email, FullName = name,
            Role = ParticipantRole.Sponsor, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
            SponsorCompanyId = CompanyId,
            IsEventCoordinator = coordinator, IsSigner = signer,
        };

    /// <summary>Adds the company task with no assignee — the shape every sponsor task really has.</summary>
    private static void AddCompanyTask(CommunityHubDbContext db, int eventId, DateOnly due) =>
        db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId,
            AssignedParticipantId = null,           // 🔑 the whole point
            SponsorCompanyId = CompanyId,
            SourceKey = $"sponsor:{CompanyId}:upload-sponsor-wall-design-in-vector-format",
            Title = "Upload your booth artwork",
            DueDate = due,
            State = TaskState.Open,
        });

    /// <summary>
    /// 🔴 THE REGRESSION TEST FOR THE GAP. Two coordinators, one company task due today ⇒ both are
    /// chased. Before §1081 this returned nothing at all.
    /// </summary>
    [Fact]
    public async Task A_company_task_with_no_assignee_chases_every_coordinator()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        db.Participants.AddRange(
            Contact(ev, "anna@example.test", "Anna Berg", coordinator: true),
            Contact(ev, "bo@example.test", "Bo Dahl", coordinator: true));
        AddCompanyTask(db, ev, DateOnly.FromDateTime(Now.UtcDateTime));
        await db.SaveChangesAsync();

        var due = await NewBuilder(db).BuildDueAsync(ev);

        Assert.Equal(2, due.Count);
        Assert.Contains(due, m => m.RecipientEmail == "anna@example.test");
        Assert.Contains(due, m => m.RecipientEmail == "bo@example.test");
    }

    /// <summary>
    /// 🔒 §7c — a SIGNER-ONLY contact is never chased about booth logistics. The company task is
    /// the coordinators' to finish.
    /// </summary>
    [Fact]
    public async Task A_signer_only_contact_is_never_chased()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        db.Participants.AddRange(
            Contact(ev, "anna@example.test", "Anna Berg", coordinator: true),
            Contact(ev, "finance@example.test", "Finn Sørensen", coordinator: false, signer: true));
        AddCompanyTask(db, ev, DateOnly.FromDateTime(Now.UtcDateTime));
        await db.SaveChangesAsync();

        var due = await NewBuilder(db).BuildDueAsync(ev);

        Assert.Equal("anna@example.test", Assert.Single(due).RecipientEmail);
    }

    /// <summary>
    /// 🔴 HIS OPTION A: a company with NO coordinator is chased by NOBODY — the hub never falls back
    /// to a signer. Raising that gap belongs to the sync job (stage 2c), not to this builder.
    /// </summary>
    [Fact]
    public async Task No_coordinator_means_nobody_is_chased_and_no_fallback_to_signers()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        db.Participants.Add(
            Contact(ev, "finance@example.test", "Finn Sørensen", coordinator: false, signer: true));
        AddCompanyTask(db, ev, DateOnly.FromDateTime(Now.UtcDateTime));
        await db.SaveChangesAsync();

        Assert.Empty(await NewBuilder(db).BuildDueAsync(ev));
    }

    /// <summary>
    /// ✅ HIS POINT, PINNED. A future due date sends nothing — §81 fires ON the due day. This is why
    /// 123 of the 131 prod rows were correctly silent, and it is what keeps this change from
    /// becoming a backlog blast on deploy.
    /// </summary>
    [Fact]
    public async Task A_future_dated_company_task_chases_nobody_yet()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        db.Participants.Add(Contact(ev, "anna@example.test", "Anna Berg", coordinator: true));
        AddCompanyTask(db, ev, DateOnly.FromDateTime(Now.UtcDateTime).AddDays(30));
        await db.SaveChangesAsync();

        Assert.Empty(await NewBuilder(db).BuildDueAsync(ev));
    }

    /// <summary>An overdue task still fires once — a missed run must self-heal (§81).</summary>
    [Fact]
    public async Task An_overdue_company_task_still_fires()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        db.Participants.Add(Contact(ev, "anna@example.test", "Anna Berg", coordinator: true));
        AddCompanyTask(db, ev, DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-9));
        await db.SaveChangesAsync();

        var msg = Assert.Single(await NewBuilder(db).BuildDueAsync(ev));
        Assert.Contains("overdue", msg.HtmlBody);
    }

    /// <summary>
    /// 🔒 §707.11 — each coordinator carries their OWN occasion root (the address is IN the root and
    /// the date is the last segment), so they are deduped and paced independently. If the date came
    /// first, every day would be a new root and the cadence would never hold anyone back.
    /// 🔒 §169 — and each carries their own body, because the CTA is a personal magic link; one
    /// shared render would be one person's credential mailed to their colleague.
    /// </summary>
    [Fact]
    public async Task Each_coordinator_gets_their_own_occasion_key_and_their_own_body()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        db.Participants.AddRange(
            Contact(ev, "anna@example.test", "Anna Berg", coordinator: true),
            Contact(ev, "bo@example.test", "Bo Dahl", coordinator: true));
        AddCompanyTask(db, ev, DateOnly.FromDateTime(Now.UtcDateTime));
        await db.SaveChangesAsync();

        var due = await NewBuilder(db).BuildDueAsync(ev);

        Assert.Equal(2, due.Select(m => m.OccasionKey).Distinct().Count());
        Assert.All(due, m => Assert.Contains(m.RecipientEmail, m.OccasionKey));
        // Each is addressed to its own recipient participant, which is what makes the link personal.
        Assert.Equal(2, due.Select(m => m.ParticipantId).Distinct().Count());
    }

    /// <summary>
    /// 🔒 A DONE task chases nobody — completion by ONE coordinator closes it for the company, which
    /// is the entire reason these rows have no assignee.
    /// </summary>
    [Fact]
    public async Task A_completed_company_task_chases_nobody()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        db.Participants.AddRange(
            Contact(ev, "anna@example.test", "Anna Berg", coordinator: true),
            Contact(ev, "bo@example.test", "Bo Dahl", coordinator: true));
        db.Tasks.Add(new ParticipantTask
        {
            EventId = ev, AssignedParticipantId = null, SponsorCompanyId = CompanyId,
            SourceKey = $"sponsor:{CompanyId}:upload-sponsor-wall-design-in-vector-format",
            Title = "Upload your booth artwork",
            DueDate = DateOnly.FromDateTime(Now.UtcDateTime),
            State = TaskState.Done,
        });
        await db.SaveChangesAsync();

        Assert.Empty(await NewBuilder(db).BuildDueAsync(ev));
    }

    /// <summary>
    /// 🔒 A deactivated coordinator leaves the audience immediately (§502's leaver rule, §253 G9) —
    /// the resolver excludes inactive contacts, so this holds for company tasks as it already did
    /// for assigned ones.
    /// </summary>
    [Fact]
    public async Task A_deactivated_coordinator_is_not_chased()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var gone = Contact(ev, "left@example.test", "Lea Toft", coordinator: true);
        gone.IsActive = false;
        db.Participants.AddRange(Contact(ev, "anna@example.test", "Anna Berg", coordinator: true), gone);
        AddCompanyTask(db, ev, DateOnly.FromDateTime(Now.UtcDateTime));
        await db.SaveChangesAsync();

        Assert.Equal("anna@example.test", Assert.Single(await NewBuilder(db).BuildDueAsync(ev)).RecipientEmail);
    }
}
