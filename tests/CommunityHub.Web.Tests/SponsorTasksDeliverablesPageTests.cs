using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Sponsors;
using CommunityHub.Pages.Sponsor;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §135 (operator 2026-06-27): the sponsor deliverables rollup (X of N done, % + the
/// still-missing/overdue items with deep links) now lives at the TOP of the Sponsor My Tasks
/// page (the standalone /Sponsor/Deliverables nav item is removed; the page itself stays
/// reachable). These web tests drive the real <see cref="TasksModel"/> and prove it surfaces a
/// deliverables rollup for a sponsor linked to a company, and null when there is no company
/// link (so the view omits the card). FAKE names only.
/// </summary>
public sealed class SponsorTasksDeliverablesPageTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"spo-tasks-{Guid.NewGuid():N}")
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

    private static TasksModel NewModel(CommunityHubDbContext db, DefaultHttpContext http)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var model = new TasksModel(
            db,
            accessor,
            TimeProvider.System,
            new SponsorDeliverablesService(db));

        var actionContext = new ActionContext(
            http, new RouteData(), new PageActionDescriptor(), new ModelStateDictionary());
        model.PageContext = new PageContext(actionContext);
        return model;
    }

    private static async Task<int> NewEventAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "SPO27", CommunityName = "C", DisplayName = "SPO 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        return evt.Id;
    }

    private static async Task<Participant> NewSponsorAsync(
        CommunityHubDbContext db, int eventId, string? companyId)
    {
        var p = new Participant
        {
            EventId = eventId, FullName = "Sue Sponsor", Email = "sue@example.test",
            Role = ParticipantRole.Sponsor, IsActive = true, SponsorCompanyId = companyId,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    [Fact]
    public async Task Tasks_page_exposes_deliverables_rollup_for_a_linked_sponsor()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        var sponsor = await NewSponsorAsync(db, eventId, companyId: "9001");
        // A description on file marks the "onboarding" stage done, so the rollup has progress.
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = eventId, SponsorCompanyId = "9001",
            SponsorPackage = SponsorPackage.Silver, CompanyDescription = "We build clouds.",
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(sponsor) };
        var model = NewModel(db, http);

        await model.OnGetAsync(default);

        Assert.False(model.NoCompanyLink);
        Assert.NotNull(model.Deliverables);
        Assert.True(model.Deliverables!.ApplicableCount > 0);
        Assert.Contains(model.Deliverables.DoneStages, s => s.Key == "onboarding");
    }

    [Fact]
    public async Task Tasks_page_deliverables_is_null_when_sponsor_has_no_company_link()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        var sponsor = await NewSponsorAsync(db, eventId, companyId: null);
        var http = new DefaultHttpContext { User = Session(sponsor) };
        var model = NewModel(db, http);

        await model.OnGetAsync(default);

        // No company link -> nothing to roll up; the view omits the card.
        Assert.True(model.NoCompanyLink);
        Assert.Null(model.Deliverables);
    }
}
