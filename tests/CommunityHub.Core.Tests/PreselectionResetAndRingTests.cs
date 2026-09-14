using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1146c/§1146d — the two new queue controls: set a ring, and undo a preselection.
///
/// <para>Operator 2026-08-28: <i>"ring selection button + current ring + their id"</i> and
/// <i>"i also want a possibility to Reset a preselection (or remove) but NOT delete the
/// sign-up"</i>.</para>
///
/// <para>🔑 <b>Reset is the ONE backward move on a deliberately forward-only lifecycle</b>, and the
/// tests here are mostly about the line it must not cross: an onboarded person has signed in and
/// been welcomed, so returning them to the queue would revoke a live login.</para>
///
/// <para>NO real names — Ada / Grace only.</para>
/// </summary>
public sealed class PreselectionResetAndRingTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"psq-reset-{Guid.NewGuid():N}").Options);

    private static async Task<int> SeedAsync(
        CommunityHubDbContext db, ParticipantLifecycleState state,
        Ring ring = Ring.Broad, bool isActive = false, int eventId = EventId)
    {
        var p = new Participant
        {
            EventId = eventId,
            FullName = "Ada Lovelace",
            Email = $"ada{Guid.NewGuid():N}@example.test",
            Role = ParticipantRole.Volunteer,
            LifecycleState = state,
            IsActive = isActive,
            Ring = ring,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    // ---- §1146d reset -----------------------------------------------------------------------

    [Fact]
    public async Task A_PRESELECTED_row_resets_to_Inactive()
    {
        using var db = NewDb();
        var id = await SeedAsync(db, ParticipantLifecycleState.Preselected);
        var svc = new PreselectionQueueService(db);

        var outcome = await svc.ResetAsync(EventId, id);

        Assert.Equal(PreselectionQueueService.ResetOutcome.Reset, outcome);
        var after = await db.Participants.AsNoTracking().FirstAsync(p => p.Id == id);
        Assert.Equal(ParticipantLifecycleState.Inactive, after.LifecycleState);
    }

    /// <summary>
    /// 🔒 THE LINE THE RESET MUST NOT CROSS.
    /// </summary>
    /// <remarks>
    /// An Active volunteer has signed in and been welcomed. Quietly putting them back in the queue
    /// would revoke a login somebody is using and leave a welcomed person looking un-invited.
    /// Removing an onboarded volunteer is DEACTIVATION — a different, deliberate action.
    /// </remarks>
    [Fact]
    public async Task An_ONBOARDED_row_is_REFUSED_not_reset()
    {
        using var db = NewDb();
        var id = await SeedAsync(db, ParticipantLifecycleState.Active, isActive: true);
        var svc = new PreselectionQueueService(db);

        var outcome = await svc.ResetAsync(EventId, id);

        Assert.Equal(PreselectionQueueService.ResetOutcome.RefusedActive, outcome);

        var after = await db.Participants.AsNoTracking().FirstAsync(p => p.Id == id);
        Assert.Equal(ParticipantLifecycleState.Active, after.LifecycleState);
        Assert.True(after.IsActive, "an onboarded volunteer's login must survive a refused reset");
    }

    [Fact]
    public async Task An_ALREADY_INACTIVE_row_reports_no_change()
    {
        using var db = NewDb();
        var id = await SeedAsync(db, ParticipantLifecycleState.Inactive);
        var svc = new PreselectionQueueService(db);

        Assert.Equal(
            PreselectionQueueService.ResetOutcome.NoChange, await svc.ResetAsync(EventId, id));
    }

    /// <summary>⚠️ The whole point of the feature: it is NOT a delete.</summary>
    [Fact]
    public async Task Reset_KEEPS_the_sign_up_row_and_its_availability()
    {
        using var db = NewDb();
        var id = await SeedAsync(db, ParticipantLifecycleState.Preselected);

        db.VolunteerAvailabilities.Add(new VolunteerAvailability
        {
            EventId = EventId, ParticipantId = id, ProfileConsent = true,
            PhotoUrl = "https://example.test/photo.jpg", CreatedAt = DateTimeOffset.UtcNow,
        });
        db.VolunteerDayAvailabilities.Add(new VolunteerDayAvailability
        {
            EventId = EventId, ParticipantId = id,
            Day = new DateOnly(2027, 2, 10), Level = VolunteerAvailabilityLevel.Full,
            Note = "[Full day]",
        });
        await db.SaveChangesAsync();

        await new PreselectionQueueService(db).ResetAsync(EventId, id);

        Assert.True(await db.Participants.AnyAsync(p => p.Id == id));
        Assert.True(await db.VolunteerAvailabilities.AnyAsync(v => v.ParticipantId == id));
        Assert.True(await db.VolunteerDayAvailabilities.AnyAsync(v => v.ParticipantId == id));

        var meta = await db.VolunteerAvailabilities.AsNoTracking().FirstAsync(v => v.ParticipantId == id);
        Assert.True(meta.ProfileConsent);
        Assert.Equal("https://example.test/photo.jpg", meta.PhotoUrl);
    }

    [Fact]
    public async Task Reset_is_scoped_to_the_edition()
    {
        using var db = NewDb();
        var otherEdition = await SeedAsync(db, ParticipantLifecycleState.Preselected, eventId: 99);
        var svc = new PreselectionQueueService(db);

        Assert.Equal(
            PreselectionQueueService.ResetOutcome.NotFound, await svc.ResetAsync(EventId, otherEdition));

        var after = await db.Participants.AsNoTracking().FirstAsync(p => p.Id == otherEdition);
        Assert.Equal(ParticipantLifecycleState.Preselected, after.LifecycleState);
    }

    // ---- §1146c ring ------------------------------------------------------------------------

    [Fact]
    public async Task The_ring_can_be_set()
    {
        using var db = NewDb();
        var id = await SeedAsync(db, ParticipantLifecycleState.Inactive, Ring.Broad);
        var svc = new PreselectionQueueService(db);

        Assert.True(await svc.SetRingAsync(EventId, id, Ring.Ring1));

        var after = await db.Participants.AsNoTracking().FirstAsync(p => p.Id == id);
        Assert.Equal(Ring.Ring1, after.Ring);
    }

    /// <summary>
    /// ⚠️ Unlike the lifecycle, the ring is NOT forward-only — a narrower ring set by mistake has to
    /// be correctable from the same control.
    /// </summary>
    [Fact]
    public async Task The_ring_can_move_in_BOTH_directions()
    {
        using var db = NewDb();
        var id = await SeedAsync(db, ParticipantLifecycleState.Inactive, Ring.Ring0);
        var svc = new PreselectionQueueService(db);

        await svc.SetRingAsync(EventId, id, Ring.Broad);
        Assert.Equal(Ring.Broad, (await db.Participants.AsNoTracking().FirstAsync(p => p.Id == id)).Ring);

        await svc.SetRingAsync(EventId, id, Ring.Ring1);
        Assert.Equal(Ring.Ring1, (await db.Participants.AsNoTracking().FirstAsync(p => p.Id == id)).Ring);
    }

    [Fact]
    public async Task Setting_a_ring_is_scoped_to_the_edition()
    {
        using var db = NewDb();
        var otherEdition = await SeedAsync(db, ParticipantLifecycleState.Inactive, Ring.Broad, eventId: 99);
        var svc = new PreselectionQueueService(db);

        Assert.False(await svc.SetRingAsync(EventId, otherEdition, Ring.Ring0));

        var after = await db.Participants.AsNoTracking().FirstAsync(p => p.Id == otherEdition);
        Assert.Equal(Ring.Broad, after.Ring);
    }

    /// <summary>
    /// 🔑 A new volunteer defaults to Ring 3 (Broad), which is what holds the welcome mail back
    /// while the welcome cap sits at Ring 1. The picker must not quietly change that default.
    /// </summary>
    [Fact]
    public async Task A_new_volunteer_defaults_to_Broad()
    {
        using var db = NewDb();
        var p = new Participant
        {
            EventId = EventId, FullName = "Grace Hopper", Email = "grace@example.test",
            Role = ParticipantRole.Volunteer,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        var after = await db.Participants.AsNoTracking().FirstAsync(x => x.Id == p.Id);
        Assert.Equal(Ring.Broad, after.Ring);
    }
}
