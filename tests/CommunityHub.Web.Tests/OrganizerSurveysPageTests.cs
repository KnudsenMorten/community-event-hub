using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Pages.Organizer;
using CommunityHub.Surveys;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Web tests for the organizer Surveys management page (<see cref="SurveysModel"/>,
/// REQUIREMENTS §24). Drives the real page model over a fake HttpContext + a temp
/// content root holding a survey JSON. Proves:
///   • an organizer sees the survey list with response count + open status;
///   • a non-organizer is access-denied (friendly, not the content);
///   • the shareable submit/results links resolve to the right relative URLs;
///   • close flips the survey closed (the gate the public page reads), activate reopens;
///   • reset deletes only the target slug's responses, and the type-to-confirm gate
///     blocks a mismatched confirmation;
///   • inline results render the same aggregated numbers the public dashboard uses.
/// FAKE names only.
/// </summary>
public sealed class OrganizerSurveysPageTests : IDisposable
{
    private readonly string _contentRoot;

    public OrganizerSurveysPageTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), $"ceh-surveys-{Guid.NewGuid():N}");
        var dir = Path.Combine(_contentRoot, "App_Data", "Surveys");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "demo-topics.json"), DemoSurveyJson);
    }

    public void Dispose()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch { /* best-effort */ }
    }

    private const string Slug = "demo-topics";
    private const string DemoSurveyJson = """
    {
      "slug": "demo-topics",
      "title": "Demo survey",
      "tracks": [
        { "id": "sec", "name": "Security", "topics": [
            { "id": "intune", "category": "Endpoint", "title": "Intune" },
            { "id": "defender", "category": "Endpoint", "title": "Defender" }
        ] },
        { "id": "dev", "name": "Dev", "topics": [
            { "id": "ci", "category": "DevOps", "title": "CI/CD" }
        ] }
      ]
    }
    """;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"surveys-{Guid.NewGuid():N}")
            .Options);

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "CommunityHub.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Development";
    }

    private static ClaimsPrincipal Session(Participant p)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new(ClaimTypes.Email, p.Email),
            new(ClaimTypes.Name, p.FullName),
            new(ClaimTypes.Role, p.Role.ToString()),
            new("EventId", p.EventId.ToString()),
        };
        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private SurveysModel NewModel(CommunityHubDbContext db, HttpContext http)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var env = new FakeHostEnvironment { ContentRootPath = _contentRoot };
        var provider = new SurveyDefinitionProvider(env, NullLogger<SurveyDefinitionProvider>.Instance);
        var summary = new SurveySummaryService(db, TimeProvider.System, NullLogger<SurveySummaryService>.Instance);
        return new SurveysModel(accessor, provider, summary)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private static async Task<Participant> SeedOrganizerAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "SUR27", CommunityName = "C", DisplayName = "SUR 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var organizer = new Participant
        {
            EventId = evt.Id, FullName = "Olivia Organizer", Email = "olivia@example.test",
            Role = ParticipantRole.Organizer, IsActive = true,
        };
        db.Participants.Add(organizer);
        await db.SaveChangesAsync();
        return organizer;
    }

    private static void SeedResponses(CommunityHubDbContext db, string slug, int n, string track = "sec")
    {
        for (var i = 0; i < n; i++)
        {
            db.SurveyResponses.Add(new SurveyResponse
            {
                SurveySlug = slug,
                SelectedTrackId = track,
                SubmittedAt = DateTimeOffset.UtcNow,
                Picks = new List<SurveyResponsePick>
                {
                    new() { Rank = 1, TopicId = "intune", DesiredLevel = SurveyLevel.Advanced },
                    new() { Rank = 2, TopicId = "defender", DesiredLevel = SurveyLevel.Expert },
                },
            });
        }
        db.SaveChanges();
    }

    [Fact]
    public async Task OnGet_lists_surveys_with_count_and_open_status()
    {
        using var db = NewDb();
        var organizer = await SeedOrganizerAsync(db);
        SeedResponses(db, Slug, 3);

        var http = new DefaultHttpContext { User = Session(organizer) };
        var model = NewModel(db, http);

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.False(model.AccessDenied);
        var row = Assert.Single(model.Surveys);
        Assert.Equal(Slug, row.Slug);
        Assert.Equal("Demo survey", row.Title);
        Assert.Equal(3, row.ResponseCount);
        Assert.True(row.IsOpen); // default open
    }

    [Fact]
    public async Task Non_organizer_role_is_access_denied()
    {
        using var db = NewDb();
        var organizer = await SeedOrganizerAsync(db);
        organizer.Role = ParticipantRole.Attendee;
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(organizer) };
        var model = NewModel(db, http);

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.True(model.AccessDenied);
        Assert.Empty(model.Surveys);
    }

    [Fact]
    public async Task Shareable_links_return_the_right_urls()
    {
        using var db = NewDb();
        var organizer = await SeedOrganizerAsync(db);
        var http = new DefaultHttpContext { User = Session(organizer) };
        var model = NewModel(db, http);

        Assert.Equal($"/survey/{Slug}", model.SubmitUrl(Slug));
        Assert.Equal($"/survey/{Slug}/results", model.ResultsUrl(Slug));
    }

    [Fact]
    public async Task Close_then_activate_toggles_the_public_gate()
    {
        using var db = NewDb();
        var organizer = await SeedOrganizerAsync(db);
        var http = new DefaultHttpContext { User = Session(organizer) };
        var model = NewModel(db, http);

        // Close: the public gate flips to false.
        var closed = await model.OnPostToggleOpenAsync(Slug, open: false, default);
        Assert.IsType<RedirectToPageResult>(closed);
        Assert.False(await new SurveySummaryService(db, TimeProvider.System,
            NullLogger<SurveySummaryService>.Instance).IsOpenAsync(Slug, default));

        // Activate: flips back to open.
        await model.OnPostToggleOpenAsync(Slug, open: true, default);
        Assert.True(await new SurveySummaryService(db, TimeProvider.System,
            NullLogger<SurveySummaryService>.Instance).IsOpenAsync(Slug, default));
    }

    [Fact]
    public async Task Reset_with_matching_confirm_deletes_only_target_slug()
    {
        using var db = NewDb();
        var organizer = await SeedOrganizerAsync(db);
        SeedResponses(db, Slug, 2);
        SeedResponses(db, "other-survey", 1);

        var http = new DefaultHttpContext { User = Session(organizer) };
        var model = NewModel(db, http);

        var result = await model.OnPostResetAsync(Slug, confirmSlug: Slug, default);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Empty(db.SurveyResponses.Where(r => r.SurveySlug == Slug));
        Assert.Single(db.SurveyResponses.Where(r => r.SurveySlug == "other-survey"));
    }

    [Fact]
    public async Task Reset_with_mismatched_confirm_deletes_nothing()
    {
        using var db = NewDb();
        var organizer = await SeedOrganizerAsync(db);
        SeedResponses(db, Slug, 2);

        var http = new DefaultHttpContext { User = Session(organizer) };
        var model = NewModel(db, http);

        var result = await model.OnPostResetAsync(Slug, confirmSlug: "wrong", default);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.True((bool)redirect.RouteValues!["msgErr"]!);
        Assert.Equal(2, db.SurveyResponses.Count(r => r.SurveySlug == Slug)); // untouched
    }

    [Fact]
    public async Task Inline_results_render_the_shared_aggregation()
    {
        using var db = NewDb();
        var organizer = await SeedOrganizerAsync(db);
        SeedResponses(db, Slug, 2); // 2 responses, both rank-1 intune, rank-2 defender

        var http = new DefaultHttpContext { User = Session(organizer) };
        var model = NewModel(db, http);
        model.View = Slug;

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.ViewSummary);
        Assert.Equal(2, model.ViewSummary!.TotalResponses);
        var intune = model.ViewSummary.TopTopicsOverall.First(t => t.TopicId == "intune");
        Assert.Equal(2, intune.PickCount);
        Assert.Equal(6, intune.WeightedScore); // 2 × rank-1 weight (3)
    }
}
