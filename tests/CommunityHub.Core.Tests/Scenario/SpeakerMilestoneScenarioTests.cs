using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
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
    // The current speaker-task set (operator 2026-06-27): Hotel, Appreciation Dinner,
    // Swag/Speaker gift, Pre-day Lunch, the §143 country-gated "Submit travel
    // reimbursement" (non-Denmark speakers only), plus the two KEPT presentation
    // uploads (preview + final). Logistics deadlines are P12 entitlement-gated; the
    // uploads are NEVER gated; the travel task is country-gated. The seeded cast has
    // no country set, so they count as non-Denmark and DO get the travel task.
    // A Master Class (pre-day) speaker is entitled to the Pre-day Lunch, so gets all
    // 7; a plain speaker not on the pre-/main-day has no lunch entitlement, so the
    // Lunch deadline is gated out and they get 6.
    private static readonly DateOnly Oct1Due = new(2026, 10, 1);    // Hotel, Dinner, Swag
    private static readonly DateOnly LunchDue = new(2027, 1, 10);   // Pre-day Lunch (pre-day speakers only)
    private static readonly DateOnly TravelDue = new(2027, 1, 10);  // Submit travel reimbursement (non-DK)
    private static readonly DateOnly PreviewDue = new(2027, 1, 20); // Upload preview presentation
    private static readonly DateOnly FinalDue = new(2027, 2, 3);    // Upload final presentation
    private const string LunchTitle = "Pre-day Lunch";
    private const string TravelTitle = "Submit travel reimbursement";
    private const int MasterclassTaskCount = 7;   // + Pre-day Lunch (pre-day entitlement) + travel
    private const int SpeakerTaskCount = 6;        // no Pre-day Lunch (not entitled), + travel

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

        // A Master Class (pre-day) speaker gets the full set of 7: Hotel/Dinner/Swag
        // (1 Oct), Pre-day Lunch (10 Jan), Submit travel reimbursement (10 Jan, non-DK),
        // upload preview (20 Jan), upload final (3 Feb). Lunch + travel share 10 Jan, so
        // those two are asserted by title rather than by due date.
        Assert.Equal(MasterclassTaskCount, mcTasks.Count);
        Assert.Equal(3, mcTasks.Count(t => t.DueDate == Oct1Due));
        Assert.Single(mcTasks, t => t.Title == LunchTitle && t.DueDate == LunchDue);
        Assert.Single(mcTasks, t => t.Title == TravelTitle && t.DueDate == TravelDue);
        Assert.Equal(2, mcTasks.Count(t => t.DueDate == LunchDue)); // lunch + travel
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
        // Pre-day Lunch deadline is P12-gated out: they get 6 tasks — Hotel/Dinner/Swag
        // (1 Oct) + the §143 travel task (non-DK) + the two presentation uploads — but
        // NOT the Pre-day Lunch the Master Class speaker gets.
        var s1Tasks = await db.Tasks
            .Where(t => t.AssignedParticipantId == seed.SpeakerOneId)
            .ToListAsync();

        Assert.Equal(SpeakerTaskCount, s1Tasks.Count);
        Assert.Equal(3, s1Tasks.Count(t => t.DueDate == Oct1Due));
        Assert.DoesNotContain(s1Tasks, t => t.Title == LunchTitle); // P12: lunch gated out
        Assert.Single(s1Tasks, t => t.Title == TravelTitle);        // §143: non-DK gets travel
        Assert.Single(s1Tasks, t => t.DueDate == PreviewDue);
        Assert.Single(s1Tasks, t => t.DueDate == FinalDue);
    }

    [Fact]
    public async Task Travel_task_is_non_denmark_only_and_links_to_the_travel_form()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        // Make SpeakerOne a Danish speaker; the Master Class speaker stays non-DK
        // (no country set). §143: Danish speakers must NOT get the travel task.
        var s1Profile = await db.SpeakerProfiles.FirstAsync(p => p.ParticipantId == seed.SpeakerOneId);
        s1Profile.Country = "DK";
        await db.SaveChangesAsync();

        await NewSeeder(db).SeedAsync(seed.EventId);

        var dkTasks = await db.Tasks
            .Where(t => t.AssignedParticipantId == seed.SpeakerOneId).ToListAsync();
        var nonDkTasks = await db.Tasks
            .Where(t => t.AssignedParticipantId == seed.MasterclassSpeakerId).ToListAsync();

        // Danish speaker: no travel task.
        Assert.DoesNotContain(dkTasks, t => t.Title == TravelTitle);
        // Non-Denmark speaker: gets it, dated 10 Jan 2027, linking to the travel form.
        var travel = Assert.Single(nonDkTasks, t => t.Title == TravelTitle);
        Assert.Equal(TravelDue, travel.DueDate);
        Assert.Contains("/Forms/Travel", travel.Description);
    }

    [Fact]
    public async Task Travel_task_can_be_marked_complete_without_claiming()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await NewSeeder(db).SeedAsync(seed.EventId);

        var travel = await db.Tasks.FirstAsync(
            t => t.AssignedParticipantId == seed.SpeakerOneId && t.Title == TravelTitle);
        Assert.Equal(TaskState.Open, travel.State);

        // The /Speaker/Tasks "Mark done" path: SpeakerMilestoneService.ToggleAsync —
        // it flips any speakerdl: task, so the speaker opts out without ever claiming.
        var svc = new SpeakerMilestoneService(db, ScenarioFixture.Clock);
        var changed = await svc.ToggleAsync(seed.EventId, seed.SpeakerOneId, travel.Id);

        Assert.True(changed);
        var after = await db.Tasks.FirstAsync(t => t.Id == travel.Id);
        Assert.Equal(TaskState.Done, after.State);
        Assert.NotNull(after.CompletedAt);
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
