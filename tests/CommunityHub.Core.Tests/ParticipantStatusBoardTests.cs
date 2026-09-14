using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Participants;
using CommunityHub.Core.Sponsors;
using CommunityHub.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1085 — ONE participant-status board for all roles. FAKE names only.
/// </summary>
/// <remarks>
/// <para>🔑 The load-bearing property is the FIRST test: the board's percent is the WIZARD's percent.
/// Everything else on the page is presentation; that equality is why the operator can trust a number
/// here after distrusting two boards that each did their own arithmetic (§1081).</para>
/// </remarks>
public sealed class ParticipantStatusBoardTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 6, 15);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"pstatus-{Guid.NewGuid():N}")
            .Options);

    /// <summary>e-conomic unreachable ⇒ the sponsor contacts step is undeterminable (Done == null),
    /// which is the state §1081 made sure never sits in the denominator.</summary>
    private sealed class NoEconomicClient : IEconomicContactAdminClient
    {
        public bool CanWrite => false;
        public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(
            string? search, int? customerGroup = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicCustomerRow>>(Array.Empty<EconomicCustomerRow>());
        public Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(
            int customerNumber, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicContactRow>>(Array.Empty<EconomicContactRow>());
        public Task<int> CreateContactAsync(
            int customerNumber, EconomicContactInput input, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task UpdateContactAsync(
            int customerNumber, int contactNumber, EconomicContactInput input, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteContactAsync(
            int customerNumber, int contactNumber, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static SponsorWizardService NewSponsorWizard(CommunityHubDbContext db)
    {
        var cmOptions = new CompanyManagerOptions();
        return new SponsorWizardService(
            db, new CompanyManagerClient(new HttpClient(), cmOptions), cmOptions,
            new EconomicContactAdminService(new NoEconomicClient()),
            NullLogger<SponsorWizardService>.Instance);
    }

    // The REAL wizard services — the point of the board is that it agrees with them.
    private static WizardProgressReader NewReader(CommunityHubDbContext db) =>
        new(db, new SpeakerWizardService(db), new RoleWizardService(db),
            new AttendeeWizardService(db), NewSponsorWizard(db));

    private static ParticipantStatusBoardBuilder NewBoard(CommunityHubDbContext db) =>
        new(db, NewReader(db), new FixedClock());

    private static async Task<int> SeedEventAsync(CommunityHubDbContext db)
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

    private static async Task<Participant> AddPersonAsync(
        CommunityHubDbContext db, int eventId, ParticipantRole role,
        string name, string email, string? companyId = null,
        bool isActive = true, bool isCoordinator = false)
    {
        var p = new Participant
        {
            EventId = eventId, Email = email, FullName = name, Role = role,
            IsActive = isActive, SponsorCompanyId = companyId, IsEventCoordinator = isCoordinator,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// 🔑 THE INVARIANT. The board reports the participant's OWN wizard percent — not a recount of
    /// their tasks. §1081 is the whole reason: two boards with their own arithmetic disagreed with
    /// each other and with the participant's page, and the operator stopped trusting both.
    /// </summary>
    [Fact]
    public async Task Percent_is_the_wizard_percent_not_a_second_opinion()
    {
        using var db = NewDb();
        var evId = await SeedEventAsync(db);
        var p = await AddPersonAsync(db, evId, ParticipantRole.Volunteer, "Vera Volunteer", "vera@example.test");

        // A pile of unrelated open tasks: a board that counted TASKS would move; one that reads the
        // wizard cannot.
        db.Tasks.AddRange(
            new ParticipantTask { EventId = evId, AssignedParticipantId = p.Id, Title = "T1", State = TaskState.Open },
            new ParticipantTask { EventId = evId, AssignedParticipantId = p.Id, Title = "T2", State = TaskState.Open });
        await db.SaveChangesAsync();

        var wizard = await new RoleWizardService(db).BuildAsync(evId, p.Id);
        var row = Assert.Single(await NewBoard(db).BuildAsync(evId, ParticipantRole.Volunteer));

        Assert.Equal(wizard.Percent, row.Percent);
        Assert.Equal(wizard.DoneCount, row.DoneCount);
        Assert.Equal(wizard.EntitledCount, row.EvaluableCount);
    }

    /// <summary>
    /// 🔴 Sponsors are ONE ROW PER COMPANY. Their wizard and their tasks are company-scoped, so a
    /// row per contact would repeat one company's state N times — and invite the §1081 incident of
    /// chasing a coordinator for work a colleague had already done.
    /// </summary>
    [Fact]
    public async Task Sponsor_contacts_collapse_into_one_company_row()
    {
        using var db = NewDb();
        var evId = await SeedEventAsync(db);
        await AddPersonAsync(db, evId, ParticipantRole.Sponsor, "Coordinator One", "one@example.test", "9001", isCoordinator: true);
        await AddPersonAsync(db, evId, ParticipantRole.Sponsor, "Booth Two", "two@example.test", "9001");
        await AddPersonAsync(db, evId, ParticipantRole.Sponsor, "Signer Three", "three@example.test", "9001");

        var row = Assert.Single(await NewBoard(db).BuildAsync(evId, ParticipantRole.Sponsor));

        Assert.Equal("9001", row.SponsorCompanyId);
        Assert.Equal(3, row.ContactCount);
    }

    /// <summary>
    /// The company row's checklist is built for the EVENT COORDINATOR where there is one — the
    /// contact sponsor mail is addressed to (§7c), so board and mail describe the same person's view.
    /// </summary>
    [Fact]
    public async Task Company_row_is_built_for_the_coordinator()
    {
        using var db = NewDb();
        var evId = await SeedEventAsync(db);
        await AddPersonAsync(db, evId, ParticipantRole.Sponsor, "Booth Member", "booth@example.test", "9001");
        var coord = await AddPersonAsync(db, evId, ParticipantRole.Sponsor, "The Coordinator", "coord@example.test", "9001", isCoordinator: true);

        var row = Assert.Single(await NewBoard(db).BuildAsync(evId, ParticipantRole.Sponsor));

        Assert.Equal(coord.Id, row.ParticipantId);
    }

    /// <summary>
    /// §1081 — a COMPANY task row (<c>AssignedParticipantId = NULL</c>) belongs to the company row.
    /// Filtering on "assigned to me" is what left 131 open sponsor tasks chased by nothing.
    /// </summary>
    [Fact]
    public async Task Company_scoped_tasks_count_on_the_company_row()
    {
        using var db = NewDb();
        var evId = await SeedEventAsync(db);
        await AddPersonAsync(db, evId, ParticipantRole.Sponsor, "Coordinator", "coord@example.test", "9001", isCoordinator: true);
        db.Tasks.Add(new ParticipantTask
        {
            EventId = evId, SponsorCompanyId = "9001", AssignedParticipantId = null,
            Title = "Deliver booth plan", State = TaskState.Open,
            DueDate = Today.AddDays(-3),
        });
        await db.SaveChangesAsync();

        var row = Assert.Single(await NewBoard(db).BuildAsync(evId, ParticipantRole.Sponsor));

        Assert.Equal(1, row.OpenTaskCount);
        Assert.Equal(1, row.OverdueTaskCount);
        Assert.Equal(Today.AddDays(-3), row.EarliestOverdueDue);
        Assert.True(row.Overdue);
    }

    /// <summary>
    /// 🔴 §1082 — a SYSTEM-CLOSED task is neither open nor done: the question simply stopped being
    /// asked. Counting it would credit somebody with work nobody did.
    /// </summary>
    [Fact]
    public async Task System_closed_tasks_are_not_counted()
    {
        using var db = NewDb();
        var evId = await SeedEventAsync(db);
        var p = await AddPersonAsync(db, evId, ParticipantRole.Volunteer, "Vera", "vera@example.test");
        db.Tasks.AddRange(
            // Retired by the system: State=Done + a reason ⇒ invisible to the tally.
            new ParticipantTask
            {
                EventId = evId, AssignedParticipantId = p.Id, Title = "Retired", State = TaskState.Done,
                ClosedReason = TaskClosedReason.AbandonedOnDeactivation, DueDate = Today.AddDays(-9),
            },
            // Genuinely open and overdue.
            new ParticipantTask
            {
                EventId = evId, AssignedParticipantId = p.Id, Title = "Real", State = TaskState.Open,
                DueDate = Today.AddDays(-1),
            });
        await db.SaveChangesAsync();

        var row = Assert.Single(await NewBoard(db).BuildAsync(evId, ParticipantRole.Volunteer));

        Assert.Equal(1, row.OpenTaskCount);
        Assert.Equal(1, row.OverdueTaskCount);
        Assert.Equal(Today.AddDays(-1), row.EarliestOverdueDue);
    }

    /// <summary>
    /// §1071 — a deactivated contact is not current work. The operator reported exactly this on the
    /// board this one replaces: <i>"it still shows old sponsors that was deactivated"</i>.
    /// </summary>
    [Fact]
    public async Task Deactivated_people_are_not_on_the_board()
    {
        using var db = NewDb();
        var evId = await SeedEventAsync(db);
        await AddPersonAsync(db, evId, ParticipantRole.Volunteer, "Active Alex", "alex@example.test");
        await AddPersonAsync(db, evId, ParticipantRole.Volunteer, "Gone Gerda", "gerda@example.test", isActive: false);

        var rows = await NewBoard(db).BuildAsync(evId, ParticipantRole.Volunteer);

        Assert.Equal("Active Alex", Assert.Single(rows).Name);
    }

    /// <summary>
    /// A sponsor contact with NO company link has no checklist at all. Reporting them at 0% would
    /// read as "has done nothing", which is untrue — there is nothing for them to do.
    /// </summary>
    [Fact]
    public async Task Sponsor_without_a_company_has_no_wizard_rather_than_zero_percent()
    {
        using var db = NewDb();
        var evId = await SeedEventAsync(db);
        await AddPersonAsync(db, evId, ParticipantRole.Sponsor, "Unlinked Ulla", "ulla@example.test", companyId: null);

        var row = Assert.Single(await NewBoard(db).BuildAsync(evId, ParticipantRole.Sponsor));

        Assert.False(row.HasWizard);
        Assert.False(row.IsComplete);
        Assert.False(row.NotStarted);          // "not started" is a judgement; this row earns none
        Assert.Empty(row.OpenStepKeys);
    }

    /// <summary>
    /// §854/§1081 — the board NAMES the blank content fields, in the same words the sponsor's own
    /// reminder uses. "Company details are incomplete" is not chaseable.
    /// </summary>
    [Fact]
    public async Task Sponsor_row_names_the_blank_content_fields()
    {
        using var db = NewDb();
        var evId = await SeedEventAsync(db);
        await AddPersonAsync(db, evId, ParticipantRole.Sponsor, "Coordinator", "coord@example.test", "9001", isCoordinator: true);
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = evId, SponsorCompanyId = "9001",
            CompanyDescription = "We do things.",   // delivered
            SocialMediaIntro = null,                // blank ⇒ must be named
            // Not an exhibitor and below Gold ⇒ HasBooth is false (it is derived, not stored), so
            // the exhibitor-only short description must not be demanded (§1081).
            IsExhibitor = false, SponsorPackage = SponsorPackage.Silver,
        });
        await db.SaveChangesAsync();

        var row = Assert.Single(await NewBoard(db).BuildAsync(evId, ParticipantRole.Sponsor));

        Assert.Contains(SponsorCompanyContent.SocialMediaIntroKey, row.MissingFieldKeys);
        Assert.DoesNotContain(SponsorCompanyContent.CompanyDescriptionKey, row.MissingFieldKeys);
        // Not an exhibitor ⇒ never chased for the field their own form refuses to save.
        Assert.DoesNotContain(SponsorCompanyContent.ShortDescriptionKey, row.MissingFieldKeys);
    }

    /// <summary>The role filter is the scope, and it is applied in SQL before any wizard is built.</summary>
    [Fact]
    public async Task Role_filter_scopes_the_board()
    {
        using var db = NewDb();
        var evId = await SeedEventAsync(db);
        await AddPersonAsync(db, evId, ParticipantRole.Volunteer, "Vera", "vera@example.test");
        await AddPersonAsync(db, evId, ParticipantRole.Speaker, "Sara Speaker", "sara@example.test");
        await AddPersonAsync(db, evId, ParticipantRole.Media, "Mads Media", "mads@example.test");

        Assert.Equal(3, (await NewBoard(db).BuildAsync(evId)).Count);
        Assert.Equal("Sara Speaker",
            Assert.Single(await NewBoard(db).BuildAsync(evId, ParticipantRole.Speaker)).Name);
    }

    /// <summary>At-risk first — the rows that need chasing are the ones on screen.</summary>
    [Fact]
    public async Task Overdue_rows_sort_to_the_top()
    {
        using var db = NewDb();
        var evId = await SeedEventAsync(db);
        var calm = await AddPersonAsync(db, evId, ParticipantRole.Volunteer, "Aaron Calm", "aaron@example.test");
        var late = await AddPersonAsync(db, evId, ParticipantRole.Volunteer, "Zoe Late", "zoe@example.test");
        db.Tasks.AddRange(
            new ParticipantTask { EventId = evId, AssignedParticipantId = calm.Id, Title = "Later", State = TaskState.Open, DueDate = Today.AddDays(5) },
            new ParticipantTask { EventId = evId, AssignedParticipantId = late.Id, Title = "Missed", State = TaskState.Open, DueDate = Today.AddDays(-2) });
        await db.SaveChangesAsync();

        var rows = await NewBoard(db).BuildAsync(evId, ParticipantRole.Volunteer);

        // Alphabetically Aaron leads; overdue overrules it.
        Assert.Equal("Zoe Late", rows[0].Name);
        Assert.True(rows[0].Overdue);
        Assert.False(rows[1].Overdue);
    }

    /// <summary>The completion filter the page offers — each state is mutually exclusive.</summary>
    [Fact]
    public async Task Completion_filter_states_are_exclusive()
    {
        using var db = NewDb();
        var evId = await SeedEventAsync(db);
        var p = await AddPersonAsync(db, evId, ParticipantRole.Volunteer, "Vera", "vera@example.test");
        db.Tasks.Add(new ParticipantTask
        {
            EventId = evId, AssignedParticipantId = p.Id, Title = "Missed",
            State = TaskState.Open, DueDate = Today.AddDays(-1),
        });
        await db.SaveChangesAsync();

        var row = Assert.Single(await NewBoard(db).BuildAsync(evId, ParticipantRole.Volunteer));

        Assert.True(row.Matches(ParticipantStatusFilter.All));
        Assert.True(row.Matches(ParticipantStatusFilter.Overdue));
        // Exactly one of the three completion states holds.
        var states = new[]
        {
            row.Matches(ParticipantStatusFilter.Complete),
            row.Matches(ParticipantStatusFilter.InProgress),
            row.Matches(ParticipantStatusFilter.NotStarted),
        };
        Assert.Equal(1, states.Count(s => s));
    }

    /// <summary>
    /// An empty edition is an empty board, not a crash — and it costs one query.
    /// </summary>
    [Fact]
    public async Task Empty_edition_returns_no_rows()
    {
        using var db = NewDb();
        var evId = await SeedEventAsync(db);

        Assert.Empty(await NewBoard(db).BuildAsync(evId));
    }

    // ───────────────── §1212 — TEST ROWS ARE OUT UNLESS HE ASKS ─────────────────
    //
    // Operator 2026-09-12: *"it shows test users also - i need to filter that out with option to
    // include"*. The board is a chase list, and a test account is not somebody to chase — it was
    // padding the counts AND the §1211 email export.

    /// <summary>🔴 The reported case: a test PERSON is not on the board.</summary>
    [Fact]
    public async Task A_test_user_is_hidden_by_default()
    {
        using var db = NewDb();
        var evId = await SeedEventAsync(db);

        var real = await AddPersonAsync(db, evId, ParticipantRole.Volunteer, "Vera", "vera@example.test");
        var test = await AddPersonAsync(db, evId, ParticipantRole.Volunteer, "Tess", "tess@example.test");
        test.IsTestUser = true;
        await db.SaveChangesAsync();

        var rows = await NewBoard(db).BuildAsync(evId);

        Assert.Contains(rows, r => r.ParticipantId == real.Id);
        Assert.DoesNotContain(rows, r => r.ParticipantId == test.Id);
    }

    /// <summary>🔒 …but the option brings them back — hidden, not deleted.</summary>
    [Fact]
    public async Task A_test_user_returns_when_asked_for()
    {
        using var db = NewDb();
        var evId = await SeedEventAsync(db);

        var test = await AddPersonAsync(db, evId, ParticipantRole.Volunteer, "Tess", "tess@example.test");
        test.IsTestUser = true;
        await db.SaveChangesAsync();

        var rows = await NewBoard(db).BuildAsync(evId, null, default, includeTest: true);

        Assert.Contains(rows, r => r.ParticipantId == test.Id);
    }

    /// <summary>
    /// ⚠️ Both halves. Sponsors are listed PER COMPANY, so filtering only people would leave the test
    /// COMPANY on the board with its contacts removed — the row he objected to, minus the reason.
    /// </summary>
    [Fact]
    public async Task A_test_sponsor_COMPANY_is_hidden_by_default()
    {
        using var db = NewDb();
        var evId = await SeedEventAsync(db);

        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = evId, SponsorCompanyId = "co-test", CompanyName = "Test Co",
            SponsorPackage = SponsorPackage.Gold, IsTestData = true,
        });
        // A perfectly ordinary contact — the exclusion has to come from the COMPANY.
        await AddPersonAsync(db, evId, ParticipantRole.Sponsor, "Real Person",
            "real@example.test", companyId: "co-test", isCoordinator: true);
        await db.SaveChangesAsync();

        Assert.Empty(await NewBoard(db).BuildAsync(evId, ParticipantRole.Sponsor));
        Assert.NotEmpty(await NewBoard(db).BuildAsync(
            evId, ParticipantRole.Sponsor, default, includeTest: true));
    }

    /// <summary>🔑 The ROLE filter still narrows — the bug he saw was the board answering for all roles.</summary>
    [Fact]
    public async Task The_role_filter_returns_only_that_role()
    {
        using var db = NewDb();
        var evId = await SeedEventAsync(db);

        await AddPersonAsync(db, evId, ParticipantRole.Volunteer, "Vera", "vera@example.test");
        await AddPersonAsync(db, evId, ParticipantRole.Speaker, "Sam", "sam@example.test");

        var rows = await NewBoard(db).BuildAsync(evId, ParticipantRole.Speaker);

        Assert.All(rows, r => Assert.Equal(ParticipantRole.Speaker, r.Role));
    }
}
