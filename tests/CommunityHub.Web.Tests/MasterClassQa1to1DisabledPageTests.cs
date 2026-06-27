using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Pages;
using CommunityHub.Pages.Sessions;
using CommunityHub.Pages.Speaker;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §136 (operator 2026-06-27): attendee 1:1 questions are DISABLED; the open Master
/// Class Group Q&amp;A is kept and is clearly PER-master-class. These web tests prove,
/// over the real page models:
///   • the public /sessions/{token}/ask page no longer writes a SessionQuestion (POST
///     is inert) and renders rather than 404s;
///   • the Master Class landing page's Ask handler is inert (no 1:1 created);
///   • the repurposed Speaker Group Q&amp;A page loads ONE board PER master class
///     (scoped per SessionId, never merged) and a speaker can reply onto a specific MC.
/// FAKE names only.
/// </summary>
public sealed class MasterClassQa1to1DisabledPageTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"mcqa-{Guid.NewGuid():N}")
            .Options);

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
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

    private static MasterClassPrepService Prep(CommunityHubDbContext db) =>
        new(db, TimeProvider.System);

    private sealed record Seed(int EventId, Participant Speaker, Session Mc1, Session Mc2, Attendee Attendee);

    private static async Task<Seed> SeedAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "MCQA27", CommunityName = "C", DisplayName = "MCQA 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var speaker = new Participant
        {
            EventId = evt.Id, FullName = "Sam Speaker", Email = "sam@example.test",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(speaker);
        await db.SaveChangesAsync();

        var mc1 = new Session { EventId = evt.Id, Title = "Alpha MC", Type = SessionType.MasterClass, MasterClassCapacity = 10 };
        var mc2 = new Session { EventId = evt.Id, Title = "Bravo MC", Type = SessionType.MasterClass, MasterClassCapacity = 10 };
        db.Sessions.AddRange(mc1, mc2);
        await db.SaveChangesAsync();

        db.SessionSpeakers.AddRange(
            new SessionSpeaker { SessionId = mc1.Id, ParticipantId = speaker.Id },
            new SessionSpeaker { SessionId = mc2.Id, ParticipantId = speaker.Id });

        var att = new Attendee
        {
            EventId = evt.Id, Email = "seat@example.test", FirstName = "Ada", LastName = "Attendee",
            TicketStatus = TicketStatus.TwoDay,
        };
        db.Attendees.Add(att);
        await db.SaveChangesAsync();

        db.MasterClassSignups.Add(new MasterClassSignup
        {
            EventId = evt.Id, SessionId = mc1.Id, AttendeeId = att.Id,
            Status = MasterClassSignupStatus.Confirmed,
        });
        await db.SaveChangesAsync();

        return new Seed(evt.Id, speaker, mc1, mc2, att);
    }

    // ---------------------------------------------------- public Ask page is inert ---

    [Fact]
    public async Task Public_ask_page_post_writes_no_session_question()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);
        var qsvc = new SessionQuestionService(db, TimeProvider.System);
        var token = await qsvc.EnsurePublicTokenAsync(seed.Mc1.Id);

        var model = new AskModel(qsvc)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
        };

        var result = await model.OnPostAsync(token, default);

        Assert.IsType<PageResult>(result);
        Assert.Equal(0, await db.SessionQuestions.CountAsync());
    }

    [Fact]
    public async Task Public_ask_page_get_renders_even_for_unknown_token()
    {
        using var db = NewDb();
        var qsvc = new SessionQuestionService(db, TimeProvider.System);
        var model = new AskModel(qsvc)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
        };

        // No 404 now — the route is kept and shows the "no longer available" note.
        var result = await model.OnGetAsync("not-a-real-token", default);
        Assert.IsType<PageResult>(result);
    }

    // ------------------------------------- Master Class landing Ask handler inert ---

    [Fact]
    public async Task MasterClass_landing_ask_handler_is_inert()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);

        var http = new DefaultHttpContext { User = Session(seed.Speaker) };
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var model = new MasterClassPageModel(Prep(db), new MasterClassSignupService(db), accessor)
        {
            PageContext = new PageContext { HttpContext = http },
        };

        var result = model.OnPostAsk(seed.Mc1.Id, null);

        // Bounces back to the page; never creates a 1:1 question.
        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(0, await db.SessionQuestions.CountAsync());
    }

    // ------------------------------------- Speaker per-MC Group Q&A (scoped) --------

    [Fact]
    public async Task Speaker_group_qa_page_shows_one_board_per_master_class()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);

        // An attendee question on Alpha only.
        db.MasterClassComments.Add(new MasterClassComment
        {
            EventId = seed.EventId, SessionId = seed.Mc1.Id, AuthorAttendeeId = seed.Attendee.Id,
            AuthorDisplayName = "Ada Attendee", Body = "What should I install?",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(seed.Speaker) };
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var model = new QuestionsModel(accessor, Prep(db))
        {
            PageContext = new PageContext { HttpContext = http },
        };

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.False(model.AccessDenied);
        Assert.Equal(2, model.Boards.Count); // one per master class
        var alpha = model.Boards.Single(b => b.Session.Id == seed.Mc1.Id);
        var bravo = model.Boards.Single(b => b.Session.Id == seed.Mc2.Id);
        // The question sits ONLY on Alpha's board — never merged onto Bravo.
        Assert.Equal("What should I install?", Assert.Single(alpha.Comments).Body);
        Assert.Empty(bravo.Comments);
    }

    [Fact]
    public async Task Speaker_reply_lands_on_the_named_master_class_only()
    {
        using var db = NewDb();
        var seed = await SeedAsync(db);

        var http = new DefaultHttpContext { User = Session(seed.Speaker) };
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var model = new QuestionsModel(accessor, Prep(db))
        {
            PageContext = new PageContext { HttpContext = http },
            ReplyBody = "See the prep notes for setup.",
        };

        var result = await model.OnPostReplyAsync(seed.Mc2.Id, default);

        Assert.IsType<RedirectToPageResult>(result);
        // The reply is scoped to Bravo (mc2) — Alpha (mc1) stays empty.
        Assert.Equal(1, await db.MasterClassComments.CountAsync(c => c.SessionId == seed.Mc2.Id));
        Assert.Equal(0, await db.MasterClassComments.CountAsync(c => c.SessionId == seed.Mc1.Id));
    }
}
