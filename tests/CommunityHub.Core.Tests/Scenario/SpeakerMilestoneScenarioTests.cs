using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: a speaker works through their milestone deadlines in the Speaker
/// hub. The GUI counterpart (scenario-speaker.spec.ts) drives /Tasks and the
/// hub progress; this backend half proves the DB state the GUI reflects:
///
///  - The seeder creates one task per shipped milestone, at the ABSOLUTE dates
///    documented in REQUIREMENTS §5 / speaker-deadlines.eldk27.json:
///       title+abstract  20 Jun 2026  (Master Class speakers ONLY)
///       verify bio+photo  1 Oct 2026  (all speakers)
///       draft deck       20 Jan 2027  (all speakers)
///       final deck        3 Feb 2027  (all speakers)
///  - The masterclass-only milestone is seeded for the Master Class speaker but
///    NOT for plain session speakers.
///  - Completing a task (the /Tasks "Mark done" postback) flips the row to Done
///    + stamps CompletedAt — i.e. the progress bar / countdown advance.
///  - The seeder is idempotent (a re-run creates nothing new).
/// </summary>
public sealed class SpeakerMilestoneScenarioTests
{
    private static readonly DateOnly TitleAbstractDue = new(2026, 6, 20);
    private static readonly DateOnly BioPhotoDue = new(2026, 10, 1);
    private static readonly DateOnly DraftDeckDue = new(2027, 1, 20);
    private static readonly DateOnly FinalDeckDue = new(2027, 2, 3);

    private static SpeakerDeadlineSeeder NewSeeder(Data.CommunityHubDbContext db) =>
        new(db,
            new SpeakerDeadlineOptions { ConfigPath = RepoPaths.SpeakerDeadlinesConfig() },
            ScenarioFixture.Clock);

    [Fact]
    public async Task Seeds_milestone_tasks_at_the_absolute_documented_dates()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var created = await NewSeeder(db).SeedAsync(seed.EventId);
        Assert.True(created > 0, "the seeder should create speaker-deadline tasks");

        // The Master Class speaker gets all four milestones at their exact dates.
        var mcTasks = await db.Tasks
            .Where(t => t.AssignedParticipantId == seed.MasterclassSpeakerId)
            .OrderBy(t => t.DueDate)
            .ToListAsync();

        Assert.Equal(4, mcTasks.Count);
        Assert.Equal(TitleAbstractDue, mcTasks[0].DueDate);
        Assert.Equal(BioPhotoDue, mcTasks[1].DueDate);
        Assert.Equal(DraftDeckDue, mcTasks[2].DueDate);
        Assert.Equal(FinalDeckDue, mcTasks[3].DueDate);
        Assert.All(mcTasks, t => Assert.Equal(TaskState.Open, t.State));
    }

    [Fact]
    public async Task Title_and_abstract_milestone_is_masterclass_only()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await NewSeeder(db).SeedAsync(seed.EventId);

        // The plain session speaker gets the three all-speaker milestones, but
        // NOT the masterclass-only title+abstract one (20 Jun 2026).
        var s1Tasks = await db.Tasks
            .Where(t => t.AssignedParticipantId == seed.SpeakerOneId)
            .ToListAsync();

        Assert.Equal(3, s1Tasks.Count);
        Assert.DoesNotContain(s1Tasks, t => t.DueDate == TitleAbstractDue);
        Assert.Contains(s1Tasks, t => t.DueDate == BioPhotoDue);
        Assert.Contains(s1Tasks, t => t.DueDate == DraftDeckDue);
        Assert.Contains(s1Tasks, t => t.DueDate == FinalDeckDue);
    }

    [Fact]
    public async Task Completing_a_milestone_flips_the_row_done_and_advances_progress()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await NewSeeder(db).SeedAsync(seed.EventId);

        var speakerTasks = await db.Tasks
            .Where(t => t.AssignedParticipantId == seed.SpeakerOneId)
            .ToListAsync();
        var totalBefore = speakerTasks.Count;
        var doneBefore = speakerTasks.Count(t => t.State == TaskState.Done);
        Assert.Equal(0, doneBefore);

        // Simulate the /Tasks "Mark done" postback (same mutation the page does).
        var first = speakerTasks.OrderBy(t => t.DueDate).First();
        first.State = TaskState.Done;
        first.CompletedAt = ScenarioFixture.Clock.GetUtcNow();
        await db.SaveChangesAsync();

        var after = await db.Tasks
            .Where(t => t.AssignedParticipantId == seed.SpeakerOneId)
            .ToListAsync();
        var doneAfter = after.Count(t => t.State == TaskState.Done);

        Assert.Equal(1, doneAfter);
        Assert.Equal(totalBefore, after.Count); // nothing added/removed
        Assert.NotNull(after.Single(t => t.Id == first.Id).CompletedAt);

        // The progress fraction the hub renders advances 0/3 -> 1/3.
        Assert.Equal(0, doneBefore);
        Assert.True(doneAfter > doneBefore);
    }

    [Fact]
    public async Task Seeder_is_idempotent()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var firstRun = await NewSeeder(db).SeedAsync(seed.EventId);
        var secondRun = await NewSeeder(db).SeedAsync(seed.EventId);

        Assert.True(firstRun > 0);
        Assert.Equal(0, secondRun); // re-run creates nothing
    }
}
