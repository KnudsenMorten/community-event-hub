using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for the master-class master-class features (REQUIREMENTS § 6c):
///  - the <b>public logistics page</b> (slug minting + anonymous read view),
///  - the <b>edit scope</b> (involved speaker OR organizer only),
///  - the <b>per-master-class Zoho Booking endpoint</b> config (master-class-only),
///  - the <b>one-way Booking → hub participant sync</b> (idempotent + gated/stubbed).
///
/// No real customer / person data — synthetic ids + example.test addresses.
/// </summary>
public sealed class MasterClassFeaturesTests
{
    private static readonly DateTimeOffset Now =
        new(2027, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // ---- A Booking fetcher returning a deterministic, controllable list -------
    private sealed class FakeBookingFetcher : IMasterClassBookingFetcher
    {
        private readonly List<MasterClassBooking> _bookings;
        public FakeBookingFetcher(IEnumerable<MasterClassBooking> bookings) =>
            _bookings = bookings.ToList();
        public bool CanFetch => true;
        public string? LastEndpoint { get; private set; }
        public Task<IReadOnlyList<MasterClassBooking>> FetchAsync(
            string bookingEndpointUri, CancellationToken ct = default)
        {
            LastEndpoint = bookingEndpointUri;
            return Task.FromResult<IReadOnlyList<MasterClassBooking>>(_bookings);
        }
    }

    private static MasterClassBookingSyncService NewSync(
        CommunityHubDbContext db, IMasterClassBookingFetcher fetcher) =>
        new(db, fetcher, new FixedClock(Now),
            NullLogger<MasterClassBookingSyncService>.Instance);

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

    // -------------------------------------------- Zoho Booking endpoint config ----

    [Fact]
    public async Task BookingSync_is_gated_when_no_endpoint_is_mapped()
    {
        using var db = TestDb.New();
        var (eventId, _, _, _, mcId) = await SeedAsync(db);
        // Fetcher CAN fetch, but no endpoint is configured on the session.
        var sync = NewSync(db, new FakeBookingFetcher(Array.Empty<MasterClassBooking>()));

        var result = await sync.SyncSessionAsync(eventId, mcId);

        Assert.False(result.Ran);
        Assert.Contains("No Zoho Booking endpoint", result.Message);
    }

    [Fact]
    public async Task BookingSync_is_gated_when_seam_not_wired_even_with_endpoint()
    {
        using var db = TestDb.New();
        var (eventId, _, _, _, mcId) = await SeedAsync(db);
        var s = await db.Sessions.SingleAsync(x => x.Id == mcId);
        s.BookingEndpointUri = "https://zoho.example.test/bookings/mc";
        await db.SaveChangesAsync();

        // Null fetcher (the default registration) performs NO call.
        var sync = NewSync(db, new NullMasterClassBookingFetcher());
        var result = await sync.SyncSessionAsync(eventId, mcId);

        Assert.False(result.Ran);
        Assert.Contains("not configured", result.Message);
        Assert.Equal(0, await db.MasterClassParticipants.CountAsync());
    }

    [Fact]
    public async Task BookingSync_only_applies_to_a_master_class()
    {
        using var db = TestDb.New();
        var (eventId, _, _, _, _) = await SeedAsync(db);
        var mgmt = new SessionManagementService(db, new NullRoomQrProvider(), new FixedClock(Now));
        var tech = await mgmt.AddHubSessionAsync(
            eventId, "A Talk", SessionType.TechnicalSession, SessionLength.SixtyMin);
        var tsess = await db.Sessions.SingleAsync(x => x.Id == tech.Id);
        tsess.BookingEndpointUri = "https://zoho.example.test/bookings/x";
        await db.SaveChangesAsync();

        var sync = NewSync(db, new FakeBookingFetcher(Array.Empty<MasterClassBooking>()));
        var result = await sync.SyncSessionAsync(eventId, tech.Id);

        Assert.False(result.Ran);
        Assert.Contains("master-class", result.Message);
    }

    // ---------------------------------------------- participant sync (live) ----

    [Fact]
    public async Task BookingSync_upserts_participants_and_links_idempotently()
    {
        using var db = TestDb.New();
        var (eventId, _, _, _, mcId) = await SeedAsync(db);
        var s = await db.Sessions.SingleAsync(x => x.Id == mcId);
        s.BookingEndpointUri = "https://zoho.example.test/bookings/mc";
        await db.SaveChangesAsync();

        var bookings = new[]
        {
            new MasterClassBooking("bk-1", "Alice@Example.Test", "Alice A", "upcoming"),
            new MasterClassBooking("bk-2", "bob@example.test", "Bob B", "upcoming"),
        };
        var sync = NewSync(db, new FakeBookingFetcher(bookings));

        var first = await sync.SyncSessionAsync(eventId, mcId);
        Assert.True(first.Ran);
        Assert.Equal(2, first.ParticipantsCreated);
        Assert.Equal(2, first.LinksCreated);
        Assert.Equal(0, first.LinksUpdated);
        Assert.Equal(2, await db.MasterClassParticipants.CountAsync());

        // Email is lower-cased on the created participant.
        Assert.True(await db.Participants.AnyAsync(p => p.Email == "alice@example.test"));

        // Re-sync the SAME bookings → no duplicates, links updated in place.
        var second = await sync.SyncSessionAsync(eventId, mcId);
        Assert.True(second.Ran);
        Assert.Equal(0, second.ParticipantsCreated);
        Assert.Equal(0, second.LinksCreated);
        Assert.Equal(2, second.LinksUpdated);
        Assert.Equal(2, await db.MasterClassParticipants.CountAsync());
    }

    [Fact]
    public async Task BookingSync_created_participant_is_lifecycle_gated_not_login_ready()
    {
        using var db = TestDb.New();
        var (eventId, _, _, _, mcId) = await SeedAsync(db);
        var s = await db.Sessions.SingleAsync(x => x.Id == mcId);
        s.BookingEndpointUri = "https://zoho.example.test/bookings/mc";
        await db.SaveChangesAsync();

        var sync = NewSync(db, new FakeBookingFetcher(new[]
        {
            new MasterClassBooking("bk-1", "newbie@example.test", "New Bie", "upcoming"),
        }));
        await sync.SyncSessionAsync(eventId, mcId);

        var p = await db.Participants.SingleAsync(x => x.Email == "newbie@example.test");
        // Lifecycle gate: NOT Active → cannot sign in until an organizer validates.
        Assert.Equal(ParticipantLifecycleState.Inactive, p.LifecycleState);
        Assert.Equal(ParticipantRole.Attendee, p.Role);
    }

    [Fact]
    public async Task BookingSync_links_to_existing_participant_without_duplicating()
    {
        using var db = TestDb.New();
        var (eventId, _, speakerId, _, mcId) = await SeedAsync(db);
        var s = await db.Sessions.SingleAsync(x => x.Id == mcId);
        s.BookingEndpointUri = "https://zoho.example.test/bookings/mc";
        await db.SaveChangesAsync();
        var before = await db.Participants.CountAsync();

        // Book the already-existing speaker's email.
        var sync = NewSync(db, new FakeBookingFetcher(new[]
        {
            new MasterClassBooking("bk-9", "mcspeaker@example.test", "MC Speaker", "upcoming"),
        }));
        var result = await sync.SyncSessionAsync(eventId, mcId);

        Assert.Equal(0, result.ParticipantsCreated); // matched the existing one
        Assert.Equal(before, await db.Participants.CountAsync());
        var link = await db.MasterClassParticipants.SingleAsync();
        Assert.Equal(speakerId, link.ParticipantId);
    }

    [Fact]
    public async Task BookingSync_cancelled_booking_deactivates_link_not_deletes()
    {
        using var db = TestDb.New();
        var (eventId, _, _, _, mcId) = await SeedAsync(db);
        var s = await db.Sessions.SingleAsync(x => x.Id == mcId);
        s.BookingEndpointUri = "https://zoho.example.test/bookings/mc";
        await db.SaveChangesAsync();

        // First: active booking.
        await NewSync(db, new FakeBookingFetcher(new[]
        {
            new MasterClassBooking("bk-1", "x@example.test", "X", "upcoming"),
        })).SyncSessionAsync(eventId, mcId);
        Assert.True((await db.MasterClassParticipants.SingleAsync()).IsActive);

        // Then: same booking comes back cancelled.
        await NewSync(db, new FakeBookingFetcher(new[]
        {
            new MasterClassBooking("bk-1", "x@example.test", "X", "cancelled"),
        })).SyncSessionAsync(eventId, mcId);

        var link = await db.MasterClassParticipants.SingleAsync(); // not deleted
        Assert.False(link.IsActive);
        Assert.Equal("cancelled", link.BookingStatus);
    }

    [Fact]
    public async Task BookingSync_skips_bookings_with_blank_id_or_email()
    {
        using var db = TestDb.New();
        var (eventId, _, _, _, mcId) = await SeedAsync(db);
        var s = await db.Sessions.SingleAsync(x => x.Id == mcId);
        s.BookingEndpointUri = "https://zoho.example.test/bookings/mc";
        await db.SaveChangesAsync();

        var sync = NewSync(db, new FakeBookingFetcher(new[]
        {
            new MasterClassBooking("", "noid@example.test", "No Id", "upcoming"),
            new MasterClassBooking("bk-ok", "", "No Email", "upcoming"),
            new MasterClassBooking("bk-good", "good@example.test", "Good One", "upcoming"),
        }));
        var result = await sync.SyncSessionAsync(eventId, mcId);

        Assert.True(result.Ran);
        Assert.Equal(1, result.LinksCreated); // only the well-formed one
        Assert.Equal(1, await db.MasterClassParticipants.CountAsync());
    }
}
