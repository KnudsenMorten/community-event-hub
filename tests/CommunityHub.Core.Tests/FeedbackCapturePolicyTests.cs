using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1354 — feedback capture policies. Operator 2026-10-19: *"qr get a) 1 page + free text b) 3 pages, no free
/// text c) 3 pages + free text. physical devices can have either 1 or 3 pages. i want the possibility to link
/// different capture feedback-policy per device so we can test what works. i want also a default feedback
/// capture kiosk policy, default feedback capture qr code policy"*.
/// </summary>
public sealed class FeedbackCapturePolicyTests
{
    private const int EventId = 1;
    private const string Token = "qr-token-policy-aaaaaaaa";

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Start = new(2027, 2, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset During = Start.AddMinutes(20);

    private sealed record World(CommunityHubDbContext Db, EvaluationRoom Room, EvaluationPhysicalRoom Physical, EvaluationDevice Kiosk);

    private static async Task<World> SeedAsync()
    {
        var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), PreDayDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        var room = new EvaluationRoom { EventId = EventId, Name = "Room-16-Floor 1-Max 44", NameKey = "room-16-floor 1-max 44" };
        db.EvaluationRooms.Add(room);
        db.Sessions.Add(new Session { Id = 55, EventId = EventId, Title = "Talk", PublicToken = Token });
        await db.SaveChangesAsync();
        db.EvaluationSessions.Add(new EvaluationSession
        {
            EventId = EventId, CehSessionId = 55, RoomId = room.Id, Title = "Talk",
            ScheduledStart = Start, ScheduledEnd = Start.AddHours(1),
            CollectionWindowOpensAt = Start, CollectionWindowClosesAt = Start.AddMinutes(90),
        });
        var physical = new EvaluationPhysicalRoom { EventId = EventId, Name = "Room 16" };
        db.EvaluationPhysicalRooms.Add(physical);
        await db.SaveChangesAsync();
        db.EvaluationRoomPlacements.Add(new EvaluationRoomPlacement { PhysicalRoomId = physical.Id, EvaluationRoomId = room.Id });
        var kiosk = new EvaluationDevice
        {
            EventId = EventId, SerialNumber = "ELDK-A", PhysicalRoomId = physical.Id, IsActive = true, IsKiosk = true,
            KeyHash = EvaluationIngestService.HashKey("k"), CreatedAt = Start, UpdatedAt = Start,
        };
        db.EvaluationDevices.Add(kiosk);
        await db.SaveChangesAsync();
        return new World(db, room, physical, kiosk);
    }

    // ---- kiosk resolution ---------------------------------------------------------------------

    [Fact]
    public async Task A_kiosk_asks_the_built_in_default_then_the_edition_default_then_its_own_policy()
    {
        var w = await SeedAsync();
        Assert.Equal(FeedbackCapturePolicy.RatingFollowUps, await FeedbackCapturePolicies.ForDeviceAsync(w.Db, w.Kiosk));

        (await w.Db.Events.SingleAsync()).KioskCapturePolicy = FeedbackCapturePolicy.RatingOnly;   // one-tap for everyone
        await w.Db.SaveChangesAsync();
        Assert.Equal(FeedbackCapturePolicy.RatingOnly, await FeedbackCapturePolicies.ForDeviceAsync(w.Db, w.Kiosk));

        w.Kiosk.CapturePolicy = FeedbackCapturePolicy.RatingFollowUps;   // this tablet tests the 3 pages
        await w.Db.SaveChangesAsync();
        Assert.Equal(FeedbackCapturePolicy.RatingFollowUps, await FeedbackCapturePolicies.ForDeviceAsync(w.Db, w.Kiosk));
    }

    [Theory]
    [InlineData(FeedbackCapturePolicy.RatingFreeText, FeedbackCapturePolicy.RatingOnly)]
    [InlineData(FeedbackCapturePolicy.RatingFollowUpsFreeText, FeedbackCapturePolicy.RatingFollowUps)]
    public void A_kiosk_never_asks_free_text(FeedbackCapturePolicy asked, FeedbackCapturePolicy onKiosk) =>
        Assert.Equal(onKiosk, FeedbackCapturePolicies.ForKiosk(asked));

    // ---- QR resolution ------------------------------------------------------------------------

    [Fact]
    public async Task A_QR_session_uses_its_physical_room_policy_else_the_edition_default_else_the_built_in_default()
    {
        var w = await SeedAsync();
        var qr = new EvaluationQrService(w.Db, new FixedClock(During));
        Assert.Equal(FeedbackCapturePolicy.RatingFollowUpsFreeText, (await qr.ResolveAsync(Token)).Policy);

        (await w.Db.Events.SingleAsync()).QrCapturePolicy = FeedbackCapturePolicy.RatingFollowUps;
        await w.Db.SaveChangesAsync();
        Assert.Equal(FeedbackCapturePolicy.RatingFollowUps, (await qr.ResolveAsync(Token)).Policy);

        w.Physical.QrCapturePolicy = FeedbackCapturePolicy.RatingFreeText;
        await w.Db.SaveChangesAsync();
        Assert.Equal(FeedbackCapturePolicy.RatingFreeText, (await qr.ResolveAsync(Token)).Policy);
    }

    [Fact]
    public async Task A_split_room_whose_physical_rooms_disagree_uses_the_default_rather_than_guess()
    {
        var w = await SeedAsync();
        w.Physical.QrCapturePolicy = FeedbackCapturePolicy.RatingFreeText;
        var other = new EvaluationPhysicalRoom { EventId = EventId, Name = "Room 17", QrCapturePolicy = FeedbackCapturePolicy.RatingOnly };
        w.Db.EvaluationPhysicalRooms.Add(other);
        await w.Db.SaveChangesAsync();
        w.Db.EvaluationRoomPlacements.Add(new EvaluationRoomPlacement { PhysicalRoomId = other.Id, EvaluationRoomId = w.Room.Id });
        await w.Db.SaveChangesAsync();

        Assert.Equal(FeedbackCapturePolicies.DefaultQr, await FeedbackCapturePolicies.ForQrAsync(w.Db, EventId, w.Room.Id));
    }

    // ---- what is recorded ---------------------------------------------------------------------

    [Theory]
    [InlineData(FeedbackCapturePolicy.RatingOnly, false, false)]
    [InlineData(FeedbackCapturePolicy.RatingFreeText, false, true)]
    [InlineData(FeedbackCapturePolicy.RatingFollowUps, true, false)]
    [InlineData(FeedbackCapturePolicy.RatingFollowUpsFreeText, true, true)]
    public async Task A_QR_submission_keeps_only_what_its_policy_asks_and_records_the_policy(
        FeedbackCapturePolicy policy, bool keepsFollowUps, bool keepsText)
    {
        var w = await SeedAsync();
        w.Physical.QrCapturePolicy = policy;
        await w.Db.SaveChangesAsync();
        var good = EvaluationFollowUps.Positive[0].Code;
        var better = EvaluationFollowUps.Improvement[0].Code;

        await new EvaluationQrService(w.Db, new FixedClock(During)).SubmitAsync(Token, 4, "Great demo", good, better);

        var r = await w.Db.EvaluationResponses.SingleAsync();
        Assert.Equal(4, r.Rating);
        Assert.Equal(policy, r.CapturePolicy);
        Assert.Equal(keepsFollowUps ? good : null, r.PositivePoint);
        Assert.Equal(keepsFollowUps ? better : null, r.ImprovementPoint);
        Assert.Equal(keepsText ? "Great demo" : null, r.FreeText);
    }

    [Fact]
    public async Task A_kiosk_vote_records_the_policy_it_was_asked_under_else_the_policy_in_force()
    {
        var w = await SeedAsync();
        w.Kiosk.CapturePolicy = FeedbackCapturePolicy.RatingOnly;   // in force NOW
        await w.Db.SaveChangesAsync();
        var ingest = new EvaluationIngestService(w.Db, new FixedClock(During));

        await ingest.IngestAsync(w.Kiosk,
            [
                // queued while the tablet still asked 3 pages — the tablet says so
                new EvaluationIngestService.IncomingRecord("queued", 3, During, Policy: (int)FeedbackCapturePolicy.RatingFollowUps),
                // an older page that sends no policy
                new EvaluationIngestService.IncomingRecord("old-page", 2, During),
            ],
            firmwareVersion: "web-kiosk/1", clockSyncedAt: During, eventClosed: false);

        Assert.Equal(FeedbackCapturePolicy.RatingFollowUps, (await w.Db.EvaluationResponses.SingleAsync(r => r.DeviceRecordId == "queued")).CapturePolicy);
        Assert.Equal(FeedbackCapturePolicy.RatingOnly, (await w.Db.EvaluationResponses.SingleAsync(r => r.DeviceRecordId == "old-page")).CapturePolicy);
    }

    [Fact]
    public async Task A_built_box_records_no_policy()
    {
        var w = await SeedAsync();
        w.Kiosk.IsKiosk = false;
        await w.Db.SaveChangesAsync();

        await new EvaluationIngestService(w.Db, new FixedClock(During)).IngestAsync(w.Kiosk,
            [new EvaluationIngestService.IncomingRecord("box-1", 4, During)], "box/2", During, false);

        Assert.Null((await w.Db.EvaluationResponses.SingleAsync()).CapturePolicy);
    }

    // ---- the comparison -----------------------------------------------------------------------

    [Fact]
    public async Task The_comparison_splits_by_channel_and_policy_with_answer_rates_only_where_asked()
    {
        var w = await SeedAsync();
        void Add(string source, FeedbackCapturePolicy? p, int rating, bool followUp = false, string? text = null) =>
            w.Db.EvaluationResponses.Add(new EvaluationResponse
            {
                EventId = EventId, Source = source, CapturePolicy = p, Rating = rating,
                CollectionTimestamp = During, ReceivedTimestamp = During,
                PositivePoint = followUp ? EvaluationFollowUps.Positive[0].Code : null, FreeText = text,
                SerialNumber = source == EvaluationResponseSources.Qr ? null : "ELDK-A",
                DeviceRecordId = source == EvaluationResponseSources.Qr ? null : Guid.NewGuid().ToString(),
            });
        Add(EvaluationResponseSources.Kiosk, FeedbackCapturePolicy.RatingOnly, 4);
        Add(EvaluationResponseSources.Kiosk, FeedbackCapturePolicy.RatingOnly, 2);
        Add(EvaluationResponseSources.Kiosk, FeedbackCapturePolicy.RatingFollowUps, 3, followUp: true);
        Add(EvaluationResponseSources.Kiosk, FeedbackCapturePolicy.RatingFollowUps, 3);
        Add(EvaluationResponseSources.Qr, FeedbackCapturePolicy.RatingFreeText, 4, text: "Nice");
        await w.Db.SaveChangesAsync();

        var rows = await EvaluationPolicyComparison.BuildAsync(w.Db, EventId);

        var oneTap = rows.Single(r => r.Channel == "Kiosk" && r.Policy == FeedbackCapturePolicy.RatingOnly);
        Assert.Equal(2, oneTap.Responses);
        Assert.Equal(3.0, oneTap.AverageRating);
        Assert.Null(oneTap.FollowUpRate);   // not asked
        var threePages = rows.Single(r => r.Channel == "Kiosk" && r.Policy == FeedbackCapturePolicy.RatingFollowUps);
        Assert.Equal(0.5, threePages.FollowUpRate);
        var qr = rows.Single(r => r.Channel == "QR code");
        Assert.Equal(1.0, qr.FreeTextRate);
        Assert.Null(qr.FollowUpRate);
    }
}
