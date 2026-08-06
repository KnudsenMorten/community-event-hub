using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests.Organizer;

/// <summary>
/// §889 — THE LIST VIEW: find a post without walking to it.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-06: <i>"i need to see everything in list format first and then have edit
/// option - i cannot go through 72 not approved every time to find number 33"</i>.</para>
///
/// <para>🔒 The behaviours pinned here are the ones that make the page worth having: <b>find by
/// remembered copy</b>, <b>find by subject</b> (the only thing separating sixteen identical track
/// bodies), and <b>a state filter that includes published</b> — §889.1's post did not vanish, it
/// changed state and left the filter he was looking through.</para>
/// </remarks>
public sealed class SoMeQueueListViewTests
{
    private const int EventId = 91;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"someq-{Guid.NewGuid():N}").Options);

    private sealed class FakeAccessor(CurrentParticipant? cur) : ICurrentParticipantAccessor
    {
        public CurrentParticipant? Current { get; } = cur;
    }

    private static CurrentParticipant? Session(Participant p)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new(ClaimTypes.Email, p.Email),
            new(ClaimTypes.Name, p.FullName),
            new(ClaimTypes.Role, p.Role.ToString()),
            new("EventId", p.EventId.ToString()),
        };
        return CurrentParticipant.FromPrincipal(new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    }

    private static async Task<Participant> SeedAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "Q27", CommunityName = "C", DisplayName = "Q 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        var org = new Participant
        {
            EventId = EventId, Email = "org@x.test", FullName = "Org",
            Role = ParticipantRole.Organizer, IsActive = true,
        };
        db.Participants.Add(org);

        db.Sessions.Add(new Session
        {
            Id = 700, EventId = EventId, SessionizeId = "s700",
            Title = "Persistence for fun & profit", Type = SessionType.TechnicalSession,
        });
        db.EventSoMePosts.Add(new EventSoMePost
        {
            EventId = EventId, Slug = "early-bird", Title = "Early bird", Body = "…",
        });

        // The post he remembers by its CONTENT.
        db.SoMePosts.Add(new SoMePost
        {
            Id = 11, EventId = EventId, TemplateKind = SoMeTemplateKind.EventPost,
            SubjectKey = "event:early-bird", AutoText = "Early bird is limited to the first 500 tickets.",
            ScheduledAtUtc = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero),
            Status = SoMePostStatus.Queued, IsActive = false,
        });
        // A track post: its body is the shared template, so only the SUBJECT identifies it.
        db.SoMePosts.Add(new SoMePost
        {
            Id = 12, EventId = EventId, TemplateKind = SoMeTemplateKind.SpeakerTracks,
            SubjectKey = "track:Azure", AutoText = "✨ Track Speakers: {TrackName} ✨",
            ScheduledAtUtc = new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.Zero),
            Status = SoMePostStatus.Queued, IsActive = false,
        });
        // §889.1 — the one that "vanished": approved, published, gone from the planned filter.
        db.SoMePosts.Add(new SoMePost
        {
            Id = 13, EventId = EventId, TemplateKind = SoMeTemplateKind.Session,
            SubjectKey = "session:700", AutoText = "✨ Session Announcement: {SessionTitle} ✨",
            ScheduledAtUtc = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero),
            Status = SoMePostStatus.Published, IsActive = true,
            PublishedAtUtc = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero),
        });
        // 🔒 §853 — a deleted post is a tombstone, not queue content.
        db.SoMePosts.Add(new SoMePost
        {
            Id = 14, EventId = EventId, TemplateKind = SoMeTemplateKind.Sponsor,
            SubjectKey = "sponsor:co-9", AutoText = "deleted one",
            ScheduledAtUtc = new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero),
            Status = SoMePostStatus.Queued, IsDeleted = true,
        });

        await db.SaveChangesAsync();
        return org;
    }

    private static SoMeQueueModel NewModel(CommunityHubDbContext db, Participant org) =>
        new(new FakeAccessor(Session(org)), new SoMeQueueService(db, TimeProvider.System),
            new SoMeSubjectLabeller(db))
        {
            PageContext = new PageContext(),
        };

    [Fact]
    public async Task The_list_lands_showing_every_post_except_the_deleted_one()
    {
        using var db = NewDb();
        var org = await SeedAsync(db);
        var model = NewModel(db, org);

        await model.OnGetAsync(default);

        Assert.Equal(new[] { 11, 12, 13 }, model.Posts.Select(p => p.Id).ToArray());
        Assert.DoesNotContain(model.Posts, p => p.Id == 14);
    }

    /// <summary>🔑 "the post i cannot find has a picture with 500 tickets".</summary>
    [Fact]
    public async Task He_finds_a_post_by_the_words_he_remembers()
    {
        using var db = NewDb();
        var org = await SeedAsync(db);
        var model = NewModel(db, org);
        model.Q = "500 tickets";

        await model.OnGetAsync(default);

        Assert.Equal(11, Assert.Single(model.Posts).Id);
    }

    /// <summary>
    /// 🔴 The search must cover the SUBJECT too: a track post's body is the shared template and
    /// contains the word "Azure" nowhere at all.
    /// </summary>
    [Fact]
    public async Task He_finds_a_track_post_by_its_subject_which_is_absent_from_the_body()
    {
        using var db = NewDb();
        var org = await SeedAsync(db);
        var model = NewModel(db, org);
        model.Q = "azure";

        await model.OnGetAsync(default);

        Assert.Equal(12, Assert.Single(model.Posts).Id);
        Assert.DoesNotContain("Azure", model.Posts[0].AutoText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task He_finds_a_post_by_its_number()
    {
        using var db = NewDb();
        var org = await SeedAsync(db);
        var model = NewModel(db, org);
        model.Q = "13";

        await model.OnGetAsync(default);

        Assert.Equal(13, Assert.Single(model.Posts).Id);
    }

    /// <summary>§889.1 — the published post is findable instead of appearing lost.</summary>
    [Fact]
    public async Task The_state_filter_reaches_published_posts_too()
    {
        using var db = NewDb();
        var org = await SeedAsync(db);

        var planned = NewModel(db, org);
        planned.State = "planned";
        await planned.OnGetAsync(default);
        Assert.Equal(new[] { 11, 12 }, planned.Posts.Select(p => p.Id).ToArray());

        var published = NewModel(db, org);
        published.State = "published";
        await published.OnGetAsync(default);
        Assert.Equal(13, Assert.Single(published.Posts).Id);
    }

    [Fact]
    public async Task The_type_filter_narrows_to_one_kind()
    {
        using var db = NewDb();
        var org = await SeedAsync(db);
        var model = NewModel(db, org);
        model.Kind = SoMeTemplateKind.SpeakerTracks;

        await model.OnGetAsync(default);

        Assert.Equal(12, Assert.Single(model.Posts).Id);
    }

    [Fact]
    public async Task Each_row_carries_a_subject_in_words()
    {
        using var db = NewDb();
        var org = await SeedAsync(db);
        var model = NewModel(db, org);

        await model.OnGetAsync(default);

        Assert.Equal("Early bird", model.SubjectLabels[11]);
        Assert.Equal("Azure", model.SubjectLabels[12]);
        Assert.Equal("Persistence for fun & profit", model.SubjectLabels[13]);
    }

    [Fact]
    public async Task Sorting_by_newest_puts_the_highest_id_first()
    {
        using var db = NewDb();
        var org = await SeedAsync(db);
        var model = NewModel(db, org);
        model.Sort = "id";

        await model.OnGetAsync(default);

        Assert.Equal(new[] { 13, 12, 11 }, model.Posts.Select(p => p.Id).ToArray());
    }

    /// <summary>The snippet is one line — a list row must not become a paragraph.</summary>
    [Fact]
    public void The_snippet_collapses_whitespace_and_truncates()
    {
        var p = new SoMePost { AutoText = "Line one\r\n\r\nLine   two " + new string('x', 200) };

        var snippet = SoMeQueueModel.Snippet(p);

        Assert.DoesNotContain('\n', snippet);
        Assert.StartsWith("Line one Line two", snippet, StringComparison.Ordinal);
        Assert.True(snippet.Length <= 91, $"snippet was {snippet.Length} chars");
    }
}
