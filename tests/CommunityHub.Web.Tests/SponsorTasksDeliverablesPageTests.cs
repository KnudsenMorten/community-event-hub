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
using Microsoft.Extensions.Logging.Abstractions;
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
            new SponsorDeliverablesService(db),
            new CommunityHub.Core.Email.CalendarInviteEmailService(
                db, new NoopEmailSender(),
                new CommunityHub.Core.Email.EmailContextAccessor(), TimeProvider.System),
            // §603 — the shared artefact uploader. Null is safe here: these tests exercise the task
            // LIST and the deliverables rollup, and the model only touches the uploader inside
            // OnPostUploadTaskFileAsync, which they never call.
            uploader: null!,
            // §684 — the migrated-body pipeline. REAL instances, not nulls: LoadAsync asks the
            // service whether each task is migrated, so a null here would NRE on every page load
            // these tests exercise. The resolver gets NO data providers, which is the honest
            // configuration for a test with no webshop — a :::data directive then renders
            // "we could not check", exactly as it would in production with the integration off.
            new CommunityHub.Core.Tasks.TaskBodyService(
                CommunityHub.Core.Tasks.Definitions.TaskDefinitionRegistry.Shipped,
                new CommunityHub.Core.Tasks.Definitions.TaskBodyStore(),
                new CommunityHub.Core.Tasks.Data.TaskDataResolver(
                    Array.Empty<CommunityHub.Core.Tasks.Data.ITaskDataProvider>(),
                    new Microsoft.Extensions.Caching.Memory.MemoryCache(
                        new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
                    NullLogger<CommunityHub.Core.Tasks.Data.TaskDataResolver>.Instance)),
            new CommunityHub.Core.Tasks.SponsorTaskPlaceholderBuilder(
                db,
                new CommunityHub.Core.Config.EventEditionConfigLoader(),
                new CommunityHub.Core.Config.EventConfigOptions(),
                new CommunityHub.Core.Config.SponsorConfigLoader(),
                new CommunityHub.Core.Config.SponsorConfigOptions(),
                NullLogger<CommunityHub.Core.Tasks.SponsorTaskPlaceholderBuilder>.Instance),
            // §687.8 — the purchase reconciler. Given a REAL instance with a webshop-less summary
            // service: it then reports "could not check" for every category, and the reconciler's
            // outage guard means it changes NOTHING. That is exactly the behaviour these tests want
            // (they assert the task LIST and the rollup), and it exercises the guard for free.
            new CommunityHub.Core.Tasks.PurchaseTaskReconciler(
                db,
                new CommunityHub.Core.Integrations.SponsorPurchaseSummaryService(
                    new CommunityHub.Core.Integrations.WooCommerceClient(
                        new HttpClient(), new CommunityHub.Core.Integrations.WooCommerceOptions()),
                    new CommunityHub.Core.Integrations.WooCommerceOptions(),
                    new Microsoft.Extensions.Caching.Memory.MemoryCache(
                        new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
                    NullLogger<CommunityHub.Core.Integrations.SponsorPurchaseSummaryService>.Instance),
                CommunityHub.Core.Tasks.Definitions.TaskDefinitionRegistry.Shipped,
                new CommunityHub.Core.Tasks.TaskBodyService(
                    CommunityHub.Core.Tasks.Definitions.TaskDefinitionRegistry.Shipped,
                    new CommunityHub.Core.Tasks.Definitions.TaskBodyStore(),
                    new CommunityHub.Core.Tasks.Data.TaskDataResolver(
                        Array.Empty<CommunityHub.Core.Tasks.Data.ITaskDataProvider>(),
                        new Microsoft.Extensions.Caching.Memory.MemoryCache(
                            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
                        NullLogger<CommunityHub.Core.Tasks.Data.TaskDataResolver>.Instance)),
                TimeProvider.System,
                NullLogger<CommunityHub.Core.Tasks.PurchaseTaskReconciler>.Instance),
            // §688.12 — the Zoho sync behind the embedded booth-members editor. Null is safe here:
            // these tests exercise the task LIST and the deliverables rollup, and the model only
            // touches it inside the booth-member handlers, which they never call.
            zohoSync: null!,
            // §690 — the per-tier booth-member allowance. REAL instances: LoadAsync reads them on
            // every page load, and the lookup is already fail-soft, so a config the test box cannot
            // resolve simply omits the allowance line.
            new CommunityHub.Core.Config.SponsorConfigLoader(),
            new CommunityHub.Core.Config.SponsorConfigOptions(),
            NullLogger<TasksModel>.Instance);

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
        // Company content on file marks the "onboarding" stage done, so the rollup has progress.
        // §1081 — that stage now asks SponsorCompanyContent (description AND SoMe branding text, plus
        // the short description for exhibitors) rather than the description alone, so a fixture that
        // wants the stage DONE has to deliver it. Silver = no booth, so no short description here.
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = eventId, SponsorCompanyId = "9001",
            SponsorPackage = SponsorPackage.Silver, CompanyDescription = "We build clouds.",
            SocialMediaIntro = "SoMe branding text on file.",
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

    private sealed class NoopEmailSender : CommunityHub.Core.Email.IEmailSender
    {
        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithIcsAsync(string toEmail, string subject, string htmlBody, string icsContent, string icsFileName, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<CommunityHub.Core.Email.EmailAttachment> attachments, CancellationToken ct = default) => Task.CompletedTask;
    }
}
