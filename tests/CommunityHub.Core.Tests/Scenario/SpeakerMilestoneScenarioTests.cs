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
    // The current speaker-task set (operator 2026-06-25): the 6 configured speaker
    // deadlines — Hotel, Appreciation Dinner, Swag/Speaker gift, Pre-day Lunch, plus
    // the two KEPT presentation uploads (preview + final). Logistics deadlines are
    // P12 entitlement-gated; the uploads are NEVER gated (every speaker gets them).
    // A Master Class (pre-day) speaker is entitled to the Pre-day Lunch, so gets all
    // 6; a plain speaker not on the pre-/main-day has no lunch entitlement, so the
    // Lunch deadline is gated out and they get 5.
    private static readonly DateOnly Oct1Due = new(2026, 10, 1);    // Hotel, Dinner, Swag
    private static readonly DateOnly LunchDue = new(2027, 1, 10);   // Pre-day Lunch (pre-day speakers only)
    private static readonly DateOnly PreviewDue = new(2027, 1, 20); // Upload preview presentation
    private static readonly DateOnly FinalDue = new(2027, 2, 3);    // Upload final presentation
    private const int MasterclassTaskCount = 6;   // + Pre-day Lunch (pre-day entitlement)
    private const int SpeakerTaskCount = 5;       // no Pre-day Lunch (not entitled)

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

        var mcTasks = await db.Tasks
            .Where(t => t.AssignedParticipantId == seed.MasterclassSpeakerId)
            .ToListAsync();

        // A Master Class (pre-day) speaker gets the full set of 6: Hotel/Dinner/Swag
        // (1 Oct), Pre-day Lunch (10 Jan), upload preview (20 Jan), upload final (3 Feb).
        Assert.Equal(MasterclassTaskCount, mcTasks.Count);
        Assert.Equal(3, mcTasks.Count(t => t.DueDate == Oct1Due));
        Assert.Single(mcTasks, t => t.DueDate == LunchDue);
        Assert.Single(mcTasks, t => t.DueDate == PreviewDue);
        Assert.Single(mcTasks, t => t.DueDate == FinalDue);
        Assert.All(mcTasks, t => Assert.Equal(TaskState.Open, t.State));
    }

    [Fact]
    public async Task All_speakers_get_the_same_task_set()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await NewSeeder(db).SeedAsync(seed.EventId);

        // A plain session speaker (not pre-/main-day) is NOT entitled to lunch, so the
        // Pre-day Lunch deadline is P12-gated out: they get 5 tasks — Hotel/Dinner/Swag
        // (1 Oct) + the two presentation uploads — but NOT the Pre-day Lunch the Master
        // Class speaker gets.
        var s1Tasks = await db.Tasks
            .Where(t => t.AssignedParticipantId == seed.SpeakerOneId)
            .ToListAsync();

        Assert.Equal(SpeakerTaskCount, s1Tasks.Count);
        Assert.Equal(3, s1Tasks.Count(t => t.DueDate == Oct1Due));
        Assert.DoesNotContain(s1Tasks, t => t.DueDate == LunchDue); // P12: lunch gated out
        Assert.Single(s1Tasks, t => t.DueDate == PreviewDue);
        Assert.Single(s1Tasks, t => t.DueDate == FinalDue);
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
