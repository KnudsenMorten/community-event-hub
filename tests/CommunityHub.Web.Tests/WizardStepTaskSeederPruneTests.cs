using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using CommunityHub.Forms.Steps;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §253 G17 hygiene — <see cref="WizardStepTaskSeeder"/> is no longer
/// creation-only: a step that VANISHES from the participant's wizard (an
/// entitlement override-exclude shrank it) gets its still-OPEN mirror task
/// pruned on the next seeder pass, instead of polluting My-Tasks (and, for a
/// dated logistics task, firing a due-day reminder for a form the person can
/// no longer even open). Completed (Done) tasks keep their audit trail; keys
/// owned by other seeders (<c>party-form:</c>, <c>speakerdl:</c>) are never
/// touched. FAKE names.
/// </summary>
public sealed class WizardStepTaskSeederPruneTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 8, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"seederprune-{Guid.NewGuid():N}")
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

    [Fact]
    public async Task Override_exclude_prunes_the_now_stale_open_step_task()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedAsync(db, ParticipantRole.Media);
        var seeder = Seeder(db);

        // First pass: media are hotel-entitled, so the hotel step task seeds.
        await seeder.EnsureForParticipantAsync(eventId, p.Id, p.Role);
        var hotelKey = $"{HotelFormService.HotelTaskKey}:{p.Id}";
        Assert.NotNull(await db.Tasks.SingleOrDefaultAsync(t => t.SourceKey == hotelKey));

        // The organizer force-EXCLUDES hotel — the wizard step disappears.
        db.ParticipantOrderOverrides.Add(new ParticipantOrderOverride
        {
            EventId = eventId, ParticipantId = p.Id,
            Item = OrderItem.Hotel, Include = false,
        });
        await db.SaveChangesAsync();

        await seeder.EnsureForParticipantAsync(eventId, p.Id, p.Role);

        // The stale OPEN task is gone; the still-valid steps keep theirs.
        Assert.Null(await db.Tasks.SingleOrDefaultAsync(t => t.SourceKey == hotelKey));
        Assert.NotNull(await db.Tasks.SingleOrDefaultAsync(
            t => t.SourceKey == $"{DinnerFormService.DinnerTaskKey}:{p.Id}"));
    }

    [Fact]
    public async Task Prune_never_touches_a_completed_task_or_foreign_keys()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedAsync(db, ParticipantRole.Media);
        var seeder = Seeder(db);
        await seeder.EnsureForParticipantAsync(eventId, p.Id, p.Role);

        // The person COMPLETED the hotel form before losing the entitlement.
        var hotelKey = $"{HotelFormService.HotelTaskKey}:{p.Id}";
        var hotelTask = await db.Tasks.SingleAsync(t => t.SourceKey == hotelKey);
        hotelTask.State = TaskState.Done;
        hotelTask.CompletedAt = Now;
        // A key owned by ANOTHER seeder must survive any prune, whatever its state.
        db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId, AssignedParticipantId = p.Id,
            Title = "Party sign-up", State = TaskState.Open,
            SourceKey = Core.Config.PartyTaskSeeder.SourceKeyFor(p.Id),
        });
        db.ParticipantOrderOverrides.Add(new ParticipantOrderOverride
        {
            EventId = eventId, ParticipantId = p.Id,
            Item = OrderItem.Hotel, Include = false,
        });
        await db.SaveChangesAsync();

        await seeder.EnsureForParticipantAsync(eventId, p.Id, p.Role);

        // Done task keeps its audit trail; the party seeder's row is untouched.
        Assert.Equal(TaskState.Done,
            (await db.Tasks.SingleAsync(t => t.SourceKey == hotelKey)).State);
        Assert.NotNull(await db.Tasks.SingleOrDefaultAsync(
            t => t.SourceKey == Core.Config.PartyTaskSeeder.SourceKeyFor(p.Id)));
    }

    [Fact]
    public async Task Legacy_speaker_form_key_tasks_are_pruned_in_favour_of_speakerdl()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedAsync(db, ParticipantRole.Speaker);

        // A pre-§234-7c leftover: a speaker holding a FORM-key logistics task
        // (their logistics are owned by speakerdl: deadline tasks instead).
        db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId, AssignedParticipantId = p.Id,
            Title = "Complete the Hotel form", State = TaskState.Open,
            SourceKey = $"{HotelFormService.HotelTaskKey}:{p.Id}",
        });
        // A speakerdl task must never be touched by THIS seeder's prune.
        db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId, AssignedParticipantId = p.Id,
            Title = "Upload preview deck", State = TaskState.Open,
            SourceKey = $"speakerdl:{p.Id}:preview-deck",
        });
        await db.SaveChangesAsync();

        await Seeder(db).EnsureForParticipantAsync(eventId, p.Id, p.Role);

        Assert.Null(await db.Tasks.SingleOrDefaultAsync(
            t => t.SourceKey == $"{HotelFormService.HotelTaskKey}:{p.Id}"));
        Assert.NotNull(await db.Tasks.SingleOrDefaultAsync(
            t => t.SourceKey == $"speakerdl:{p.Id}:preview-deck"));
    }
}
