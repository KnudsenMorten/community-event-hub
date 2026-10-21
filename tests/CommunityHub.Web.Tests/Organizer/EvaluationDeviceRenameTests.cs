using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CommunityHub.Web.Tests.Organizer;

/// <summary>
/// §1265 — a device's name can be changed after it was created (operator 2026-09-21: a kiosk approved
/// before §1261 kept "Serial KIOSK-…", and the name was only settable at Add).
/// </summary>
public sealed class EvaluationDeviceRenameTests
{
    private const int EventId = 93;

    private sealed class FakeAccessor(CurrentParticipant? cur) : ICurrentParticipantAccessor
    {
        public CurrentParticipant? Current { get; } = cur;
    }

    private static CurrentParticipant? SignedIn(Participant p) =>
        CurrentParticipant.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new(ClaimTypes.Email, p.Email),
            new(ClaimTypes.Name, p.FullName),
            new(ClaimTypes.Role, p.Role.ToString()),
            new("EventId", p.EventId.ToString()),
        }, CookieAuthenticationDefaults.AuthenticationScheme)));

    private static async Task<(EvaluationDevicesModel Page, CommunityHubDbContext Db, int DeviceId)> SetUpAsync(
        ParticipantRole role, int deviceEventId = EventId)
    {
        var db = new CommunityHubDbContext(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"evaldev-rename-{Guid.NewGuid():N}").Options);
        db.Events.Add(new Event
        {
            Id = EventId, Code = "R27", CommunityName = "C", DisplayName = "R 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        var me = new Participant
        {
            EventId = EventId, Email = "org@x.test", FullName = "Org", Role = role, IsActive = true,
        };
        db.Participants.Add(me);
        var device = new EvaluationDevice
        {
            EventId = deviceEventId, SerialNumber = "KIOSK-DA7BCF9411", Label = "Serial KIOSK-DA7BCF9411",
            KeyHash = "x", IsActive = true,
        };
        db.EvaluationDevices.Add(device);
        await db.SaveChangesAsync();

        // The page reads an optional setting from the request's services; an empty container answers null.
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("hub.example");
        var page = new EvaluationDevicesModel(
            new FakeAccessor(SignedIn(me)), db, TimeProvider.System,
            new EvaluationProvisioningService(db), new EvaluationKioskService(db, TimeProvider.System))
        {
            PageContext = new PageContext { HttpContext = http },
        };
        return (page, db, device.Id);
    }

    [Fact]
    public async Task An_organizer_can_rename_a_device_and_the_name_is_trimmed()
    {
        var (page, db, id) = await SetUpAsync(ParticipantRole.Organizer);

        await page.OnPostRenameAsync(id, "  Room 16 stand  ", CancellationToken.None);

        Assert.Equal("Room 16 stand", (await db.EvaluationDevices.SingleAsync()).Label);
    }

    [Fact]
    public async Task A_blank_name_clears_it()
    {
        var (page, db, id) = await SetUpAsync(ParticipantRole.Organizer);

        await page.OnPostRenameAsync(id, "   ", CancellationToken.None);

        Assert.Null((await db.EvaluationDevices.SingleAsync()).Label);
    }

    [Fact]
    public async Task A_non_organizer_cannot_rename()
    {
        var (page, db, id) = await SetUpAsync(ParticipantRole.Speaker);

        var result = await page.OnPostRenameAsync(id, "Mine now", CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Equal("Serial KIOSK-DA7BCF9411", (await db.EvaluationDevices.SingleAsync()).Label);
    }

    // ---- §1349: the pre-day (master-class) room is a second, independent link -------------

    private static async Task<(int Main, int Mc)> RoomsAsync(CommunityHubDbContext db)
    {
        var main = new EvaluationRoom { EventId = EventId, Name = "Room-16-Floor 1-Max 44", NameKey = "room-16-floor 1-max 44" };
        var mc = new EvaluationRoom { EventId = EventId, Name = "Room-16-Floor 1-Max 32-MC", NameKey = "room-16-floor 1-max 32-mc" };
        db.EvaluationRooms.AddRange(main, mc);
        await db.SaveChangesAsync();
        return (main.Id, mc.Id);
    }

    [Fact]
    public async Task An_organizer_links_a_device_to_its_main_day_AND_its_master_class_room()
    {
        // Operator 2026-10-19: room 16 is two records — "-MC" for the pre-day, the plain one for
        // the main days — and one tablet stands in it.
        var (page, db, id) = await SetUpAsync(ParticipantRole.Organizer);
        var (main, mc) = await RoomsAsync(db);

        await page.OnPostSetRoomAsync(id, main, CancellationToken.None);
        await page.OnPostSetMasterClassRoomAsync(id, mc, CancellationToken.None);

        var device = await db.EvaluationDevices.SingleAsync();
        Assert.Equal(main, device.RoomId);
        Assert.Equal(mc, device.MasterClassRoomId);
        Assert.Equal(new[] { main, mc }, device.LinkedRoomIds());
    }

    [Fact]
    public async Task Clearing_the_master_class_room_keeps_the_main_day_room()
    {
        var (page, db, id) = await SetUpAsync(ParticipantRole.Organizer);
        var (main, mc) = await RoomsAsync(db);
        await page.OnPostSetRoomAsync(id, main, CancellationToken.None);
        await page.OnPostSetMasterClassRoomAsync(id, mc, CancellationToken.None);

        await page.OnPostSetMasterClassRoomAsync(id, null, CancellationToken.None);

        var device = await db.EvaluationDevices.SingleAsync();
        Assert.Equal(main, device.RoomId);
        Assert.Null(device.MasterClassRoomId);
    }

    [Fact]
    public async Task A_non_organizer_cannot_set_the_master_class_room()
    {
        var (page, db, id) = await SetUpAsync(ParticipantRole.Speaker);
        var (_, mc) = await RoomsAsync(db);

        var result = await page.OnPostSetMasterClassRoomAsync(id, mc, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Null((await db.EvaluationDevices.SingleAsync()).MasterClassRoomId);
    }

    // ---- §1353/§1354: capture policies ---------------------------------------------------------

    [Fact]
    public async Task An_organizer_switches_the_default_kiosk_policy_to_one_tap_and_back_and_the_tablets_are_told()
    {
        var (page, db, id) = await SetUpAsync(ParticipantRole.Organizer);
        var kiosk = new EvaluationKioskService(db, TimeProvider.System);
        var device = await db.EvaluationDevices.SingleAsync(d => d.Id == id);
        Assert.Equal(FeedbackCapturePolicy.RatingFollowUps, await kiosk.CapturePolicyAsync(device));   // default

        await page.OnPostSetDefaultPolicyAsync("kiosk", (int)FeedbackCapturePolicy.RatingOnly, CancellationToken.None);
        Assert.Equal(FeedbackCapturePolicy.RatingOnly, page.DefaultKioskPolicy);
        Assert.Equal(FeedbackCapturePolicy.RatingOnly, await kiosk.CapturePolicyAsync(device));

        await page.OnPostSetDefaultPolicyAsync("kiosk", (int)FeedbackCapturePolicy.RatingFollowUps, CancellationToken.None);
        Assert.Equal(FeedbackCapturePolicy.RatingFollowUps, await kiosk.CapturePolicyAsync(device));
    }

    [Fact]
    public async Task A_kiosk_default_refuses_a_free_text_policy_and_the_QR_default_accepts_it()
    {
        var (page, db, _) = await SetUpAsync(ParticipantRole.Organizer);

        await page.OnPostSetDefaultPolicyAsync("kiosk", (int)FeedbackCapturePolicy.RatingFreeText, CancellationToken.None);
        await page.OnPostSetDefaultPolicyAsync("qr", (int)FeedbackCapturePolicy.RatingFreeText, CancellationToken.None);

        var ev = await db.Events.SingleAsync();
        Assert.Null(ev.KioskCapturePolicy);
        Assert.Equal(FeedbackCapturePolicy.RatingFreeText, ev.QrCapturePolicy);
        Assert.Equal(FeedbackCapturePolicy.RatingFreeText, page.DefaultQrPolicy);
    }

    [Fact]
    public async Task A_tablet_gets_its_own_policy_and_back_to_the_default()
    {
        var (page, db, id) = await SetUpAsync(ParticipantRole.Organizer);

        await page.OnPostSetDevicePolicyAsync(id, (int)FeedbackCapturePolicy.RatingOnly, CancellationToken.None);
        Assert.Equal(FeedbackCapturePolicy.RatingOnly, (await db.EvaluationDevices.SingleAsync()).CapturePolicy);

        await page.OnPostSetDevicePolicyAsync(id, null, CancellationToken.None);
        Assert.Null((await db.EvaluationDevices.SingleAsync()).CapturePolicy);
    }

    [Fact]
    public async Task A_physical_room_gets_its_own_QR_policy()
    {
        var (page, db, _) = await SetUpAsync(ParticipantRole.Organizer);
        await page.OnPostAddPhysicalRoomAsync("Room 16", CancellationToken.None);
        var room = await db.EvaluationPhysicalRooms.SingleAsync();

        await page.OnPostSetRoomQrPolicyAsync(room.Id, (int)FeedbackCapturePolicy.RatingFollowUps, CancellationToken.None);

        Assert.Equal(FeedbackCapturePolicy.RatingFollowUps, (await db.EvaluationPhysicalRooms.SingleAsync()).QrCapturePolicy);
    }

    // ---- §1355: rating-button symbols ----------------------------------------------------------

    [Fact]
    public async Task An_organizer_sets_default_symbols_per_channel_and_refuses_three_at_once()
    {
        var (page, db, _) = await SetUpAsync(ParticipantRole.Organizer);

        await page.OnPostSetDefaultSymbolsAsync("kiosk", (int)(RatingSymbols.Faces | RatingSymbols.Stars), CancellationToken.None);
        await page.OnPostSetDefaultSymbolsAsync("qr", (int)RatingSymbols.Points, CancellationToken.None);
        await page.OnPostSetDefaultSymbolsAsync("qr", 7, CancellationToken.None);   // three — refused

        var ev = await db.Events.SingleAsync();
        Assert.Equal(RatingSymbols.Faces | RatingSymbols.Stars, ev.KioskRatingSymbols);
        Assert.Equal(RatingSymbols.Points, ev.QrRatingSymbols);
        Assert.Equal(RatingSymbols.Points, page.DefaultQrSymbols);
    }

    [Fact]
    public async Task A_tablet_and_a_room_get_their_own_symbols_and_back_to_the_default()
    {
        var (page, db, id) = await SetUpAsync(ParticipantRole.Organizer);
        await page.OnPostAddPhysicalRoomAsync("Room 18", CancellationToken.None);
        var room = await db.EvaluationPhysicalRooms.SingleAsync();

        await page.OnPostSetDeviceSymbolsAsync(id, (int)RatingSymbols.Stars, CancellationToken.None);
        await page.OnPostSetRoomQrSymbolsAsync(room.Id, (int)(RatingSymbols.Stars | RatingSymbols.Points), CancellationToken.None);
        Assert.Equal(RatingSymbols.Stars, (await db.EvaluationDevices.SingleAsync()).RatingSymbols);
        Assert.Equal(RatingSymbols.Stars | RatingSymbols.Points, (await db.EvaluationPhysicalRooms.SingleAsync()).QrRatingSymbols);

        await page.OnPostSetDeviceSymbolsAsync(id, null, CancellationToken.None);
        Assert.Null((await db.EvaluationDevices.SingleAsync()).RatingSymbols);
    }

    [Fact]
    public async Task A_non_organizer_cannot_change_a_policy()
    {
        var (page, db, id) = await SetUpAsync(ParticipantRole.Speaker);

        Assert.IsType<ForbidResult>(await page.OnPostSetDefaultPolicyAsync("kiosk", 0, CancellationToken.None));
        Assert.IsType<ForbidResult>(await page.OnPostSetDevicePolicyAsync(id, 0, CancellationToken.None));
        Assert.Null((await db.Events.SingleAsync()).KioskCapturePolicy);
        Assert.Null((await db.EvaluationDevices.SingleAsync()).CapturePolicy);
    }

    // ---- §1350: physical rooms -----------------------------------------------------------------

    [Fact]
    public async Task An_organizer_adds_a_physical_room_maps_schedule_rooms_to_it_and_links_a_tablet()
    {
        var (page, db, id) = await SetUpAsync(ParticipantRole.Organizer);
        var (main, mc) = await RoomsAsync(db);

        await page.OnPostAddPhysicalRoomAsync("  Room 16  ", CancellationToken.None);
        var room = await db.EvaluationPhysicalRooms.SingleAsync();
        Assert.Equal("Room 16", room.Name);

        await page.OnPostSavePhysicalRoomAsync(room.Id, "Room 16", new[] { main, mc }, CancellationToken.None);
        await page.OnPostSetPhysicalRoomAsync(id, room.Id, CancellationToken.None);

        Assert.Equal(new[] { main, mc }.OrderBy(x => x),
            (await db.EvaluationRoomPlacements.Where(p => p.PhysicalRoomId == room.Id).Select(p => p.EvaluationRoomId).ToListAsync()).OrderBy(x => x));
        Assert.Equal(room.Id, (await db.EvaluationDevices.SingleAsync()).PhysicalRoomId);
    }

    [Fact]
    public async Task Saving_replaces_the_mapping_and_ignores_a_room_of_another_edition()
    {
        var (page, db, _) = await SetUpAsync(ParticipantRole.Organizer);
        var (main, mc) = await RoomsAsync(db);
        var foreign = new EvaluationRoom { EventId = EventId + 1, Name = "Elsewhere", NameKey = "elsewhere" };
        db.EvaluationRooms.Add(foreign);
        await db.SaveChangesAsync();
        await page.OnPostAddPhysicalRoomAsync("Room 16", CancellationToken.None);
        var room = await db.EvaluationPhysicalRooms.SingleAsync();
        await page.OnPostSavePhysicalRoomAsync(room.Id, "Room 16", new[] { main, mc }, CancellationToken.None);

        await page.OnPostSavePhysicalRoomAsync(room.Id, "Room 16", new[] { mc, foreign.Id }, CancellationToken.None);

        Assert.Equal(new[] { mc }, await db.EvaluationRoomPlacements.Select(p => p.EvaluationRoomId).ToListAsync());
    }

    [Fact]
    public async Task A_duplicate_physical_room_name_is_refused()
    {
        var (page, db, _) = await SetUpAsync(ParticipantRole.Organizer);
        await page.OnPostAddPhysicalRoomAsync("Room 16", CancellationToken.None);

        await page.OnPostAddPhysicalRoomAsync("room 16 ", CancellationToken.None);

        Assert.Single(await db.EvaluationPhysicalRooms.ToListAsync());
    }

    [Fact]
    public async Task A_physical_room_with_a_tablet_cannot_be_deleted_and_an_empty_one_can()
    {
        var (page, db, id) = await SetUpAsync(ParticipantRole.Organizer);
        var (main, _) = await RoomsAsync(db);
        await page.OnPostAddPhysicalRoomAsync("Room 16", CancellationToken.None);
        var room = await db.EvaluationPhysicalRooms.SingleAsync();
        await page.OnPostSavePhysicalRoomAsync(room.Id, "Room 16", new[] { main }, CancellationToken.None);
        await page.OnPostSetPhysicalRoomAsync(id, room.Id, CancellationToken.None);

        await page.OnPostDeletePhysicalRoomAsync(room.Id, CancellationToken.None);
        Assert.Single(await db.EvaluationPhysicalRooms.ToListAsync());

        await page.OnPostSetPhysicalRoomAsync(id, null, CancellationToken.None);
        await page.OnPostDeletePhysicalRoomAsync(room.Id, CancellationToken.None);
        Assert.Empty(await db.EvaluationPhysicalRooms.ToListAsync());
        Assert.Empty(await db.EvaluationRoomPlacements.ToListAsync());
    }

    [Fact]
    public async Task A_non_organizer_cannot_add_a_physical_room()
    {
        var (page, db, _) = await SetUpAsync(ParticipantRole.Speaker);

        var result = await page.OnPostAddPhysicalRoomAsync("Room 16", CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(await db.EvaluationPhysicalRooms.ToListAsync());
    }

    [Fact]
    public async Task A_device_of_ANOTHER_edition_is_untouched()
    {
        var (page, db, id) = await SetUpAsync(ParticipantRole.Organizer, deviceEventId: EventId + 1);

        await page.OnPostRenameAsync(id, "Not yours", CancellationToken.None);

        Assert.Equal("Serial KIOSK-DA7BCF9411", (await db.EvaluationDevices.SingleAsync()).Label);
    }
}
