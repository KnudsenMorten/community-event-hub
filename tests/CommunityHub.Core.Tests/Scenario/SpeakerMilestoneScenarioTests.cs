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
///       Hotel / Dinner / Swag  1 Oct 2026  (entitlement-gated)
///       Pre-day Lunch + Travel 10 Jan 2027 (gated / non-DK)
///       Upload preview deck    20 Jan 2027  (all speakers)
///       Upload final deck        3 Feb 2027  (all speakers)
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
    // §295 (operator 2026-07-11): EVERY speaker is now entitled to the Pre-day Lunch (opt-in), so
    // both the master-class speaker AND a plain speaker get the full set of 7 (incl. the Pre-day
    // Lunch deadline). The test name holds literally: all speakers get the SAME task set.
    private static readonly DateOnly Oct1Due = new(2026, 10, 1);    // Hotel, Dinner, Swag
    private static readonly DateOnly LunchDue = new(2027, 1, 10);   // Pre-day Lunch (pre-day speakers only)
    private static readonly DateOnly TravelDue = new(2027, 1, 10);  // Submit travel reimbursement (non-DK)
    private static readonly DateOnly PromoteDue = new(2027, 1, 15); // §314: Help to promote your session(s)
    private static readonly DateOnly PreviewDue = new(2027, 1, 20); // Upload preview presentation
    private static readonly DateOnly FinalDue = new(2027, 2, 3);    // Upload final presentation
    private const string LunchTitle = "Pre-day Lunch";
    private const string TravelTitle = "Submit travel reimbursement";
    private const string PromoteTitle = "Help to promote your session(s)";
    private const int MasterclassTaskCount = 8;   // + Pre-day Lunch (pre-day entitlement) + travel + §314 promote
    private const int SpeakerTaskCount = 8;        // §295: every speaker now gets the Pre-day Lunch too; §314 promote

    private static SpeakerDeadlineSeeder NewSeeder(Data.CommunityHubDbContext db) =>
        new(db,
            new SpeakerDeadlineOptions { ConfigPath = RepoPaths.SpeakerDeadlinesConfig() },
            ScenarioFixture.Clock);

    [PrivateContentFact] // pins the upstream edition's own config values
    public async Task Seeds_milestone_tasks_at_the_absolute_documented_dates()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var created = await NewSeeder(db).SeedAsync(seed.EventId);
        Assert.True(created > 0, "the seeder should create speaker-deadline tasks");

        var mcTasks = await db.Tasks
            .Where(t => t.AssignedParticipantId == seed.MasterclassSpeakerId)
            .ToListAsync();

        // A Master Class (pre-day) speaker gets the full set of 8: Hotel/Dinner/Swag
        // (1 Oct), Pre-day Lunch (10 Jan), Submit travel reimbursement (10 Jan, non-DK),
        // Help to promote (15 Jan, §314), upload preview (20 Jan), upload final (3 Feb).
        // Lunch + travel share 10 Jan, so those two are asserted by title rather than by
        // due date.
        Assert.Equal(MasterclassTaskCount, mcTasks.Count);
        Assert.Equal(3, mcTasks.Count(t => t.DueDate == Oct1Due));
        Assert.Single(mcTasks, t => t.Title == LunchTitle && t.DueDate == LunchDue);
        Assert.Single(mcTasks, t => t.Title == TravelTitle && t.DueDate == TravelDue);
        Assert.Equal(2, mcTasks.Count(t => t.DueDate == LunchDue)); // lunch + travel
        Assert.Single(mcTasks, t => t.Title == PromoteTitle && t.DueDate == PromoteDue);
        Assert.Single(mcTasks, t => t.DueDate == PreviewDue);
        Assert.Single(mcTasks, t => t.DueDate == FinalDue);
        Assert.All(mcTasks, t => Assert.Equal(TaskState.Open, t.State));
    }

    [PrivateContentFact] // pins the upstream edition's own config values
    public async Task All_speakers_get_the_same_task_set()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await NewSeeder(db).SeedAsync(seed.EventId);

        // §295: a plain session speaker IS now entitled to the Pre-day Lunch (opt-in), so they get
        // the same 7 tasks as the Master Class speaker — Hotel/Dinner/Swag (1 Oct) + Pre-day Lunch
        // (10 Jan) + the §143 travel task (non-DK) + the two presentation uploads.
        var s1Tasks = await db.Tasks
            .Where(t => t.AssignedParticipantId == seed.SpeakerOneId)
            .ToListAsync();

        Assert.Equal(SpeakerTaskCount, s1Tasks.Count);
        Assert.Equal(3, s1Tasks.Count(t => t.DueDate == Oct1Due));
        Assert.Single(s1Tasks, t => t.Title == LunchTitle);         // §295: every speaker gets lunch
        Assert.Single(s1Tasks, t => t.Title == TravelTitle);        // §143: non-DK gets travel
        Assert.Single(s1Tasks, t => t.Title == PromoteTitle);       // §314: every speaker promotes
        Assert.Single(s1Tasks, t => t.DueDate == PreviewDue);
        Assert.Single(s1Tasks, t => t.DueDate == FinalDue);
    }

    [PrivateContentFact] // pins the upstream edition's own config values
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

        // 🔒 §708 — THE LINK MOVED, AND THAT MOVE *IS* THE MIGRATION.
        //
        // This used to assert `travel.Description` contained "/Forms/Travel". A registry-backed row
        // stores NO prose (§684.14), so the column is null by design — asserting on it now would
        // pass only if the migration had failed. The destination lives in the authored body, as a
        // canonical route resolved from FormRoutes rather than the absolute
        // "https://eldk27.eventhub.expertslive.dk/Forms/Travel" the config used to hardcode
        // (§708.2a — "door 3 must go": edition-specific, unable to follow a route change).
        Assert.Null(travel.Description);

        var body = new CommunityHub.Core.Tasks.Definitions.TaskBodyStore().LoadRaw("speaker/travel");
        Assert.Contains("{{travelFormUrl}}", body);
        Assert.Equal("/Forms/Travel", CommunityHub.Core.Forms.FormRoutes.For("travel"));
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
