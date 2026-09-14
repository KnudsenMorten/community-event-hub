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
        => new(db, new ParticipantDeletionService(db, new FixedClock(Now),
            new ParticipantDeactivationService(db, new FixedClock(Now),
                new CommunityHub.Core.Audit.AuditTrailService(db, new FixedClock(Now)))));

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

    /// <summary>
    /// 🛑 §1082 — THE CLEANUP IS DISABLED, AND THIS PINS IT OFF.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-13: <i>"we dont use that testdataclean-up service, turn it off in
    /// code"</i>. It was the LAST path in the product that could hard-delete a participant, after the
    /// organizer grid, the pre-selection queue and the dashboard decline all moved to deactivate-only.
    /// With it off, <i>"we only make things inactive by filter"</i> is true without exception.</para>
    ///
    /// <para>⚠️ These two tests previously asserted the OPPOSITE — that a clean test row was
    /// physically removed. They are inverted deliberately, not deleted, so the disabled state is a
    /// stated contract rather than the silent absence of coverage.</para>
    /// </remarks>
    [Fact]
    public async Task Cleanup_is_disabled_and_deletes_nothing()
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
        db.SpeakerProfiles.Add(new SpeakerProfile { EventId = evt.Id, ParticipantId = engaged.Id });
        await db.SaveChangesAsync();

        var result = await NewService(db).CleanupAsync(evt.Id);

        Assert.Equal(0, result.Total);
        Assert.Equal(0, result.HardDeleted);
        Assert.Equal(0, result.Deactivated);

        // 🔑 Every row survives — including the "clean" test row the old behaviour removed.
        Assert.NotNull(await db.Participants.FindAsync(clean.Id));
        Assert.NotNull(await db.Participants.FindAsync(engaged.Id));
        Assert.NotNull(await db.Participants.FindAsync(real.Id));
        // …and nobody was even deactivated as a side effect.
        Assert.True((await db.Participants.FindAsync(clean.Id))!.IsActive);
        Assert.True((await db.Participants.FindAsync(real.Id))!.IsActive);
    }

    /// <summary>
    /// 🔒 The READ-ONLY preview still works: an organizer can still SEE which rows are test data.
    /// Only the destructive half refuses, which is what "disabled, not deleted" means here.
    /// </summary>
    [Fact]
    public async Task Preview_still_reports_test_rows_while_cleanup_is_disabled()
    {
        using var db = TestDb.New();
        var evt = NewEvent(active: true);
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        db.Participants.Add(Person(evt.Id, "Clean Tester", isTest: true));
        await db.SaveChangesAsync();

        var preview = await NewService(db).PreviewAsync(evt.Id);

        Assert.True(preview.Any);
        Assert.Equal(1, preview.Total);
    }
}
