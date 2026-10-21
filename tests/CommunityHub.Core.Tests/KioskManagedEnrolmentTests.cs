using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1303 — the organizer PIN and one-tap enrolment of Intune-managed kiosk tablets.
/// </summary>
/// <remarks>
/// Operator 2026-09-25: <i>"none must be able to modify except organizers with pin code"</i> ·
/// <i>"use the intune device name during enrollment … then it should auto-approve it"</i>.
/// </remarks>
public class KioskManagedEnrolmentTests
{
    private const int EventId = 1;

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = new(2027, 2, 9, 10, 15, 0, TimeSpan.Zero);

    // ---- PIN guard ---------------------------------------------------------------------------

    [Fact]
    public void Right_PIN_passes_and_wrong_PIN_is_refused()
    {
        var guard = new KioskPinGuard(new MovableClock(T0));
        Assert.Equal(KioskPinGuard.Verdict.Ok, guard.Check("4711", "4711", "10.0.0.1"));
        Assert.Equal(KioskPinGuard.Verdict.Ok, guard.Check("4711", " 4711 ", "10.0.0.1"));
        Assert.Equal(KioskPinGuard.Verdict.Wrong, guard.Check("4711", "4712", "10.0.0.1"));
        Assert.Equal(KioskPinGuard.Verdict.Wrong, guard.Check("4711", null, "10.0.0.1"));
    }

    [Fact]
    public void No_PIN_configured_says_so_instead_of_accepting_or_refusing()
    {
        var guard = new KioskPinGuard(new MovableClock(T0));
        Assert.Equal(KioskPinGuard.Verdict.NotConfigured, guard.Check("", "anything", "10.0.0.1"));
        Assert.Equal(KioskPinGuard.Verdict.NotConfigured, guard.Check(null, null, "10.0.0.1"));
    }

    [Fact]
    public void Five_wrong_PINs_lock_the_client_out_even_for_the_right_PIN_until_the_window_passes()
    {
        var clock = new MovableClock(T0);
        var guard = new KioskPinGuard(clock);
        for (var i = 0; i < KioskPinGuard.MaxFailures; i++)
            Assert.Equal(KioskPinGuard.Verdict.Wrong, guard.Check("4711", "0000", "10.0.0.1"));

        Assert.Equal(KioskPinGuard.Verdict.LockedOut, guard.Check("4711", "4711", "10.0.0.1"));
        // Another client is not affected.
        Assert.Equal(KioskPinGuard.Verdict.Ok, guard.Check("4711", "4711", "10.0.0.2"));

        clock.Now = T0 + KioskPinGuard.Window;
        Assert.Equal(KioskPinGuard.Verdict.Ok, guard.Check("4711", "4711", "10.0.0.1"));
    }

    [Fact]
    public void A_right_PIN_clears_earlier_failures()
    {
        var guard = new KioskPinGuard(new MovableClock(T0));
        for (var i = 0; i < KioskPinGuard.MaxFailures - 1; i++) guard.Check("4711", "0000", "c");
        Assert.Equal(KioskPinGuard.Verdict.Ok, guard.Check("4711", "4711", "c"));
        for (var i = 0; i < KioskPinGuard.MaxFailures - 1; i++)
            Assert.Equal(KioskPinGuard.Verdict.Wrong, guard.Check("4711", "0000", "c"));
    }

    // ---- managed enrolment ---------------------------------------------------------------------

    private static async Task<CommunityHub.Core.Data.CommunityHubDbContext> SeedAsync()
    {
        var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task A_new_Intune_name_becomes_an_approved_kiosk_named_after_it_with_a_working_key()
    {
        await using var db = await SeedAsync();
        var kiosk = new EvaluationKioskService(db, new MovableClock(T0));

        var paired = await kiosk.EnrolManagedAsync(EventId, "ELDK-HA2QHAN3");

        Assert.Equal("ELDK-HA2QHAN3", paired.SerialNumber);
        Assert.Equal("ELDK-HA2QHAN3", paired.Label);
        var device = await db.EvaluationDevices.SingleAsync();
        Assert.True(device.IsActive);
        Assert.True(device.IsKiosk);
        Assert.True(EvaluationIngestService.VerifyKey(paired.DeviceKey, device.KeyHash));
        // Approved on the spot: nothing waits in the queue.
        Assert.Empty(db.EvaluationDeviceProvisionRequests);
    }

    [Fact]
    public async Task Enrolling_the_same_name_again_reconnects_that_device_and_keeps_its_room()
    {
        await using var db = await SeedAsync();
        var room = new EvaluationRoom { EventId = EventId, Name = "Hall A", NameKey = "hall a" };
        db.EvaluationRooms.Add(room);
        await db.SaveChangesAsync();
        var kiosk = new EvaluationKioskService(db, new MovableClock(T0));

        var first = await kiosk.EnrolManagedAsync(EventId, "ELDK-HA2QHAN3");
        var device = await db.EvaluationDevices.SingleAsync();
        device.RoomId = room.Id;
        await db.SaveChangesAsync();

        var second = await kiosk.EnrolManagedAsync(EventId, "ELDK-HA2QHAN3");

        Assert.Single(db.EvaluationDevices);
        Assert.Equal("Hall A", second.RoomName);
        Assert.True(EvaluationIngestService.VerifyKey(second.DeviceKey, device.KeyHash));
        // The previous key still works, so a queue that is still flushing is not cut off.
        Assert.True(EvaluationIngestService.VerifyKey(first.DeviceKey, device.PreviousKeyHash));
    }

    [Fact]
    public async Task Enrolled_rooms_reports_which_names_are_enrolled_and_where()
    {
        await using var db = await SeedAsync();
        var kiosk = new EvaluationKioskService(db, new MovableClock(T0));
        await kiosk.EnrolManagedAsync(EventId, "ELDK-AAAA1111");

        var map = await kiosk.EnrolledRoomsAsync(EventId, new[] { "ELDK-AAAA1111", "ELDK-BBBB2222" });

        Assert.True(map.ContainsKey("ELDK-AAAA1111"));
        Assert.Null(map["ELDK-AAAA1111"]);
        Assert.False(map.ContainsKey("ELDK-BBBB2222"));
    }
}
