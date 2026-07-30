using System.Security.Claims;
using ClosedXML.Excel;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §253 vendor-export TRUTH tests — the two organizer downloads that leave the
/// building and become money: the hotel ROOMING LIST (G2) and the SWAG ORDER
/// workbook the vendor produces from (G5). Both must exclude a deactivated
/// participant's surviving rows: the hotel would bill an empty room, the vendor
/// would stitch a polo nobody collects. Drives the REAL page models over a fake
/// organizer session and parses the actual xlsx bytes returned. FAKE names.
/// </summary>
public sealed class VendorExportTruthTests
{
    private const int EventId = 42;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vendor-truth-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-07-07T10:00:00Z");
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static DefaultHttpContext OrganizerContext()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "1"),
            new(ClaimTypes.Email, "org@example.test"),
            new(ClaimTypes.Name, "Olive Organizer"),
            new(ClaimTypes.Role, ParticipantRole.Organizer.ToString()),
            new("EventId", EventId.ToString()),
        };
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
        };
    }

    private static ICurrentParticipantAccessor Accessor(HttpContext http) =>
        new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));

    private static DataGridModel NewDataGrid(CommunityHubDbContext db, DefaultHttpContext http)
    {
        var clock = new FixedClock();
        return new DataGridModel(db, Accessor(http), clock,
            new ParticipantDeactivationService(
                db, clock, new CommunityHub.Core.Audit.AuditTrailService(db, clock)));
    }

    private static SwagModel NewSwag(CommunityHubDbContext db, DefaultHttpContext http) =>
        new(db, Accessor(http));

    /// <summary>One ACTIVE and one DEACTIVATED person, both with a room-need
    /// booking and full swag preferences (the ghost's rows survived a legacy
    /// flag-only deactivation).</summary>
    private static async Task SeedAsync(CommunityHubDbContext db)
    {
        var active = new Participant
        {
            EventId = EventId, Email = "alpha@example.test", FullName = "Active Alpha",
            Role = ParticipantRole.Speaker, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        var ghost = new Participant
        {
            EventId = EventId, Email = "golf@example.test", FullName = "Ghost Golf",
            Role = ParticipantRole.Speaker, IsActive = false,
            LifecycleState = ParticipantLifecycleState.Inactive,
        };
        db.Participants.AddRange(active, ghost);
        await db.SaveChangesAsync();

        db.HotelBookings.Add(new HotelBooking
        {
            EventId = EventId, ParticipantId = active.Id, NeedsRoom = true,
            CheckInDate = new DateOnly(2027, 2, 8), CheckOutDate = new DateOnly(2027, 2, 11),
        });
        db.HotelBookings.Add(new HotelBooking
        {
            EventId = EventId, ParticipantId = ghost.Id, NeedsRoom = true,
            CheckInDate = new DateOnly(2027, 2, 8), CheckOutDate = new DateOnly(2027, 2, 11),
        });
        db.SwagPreferences.Add(new SwagPreference
        {
            EventId = EventId, ParticipantId = active.Id,
            WantsPolo = true, PoloSize = "L", WantsJacket = true, JacketSize = "M",
            WantsGift = true, WantsCredlyBadge = true,
        });
        db.SwagPreferences.Add(new SwagPreference
        {
            EventId = EventId, ParticipantId = ghost.Id,
            WantsPolo = true, PoloSize = "XL", WantsJacket = true, JacketSize = "XL",
            WantsGift = true, WantsCredlyBadge = true,
        });
        await db.SaveChangesAsync();
    }

    private static XLWorkbook Parse(IActionResult result)
    {
        var file = Assert.IsType<FileContentResult>(result);
        return new XLWorkbook(new MemoryStream(file.FileContents));
    }

    /// <summary>All non-empty values in one worksheet column (below the header).</summary>
    private static List<string> Column(IXLWorksheet ws, int col) =>
        ws.RowsUsed().Skip(1)
          .Select(r => r.Cell(col).GetString())
          .Where(v => !string.IsNullOrWhiteSpace(v))
          .ToList();

    /// <summary>The PEOPLE in a per-person sheet's name column — the bold "Grand Total"
    /// footer lives in the same column and is not a person (§326bu).</summary>
    private static List<string> Names(IXLWorksheet ws) =>
        Column(ws, 1).Where(v => v != "Grand Total").ToList();

    [Fact]
    public async Task Rooming_list_the_hotel_receives_excludes_deactivated_people()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var result = await NewDataGrid(db, OrganizerContext()).OnGetRoomingListAsync(default);

        using var wb = Parse(result);
        var names = Column(wb.Worksheet("Rooming list"), 1);
        Assert.Equal(new[] { "Active Alpha" }, names);   // the ghost's NeedsRoom row is out
    }

    [Fact]
    public async Task Swag_order_workbook_and_onscreen_aggregates_exclude_deactivated_people()
    {
        using var db = NewDb();
        await SeedAsync(db);

        // On-screen aggregates (must equal the vendor sheets).
        var page = NewSwag(db, OrganizerContext());
        await page.OnGetAsync(default);
        Assert.Equal(1, page.TotalSubmissions);
        Assert.Equal(1, page.PoloYesCount);
        var polo = Assert.Single(page.PoloLines);
        Assert.Equal("L", polo.Size);                    // no ghost "XL" line

        // §326bu: ONE FILE PER ITEM, each naming the people — the hand-out list.
        var result = await NewSwag(db, OrganizerContext()).OnGetPoloXlsxAsync(default);
        using var wb = Parse(result);

        // The per-person sheet is the point of the change: exactly the active person, by
        // name, with their size — never the deactivated one.
        var people = wb.Worksheet("Polo per person");
        Assert.Equal(new[] { "Active Alpha" }, Names(people));
        Assert.Equal(new[] { "L" }, Column(people, 4));

        // The vendor roll-up sheet agrees with it: the ghost's XL is never ordered.
        Assert.DoesNotContain("XL", Column(wb.Worksheet("Size totals"), 2));
    }

    [Fact]
    public async Task Award_and_credly_are_separate_files_that_carry_the_person_name()
    {
        using var db = NewDb();
        await SeedAsync(db);

        // §326bu: award and Credly are their OWN files now, not sheets in a shared book —
        // each goes to a different place, so each must stand alone.
        using var award = Parse(await NewSwag(db, OrganizerContext()).OnGetAwardXlsxAsync(default));
        Assert.Equal(new[] { "Active Alpha" }, Names(award.Worksheet("Award per person")));
        Assert.False(award.Worksheets.Contains("Credly per person"));

        using var credly = Parse(await NewSwag(db, OrganizerContext()).OnGetCredlyXlsxAsync(default));
        var credlySheet = credly.Worksheet("Credly per person");
        Assert.Equal(new[] { "Active Alpha" }, Names(credlySheet));
        // A Credly badge is issued to an ADDRESS, so the email must travel with the name.
        Assert.All(Column(credlySheet, 2), v => Assert.Contains("@", v));
        Assert.False(credly.Worksheets.Contains("Award per person"));
    }
}
