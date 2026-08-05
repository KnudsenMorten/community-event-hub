using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Pages.Speaker;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §748.1 — what must remain true of the RETIRED <c>/Speaker/Evaluations</c> page.
/// </summary>
/// <remarks>
/// The 1–5 ratings body and its tests are gone with the model they read. These two facts are the
/// part that still matters, and both are things a later tidy-up would plausibly break:
/// <list type="number">
///   <item>🔒 <b>The route still answers, as a REDIRECT</b> — never a 404. The link sat in the
///   speaker menu for weeks and may be in a bookmark or a sent mail; an error page would make a
///   deliberate hiding look like a broken site (§748.5).</item>
///   <item>🔒 <b>The page still exists at all</b>, because <c>/Speaker/Index</c> links straight to its
///   <c>Qr</c> handler for the per-session QR download (§749.2). Deleting the page would break a live
///   speaker feature that has nothing to do with the retired ratings.</item>
/// </list>
/// </remarks>
public sealed class SpeakerEvaluationsRetiredPageTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"spk-eval-retired-{Guid.NewGuid():N}")
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

    [Fact]
    public async Task Signed_in_speaker_is_redirected_to_Speaker_index_never_404()
    {
        using var db = NewDb();
        var evt = new Event
        {
            Code = "SPK27", CommunityName = "C", DisplayName = "SPK 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var speaker = new Participant
        {
            EventId = evt.Id, FullName = "Test Speaker", Email = "speaker@example.test",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(speaker);
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(speaker) };
        var model = NewModel(db, http);

        var result = model.OnGet();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Speaker/Index", redirect.PageName);
    }

    [Fact]
    public void The_Qr_handler_still_exists_because_Speaker_Index_links_to_it()
    {
        // A reflection check, deliberately: the handler is reached BY NAME from Razor markup
        // (asp-page-handler="Qr"), so renaming or removing it compiles cleanly and breaks the
        // speaker's QR download silently.
        Assert.NotNull(typeof(EvaluationsModel).GetMethod("OnGetQrAsync"));
    }

    private static EvaluationsModel NewModel(CommunityHubDbContext db, HttpContext http)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        // Inert QR service (null store, no folder) — nothing here needs a real file.
        var qr = new CommunityHub.Core.Integrations.Graphics.SessionEvalsQrService(
            new CommunityHub.Core.Integrations.Graphics.NullSharePointFileStore(),
            Microsoft.Extensions.Options.Options.Create(
                new CommunityHub.Core.Integrations.Graphics.GraphicsSharePointOptions()), TestDocLibrary.Resolver());

        return new EvaluationsModel(
            accessor,
            new CommunityHub.Core.Reminders.SpeakerSessionsService(db),
            qr)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }
}
