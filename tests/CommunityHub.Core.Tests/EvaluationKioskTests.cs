using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1259 — the WEB KIOSK option: an iPad doing the job of a built evaluation box.
///
/// <para>🔒 The built-unit design is untouched, and these tests pin the property that makes that
/// true: a kiosk press travels the SAME ingest path, gets the SAME time attribution and lands as
/// the same kind of response — only its recorded <c>Source</c> differs, for diagnostics.</para>
/// </summary>
public class EvaluationKioskTests
{
    private const int EventId = 1;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2027, 2, 9, 10, 15, 0, TimeSpan.Zero);

    private static async Task<(CommunityHub.Core.Data.CommunityHubDbContext Db, int DeviceId, int RoomId)>
        SeedAsync(bool withSession = true)
    {
        var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        await db.SaveChangesAsync();

        var room = new EvaluationRoom { EventId = EventId, Name = "Hall A", NameKey = "hall a" };
        db.EvaluationRooms.Add(room);
        await db.SaveChangesAsync();

        var device = new EvaluationDevice
        {
            EventId = EventId, SerialNumber = "IPAD-01", RoomId = room.Id, IsActive = true,
            KeyHash = EvaluationIngestService.HashKey("placeholder"),
            CreatedAt = Now, UpdatedAt = Now,
        };
        db.EvaluationDevices.Add(device);

        if (withSession)
        {
            db.EvaluationSessions.Add(new EvaluationSession
            {
                EventId = EventId, RoomId = room.Id, Title = "Zero Trust in practice",
                ScheduledStart = Now.AddMinutes(-15), ScheduledEnd = Now.AddMinutes(45),
                CollectionWindowOpensAt = Now.AddMinutes(-15),
                CollectionWindowClosesAt = Now.AddMinutes(75),
            });
        }
        await db.SaveChangesAsync();
        return (db, device.Id, room.Id);
    }

    private static EvaluationKioskService Kiosk(CommunityHub.Core.Data.CommunityHubDbContext db) =>
        new(db, new FixedClock(Now));

    // ---- pairing ------------------------------------------------------------------------

    [Fact]
    public async Task A_code_pairs_the_iPad_and_hands_back_a_usable_key()
    {
        var (db, deviceId, _) = await SeedAsync();
        var issued = await Kiosk(db).IssuePairingCodeAsync(deviceId);
        Assert.NotNull(issued);

        var paired = await Kiosk(db).RedeemAsync(issued!.Code);

        Assert.NotNull(paired);
        Assert.Equal("IPAD-01", paired!.SerialNumber);
        Assert.Equal("Hall A", paired.RoomName);
        // The key it received must actually authenticate against the ingest path.
        var device = await db.EvaluationDevices.FirstAsync();
        Assert.True(EvaluationIngestService.VerifyKey(paired.DeviceKey, device.KeyHash));
        Assert.True(device.IsKiosk);
    }

    [Fact]
    public async Task A_code_works_exactly_ONCE()
    {
        // 🔒 The second use is what a shoulder-surfed code would be.
        var (db, deviceId, _) = await SeedAsync();
        var issued = await Kiosk(db).IssuePairingCodeAsync(deviceId);

        Assert.NotNull(await Kiosk(db).RedeemAsync(issued!.Code));
        Assert.Null(await Kiosk(db).RedeemAsync(issued.Code));
    }

    [Fact]
    public async Task An_EXPIRED_code_is_refused()
    {
        var (db, deviceId, _) = await SeedAsync();
        var issued = await new EvaluationKioskService(db, new FixedClock(Now.AddHours(-3)))
            .IssuePairingCodeAsync(deviceId);

        Assert.Null(await Kiosk(db).RedeemAsync(issued!.Code));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NOPE-NOPE")]
    public async Task A_wrong_or_empty_code_is_refused(string code)
    {
        var (db, deviceId, _) = await SeedAsync();
        await Kiosk(db).IssuePairingCodeAsync(deviceId);

        Assert.Null(await Kiosk(db).RedeemAsync(code));
    }

    [Fact]
    public async Task The_code_is_forgiving_about_case_spaces_and_the_dash()
    {
        // It is read off one screen and typed on another, by someone standing in a corridor.
        var (db, deviceId, _) = await SeedAsync();
        var issued = await Kiosk(db).IssuePairingCodeAsync(deviceId);
        var messy = " " + issued!.Code.Replace("-", " ").ToLowerInvariant() + " ";

        Assert.NotNull(await Kiosk(db).RedeemAsync(messy));
    }

    [Fact]
    public async Task Re_pairing_ROTATES_the_key_so_a_lost_iPad_loses_access()
    {
        var (db, deviceId, _) = await SeedAsync();
        var first = await Kiosk(db).RedeemAsync((await Kiosk(db).IssuePairingCodeAsync(deviceId))!.Code);
        var second = await Kiosk(db).RedeemAsync((await Kiosk(db).IssuePairingCodeAsync(deviceId))!.Code);

        Assert.NotEqual(first!.DeviceKey, second!.DeviceKey);
        var device = await db.EvaluationDevices.FirstAsync();
        Assert.True(EvaluationIngestService.VerifyKey(second.DeviceKey, device.KeyHash));
        // The old key survives as the PREVIOUS one — the same staggered-rollout rule the hardware
        // rotation uses, so an iPad mid-flush of its offline queue is not cut off in that instant.
        Assert.True(EvaluationIngestService.VerifyKey(first.DeviceKey, device.PreviousKeyHash));
    }

    // ---- §1259c: the enrolment link (self-healing, no daily login) -------------------------

    [Fact]
    public async Task The_enrolment_link_works_AGAIN_after_the_browser_forgets()
    {
        // 🔑 The whole point: a storage eviction, a wipe or a replacement iPad must not need a
        // human. Unlike a pairing code, this exchange is repeatable.
        var (db, deviceId, _) = await SeedAsync();
        var token = await Kiosk(db).IssueEnrolmentTokenAsync(deviceId);

        var first = await Kiosk(db).EnrolAsync(token);
        var second = await Kiosk(db).EnrolAsync(token);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("Hall A", second!.RoomName);
    }

    [Fact]
    public async Task A_replacement_iPad_can_enrol_while_the_old_one_still_flushes_its_queue()
    {
        // The two-deep key history doing its job: the outgoing tablet keeps uploading what it
        // already collected instead of being cut off the instant the spare is brought out.
        var (db, deviceId, _) = await SeedAsync();
        var token = await Kiosk(db).IssueEnrolmentTokenAsync(deviceId);
        var oldPad = await Kiosk(db).EnrolAsync(token);
        var newPad = await Kiosk(db).EnrolAsync(token);

        var ingest = new EvaluationIngestService(db, new FixedClock(Now));
        var (viaOld, _) = await ingest.AuthenticateAsync("IPAD-01", oldPad!.DeviceKey);
        var (viaNew, _) = await ingest.AuthenticateAsync("IPAD-01", newPad!.DeviceKey);

        Assert.NotNull(viaOld);
        Assert.NotNull(viaNew);
    }

    [Fact]
    public async Task Re_issuing_the_link_REVOKES_the_previous_one()
    {
        var (db, deviceId, _) = await SeedAsync();
        var first = await Kiosk(db).IssueEnrolmentTokenAsync(deviceId);
        var second = await Kiosk(db).IssueEnrolmentTokenAsync(deviceId);

        Assert.Null(await Kiosk(db).EnrolAsync(first));
        Assert.NotNull(await Kiosk(db).EnrolAsync(second));
    }

    [Fact]
    public async Task A_revoked_link_stops_working_immediately()
    {
        // The lost-iPad path.
        var (db, deviceId, _) = await SeedAsync();
        var token = await Kiosk(db).IssueEnrolmentTokenAsync(deviceId);
        Assert.True(await Kiosk(db).RevokeEnrolmentTokenAsync(deviceId));

        Assert.Null(await Kiosk(db).EnrolAsync(token));
    }

    [Fact]
    public async Task A_RETIRED_device_cannot_enrol_even_with_a_valid_link()
    {
        // 🔒 Deactivating a device is an organiser's decision and must beat a bookmark.
        var (db, deviceId, _) = await SeedAsync();
        var token = await Kiosk(db).IssueEnrolmentTokenAsync(deviceId);
        var device = await db.EvaluationDevices.FirstAsync(d => d.Id == deviceId);
        device.IsActive = false;
        await db.SaveChangesAsync();

        Assert.Null(await Kiosk(db).EnrolAsync(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-real-token")]
    public async Task A_wrong_or_missing_enrolment_token_is_refused(string? token)
    {
        var (db, deviceId, _) = await SeedAsync();
        await Kiosk(db).IssueEnrolmentTokenAsync(deviceId);

        Assert.Null(await Kiosk(db).EnrolAsync(token));
    }

    // ---- §1259d: self-enrolment into the queue a human already watches ---------------------

    [Fact]
    public async Task A_new_screen_may_join_while_the_queue_has_room()
    {
        var (db, _, _) = await SeedAsync();
        Assert.True(await Kiosk(db).MayJoinAsync(EventId, "KIOSK-NEW0001"));
    }

    [Fact]
    public async Task A_FULL_queue_refuses_a_new_screen_but_never_one_already_waiting()
    {
        // 🔒 The cap protects a person's inbox from being buried — the realistic abuse of an
        // endpoint that grants nothing. But a device ALREADY in the queue is polling for its
        // answer, and locking it out because others are also enrolling would strand exactly the
        // tablets an organiser is working through.
        var (db, _, _) = await SeedAsync();
        var now = Now;
        for (var i = 0; i < EvaluationKioskService.MaxPendingJoins; i++)
        {
            db.EvaluationDeviceProvisionRequests.Add(new EvaluationDeviceProvisionRequest
            {
                EventId = EventId, SerialNumber = $"KIOSK-FILL{i:0000}",
                Status = EvaluationProvisionStatus.Pending,
                RequestedAt = now, LastRequestedAt = now, CreatedAt = now,
            });
        }
        await db.SaveChangesAsync();

        Assert.False(await Kiosk(db).MayJoinAsync(EventId, "KIOSK-LATE0001"));
        Assert.True(await Kiosk(db).MayJoinAsync(EventId, "KIOSK-FILL0003"));
    }

    [Fact]
    public async Task An_APPROVED_screen_does_not_count_against_the_cap()
    {
        // Only PENDING rows are the inbox; approving is what empties it.
        var (db, _, _) = await SeedAsync();
        var now = Now;
        for (var i = 0; i < EvaluationKioskService.MaxPendingJoins; i++)
        {
            db.EvaluationDeviceProvisionRequests.Add(new EvaluationDeviceProvisionRequest
            {
                EventId = EventId, SerialNumber = $"KIOSK-DONE{i:0000}",
                Status = EvaluationProvisionStatus.Approved,
                RequestedAt = now, LastRequestedAt = now, CreatedAt = now,
            });
        }
        await db.SaveChangesAsync();

        Assert.True(await Kiosk(db).MayJoinAsync(EventId, "KIOSK-NEW0002"));
    }

    [Fact]
    public async Task Approving_a_joined_screen_marks_it_as_a_kiosk_so_its_votes_say_so()
    {
        var (db, deviceId, _) = await SeedAsync();
        var device = await db.EvaluationDevices.FirstAsync(d => d.Id == deviceId);
        Assert.False(device.IsKiosk);

        await Kiosk(db).MarkAsKioskAsync(deviceId);

        Assert.True((await db.EvaluationDevices.FirstAsync(d => d.Id == deviceId)).IsKiosk);
    }

    // ---- §1270: the running session says when feedback closes -----------------------------

    [Fact]
    public async Task The_running_session_reports_its_close_as_end_plus_the_30_minute_grace()
    {
        // Operator 2026-09-21: "once the session stops, then capture must be available for 30 min
        // extra … then it capture closes for that session. confirm that is the rule". It is — and
        // the screen now shows it, so the close is never a surprise.
        var (db, deviceId, _) = await SeedAsync();   // session 09:45–10:45 UTC, window to 11:15
        var device = await db.EvaluationDevices.FirstAsync(d => d.Id == deviceId);

        var current = await Kiosk(db).CurrentSessionAsync(device);

        Assert.NotNull(current);
        Assert.Equal(Now.AddMinutes(45), current!.EndsAt);
        Assert.Equal(Now.AddMinutes(75), current.ClosesAt);
        Assert.Equal(current.EndsAt!.Value.AddMinutes(EvaluationAttribution.GraceMinutes), current.ClosesAt);
    }

    // ---- §1262: an idle screen counts down to the next session --------------------------

    [Fact]
    public async Task The_next_session_is_the_EARLIEST_window_still_to_open_in_the_room()
    {
        // The running session (window opened 15 min ago) is not "next"; of the two later ones the
        // earlier wins, and a later session in ANOTHER room never shows on this screen.
        var (db, deviceId, roomId) = await SeedAsync();
        var otherRoom = new EvaluationRoom { EventId = EventId, Name = "Hall B", NameKey = "hall b" };
        db.EvaluationRooms.Add(otherRoom);
        await db.SaveChangesAsync();
        db.EvaluationSessions.AddRange(
            Slot(roomId, "Afternoon keynote", Now.AddHours(4)),
            Slot(roomId, "After lunch", Now.AddHours(2)),
            Slot(otherRoom.Id, "Elsewhere", Now.AddHours(1)));
        await db.SaveChangesAsync();

        var next = await Kiosk(db).NextSessionAsync(await db.EvaluationDevices.FirstAsync(d => d.Id == deviceId));

        Assert.NotNull(next);
        Assert.Equal("After lunch", next!.Title);
        Assert.Equal(Now.AddHours(2), next.OpensAt);
        // Closes at the COLLECTION window's end (slot end + grace), which is what the screen promises.
        Assert.Equal(Now.AddHours(3).AddMinutes(EvaluationAttribution.GraceMinutes), next.ClosesAt);
    }

    [Fact]
    public async Task There_is_no_next_session_when_nothing_is_left_in_the_room_or_no_room_is_linked()
    {
        var (db, deviceId, _) = await SeedAsync();
        var device = await db.EvaluationDevices.FirstAsync(d => d.Id == deviceId);
        Assert.Null(await Kiosk(db).NextSessionAsync(device));

        device.RoomId = null;
        Assert.Null(await Kiosk(db).NextSessionAsync(device));
    }

    private static EvaluationSession Slot(int roomId, string title, DateTimeOffset start)
    {
        var (opens, closes) = EvaluationAttribution.WindowFor(start, start.AddHours(1));
        return new EvaluationSession
        {
            EventId = EventId, RoomId = roomId, Title = title,
            ScheduledStart = start, ScheduledEnd = start.AddHours(1),
            CollectionWindowOpensAt = opens, CollectionWindowClosesAt = closes,
        };
    }

    [Theory]
    [InlineData("Room 17 stand", "Room 17 stand")]
    [InlineData(null, null)]      // §1266 — no name, no label: the page shows the serial instead
    [InlineData("   ", null)]
    public async Task An_approved_screen_is_labelled_with_the_name_typed_at_enrollment(
        string? typedName, string? expectedLabel)
    {
        // Operator 2026-09-21: *"i added a name when i enrolled, why is that not used"* — the
        // device was minted as "Serial KIOSK-…" and the name only lived on the queue row.
        var (db, _, _) = await SeedAsync();
        var provisioning = new EvaluationProvisioningService(db, new FixedClock(Now));

        var pending = await provisioning.RequestAsync(EventId, "KIOSK-NAME0001", "web-kiosk/1", typedName);
        Assert.Equal(EvaluationProvisioningService.Outcome.Pending, pending.Outcome);
        Assert.True(await provisioning.ApproveAsync(pending.Request.Id, "organiser@example.test"));

        // The next poll after approval mints the device — without re-sending the name.
        var approved = await provisioning.RequestAsync(EventId, "KIOSK-NAME0001", "web-kiosk/1", note: null);

        Assert.Equal(EvaluationProvisioningService.Outcome.Approved, approved.Outcome);
        Assert.Equal(expectedLabel, approved.Device!.Label);
    }

    [Fact]
    public void The_enrolment_QR_renders_a_real_png()
    {
        // The tablets have no mail and no comfortable keyboard, so the camera IS the way in —
        // a QR that fails to render is the difference between a stand working and not.
        var png = EvaluationKioskService.RenderQrPng("https://example.test/eval");

        Assert.True(png.Length > 100);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());   // PNG magic
    }

    // ---- what the screen says ------------------------------------------------------------

    [Fact]
    public async Task The_kiosk_is_told_the_session_running_in_ITS_room()
    {
        var (db, deviceId, _) = await SeedAsync();
        var device = await db.EvaluationDevices.FirstAsync(d => d.Id == deviceId);

        var session = await Kiosk(db).CurrentSessionAsync(device);

        Assert.NotNull(session);
        Assert.Equal("Zero Trust in practice", session!.Title);
    }

    [Fact]
    public async Task Between_sessions_the_kiosk_is_told_NOTHING_rather_than_something_stale()
    {
        // 🔑 A blank line is honest; a leftover title would have attendees rating a talk that
        // finished an hour ago. The press is still recorded either way.
        var (db, deviceId, _) = await SeedAsync(withSession: false);
        var device = await db.EvaluationDevices.FirstAsync(d => d.Id == deviceId);

        Assert.Null(await Kiosk(db).CurrentSessionAsync(device));
    }

    [Fact]
    public async Task A_device_with_no_room_has_no_session_to_show()
    {
        var (db, deviceId, _) = await SeedAsync();
        var device = await db.EvaluationDevices.FirstAsync(d => d.Id == deviceId);
        device.RoomId = null;
        await db.SaveChangesAsync();

        Assert.Null(await Kiosk(db).CurrentSessionAsync(device));
    }

    // ---- one instrument --------------------------------------------------------------------

    [Fact]
    public async Task A_kiosk_press_lands_on_the_same_session_as_a_box_press_and_is_marked_kiosk()
    {
        // 🔒 The property that keeps the two channels one instrument: same ingest call, same
        // attribution, same row — only Source differs, and that is for diagnostics only.
        var (db, deviceId, _) = await SeedAsync();
        var paired = await Kiosk(db).RedeemAsync((await Kiosk(db).IssuePairingCodeAsync(deviceId))!.Code);
        var ingest = new EvaluationIngestService(db, new FixedClock(Now));

        var (device, _) = await ingest.AuthenticateAsync("IPAD-01", paired!.DeviceKey);
        Assert.NotNull(device);

        await ingest.IngestAsync(
            device!,
            [new EvaluationIngestService.IncomingRecord("vote-1", 4, Now)],
            firmwareVersion: "web-kiosk/1", clockSyncedAt: Now, eventClosed: false);

        var stored = await db.EvaluationResponses.SingleAsync();
        Assert.Equal(4, stored.Rating);
        Assert.Equal(EvaluationResponseSources.Kiosk, stored.Source);
        Assert.NotNull(stored.SessionId);   // attributed by room + time, exactly like a box press
    }

    [Fact]
    public async Task A_replayed_vote_from_an_offline_queue_cannot_double_count()
    {
        // The offline queue retries whole batches, so the idempotency key is what stops a flaky
        // network inflating a speaker's score.
        var (db, deviceId, _) = await SeedAsync();
        var paired = await Kiosk(db).RedeemAsync((await Kiosk(db).IssuePairingCodeAsync(deviceId))!.Code);
        var ingest = new EvaluationIngestService(db, new FixedClock(Now));
        var (device, _) = await ingest.AuthenticateAsync("IPAD-01", paired!.DeviceKey);

        var batch = new List<EvaluationIngestService.IncomingRecord>
        {
            new("vote-1", 4, Now),
        };
        await ingest.IngestAsync(device!, batch, "web-kiosk/1", Now, false);
        var second = await ingest.IngestAsync(device!, batch, "web-kiosk/1", Now, false);

        Assert.Single(await db.EvaluationResponses.ToListAsync());
        Assert.Equal(EvaluationIngestService.RecordStatus.Duplicate, second[0].Status);
    }

    // ---- §1349: one tablet, two room records (pre-day "-MC" + main day) ------------------

    /// <summary>
    /// The seed's device stands in "Hall A" (main day, session running NOW). Adds the same
    /// physical room's pre-day record "Hall A-MC" with a master class the day before, and links it.
    /// </summary>
    private static async Task<(CommunityHub.Core.Data.CommunityHubDbContext Db, EvaluationDevice Device, int McRoomId)>
        SeedWithMasterClassRoomAsync(bool withMainSession = true)
    {
        var (db, deviceId, _) = await SeedAsync(withMainSession);
        var mcRoom = new EvaluationRoom { EventId = EventId, Name = "Hall A-MC", NameKey = "hall a-mc" };
        db.EvaluationRooms.Add(mcRoom);
        await db.SaveChangesAsync();
        db.EvaluationSessions.Add(Slot(mcRoom.Id, "Intune Master Class", Now.AddDays(-1)));
        var device = await db.EvaluationDevices.FirstAsync(d => d.Id == deviceId);
        device.MasterClassRoomId = mcRoom.Id;
        await db.SaveChangesAsync();
        return (db, device, mcRoom.Id);
    }

    [Fact]
    public async Task A_tablet_linked_to_both_records_attributes_each_day_to_its_own_room()
    {
        // Operator 2026-10-19: *"mc is for preday and same room withut mc is for main day"* — the
        // press time decides which of the two linked rooms' sessions a vote lands on.
        var (db, device, mcRoomId) = await SeedWithMasterClassRoomAsync();
        var ingest = new EvaluationIngestService(db, new FixedClock(Now));

        await ingest.IngestAsync(device,
            [
                new EvaluationIngestService.IncomingRecord("pre-day", 4, Now.AddDays(-1).AddMinutes(20)),
                new EvaluationIngestService.IncomingRecord("main-day", 3, Now),
            ],
            firmwareVersion: "web-kiosk/1", clockSyncedAt: Now, eventClosed: false);

        var mc = await db.EvaluationSessions.SingleAsync(s => s.RoomId == mcRoomId);
        var main = await db.EvaluationSessions.SingleAsync(s => s.Title == "Zero Trust in practice");
        Assert.Equal(mc.Id, (await db.EvaluationResponses.SingleAsync(r => r.DeviceRecordId == "pre-day")).SessionId);
        Assert.Equal(main.Id, (await db.EvaluationResponses.SingleAsync(r => r.DeviceRecordId == "main-day")).SessionId);
    }

    [Fact]
    public async Task The_kiosk_shows_and_counts_down_to_the_master_class_in_the_pre_day_room()
    {
        // Only the master-class link: the main-day room has nothing yet, the screen still works.
        var (db, device, mcRoomId) = await SeedWithMasterClassRoomAsync(withMainSession: false);
        device.RoomId = null;
        db.EvaluationSessions.Add(Slot(mcRoomId, "Azure Master Class", Now.AddMinutes(-10)));
        db.EvaluationSessions.Add(Slot(mcRoomId, "Security Master Class", Now.AddHours(2)));
        await db.SaveChangesAsync();

        Assert.Equal("Azure Master Class", (await Kiosk(db).CurrentSessionAsync(device))!.Title);
        Assert.Equal("Security Master Class", (await Kiosk(db).NextSessionAsync(device))!.Title);
        // The screen names the room it is linked to — the master-class one when that is all it has.
        Assert.Equal("Hall A-MC", await Kiosk(db).RoomNameAsync(device));
    }

    [Fact]
    public async Task With_both_links_the_screen_names_the_main_day_room()
    {
        var (db, device, _) = await SeedWithMasterClassRoomAsync();

        Assert.Equal("Hall A", await Kiosk(db).RoomNameAsync(device));
    }

    [Fact]
    public async Task Backfill_attributes_an_early_pre_day_press_once_the_master_class_room_is_linked()
    {
        // A press collected before the organiser added the master-class link stays unattributed,
        // and the sync's backfill picks it up through the new link.
        var (db, device, mcRoomId) = await SeedWithMasterClassRoomAsync();
        db.EvaluationResponses.Add(new EvaluationResponse
        {
            EventId = EventId, SerialNumber = device.SerialNumber, Rating = 4,
            CollectionTimestamp = Now.AddDays(-1).AddMinutes(20),
            ReceivedTimestamp = Now, DeviceRecordId = "early", SessionId = null,
        });
        await db.SaveChangesAsync();

        Assert.Equal(1, await new EvaluationSessionSyncService(db, new FixedClock(Now)).BackfillAsync(EventId));
        var mc = await db.EvaluationSessions.SingleAsync(s => s.RoomId == mcRoomId);
        Assert.Equal(mc.Id, (await db.EvaluationResponses.SingleAsync()).SessionId);
    }
}
