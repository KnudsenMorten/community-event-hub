using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;   // NullRoomQrProvider
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for the master-class public logistics page (REQUIREMENTS §6):
///  - the <b>public logistics page</b> (slug minting + anonymous read view),
///  - the <b>edit scope</b> (involved speaker OR organizer only).
///
/// (The legacy Zoho Booking endpoint config + one-way Booking → hub participant
/// sync were RETIRED — CEH now owns master-class seats + waitlist via
/// <see cref="MasterClassSignupService"/>, covered by MasterClassSignupServiceTests.)
///
/// No real customer / person data — synthetic ids + example.test addresses.
/// </summary>
public sealed class MasterClassFeaturesTests
{
    private static readonly DateTimeOffset Now =
        new(2027, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Seed an event + an organizer + a master-class session with two speakers.</summary>
    private static async Task<(int eventId, int orgId, int speakerId, int otherId, int mcId)>
        SeedAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "TEST27", CommunityName = "Test", DisplayName = "Test 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var org = new Participant { EventId = evt.Id, FullName = "Org One", Email = "org@example.test", Role = ParticipantRole.Organizer, IsActive = true };
        var speaker = new Participant { EventId = evt.Id, FullName = "MC Speaker", Email = "mcspeaker@example.test", Role = ParticipantRole.Speaker, IsActive = true };
        var other = new Participant { EventId = evt.Id, FullName = "Other Speaker", Email = "other@example.test", Role = ParticipantRole.Speaker, IsActive = true };
        db.Participants.AddRange(org, speaker, other);
        await db.SaveChangesAsync();

        var mgmt = new SessionManagementService(db, new NullRoomQrProvider(), new FixedClock(Now));
        var mc = await mgmt.AddHubSessionAsync(
            evt.Id, "Hands-on Master Class", SessionType.MasterClass,
            SessionLength.FullDay, room: "Lab", speakerParticipantIds: new[] { speaker.Id });

        return (evt.Id, org.Id, speaker.Id, other.Id, mc.Id);
    }

    // ----------------------------------------------- public logistics page ----

    [Fact]
    public async Task EnsureSlug_mints_a_stable_slug_for_a_master_class()
    {
        using var db = TestDb.New();
        var (eventId, _, _, _, mcId) = await SeedAsync(db);
        var svc = new MasterClassLogisticsService(db, new FixedClock(Now));

        var slug1 = await svc.EnsureSlugAsync(eventId, mcId);
        var slug2 = await svc.EnsureSlugAsync(eventId, mcId);

        Assert.False(string.IsNullOrWhiteSpace(slug1));
        Assert.Equal(slug1, slug2); // idempotent
    }

    [Fact]
    public async Task EnsureSlug_rejects_a_non_masterclass_session()
    {
        using var db = TestDb.New();
        var (eventId, _, _, _, _) = await SeedAsync(db);
        var mgmt = new SessionManagementService(db, new NullRoomQrProvider(), new FixedClock(Now));
        var tech = await mgmt.AddHubSessionAsync(
            eventId, "A Talk", SessionType.TechnicalSession, SessionLength.SixtyMin);
        var svc = new MasterClassLogisticsService(db, new FixedClock(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.EnsureSlugAsync(eventId, tech.Id));
    }

    [Fact]
    public async Task PublicView_resolves_by_slug_with_no_auth_and_shows_speakers()
    {
        using var db = TestDb.New();
        var (eventId, _, _, _, mcId) = await SeedAsync(db);
        var svc = new MasterClassLogisticsService(db, new FixedClock(Now));
        var slug = await svc.EnsureSlugAsync(eventId, mcId);

        var view = await svc.GetPublicViewAsync(slug);

        Assert.NotNull(view);
        Assert.Equal(mcId, view!.SessionId);
        Assert.Equal("Hands-on Master Class", view.Title);
        Assert.Contains("MC Speaker", view.Speakers);
    }

    [Fact]
    public async Task PublicView_returns_null_for_unknown_slug()
    {
        using var db = TestDb.New();
        await SeedAsync(db);
        var svc = new MasterClassLogisticsService(db, new FixedClock(Now));

        Assert.Null(await svc.GetPublicViewAsync("does-not-exist"));
        Assert.Null(await svc.GetPublicViewAsync(""));
    }

    // ------------------------------------------------------------ edit scope ----

    [Fact]
    public async Task CanEdit_true_for_involved_speaker_and_organizer()
    {
        using var db = TestDb.New();
        var (eventId, orgId, speakerId, _, mcId) = await SeedAsync(db);
        var svc = new MasterClassLogisticsService(db, new FixedClock(Now));

        Assert.True(await svc.CanEditAsync(eventId, mcId, orgId, ParticipantRole.Organizer));
        Assert.True(await svc.CanEditAsync(eventId, mcId, speakerId, ParticipantRole.Speaker));
    }

    [Fact]
    public async Task CanEdit_false_for_uninvolved_speaker()
    {
        using var db = TestDb.New();
        var (eventId, _, _, otherId, mcId) = await SeedAsync(db);
        var svc = new MasterClassLogisticsService(db, new FixedClock(Now));

        // 'other' is a Speaker but is NOT linked to this master class.
        Assert.False(await svc.CanEditAsync(eventId, mcId, otherId, ParticipantRole.Speaker));
    }

    [Fact]
    public async Task UpdateLogistics_saves_for_involved_speaker_and_stamps_audit()
    {
        using var db = TestDb.New();
        var (eventId, _, speakerId, _, mcId) = await SeedAsync(db);
        var svc = new MasterClassLogisticsService(db, new FixedClock(Now));

        await svc.UpdateLogisticsAsync(
            eventId, mcId, speakerId, ParticipantRole.Speaker,
            "mcspeaker@example.test", "Bring your laptop charged.");

        var view = await svc.GetPublicViewAsync((await db.Sessions.SingleAsync(s => s.Id == mcId)).PublicSlug);
        Assert.Equal("Bring your laptop charged.", view!.LogisticsText);
        var s = await db.Sessions.SingleAsync(x => x.Id == mcId);
        Assert.Equal("mcspeaker@example.test", s.LogisticsUpdatedByEmail);
        Assert.NotNull(s.LogisticsUpdatedAt);
        Assert.False(string.IsNullOrWhiteSpace(s.PublicSlug)); // page exists after edit
    }

    [Fact]
    public async Task UpdateLogistics_rejects_an_uninvolved_editor()
    {
        using var db = TestDb.New();
        var (eventId, _, _, otherId, mcId) = await SeedAsync(db);
        var svc = new MasterClassLogisticsService(db, new FixedClock(Now));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.UpdateLogisticsAsync(
                eventId, mcId, otherId, ParticipantRole.Speaker,
                "other@example.test", "I should not be allowed."));

        var s = await db.Sessions.SingleAsync(x => x.Id == mcId);
        Assert.Null(s.LogisticsText); // nothing written
    }
}
