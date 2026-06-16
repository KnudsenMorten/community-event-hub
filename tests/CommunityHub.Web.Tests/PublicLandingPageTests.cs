using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Pages;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// The site-root split (REQUIREMENTS §21 PUBLIC): <c>/</c> serves the PUBLIC landing
/// to an anonymous visitor (no Login redirect — the public Sessions/Speakers/Sponsors
/// pages stay reachable + shareable) while a signed-in participant still gets their
/// hub. Drives the real <see cref="IndexModel"/> handler over a fake HttpContext.
/// FAKE names only.
/// </summary>
public sealed class PublicLandingPageTests
{
    private const int EventId = 7;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"landing-{Guid.NewGuid():N}")
            .Options);

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static IndexModel NewModel(CommunityHubDbContext db, DefaultHttpContext http)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var eventConfigOptions = new EventConfigOptions();
        var model = new IndexModel(
            db,
            accessor,
            new SpeakerDeadlineSeeder(db, new SpeakerDeadlineOptions(), TimeProvider.System),
            new EventEditionConfigLoader(),
            eventConfigOptions,
            calendarTokens: null!,   // not reached on the anonymous landing branch
            calendarBuilder: null!,  // not reached on the anonymous landing branch
            new PublicLandingService(db),
            checklist: null!,        // not reached on the anonymous landing branch
            NullLogger<IndexModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = http },
        };
        return model;
    }

    private static async Task SeedActiveEventAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "PUB27", CommunityName = "Public Community",
            DisplayName = "Public Community 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            VenueName = "Test Venue", IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Anonymous_visitor_gets_the_public_landing_not_a_login_redirect()
    {
        using var db = NewDb();
        await SeedActiveEventAsync(db);

        // Anonymous: no authenticated user on the context.
        var http = new DefaultHttpContext();
        var model = NewModel(db, http);

        var result = await model.OnGetAsync(default);

        // Renders the page (landing branch), NOT a redirect to Login.
        Assert.IsType<PageResult>(result);
        Assert.True(model.IsAnonymous);
        Assert.NotNull(model.Landing);
        Assert.Equal("Public Community 2027", model.Landing!.EventDisplayName);
    }

    [Fact]
    public async Task Anonymous_landing_still_renders_when_no_event_is_active()
    {
        using var db = NewDb();   // no active event seeded

        var http = new DefaultHttpContext();
        var model = NewModel(db, http);

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.True(model.IsAnonymous);
        Assert.Null(model.Landing);   // view shows the friendly empty state
    }

    [Fact]
    public void FormatDateRange_collapses_same_month()
    {
        // Pin the culture so the assertion is deterministic on any build agent
        // (the running culture decides month abbreviation + capitalization).
        var prior = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("en-US");

            Assert.Equal("9–10 Feb 2027",
                IndexModel.FormatDateRange(new DateOnly(2027, 2, 9), new DateOnly(2027, 2, 10)));
            Assert.Equal("9 Feb 2027",
                IndexModel.FormatDateRange(new DateOnly(2027, 2, 9), new DateOnly(2027, 2, 9)));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = prior;
        }
    }
}
