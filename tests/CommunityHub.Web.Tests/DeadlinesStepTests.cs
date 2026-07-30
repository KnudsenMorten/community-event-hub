using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using CommunityHub.Forms.Steps;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §400 — the "Your tasks &amp; deadlines" wizard step (operator 2026-07-26: <i>"1 extra step in the
/// get started wizard which lists all tasks + deadlines outside of the get started wizard … Calendar
/// Reminder per task + Send Calendar invites for all tasks (one button)"</i>).
///
/// <para>What is worth pinning is the ORDER the rows come out in and the two properties the step's
/// buttons depend on. The step is read-only, so it cannot corrupt anything — but it can mislead,
/// and a deadline list that misleads is worse than no list: someone reads the top of it and plans
/// their week around the wrong item.</para>
/// </summary>
public sealed class DeadlinesStepTests
{
    private const int EventId = 1;

    [Fact]
    public async Task Pending_is_ordered_soonest_first_with_the_UNDATED_items_last()
    {
        // "No date" is not "not required", so undated items still appear — but they cannot be acted
        // on by a date, so they must never displace a real deadline from the top of the list.
        await using var db = NewDb();
        var pid = await SeedParticipantAsync(db);

        db.Tasks.AddRange(
            Task_(pid, "Later deadline", new DateOnly(2027, 1, 10)),
            Task_(pid, "No deadline at all", null),
            Task_(pid, "Sooner deadline", new DateOnly(2026, 10, 1)));
        await db.SaveChangesAsync();

        var model = await Service(db).LoadAsync(EventId, pid, default);

        Assert.Equal(
            new[] { "Sooner deadline", "Later deadline", "No deadline at all" },
            model.Pending.Select(r => r.Title).ToArray());
        Assert.True(model.AnyDated);
    }

    [Fact]
    public async Task Completed_items_are_listed_SEPARATELY_not_mixed_into_the_pending_list()
    {
        // The step shows the whole picture, not just the nagging half — but a done item in the
        // "still to do" list would be read as still to do.
        await using var db = NewDb();
        var pid = await SeedParticipantAsync(db);

        var done = Task_(pid, "Already handled", new DateOnly(2026, 9, 1));
        done.State = TaskState.Done;
        done.CompletedAt = DateTimeOffset.UtcNow;
        db.Tasks.AddRange(done, Task_(pid, "Still open", new DateOnly(2026, 10, 1)));
        await db.SaveChangesAsync();

        var model = await Service(db).LoadAsync(EventId, pid, default);

        Assert.Equal("Still open", Assert.Single(model.Pending).Title);
        Assert.Equal("Already handled", Assert.Single(model.Completed).Title);
    }

    [Fact]
    public async Task With_nothing_outstanding_the_step_is_EMPTY_rather_than_broken()
    {
        // The most common case for an attendee, and the one an "always Done, always last" step has
        // to survive: no rows, no dated rows, and the bulk-invite button correctly says there is
        // nothing to add.
        await using var db = NewDb();
        var pid = await SeedParticipantAsync(db);

        var model = await Service(db).LoadAsync(EventId, pid, default);

        Assert.Empty(model.Pending);
        Assert.Empty(model.Completed);
        Assert.False(model.AnyDated);
        Assert.Empty(await Service(db).DatedPendingTaskIdsAsync(EventId, pid, default));
    }

    [Fact]
    public async Task Only_DATED_open_tasks_can_be_invited_to_a_calendar()
    {
        // The per-task button is hidden on undated rows and the bulk button must agree with it —
        // an invitation with no date is not an invitation.
        await using var db = NewDb();
        var pid = await SeedParticipantAsync(db);

        var dated = Task_(pid, "Dated", new DateOnly(2026, 10, 1));
        var undated = Task_(pid, "Undated", null);
        var doneDated = Task_(pid, "Dated but done", new DateOnly(2026, 10, 1));
        doneDated.State = TaskState.Done;
        db.Tasks.AddRange(dated, undated, doneDated);
        await db.SaveChangesAsync();

        var ids = await Service(db).DatedPendingTaskIdsAsync(EventId, pid, default);

        Assert.Equal(new[] { dated.Id }, ids);
    }

    [Fact]
    public async Task A_task_belonging_to_SOMEONE_ELSE_can_never_be_invited()
    {
        // The per-task button posts a task id, so the lookup is the only thing standing between a
        // crafted POST and mailing someone another person's deadline.
        await using var db = NewDb();
        var mine = await SeedParticipantAsync(db);
        var theirs = await SeedParticipantAsync(db, "other@example.test");

        var theirTask = Task_(theirs, "Their deadline", new DateOnly(2026, 10, 1));
        db.Tasks.Add(theirTask);
        await db.SaveChangesAsync();

        Assert.Null(await Service(db).DatedTaskAsync(EventId, mine, theirTask.Id, default));
        Assert.NotNull(await Service(db).DatedTaskAsync(EventId, theirs, theirTask.Id, default));
    }

    // ---- fixture ----------------------------------------------------------

    private static DeadlinesFormService Service(CommunityHubDbContext db) =>
        new(db, new ParticipantChecklistBuilder(
            db, TimeProvider.System, new FormTaskReconciler(db, TimeProvider.System)));

    private static ParticipantTask Task_(int participantId, string title, DateOnly? due) => new()
    {
        EventId = EventId,
        AssignedParticipantId = participantId,
        SourceKey = $"speakerdl:{participantId}:{title.ToLowerInvariant().Replace(' ', '-')}",
        Title = title,
        DueDate = due,
        State = TaskState.Open,
    };

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"deadlines-step-{Guid.NewGuid():N}")
            .Options);

    private static async Task<int> SeedParticipantAsync(
        CommunityHubDbContext db, string email = "speaker@example.test")
    {
        if (!await db.Events.AnyAsync(e => e.Id == EventId))
            db.Events.Add(new Event { Id = EventId, Code = "TEST", IsActive = true });

        var p = new Participant
        {
            EventId = EventId,
            Email = email,
            FullName = "Test Person",
            Role = ParticipantRole.Speaker,
            IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }
}
