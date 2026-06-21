using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for <see cref="TestDataCleanupService"/> (REQUIREMENTS §1 "Go-live
/// test-data cleanup"). Proves:
///  - the PREVIEW lists only IsTestUser rows of THIS edition (real rows + other
///    editions never appear), each flagged delete-vs-deactivate correctly,
///  - CLEANUP hard-deletes a clean test row but DEACTIVATES (keeps) a test row
///    with engagement — so cleanup can never orphan real data,
///  - a real participant is never touched,
///  - the run is idempotent (a second run finds only the kept/deactivated rows
///    and re-applies the same safe outcome; the clean ones are already gone),
///  - empty state: no test users → an empty preview + a no-op cleanup.
///
/// In-memory DbContext; synthetic ids + fake names — no real participants.
/// </summary>
public sealed class TestDataCleanupServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private static TestDataCleanupService NewService(CommunityHub.Core.Data.CommunityHubDbContext db)
        => new(db, new ParticipantDeletionService(db, new FixedClock(Now)));

    private static Event NewEvent(bool active, string code = "CLN27") => new()
    {
        Code = code, CommunityName = "Cleanup Community",
        DisplayName = $"Cleanup {code}",
        StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        IsActive = active,
    };

    private static Participant Person(
        int eventId, string name, bool isTest, ParticipantRole role = ParticipantRole.Attendee)
        => new()
        {
            EventId = eventId, FullName = name,
            Email = name.Replace(" ", ".").ToLowerInvariant() + "@example.test",
            Role = role, IsActive = true, IsTestUser = isTest,
            LifecycleState = ParticipantLifecycleState.Active,
        };

    [Fact]
    public async Task Preview_lists_only_this_editions_test_users()
    {
        using var db = TestDb.New();
        var evt = NewEvent(active: true);
        var other = NewEvent(active: false, code: "OLD26");
        db.Events.AddRange(evt, other);
        await db.SaveChangesAsync();

        db.Participants.Add(Person(evt.Id, "Test Alice", isTest: true));
        db.Participants.Add(Person(evt.Id, "Real Bob", isTest: false));     // real → excluded
        db.Participants.Add(Person(other.Id, "Other Test", isTest: true));  // other edition → excluded
        await db.SaveChangesAsync();

        var preview = await NewService(db).PreviewAsync(evt.Id);

        var row = Assert.Single(preview.Rows);
        Assert.Equal("Test Alice", row.FullName);
        Assert.True(row.WouldHardDelete);   // clean row → hard-delete
        Assert.Equal(1, preview.Total);
        Assert.Equal(1, preview.WouldHardDelete);
        Assert.Equal(0, preview.WouldDeactivate);
    }

    [Fact]
    public async Task Preview_flags_an_engaged_test_row_as_deactivate_not_delete()
    {
        using var db = TestDb.New();
        var evt = NewEvent(active: true);
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var speaker = Person(evt.Id, "Test Speaker", isTest: true, role: ParticipantRole.Speaker);
        db.Participants.Add(speaker);
        await db.SaveChangesAsync();

        // Engagement that blocks a hard-delete: a speaker profile.
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = evt.Id, ParticipantId = speaker.Id,
        });
        await db.SaveChangesAsync();

        var preview = await NewService(db).PreviewAsync(evt.Id);

        var row = Assert.Single(preview.Rows);
        Assert.False(row.WouldHardDelete);  // has engagement → would deactivate
        Assert.Equal(1, preview.WouldDeactivate);
        Assert.Equal(0, preview.WouldHardDelete);
    }

    [Fact]
    public async Task Cleanup_hard_deletes_clean_rows_and_deactivates_engaged_rows()
    {
        using var db = TestDb.New();
        var evt = NewEvent(active: true);
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var clean = Person(evt.Id, "Clean Tester", isTest: true);
        var engaged = Person(evt.Id, "Engaged Tester", isTest: true, role: ParticipantRole.Speaker);
        var real = Person(evt.Id, "Real Person", isTest: false);
        db.Participants.AddRange(clean, engaged, real);
        await db.SaveChangesAsync();

        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = evt.Id, ParticipantId = engaged.Id,
        });
        await db.SaveChangesAsync();

        var result = await NewService(db).CleanupAsync(evt.Id);

        Assert.Equal(1, result.HardDeleted);
        Assert.Equal(1, result.Deactivated);
        Assert.Equal(2, result.Total);

        // Clean tester is gone; engaged tester remains but deactivated; real
        // participant is fully untouched.
        Assert.Null(await db.Participants.FindAsync(clean.Id));

        var engagedAfter = await db.Participants.FindAsync(engaged.Id);
        Assert.NotNull(engagedAfter);
        Assert.False(engagedAfter!.IsActive);

        var realAfter = await db.Participants.FindAsync(real.Id);
        Assert.NotNull(realAfter);
        Assert.True(realAfter!.IsActive);
    }

    [Fact]
    public async Task Cleanup_is_idempotent()
    {
        using var db = TestDb.New();
        var evt = NewEvent(active: true);
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var clean = Person(evt.Id, "Clean Tester", isTest: true);
        var engaged = Person(evt.Id, "Engaged Tester", isTest: true, role: ParticipantRole.Speaker);
        db.Participants.AddRange(clean, engaged);
        await db.SaveChangesAsync();
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = evt.Id, ParticipantId = engaged.Id,
        });
        await db.SaveChangesAsync();

        var svc = NewService(db);
        await svc.CleanupAsync(evt.Id);

        // Second run: the clean row is already gone; the engaged row is still a
        // test user (now inactive) and re-applies the safe deactivate outcome.
        var second = await svc.CleanupAsync(evt.Id);
        Assert.Equal(0, second.HardDeleted);
        Assert.Equal(1, second.Deactivated);
    }

    [Fact]
    public async Task No_test_users_is_an_empty_preview_and_a_no_op_cleanup()
    {
        using var db = TestDb.New();
        var evt = NewEvent(active: true);
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        db.Participants.Add(Person(evt.Id, "Real Only", isTest: false));
        await db.SaveChangesAsync();

        var svc = NewService(db);

        var preview = await svc.PreviewAsync(evt.Id);
        Assert.False(preview.Any);
        Assert.Empty(preview.Rows);

        var result = await svc.CleanupAsync(evt.Id);
        Assert.Equal(0, result.Total);
    }
}
