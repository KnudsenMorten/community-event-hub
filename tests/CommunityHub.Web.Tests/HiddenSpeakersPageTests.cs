using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1385 — the "Hidden speakers" box at the top of /Organizer/Speakers (operator 2026-11-07:
/// <i>"we cannot have hidden speakers we forgot"</i>). Drives the real page model over a fake
/// organizer session. FAKE names only.
/// </summary>
public sealed class HiddenSpeakersPageTests
{
    private const int EventId = 42;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-11-07T10:00:00Z");
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"hidden-speakers-{Guid.NewGuid():N}")
            .Options);

    private static SpeakersModel NewPage(CommunityHubDbContext db)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "1"),
                new(ClaimTypes.Email, "org@example.test"),
                new(ClaimTypes.Name, "Olive Organizer"),
                new(ClaimTypes.Role, ParticipantRole.Organizer.ToString()),
                new("EventId", EventId.ToString()),
            }, CookieAuthenticationDefaults.AuthenticationScheme)),
        };
        return new SpeakersModel(
            db, new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http)), new FixedClock(),
            new CommunityHub.Core.Organizer.SpeakerDeletionService(db),
            new HiddenSpeakerCheckService(db))
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private static Participant Speaker(string name) => new()
    {
        EventId = EventId, FullName = name, Email = name.Replace(' ', '.').ToLowerInvariant() + "@example.test",
        Role = ParticipantRole.Speaker, IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
    };

    [Fact]
    public async Task The_page_shows_the_hidden_speakers_box_with_its_counts()
    {
        using var db = NewDb();
        var onSession = Speaker("Rita Real");
        var noSession = Speaker("Karen Nobody");
        var noProfile = Speaker("Sam Noprofile");
        db.Participants.AddRange(onSession, noSession, noProfile);
        await db.SaveChangesAsync();
        db.SpeakerProfiles.AddRange(
            new SpeakerProfile { EventId = EventId, ParticipantId = onSession.Id },
            new SpeakerProfile { EventId = EventId, ParticipantId = noSession.Id });
        var real = new Session { EventId = EventId, Title = "Real talk" };
        var panel = new Session { EventId = EventId, Title = "Panel with nobody" };
        var placeholder = new Session { EventId = EventId, Title = "Sponsor Session TBD" };
        db.Sessions.AddRange(real, panel, placeholder);
        await db.SaveChangesAsync();
        db.SessionSpeakers.AddRange(
            new SessionSpeaker { SessionId = real.Id, ParticipantId = onSession.Id },
            new SessionSpeaker { SessionId = real.Id, ParticipantId = noProfile.Id });
        await db.SaveChangesAsync();

        var page = NewPage(db);
        await page.OnGetAsync(CancellationToken.None);

        var hidden = Assert.IsType<HiddenSpeakerCheckService.Result>(page.Hidden);
        Assert.True(hidden.AnyProblem);
        Assert.Equal(noSession.Id, Assert.Single(hidden.SpeakersWithoutSession).ParticipantId);
        Assert.Equal(noProfile.Id, Assert.Single(hidden.SpeakersWithoutProfile).ParticipantId);
        Assert.Equal(panel.Id, Assert.Single(hidden.SessionsNeedingSpeakers).SessionId);
        Assert.Equal(placeholder.Id, Assert.Single(hidden.ExpectedEmptySessions).SessionId);
        Assert.Equal(
            "1 speaker on no session · 1 without a speaker profile · 1 session with no speakers (+ 1 expected empty)",
            hidden.Summary);
    }

    [Fact]
    public async Task The_box_covers_the_whole_edition_not_just_the_searched_page()
    {
        using var db = NewDb();
        db.Participants.AddRange(Speaker("Alpha Match"), Speaker("Karen Nobody"));
        await db.SaveChangesAsync();

        var page = NewPage(db);
        page.Search = "Alpha";
        await page.OnGetAsync(CancellationToken.None);

        Assert.Single(page.Rows);
        Assert.Equal(2, page.Hidden!.SpeakersWithoutSession.Count);
    }

    [Fact]
    public void The_view_renders_the_box_and_links_each_finding_to_its_fix()
    {
        // The Razor view is not executed in this suite; pin the markup that the counts and links need.
        var view = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CommunityHub", "Pages", "Organizer", "Speakers.cshtml"));

        Assert.Contains("id=\"hidden-speakers\"", view);
        Assert.Contains("Hidden speakers:", view);
        Assert.Contains("No hidden speakers", view);
        Assert.Contains("asp-page=\"/Organizer/EditParticipant\" asp-route-id=\"@s.ParticipantId\"", view);
        Assert.Contains("asp-page=\"/Organizer/Sessions\" asp-route-Search=\"@s.SessionId\"", view);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CommunityHub.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Repo root (CommunityHub.sln) not found from " + AppContext.BaseDirectory);
    }
}
