using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §743 C4a — the device-facing ingest path, tested against the promises the CONTRACT makes to the
/// vendor (`docs/internal-session-eval-device-api-contract.md`). Their firmware is written from that
/// document, and 25 fielded units cannot be revised as easily as this service, so these tests exist
/// to stop the service drifting away from what we told them.
/// </summary>
public sealed class EvaluationIngestServiceTests
{
    private const int EventId = 1;
    private const string SerialNumber = "A1:B2:C3:D4:E5:F6";

    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public void Set(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static readonly DateTimeOffset Now = new(2027, 2, 9, 9, 30, 0, TimeSpan.Zero);

    private static async Task<(CommunityHubDbContext Db, EvaluationDevice Device, string Key)>
        SeedAsync(bool active = true)
    {
        var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "Experts Live Denmark 2027",
            Code = "ELDK27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        await db.SaveChangesAsync();

        var key = EvaluationIngestService.NewKey();
        var device = new EvaluationDevice
        {
            EventId = EventId, SerialNumber = SerialNumber,
            KeyHash = EvaluationIngestService.HashKey(key),
            IsActive = active, CreatedAt = Now, UpdatedAt = Now,
        };
        db.EvaluationDevices.Add(device);
        await db.SaveChangesAsync();
        return (db, device, key);
    }

    private static EvaluationIngestService NewService(CommunityHubDbContext db, FixedClock clock) =>
        new(db, clock);

    private static EvaluationIngestService.IncomingRecord Rec(
        string id, int rating = 4, DateTimeOffset? at = null) =>
        new(id, rating, at ?? Now.AddMinutes(-5));

    // =====================================================================
    //  Credential
    // =====================================================================

    [Fact]
    public async Task A_valid_key_authenticates_and_the_key_itself_is_never_stored()
    {
        var (db, _, key) = await SeedAsync();
        using var _db = db;
        var svc = NewService(db, new FixedClock(Now));

        var (device, failure) = await svc.AuthenticateAsync(SerialNumber, key);

        Assert.NotNull(device);
        Assert.Equal(EvaluationIngestService.AuthFailure.None, failure);

        // 🔒 A database leak must not yield a working device credential.
        Assert.DoesNotContain(key, db.EvaluationDevices.Single().KeyHash);
    }

    [Fact]
    public async Task A_wrong_key_is_401_and_an_unknown_device_is_403()
    {
        // The contract distinguishes them because the FIXES differ: a bad key is a provisioning
        // error, an unknown device needs a human to register the unit. Both tell the firmware to
        // back off and KEEP its cache.
        var (db, _, _) = await SeedAsync();
        using var _db = db;
        var svc = NewService(db, new FixedClock(Now));

        var (bad, badFailure) = await svc.AuthenticateAsync(SerialNumber, "not-the-key");
        Assert.Null(bad);
        Assert.Equal(EvaluationIngestService.AuthFailure.BadKey, badFailure);

        var (unknown, unknownFailure) = await svc.AuthenticateAsync("NO-SUCH-UNIT", "anything");
        Assert.Null(unknown);
        Assert.Equal(EvaluationIngestService.AuthFailure.UnknownDevice, unknownFailure);
    }

    [Fact]
    public async Task A_revoked_device_is_indistinguishable_from_an_unregistered_one()
    {
        // Deliberate: someone holding a stolen key must not be able to tell "revoked" from
        // "never existed" — that difference would confirm the key had once been real.
        var (db, _, key) = await SeedAsync(active: false);
        using var _db = db;
        var svc = NewService(db, new FixedClock(Now));

        var (device, failure) = await svc.AuthenticateAsync(SerialNumber, key);

        Assert.Null(device);
        Assert.Equal(EvaluationIngestService.AuthFailure.UnknownDevice, failure);
    }

    [Fact]
    public async Task The_PREVIOUS_key_still_authenticates_so_an_OTA_rotation_cannot_brick_the_fleet()
    {
        // 🔒 Units update in a staggered rollout. If only the current key worked, rotating it would
        // partially brick a fleet that has no screen to explain itself.
        var (db, device, oldKey) = await SeedAsync();
        using var _db = db;
        var svc = NewService(db, new FixedClock(Now));

        var newKey = EvaluationIngestService.NewKey();
        device.PreviousKeyHash = device.KeyHash;
        device.KeyHash = EvaluationIngestService.HashKey(newKey);
        await db.SaveChangesAsync();

        Assert.NotNull((await svc.AuthenticateAsync(SerialNumber, newKey)).Device);   // updated unit
        Assert.NotNull((await svc.AuthenticateAsync(SerialNumber, oldKey)).Device);   // not yet updated
    }

    // =====================================================================
    //  Ingest
    // =====================================================================

    [Fact]
    public async Task A_batch_is_stored_with_BOTH_timestamps_and_per_record_results()
    {
        var (db, device, _) = await SeedAsync();
        using var _db = db;
        var clock = new FixedClock(Now);
        var svc = NewService(db, clock);

        var pressedAt = Now.AddMinutes(-20);
        var results = await svc.IngestAsync(
            device, new[] { Rec("r-1", 4, pressedAt), Rec("r-2", 1, pressedAt.AddMinutes(1)) },
            "1.4.2", Now.AddHours(-1), eventClosed: false);

        Assert.All(results, r => Assert.Equal("accepted", r.Status));
        Assert.Equal(2, db.EvaluationResponses.Count());

        var stored = db.EvaluationResponses.First(r => r.DeviceRecordId == "r-1");
        // 🔒 The two timestamps are not interchangeable: collection drives attribution and every
        // metric; received is diagnostics only.
        Assert.Equal(pressedAt, stored.CollectionTimestamp);
        Assert.Equal(Now, stored.ReceivedTimestamp);
        Assert.Equal(4, stored.Rating);
        Assert.Equal("device", stored.Source);
        Assert.Equal("1.4.2", stored.FirmwareVersion);
        Assert.False(stored.TimestampSuspect);
        // §5.3 — no sessions exist yet, so this is the documented "venue and date only" state.
        Assert.Null(stored.SessionId);
    }

    [Fact]
    public async Task A_RETRIED_batch_reports_duplicate_and_does_not_inflate_the_counts()
    {
        // 🔥 The whole point of offline caching: a unit that flushes, loses the response, and
        // flushes again must not double-count. Double-counting is invisible in a distribution.
        var (db, device, _) = await SeedAsync();
        using var _db = db;
        var svc = NewService(db, new FixedClock(Now));

        await svc.IngestAsync(device, new[] { Rec("r-1"), Rec("r-2") }, "1.0", null, false);
        var second = await svc.IngestAsync(device, new[] { Rec("r-1"), Rec("r-2") }, "1.0", null, false);

        Assert.All(second, r => Assert.Equal("duplicate", r.Status));
        Assert.Equal(2, db.EvaluationResponses.Count());
    }

    [Fact]
    public async Task A_duplicate_INSIDE_one_batch_is_handled_without_failing_the_whole_upload()
    {
        // A device retrying mid-batch can repeat a record id within a single request. Rejecting the
        // entire batch would strand every other record in it.
        var (db, device, _) = await SeedAsync();
        using var _db = db;
        var svc = NewService(db, new FixedClock(Now));

        var results = await svc.IngestAsync(
            device, new[] { Rec("r-1"), Rec("r-1"), Rec("r-2") }, "1.0", null, false);

        Assert.Equal("accepted", results[0].Status);
        Assert.Equal("duplicate", results[1].Status);
        Assert.Equal("accepted", results[2].Status);
        Assert.Equal(2, db.EvaluationResponses.Count());
    }

    [Fact]
    public async Task LATE_data_is_normal_operation_and_is_stored_at_its_COLLECTION_time()
    {
        // A unit offline for a day flushes on the final day. Its records belong to when they were
        // PRESSED, not when they arrived — however large the gap.
        var (db, device, _) = await SeedAsync();
        using var _db = db;
        var clock = new FixedClock(Now.AddDays(1));
        var svc = NewService(db, clock);

        var pressedYesterday = Now.AddHours(-2);
        var results = await svc.IngestAsync(
            device, new[] { Rec("late-1", 3, pressedYesterday) }, "1.0", null, eventClosed: false);

        Assert.Equal("accepted", Assert.Single(results).Status);
        var stored = db.EvaluationResponses.Single();
        Assert.Equal(pressedYesterday, stored.CollectionTimestamp);
        Assert.Equal(Now.AddDays(1), stored.ReceivedTimestamp);
        Assert.False(stored.TimestampSuspect);   // late ≠ suspect
    }

    [Fact]
    public async Task After_the_event_ingest_cut_off_records_are_REJECTED_not_silently_dropped()
    {
        // A unit found in a drawer weeks later must not retroactively change a published result —
        // but the refusal is reported so an unexpectedly large rejection count is visible.
        var (db, device, _) = await SeedAsync();
        using var _db = db;
        var svc = NewService(db, new FixedClock(Now));

        var results = await svc.IngestAsync(
            device, new[] { Rec("r-1") }, "1.0", null, eventClosed: true);

        var r = Assert.Single(results);
        Assert.Equal("rejected", r.Status);
        Assert.Equal("event_closed", r.Reason);
        Assert.Empty(db.EvaluationResponses);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public async Task An_out_of_range_rating_is_rejected_permanently(int rating)
    {
        var (db, device, _) = await SeedAsync();
        using var _db = db;
        var svc = NewService(db, new FixedClock(Now));

        var results = await svc.IngestAsync(
            device, new[] { Rec("r-1", rating) }, "1.0", null, false);

        Assert.Equal("rejected", Assert.Single(results).Status);
        Assert.Equal("invalid_rating", results[0].Reason);
        Assert.Empty(db.EvaluationResponses);
    }

    [Fact]
    public async Task A_record_with_no_idempotency_key_is_refused_rather_than_stored()
    {
        // Without a deviceRecordId we could never deduplicate it, so a retry would silently
        // duplicate it. Refusing is the safer failure.
        var (db, device, _) = await SeedAsync();
        using var _db = db;
        var svc = NewService(db, new FixedClock(Now));

        var results = await svc.IngestAsync(
            device, new[] { new EvaluationIngestService.IncomingRecord(null, 4, Now) }, "1.0", null, false);

        Assert.Equal("rejected", Assert.Single(results).Status);
        Assert.Equal("malformed", results[0].Reason);
        Assert.Empty(db.EvaluationResponses);
    }

    [Fact]
    public async Task A_FUTURE_timestamp_is_flagged_suspect_but_STILL_COUNTED()
    {
        // §5.5 — flagging is not filtering. The flag travels with the record so an implausible
        // result can be traced to a drifting unit rather than blamed on the speaker, and we never
        // silently rewrite what the device reported.
        var (db, device, _) = await SeedAsync();
        using var _db = db;
        var svc = NewService(db, new FixedClock(Now));

        var future = Now.AddHours(3);
        var results = await svc.IngestAsync(
            device, new[] { Rec("drift-1", 4, future) }, "1.0", null, false);

        Assert.Equal("accepted", Assert.Single(results).Status);
        var stored = db.EvaluationResponses.Single();
        Assert.True(stored.TimestampSuspect);
        Assert.Equal(future, stored.CollectionTimestamp);   // reported value preserved
    }

    // =====================================================================
    //  Heartbeat
    // =====================================================================

    [Fact]
    public async Task Heartbeat_records_the_telemetry_the_health_board_needs()
    {
        var (db, device, _) = await SeedAsync();
        using var _db = db;
        var svc = NewService(db, new FixedClock(Now));

        await svc.HeartbeatAsync(device, batteryPercent: 84, signalQuality: -71,
            cachedRecordCount: 12, firmwareVersion: "1.4.2", clockSyncedAt: Now.AddMinutes(-9));

        var d = db.EvaluationDevices.Single();
        Assert.Equal(84, d.LastBatteryPercent);
        Assert.Equal(-71, d.LastSignalQuality);
        Assert.Equal(12, d.CachedRecordCount);      // the silent-failure signal
        Assert.Equal("1.4.2", d.FirmwareVersion);
        Assert.Equal(Now, d.LastSeenAt);
    }

    [Fact]
    public async Task An_UPLOAD_also_counts_as_liveness_even_without_a_heartbeat()
    {
        // The units sleep, so liveness can never be inferred from a connection — but a device that
        // is uploading is plainly alive, and the health board must not call it silent.
        var (db, device, _) = await SeedAsync();
        using var _db = db;
        var clock = new FixedClock(Now);
        var svc = NewService(db, clock);

        Assert.Null(db.EvaluationDevices.Single().LastSeenAt);

        await svc.IngestAsync(device, new[] { Rec("r-1") }, "1.4.2", null, false);

        Assert.Equal(Now, db.EvaluationDevices.Single().LastSeenAt);
    }
}
