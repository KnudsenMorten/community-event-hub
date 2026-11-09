using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1379.8 — the "Linked posts — Refresh weekly" section of WordPress BlogPost Content, through the real
/// page handlers: only a real organizer can link a post, Preview renders the textarea with live data and
/// lists unknown tokens (nothing sent), and Refresh now on an unflagged post sends nothing. Fake data only.
/// </summary>
public sealed class ContentStudioRefreshWeeklyPageTests
{
    private const int EventId = 1380;

    private sealed class FakeAccessor(CurrentParticipant? cur) : ICurrentParticipantAccessor
    {
        public CurrentParticipant? Current { get; } = cur;
    }

    private sealed class RecordingPublisher : IWordPressPublisher
    {
        public bool CanWrite => true;
        public int Updates { get; private set; }

        public Task<WordPressPublishResult> CreateDraftAsync(WordPressDraft draft, CancellationToken ct = default) =>
            Task.FromResult(new WordPressPublishResult(false, null, null, "not in this test"));

        public Task<WordPressPublishResult> UpdatePostContentAsync(long postId, string contentHtml, CancellationToken ct = default)
        {
            Updates++;
            return Task.FromResult(new WordPressPublishResult(true, postId, null, "updated"));
        }
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

    private static async Task<(CommunityHubDbContext Db, Participant Organizer, Participant Speaker)> SeedAsync()
    {
        var db = new CommunityHubDbContext(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"wp-refresh-page-{Guid.NewGuid():N}").Options);
        db.Events.Add(new Event
        {
            Id = EventId, Code = "R27", CommunityName = "C", DisplayName = "R 2027",
            StartDate = new DateOnly(2027, 6, 9), EndDate = new DateOnly(2027, 6, 10), IsActive = true,
        });
        var org = new Participant { EventId = EventId, Email = "org@x.test", FullName = "Org", Role = ParticipantRole.Organizer, IsActive = true };
        var sp = new Participant { EventId = EventId, Email = "sp@x.test", FullName = "Sam Speaker", Role = ParticipantRole.Speaker, IsActive = true };
        db.Participants.AddRange(org, sp);
        await db.SaveChangesAsync();
        return (db, org, sp);
    }

    private static ContentStudioModel Page(CommunityHubDbContext db, Participant me, IWordPressPublisher wp)
    {
        var studio = new ContentStudioService(
            db,
            new AttendeeTelemetryService(db, new MemoryCache(new MemoryCacheOptions()), NullLogger<AttendeeTelemetryService>.Instance),
            new ContentTemplateEngine(),
            wp,
            new ContentStudioOptions(),
            new FeatureGateService(db),
            NullLogger<ContentStudioService>.Instance);
        var refresh = new WordPressPostRefreshService(db, new WordPressPostDataService(db), wp);
        return new ContentStudioModel(studio, new FakeAccessor(SignedIn(me)), refresh)
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() },
            },
            Kind = ContentKind.MasterClasses,
        };
    }

    [Fact]
    public async Task A_non_organizer_cannot_link_a_post()
    {
        var (db, _, speaker) = await SeedAsync();
        var page = Page(db, speaker, new RecordingPublisher());
        page.LinkPostId = 10950;
        page.LinkTemplate = "<p>{{SpeakerCount}}</p>";
        page.LinkRefreshWeekly = true;

        await page.OnPostSaveLinkAsync(CancellationToken.None);

        Assert.True(page.AccessDenied);
        Assert.Empty(db.WordPressRefreshPosts);
    }

    [Fact]
    public async Task An_organizer_links_a_post_with_the_weekly_flag()
    {
        var (db, org, _) = await SeedAsync();
        var page = Page(db, org, new RecordingPublisher());
        page.LinkPostId = 10950;
        page.LinkLabel = "Sponsors";
        page.LinkTemplate = "<p>{{SponsorCount}} sponsors</p>";
        page.LinkRefreshWeekly = true;

        await page.OnPostSaveLinkAsync(CancellationToken.None);

        var row = Assert.Single(db.WordPressRefreshPosts);
        Assert.Equal(10950, row.WordPressPostId);
        Assert.True(row.RefreshWeekly);
        Assert.False(page.LinkMessageIsError);
        Assert.Single(page.LinkedPosts);
    }

    [Fact]
    public async Task Preview_renders_live_data_lists_unknown_tokens_and_sends_nothing()
    {
        var (db, org, _) = await SeedAsync();
        var wp = new RecordingPublisher();
        var page = Page(db, org, wp);
        page.LinkTemplate = "<p>{{SpeakerCount}} speakers {{Nope}}</p>";

        await page.OnPostPreviewLinkAsync(CancellationToken.None);

        Assert.NotNull(page.LinkPreview);
        Assert.Equal("<p>1 speakers {{Nope}}</p>", page.LinkPreview!.Html);
        Assert.Equal(new[] { "Nope" }, page.LinkPreview.UnknownTokens);
        Assert.Equal(0, page.PreviewLinkId);
        Assert.Equal(0, wp.Updates);
    }

    [Fact]
    public async Task Refresh_now_on_an_unflagged_post_sends_nothing_and_says_so()
    {
        var (db, org, _) = await SeedAsync();
        db.WordPressRefreshPosts.Add(new WordPressRefreshPost
        {
            EventId = EventId, WordPressPostId = 42, Label = "Speakers", BodyTemplate = "<p>{{SpeakerCount}}</p>",
            RefreshWeekly = false,
        });
        await db.SaveChangesAsync();
        var wp = new RecordingPublisher();
        var page = Page(db, org, wp);
        page.LinkId = db.WordPressRefreshPosts.Single().Id;

        await page.OnPostRefreshNowAsync(CancellationToken.None);

        Assert.Equal(0, wp.Updates);
        Assert.True(page.LinkMessageIsError);
        Assert.Contains("not flagged", page.LinkMessage);
    }
}
