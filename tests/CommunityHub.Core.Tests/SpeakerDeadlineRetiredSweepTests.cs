using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §340-F — the retired-task sweep inside <see cref="SpeakerDeadlineSeeder"/> must match by EXACT
/// key, never by a title substring.
///
/// <para><b>The trap this closes.</b> The sweep used to delete anything whose <c>SourceKey</c>
/// started <c>speakerdl:</c> AND whose <c>Title</c> merely CONTAINED "abstract". The seeder runs on
/// speaker PAGE LOAD (§326a), so the day someone added a deadline titled e.g. <i>"Review your
/// session abstract"</i>, the per-speaker loop would CREATE it and this sweep would DELETE it in the
/// same pass — forever, on every page load, with nothing logged. A task that can never exist, whose
/// symptom points nowhere near its cause.</para>
///
/// <para>It was latent, not live: no config title contained the word. That is precisely why it
/// needed a test rather than a comment — the hazard is armed by a future CONTENT edit, and a content
/// edit is the kind of change nobody runs a code review over. So the first test drives the seeder
/// through a real CONFIG FILE containing such a deadline, which is the actual future scenario,
/// rather than planting a row and hoping the other prunes leave it alone.</para>
/// </summary>
public sealed class SpeakerDeadlineRetiredSweepTests : IDisposable
{
    private const int EventId = 1;

    private readonly string _configPath = Path.Combine(
        Path.GetTempPath(), $"speaker-deadlines-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_configPath);

    [Fact]
    public async Task A_configured_deadline_that_merely_MENTIONS_abstract_is_created_AND_KEPT()
    {
        await using var db = NewDb();
        await SeedSpeakerAsync(db);

        // Exactly the future content edit that armed the trap. The lower-case title is the one that
        // proves the fix: under the old predicate it was created and deleted in the same pass.
        //
        // The capitalised title is here as documentation, not proof — on SQL Server the old
        // Contains() became a case-INSENSITIVE LIKE and would have caught it too, but this test runs
        // on the in-memory provider where Contains is case-sensitive. Worth keeping so the widened
        // blast radius is written down; worth saying it is not what this test demonstrates.
        WriteConfig(
            ("Review your session abstract", "2026-10-01"),
            ("Polish your Abstract", "2026-10-01"));

        // Run TWICE. Once proves it is created; twice proves it is not created-then-deleted on the
        // next page load, which is how this would actually have shown up.
        await Seeder(db).SeedAsync(EventId);
        await Seeder(db).SeedAsync(EventId);

        var titles = await db.Tasks.Select(t => t.Title).ToListAsync();
        Assert.Contains("Review your session abstract", titles);
        Assert.Contains("Polish your Abstract", titles);
    }

    [Fact]
    public async Task The_actually_retired_rows_are_STILL_swept()
    {
        // Narrowing the match must not mean the sweep stops doing its job. All three retired forms
        // are still removed: the legacy key found on prod 2026-07-10, the exact `speakerdl:` slug
        // that deadline would have carried, and the exact retired title regardless of prefix.
        await using var db = NewDb();
        var pid = await SeedSpeakerAsync(db);
        WriteConfig(("Hotel", "2026-10-01"));

        db.Tasks.Add(Task_("seed:speaker:abstract", "Submit session title and abstract", pid));
        db.Tasks.Add(Task_($"speakerdl:{pid}:submit-session-title-and-abstract",
            "Submit session title and abstract", pid));
        db.Tasks.Add(Task_("legacy:other", "Submit session title and abstract", pid));
        await db.SaveChangesAsync();

        await Seeder(db).SeedAsync(EventId);

        // §1082 — SWEPT now means RETIRED, not deleted. §264 retired this deadline by DELETING its
        // rows, which is exactly the pattern the operator ruled out on 2026-08-13 — a speaker who had
        // already submitted their title and abstract lost the record of having done so. The outcome
        // §264 wanted (nobody keeps seeing a retired task) is delivered by closing it: the rows are
        // Done, labelled as a system closure, and filtered out of every participant-facing list.
        var swept = await db.Tasks
            .Where(t => t.Title == "Submit session title and abstract"
                        || t.SourceKey == "seed:speaker:abstract")
            .ToListAsync();
        Assert.NotEmpty(swept);                                            // kept for audit
        Assert.All(swept, t => Assert.Equal(TaskState.Done, t.State));     // and no longer asked
        Assert.All(swept, t => Assert.Equal(TaskClosedReason.RetiredFromCatalog, t.ClosedReason));

        // 🔑 The property that actually matters: none of them is visible as the speaker's own work.
        Assert.Empty(swept.Where(t => !CommunityHub.Core.Tasks.TaskClosure.IsSystemClosed(t)));
    }

    // ---- fixture ----------------------------------------------------------

    private void WriteConfig(params (string Title, string DueDate)[] deadlines)
    {
        var items = string.Join(",", deadlines.Select(d =>
            $$"""{"title":"{{d.Title}}","description":"x","dueDate":"{{d.DueDate}}"}"""));
        File.WriteAllText(_configPath, $$"""{"deadlines":[{{items}}]}""");
    }

    private SpeakerDeadlineSeeder Seeder(CommunityHubDbContext db) =>
        new(db, new SpeakerDeadlineOptions { ConfigPath = _configPath }, TimeProvider.System);

    private static ParticipantTask Task_(string sourceKey, string title, int participantId) => new()
    {
        EventId = EventId,
        AssignedParticipantId = participantId,
        SourceKey = sourceKey,
        Title = title,
        State = TaskState.Open,
    };

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"deadline-sweep-{Guid.NewGuid():N}")
            .Options);

    private static async Task<int> SeedSpeakerAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event { Id = EventId, Code = "TEST", IsActive = true });
        var p = new Participant
        {
            EventId = EventId,
            Email = "speaker@example.test",
            FullName = "Test Speaker",
            Role = ParticipantRole.Speaker,
            IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }
}
