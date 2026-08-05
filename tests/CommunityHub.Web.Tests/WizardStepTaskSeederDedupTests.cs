using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using CommunityHub.Forms.Steps;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §708.12 — a duplicate <see cref="ParticipantTask.SourceKey"/> is repaired, not tolerated.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Found on DEV, and it had already cost a whole feature.</b> Participant 1 held
/// <c>profile:1</c>, <c>accept:1</c> and <c>party-form:1</c> each THREE times, so <c>/Tasks</c>
/// rendered the same task three times. Worse, the seeder built its lookup with
/// <c>ToDictionaryAsync(SourceKey)</c>, which THROWS on the second row — and every caller wraps the
/// seeder in a silent <c>catch</c>. So the seeder did nothing at all for that person, which is why
/// §708.10's embedded forms never appeared.</para>
///
/// <para>A SourceKey IS the idempotency key: two rows sharing one is never correct. It is a lost
/// race between two concurrent seeds, and nothing in the database prevents it.</para>
/// </remarks>
public sealed class WizardStepTaskSeederDedupTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 8, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"seederdedup-{Guid.NewGuid():N}")
            .Options);

    private static WizardStepTaskSeeder Seeder(CommunityHubDbContext db) =>
        new(db, new SpeakerWizardService(db), new RoleWizardService(db), new FixedClock());

    private static async Task<(int EventId, Participant Person)> SeedAsync(
        CommunityHubDbContext db, ParticipantRole role)
    {
        var evt = new Event
        {
            Code = "T27", CommunityName = "Test Community",
            DisplayName = "Test Community 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        var p = new Participant
        {
            EventId = evt.Id, Email = "person@example.test", FullName = "Test Person",
            Role = role, IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return (evt.Id, p);
    }

    private static ParticipantTask Row(
        int eventId, int pid, string key, TaskState state, string title = "Your profile") => new()
        {
            EventId = eventId, AssignedParticipantId = pid, SourceKey = key,
            Title = title, State = state, CreatedAt = Now,
            CompletedAt = state == TaskState.Done ? Now : null,
        };

    [Fact]
    public async Task Duplicate_rows_are_collapsed_to_one()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedAsync(db, ParticipantRole.Media);
        var key = CommunityHub.Core.Participants.WizardStepTaskKeys.Profile(p.Id);

        // The exact DEV shape: the same key three times for one participant.
        db.Tasks.AddRange(
            Row(eventId, p.Id, key, TaskState.Open),
            Row(eventId, p.Id, key, TaskState.Open),
            Row(eventId, p.Id, key, TaskState.Open));
        await db.SaveChangesAsync();

        await Seeder(db).EnsureForParticipantAsync(eventId, p.Id, p.Role);

        Assert.Single(await db.Tasks.Where(t => t.SourceKey == key).ToListAsync());
    }

    [Fact]
    public async Task The_DONE_row_is_the_one_that_survives()
    {
        // 🔒 The rule that matters most. Completion is the only state here that cannot be
        // reconstructed — keeping an Open row over a Done one would re-open a task the person has
        // already finished and start chasing them for it by e-mail again.
        using var db = NewDb();
        var (eventId, p) = await SeedAsync(db, ParticipantRole.Media);
        var key = CommunityHub.Core.Participants.WizardStepTaskKeys.Accept(p.Id);

        // The Done row is deliberately added LAST, so "keep the first/oldest" alone would drop it.
        db.Tasks.AddRange(
            Row(eventId, p.Id, key, TaskState.Open, "Code of Conduct & Privacy"),
            Row(eventId, p.Id, key, TaskState.Open, "Code of Conduct & Privacy"),
            Row(eventId, p.Id, key, TaskState.Done, "Code of Conduct & Privacy"));
        await db.SaveChangesAsync();

        await Seeder(db).EnsureForParticipantAsync(eventId, p.Id, p.Role);

        var survivor = Assert.Single(await db.Tasks.Where(t => t.SourceKey == key).ToListAsync());
        Assert.Equal(TaskState.Done, survivor.State);
    }

    [Fact]
    public async Task A_duplicate_no_longer_stops_the_seeder_doing_its_job()
    {
        // The real damage was never the extra row on screen — it was that ToDictionary threw and
        // the whole seeder pass silently did nothing. Proven by the thing it should have done:
        // stamping FormStepKey, which is what makes /Tasks embed the form (§708.10).
        using var db = NewDb();
        var (eventId, p) = await SeedAsync(db, ParticipantRole.Media);
        var key = CommunityHub.Core.Participants.WizardStepTaskKeys.Profile(p.Id);

        db.Tasks.AddRange(
            Row(eventId, p.Id, key, TaskState.Open),
            Row(eventId, p.Id, key, TaskState.Open));
        await db.SaveChangesAsync();

        await Seeder(db).EnsureForParticipantAsync(eventId, p.Id, p.Role);

        var survivor = Assert.Single(await db.Tasks.Where(t => t.SourceKey == key).ToListAsync());
        Assert.Equal("profile", survivor.FormStepKey);
    }

    [Fact]
    public async Task Rows_with_DIFFERENT_keys_are_never_collapsed()
    {
        // The guard on the guard: de-dup keys on SourceKey, so two genuinely different tasks that
        // happen to share a title must both survive.
        using var db = NewDb();
        var (eventId, p) = await SeedAsync(db, ParticipantRole.Media);

        db.Tasks.AddRange(
            Row(eventId, p.Id, CommunityHub.Core.Participants.WizardStepTaskKeys.Profile(p.Id),
                TaskState.Open, "Same title"),
            Row(eventId, p.Id, CommunityHub.Core.Participants.WizardStepTaskKeys.Accept(p.Id),
                TaskState.Open, "Same title"));
        await db.SaveChangesAsync();

        await Seeder(db).EnsureForParticipantAsync(eventId, p.Id, p.Role);

        Assert.Equal(2, await db.Tasks.CountAsync(
            t => t.AssignedParticipantId == p.Id && t.Title == "Same title"));
    }
}
