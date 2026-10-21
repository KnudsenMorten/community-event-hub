using System.Reflection;
using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1316 — event partners see the organizer area read-only (operator 2026-10-02), and keep every export ("it is critical
/// they can access the export options. they are the ones that need them"). FAKE names only.
/// </summary>
public sealed class EventPartnerReadOnlyTests
{
    [Theory]
    // Changes in the organizer area and the shop are refused.
    [InlineData("POST", "/Organizer/Participants", "SwitchTo", true)]
    [InlineData("POST", "/Organizer/Settings", "Toggle", true)]
    [InlineData("POST", "/Organizer/EditParticipant", null, true)]
    [InlineData("DELETE", "/Organizer/Hotels", null, true)]
    [InlineData("POST", "/Sponsor/Shop/Cart", "ConfirmOffline", true)]
    [InlineData("GET", "/Organizer/LinkedInConnect", "Callback", true)]
    // Viewing and every export stay open.
    [InlineData("GET", "/Organizer/Logistics", null, false)]
    [InlineData("GET", "/Organizer/Attendees", "ExportXlsx", false)]
    [InlineData("GET", "/Organizer/Lunch", "DietaryCsv", false)]
    [InlineData("POST", "/Organizer/ShopReservations", "Export", false)]
    [InlineData("POST", "/Sponsor/Shop/Cart", "Pdf", false)]
    // Their own pages outside the organizer area are untouched.
    [InlineData("POST", "/Forms/Wizard", "Next", false)]
    [InlineData("POST", "/Tasks", null, false)]
    public void An_event_partner_can_view_and_export_but_not_change(string method, string path, string? handler, bool blocked) =>
        Assert.Equal(blocked, EventPartnerReadOnlyFilter.Blocks(ParticipantRole.EventPartner, false, method, path, handler));

    [Theory]
    [InlineData(ParticipantRole.Organizer, false)]
    [InlineData(ParticipantRole.EventPartner, true)]
    public void Only_event_partners_are_read_only(ParticipantRole role, bool blocked) =>
        Assert.Equal(blocked, EventPartnerReadOnlyFilter.Blocks(role, false, "POST", "/Organizer/Settings", "Toggle"));

    /// <summary>The filter itself: a partner's form post never reaches the handler and goes back to the page with ?readonly=1.</summary>
    [Fact]
    public async Task A_partners_form_post_is_sent_back_without_running_the_handler()
    {
        var (ctx, ran) = await RunAsync(ParticipantRole.EventPartner, "POST", "/Organizer/Settings", "Toggle", "text/html");
        Assert.False(ran);
        var redirect = Assert.IsType<RedirectResult>(ctx.Result);
        Assert.Equal("/Organizer/Settings?readonly=1", redirect.Url);
    }

    [Fact]
    public async Task A_partners_script_call_gets_403()
    {
        var (ctx, ran) = await RunAsync(ParticipantRole.EventPartner, "POST", "/Organizer/DataGrid", "SaveRow", "application/json");
        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(ctx.Result).StatusCode);
    }

    [Theory]
    [InlineData(ParticipantRole.Organizer, "POST", "Toggle")]
    [InlineData(ParticipantRole.EventPartner, "POST", "Export")]
    [InlineData(ParticipantRole.EventPartner, "GET", "ExportXlsx")]
    public async Task Allowed_requests_run_the_handler(ParticipantRole role, string method, string handler)
    {
        var (_, ran) = await RunAsync(role, method, "/Organizer/ShopReservations", handler, "text/html");
        Assert.True(ran);
    }

    private sealed class FixedAccessor(CurrentParticipant? me) : ICurrentParticipantAccessor
    {
        public CurrentParticipant? Current => me;
    }

    private static async Task<(PageHandlerExecutingContext Ctx, bool Ran)> RunAsync(
        ParticipantRole role, string method, string path, string handler, string accept)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "7"),
                new(ClaimTypes.Email, "partner@example.test"),
                new(ClaimTypes.Name, "Pat Partner"),
                new(ClaimTypes.Role, role.ToString()),
                new("EventId", "1"),
            }, CookieAuthenticationDefaults.AuthenticationScheme)),
        };
        http.Request.Method = method;
        http.Request.Path = path;
        http.Request.Headers.Accept = accept;

        var pageContext = new PageContext(new ActionContext(http, new RouteData(), new CompiledPageActionDescriptor()));
        var handlerMethod = new HandlerMethodDescriptor { Name = handler, HttpMethod = method, MethodInfo = typeof(object).GetMethod("ToString")! };
        var ctx = new PageHandlerExecutingContext(pageContext, new List<IFilterMetadata>(), handlerMethod,
            new Dictionary<string, object?>(), new object());

        var ran = false;
        var filter = new EventPartnerReadOnlyFilter(new FixedAccessor(CurrentParticipant.FromPrincipal(http.User)));
        await filter.OnPageHandlerExecutionAsync(ctx, () =>
        {
            ran = true;
            return Task.FromResult(new PageHandlerExecutedContext(pageContext, new List<IFilterMetadata>(), handlerMethod, new object()));
        });
        return (ctx, ran);
    }
}
