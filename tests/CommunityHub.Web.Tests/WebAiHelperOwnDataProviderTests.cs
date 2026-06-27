using CommunityHub.Assistant;
using CommunityHub.Core.Assistant;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Sponsors;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Own-data grounding for the AI Community Helper (REQUIREMENTS §129) exercised over the
/// REAL <see cref="WebAiHelperOwnDataProvider"/> on the EF in-memory provider. Proves the
/// role-scoped sections added for SPONSOR (own-company deliverables, §135) and
/// VOLUNTEER (own shifts + availability), and — security-critical — that every
/// query is pinned to the SERVER-supplied participant id / that participant's own
/// company:
///   • a sponsor's grounding contains THEIR company's deliverables and NEVER
///     another company's distinct deadline/state;
///   • a volunteer's grounding contains THEIR shifts + availability and NEVER
///     another volunteer's;
///   • speaker / attendee roles gain neither the sponsor nor the volunteer section.
/// FAKE names only; no network, no SQL, no secrets.
/// </summary>
public sealed class WebAiHelperOwnDataProviderTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"ai-helper-own-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-27T10:00:00Z");
    }

    private static WebAiHelperOwnDataProvider NewProvider(CommunityHubDbContext db) =>
        new(db, new FixedClock(), new SponsorDeliverablesService(db));

    private static Participant AddParticipant(
        CommunityHubDbContext db, int id, ParticipantRole role, string? sponsorCompanyId = null)
    {
        var p = new Participant
        {
            Id = id, EventId = EventId, Email = $"p{id}@fake.test", FullName = $"Person {id}",
            Role = role, IsActive = true, SponsorCompanyId = sponsorCompanyId,
        };
        db.Participants.Add(p);
        return p;
    }

    private static void AddEvent(CommunityHubDbContext db) =>
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", DisplayName = "Test Edition", CommunityName = "Test Community",
            StartDate = new DateOnly(2026, 9, 1), EndDate = new DateOnly(2026, 9, 2),
        });

    private static string Grounding(IReadOnlyList<AiHelperGroundingSection> sections) =>
        string.Join("\n\n", sections.Select(s => $"## {s.Heading}\n{s.Body}"));

    // ---------------------------------------------------------------------
    //  SPONSOR — own-company deliverables only
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Sponsor_grounding_has_own_company_deliverables_not_another_company()
    {
        using var db = NewDb();
        AddEvent(db);

        // The signed-in sponsor belongs to company "A".
        AddParticipant(db, 10, ParticipantRole.Sponsor, sponsorCompanyId: "A");
        // Another sponsor company "B" with a DISTINCT far-future deadline that must
        // never bleed into company A's grounding.
        AddParticipant(db, 11, ParticipantRole.Sponsor, sponsorCompanyId: "B");

        // Company A: nothing on file (onboarding not done) + a dated onboarding task.
        db.SponsorInfos.Add(new SponsorInfo { EventId = EventId, SponsorCompanyId = "A" });
        db.Tasks.Add(new ParticipantTask
        {
            EventId = EventId, SponsorCompanyId = "A",
            SourceKey = "sponsor:A:initial-onboarding-of-sponsor",
            Title = "Onboarding", State = TaskState.Open, DueDate = new DateOnly(2026, 7, 1),
        });

        // Company B: a distinct open to-do with a far-future deadline (2099-12-31).
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "B", CompanyDescription = "B is onboarded",
        });
        db.Tasks.Add(new ParticipantTask
        {
            EventId = EventId, SponsorCompanyId = "B",
            SourceKey = "sponsor:B:some-other-todo",
            Title = "B only", State = TaskState.Open, DueDate = new DateOnly(2099, 12, 31),
        });

        await db.SaveChangesAsync();

        var sections = await NewProvider(db).GetOwnDataAsync(EventId, 10, ParticipantRole.Sponsor);
        var text = Grounding(sections);

        Assert.Contains(sections, s => s.Heading == "Your deliverables");
        // Company A's OWN onboarding stage + its deadline are present.
        Assert.Contains("Contract & onboarding", text);
        Assert.Contains("2026-07-01", text);
        // Company B's distinct far-future deadline must NOT leak into A's grounding.
        Assert.DoesNotContain("2099-12-31", text);
    }

    [Fact]
    public async Task Sponsor_with_no_company_skips_deliverables_gracefully()
    {
        using var db = NewDb();
        AddEvent(db);
        AddParticipant(db, 20, ParticipantRole.Sponsor, sponsorCompanyId: null);
        await db.SaveChangesAsync();

        var sections = await NewProvider(db).GetOwnDataAsync(EventId, 20, ParticipantRole.Sponsor);

        Assert.DoesNotContain(sections, s => s.Heading == "Your deliverables");
    }

    // ---------------------------------------------------------------------
    //  VOLUNTEER — own shifts + availability only
    // ---------------------------------------------------------------------

    private static async Task<(int aId, int bId, int taskAId)> SeedVolunteersAsync(CommunityHubDbContext db)
    {
        AddEvent(db);
        var volA = AddParticipant(db, 30, ParticipantRole.Volunteer);
        var volB = AddParticipant(db, 31, ParticipantRole.Volunteer);
        await db.SaveChangesAsync();

        var cat = new VolunteerCategory { EventId = EventId, Name = "Registration" };
        db.VolunteerCategories.Add(cat);
        await db.SaveChangesAsync();
        var sub = new VolunteerSubcategory { EventId = EventId, CategoryId = cat.Id, Name = "Desk" };
        db.VolunteerSubcategories.Add(sub);
        await db.SaveChangesAsync();

        var taskA = new VolunteerTask
        {
            EventId = EventId, SubcategoryId = sub.Id, Title = "Staff the desk",
            DueDate = new DateOnly(2026, 9, 1), Shift = "Day 1 08:00", TimeEnd = "12:00",
        };
        var taskB = new VolunteerTask
        {
            EventId = EventId, SubcategoryId = sub.Id, Title = "Cloak room duty",
            DueDate = new DateOnly(2026, 9, 1),
        };
        db.VolunteerTasks.AddRange(taskA, taskB);
        await db.SaveChangesAsync();

        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = EventId, TaskId = taskA.Id, ParticipantId = volA.Id,
        });
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = EventId, TaskId = taskB.Id, ParticipantId = volB.Id,
        });

        db.VolunteerDayAvailabilities.Add(new VolunteerDayAvailability
        {
            EventId = EventId, ParticipantId = volA.Id, Day = new DateOnly(2026, 9, 1),
            Level = VolunteerAvailabilityLevel.Half, Note = "A-only-note",
        });
        db.VolunteerDayAvailabilities.Add(new VolunteerDayAvailability
        {
            EventId = EventId, ParticipantId = volB.Id, Day = new DateOnly(2026, 9, 1),
            Level = VolunteerAvailabilityLevel.Full, Note = "B-only-note",
        });
        db.VolunteerAvailabilities.Add(new VolunteerAvailability
        {
            EventId = EventId, ParticipantId = volA.Id,
            PreferredRole = "Registration-A", SelectedShifts = "Morning", MaxHoursPerDay = 6,
        });

        await db.SaveChangesAsync();
        return (volA.Id, volB.Id, taskA.Id);
    }

    [Fact]
    public async Task Volunteer_grounding_has_own_shifts_and_availability_not_another_volunteer()
    {
        using var db = NewDb();
        var (aId, _, _) = await SeedVolunteersAsync(db);

        var sections = await NewProvider(db).GetOwnDataAsync(EventId, aId, ParticipantRole.Volunteer);
        var text = Grounding(sections);

        Assert.Contains(sections, s => s.Heading == "Your shifts / assignments");
        Assert.Contains(sections, s => s.Heading == "Your availability");

        // Volunteer A's OWN shift + the WHEN window + own availability.
        Assert.Contains("Staff the desk", text);
        Assert.Contains("Day 1 08:00", text);
        Assert.Contains("A-only-note", text);
        Assert.Contains("Registration-A", text);

        // Volunteer B's shift + availability must NEVER appear in A's grounding.
        Assert.DoesNotContain("Cloak room duty", text);
        Assert.DoesNotContain("B-only-note", text);
    }

    [Fact]
    public async Task Volunteer_with_no_shifts_gets_a_friendly_empty_line()
    {
        using var db = NewDb();
        AddEvent(db);
        AddParticipant(db, 40, ParticipantRole.Volunteer);
        await db.SaveChangesAsync();

        var sections = await NewProvider(db).GetOwnDataAsync(EventId, 40, ParticipantRole.Volunteer);

        var shifts = Assert.Single(sections, s => s.Heading == "Your shifts / assignments");
        Assert.Contains("no volunteer shifts", shifts.Body);
        // No availability rows ⇒ no availability section.
        Assert.DoesNotContain(sections, s => s.Heading == "Your availability");
    }

    // ---------------------------------------------------------------------
    //  Other roles are unchanged — neither new section appears
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(ParticipantRole.Speaker)]
    [InlineData(ParticipantRole.Attendee)]
    public async Task Non_sponsor_non_volunteer_roles_get_neither_new_section(ParticipantRole role)
    {
        using var db = NewDb();
        AddEvent(db);
        AddParticipant(db, 50, role);
        await db.SaveChangesAsync();

        var sections = await NewProvider(db).GetOwnDataAsync(EventId, 50, role);

        Assert.DoesNotContain(sections, s => s.Heading == "Your deliverables");
        Assert.DoesNotContain(sections, s => s.Heading == "Your shifts / assignments");
        Assert.DoesNotContain(sections, s => s.Heading == "Your availability");
    }
}
