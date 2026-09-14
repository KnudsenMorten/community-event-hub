using System.Security.Claims;
using System.Text;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1077 (operator 2026-08-11: "/Organizer/Attendees shows 1-day tickets as 'other'" and
/// "prints the raw enum names" ⇒ label them "2-day" / "1-day").
///
/// The enum member is <c>Other</c> because the SYNC only knows "an active ticket that is not the
/// 2-day class"; the summary tile has counted it under "1-day tickets" since §707.36, so the page
/// disagreed with itself — the tile said 1-day, the row said Other. These pin the ORGANIZER-FACING
/// wording on both surfaces that print it (the grid's fallback cell and the export he opens in
/// Excel) and, crucially, that the enum name is still the FILTER VALUE: the label is presentation,
/// so `?Ticket=Other` links saved before this change keep working.
/// </summary>
public sealed class OrganizerAttendeeTicketLabelTests
{
    private const int EventId = 42;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"ticket-label-{Guid.NewGuid():N}")
            .Options);

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static DefaultHttpContext OrganizerContext() =>
        new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "1"),
                new Claim(ClaimTypes.Email, "org@example.test"),
                new Claim(ClaimTypes.Name, "Olive Organizer"),
                new Claim(ClaimTypes.Role, ParticipantRole.Organizer.ToString()),
                new Claim("EventId", EventId.ToString()),
            }, CookieAuthenticationDefaults.AuthenticationScheme)),
        };

    private static AttendeesModel NewAttendees(CommunityHubDbContext db, DefaultHttpContext http) =>
        new(db, new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http)))
        {
            PageContext = new PageContext { HttpContext = http },
        };

    private static async Task SeedAsync(CommunityHubDbContext db)
    {
        db.Participants.Add(new Participant
        {
            Id = 1, EventId = EventId, FullName = "Olive Organizer", Email = "org@example.test",
            Role = ParticipantRole.Organizer, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        });
        // No TicketClassName on either row: that is the case where the cell/export falls back to
        // the status, i.e. exactly where the enum name used to leak out.
        db.Attendees.AddRange(
            new Attendee
            {
                EventId = EventId, BackstageTicketId = "t1", Email = "two@example.test",
                FirstName = "Two", LastName = "Dayer", FullName = "Two Dayer",
                TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active,
            },
            new Attendee
            {
                EventId = EventId, BackstageTicketId = "t2", Email = "one@example.test",
                FirstName = "One", LastName = "Dayer", FullName = "One Dayer",
                TicketStatus = TicketStatus.Other, MirrorState = MirrorState.Active,
            });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(TicketStatus.TwoDay, "2-day")]
    [InlineData(TicketStatus.Other, "1-day")]
    [InlineData(TicketStatus.None, "No ticket")]
    public void Ticket_status_labels_are_the_organizer_wording_not_the_enum_name(
        TicketStatus status, string expected)
        => Assert.Equal(expected, status.Label());

    /// <summary>
    /// §1077 (operator 2026-08-12, pointing at <c>/Organizer</c>) — the column NEXT TO the ticket
    /// carried the same defect: <c>NotBooked</c> and <c>MultipleBookings</c> are developer
    /// spellings, shown on the same rows. Fixing one word and leaving its neighbour would have been
    /// a half-finished sentence.
    /// </summary>
    [Theory]
    [InlineData(MasterClassBookingStatus.NotBooked, "Not booked")]
    [InlineData(MasterClassBookingStatus.Booked, "Booked")]
    [InlineData(MasterClassBookingStatus.MultipleBookings, "Double-booked")]
    public void Booking_status_labels_are_the_organizer_wording_too(
        MasterClassBookingStatus status, string expected)
        => Assert.Equal(expected, status.Label());

    /// <summary>
    /// 🔴 <b>The sweep, as a test.</b> He found `TwoDay` on the organizer HOME after it had been
    /// fixed on the attendee grid — one page at a time is how this defect survives. This walks every
    /// organizer view and fails on a raw enum being written straight into the markup.
    /// </summary>
    [Fact]
    public void No_organizer_view_prints_a_raw_ticket_or_booking_enum()
    {
        var pages = Path.Combine(RepoRoot(), "src", "CommunityHub", "Pages");
        Assert.True(Directory.Exists(pages), $"organizer views not found at {pages}");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(pages, "*.cshtml", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // A Razor output expression ending at the enum itself — "@a.TicketStatus<" or
                // "@x.BookingStatus)" — with no .Label() after it.
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        line, @"@\w+\.(TicketStatus|BookingStatus)\s*(<|\)|$)"))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1} — {line.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These views print the enum instead of the label (use .Label()):\n"
            + string.Join("\n", offenders));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// The file he opens in Excel is the second place the enum name printed (§326bp: a value he
    /// has to decode is the same defect as a header he has to decode). "Other" must not appear.
    /// </summary>
    [Fact]
    public async Task Attendee_export_prints_the_labels_and_never_the_enum_names()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var http = OrganizerContext();
        var result = await NewAttendees(db, http).OnGetExportAsync(CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        var csv = Encoding.UTF8.GetString(file.FileContents);

        Assert.Contains(";2-day;", csv);
        Assert.Contains(";1-day;", csv);
        Assert.DoesNotContain(";Other;", csv);
        Assert.DoesNotContain(";TwoDay;", csv);
        // The HEADER still says TicketStatus — only the values changed.
        Assert.Contains("TicketStatus", csv);
    }

    /// <summary>
    /// 🔑 The label is presentation only. The filter still round-trips the ENUM NAME, so a saved
    /// `?Ticket=Other` link (and the option value behind the new "1-day" text) keeps selecting the
    /// 1-day holders. Had the label been made the filter value too, every stored link would 404
    /// silently into "no filter" — which reads as "there are no 1-day attendees".
    /// </summary>
    [Fact]
    public async Task Ticket_filter_still_takes_the_enum_name_so_saved_links_keep_working()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var http = OrganizerContext();
        var model = NewAttendees(db, http);
        model.Ticket = nameof(TicketStatus.Other);

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal("one@example.test", Assert.Single(model.Attendees).Email);
        Assert.Equal(1, model.OneDayCount);
        Assert.Equal(1, model.TwoDayCount);
    }

    /// <summary>
    /// The on-site printable list + its CSV (`/Organizer/Exports`) carried the same defect in an
    /// older shape — "TwoDay (2-day ticket incl. Master Class)", the enum name §380 already
    /// removed from the grid. Both consumers read one row projection, so both are pinned here.
    /// </summary>
    [Fact]
    public async Task On_site_attendee_list_and_its_csv_carry_the_label_too()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = new OrganizerExportsService(db);

        var rows = await svc.BuildAttendeeListAsync(EventId);
        Assert.Equal("1-day", rows.Single(r => r.Email == "one@example.test").TicketStatusLabel);
        Assert.Equal("2-day", rows.Single(r => r.Email == "two@example.test").TicketStatusLabel);

        var csv = await svc.BuildAttendeeListCsvAsync(EventId);
        Assert.Contains("1-day", csv);
        Assert.DoesNotContain("Other", csv);
        Assert.DoesNotContain("TwoDay,", csv);   // the value; the "TwoDay" HEADER column stays
    }
}
