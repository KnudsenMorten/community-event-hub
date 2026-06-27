using System.Reflection;
using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Resources;
using CommunityHub.Forms;
using CommunityHub.Forms.Steps;
using CommunityHub.Notify;
using CommunityHub.Pages.Forms;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// REQUIREMENTS §148 — the TRUE in-wizard stepper. Drives the generic stepper host
/// (<see cref="WizardModel"/> at <c>/Forms/Wizard</c>) over an in-memory DB + a fake
/// participant session + a real model-binding request (<see cref="WizardBindingHarness"/>),
/// and proves the corrected behaviour:
///   • a step POST through the host runs the step's REAL save (shared XxxFormService) — the
///     row is PERSISTED and its side-effects fire (Hotel: auto-task marked Done) — then the
///     host ADVANCES (PRG redirect to the next step), it does NOT link out;
///   • a validation error RE-RENDERS THE SAME step INSIDE the wizard (a PageResult with the
///     same CurrentStep + a ModelState error) — it does NOT redirect/bounce out;
///   • Previous navigates back one step with no save;
///   • the standalone form page saves the SAME row as the inline step (delegation parity);
///   • a Sponsor entering the generic host resolves to the bespoke /Sponsor/GetStarted;
///   • EVERY step the wizard SERVICES can emit has an inline handler (so the host never falls
///     back to an outbound &lt;a asp-page="@step.Route"&gt; link — the original bug).
/// FAKE names only.
/// </summary>
public sealed class WizardInlineStepperTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"wizstep-{Guid.NewGuid():N}").Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-15T10:00:00Z");
    }

    private sealed class NoOpEmailSender : IEmailSender
    {
        public Task SendAsync(string t, string s, string h, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string t, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string t, string s, string h, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithIcsAsync(string t, string s, string h, string ics, string fn, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithAttachmentsAsync(string t, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static IStringLocalizer<SharedResource> Loc()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions { ResourcesPath = "" }), NullLoggerFactory.Instance);
        return new StringLocalizer<SharedResource>(factory);
    }

    private static SignalGroupsProvider Signal() => new(new SignalGroupsConfig
    {
        BroadcastLabel = "Broadcast", BroadcastUrl = "https://signal.group/#b",
        Roles = new()
        {
            ["Speaker"] = new() { ChatLabel = "Speakers", ChatUrl = "https://signal.group/#s" },
            ["Volunteer"] = new() { ChatLabel = "Volunteers", ChatUrl = "https://signal.group/#v" },
        },
    });

    private static ICurrentParticipantAccessor Accessor(HttpContext http) =>
        new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));

    private static ClaimsPrincipal Session(Participant p) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new Claim(ClaimTypes.Email, p.Email),
            new Claim(ClaimTypes.Name, p.FullName),
            new Claim(ClaimTypes.Role, p.Role.ToString()),
            new Claim("EventId", p.EventId.ToString()),
        }, CookieAuthenticationDefaults.AuthenticationScheme));

    private static async Task<(int eventId, Participant p)> SeedAsync(
        CommunityHubDbContext db, ParticipantRole role, SpeakerProfile? speaker = null)
    {
        var ev = new Event
        {
            Code = "WIZ", CommunityName = "C", DisplayName = "Wizard 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var p = new Participant
        {
            EventId = ev.Id, FullName = "Wendy Wizard", Email = "wendy@example.test",
            Role = role, IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        if (speaker is not null)
        {
            speaker.EventId = ev.Id; speaker.ParticipantId = p.Id;
            db.SpeakerProfiles.Add(speaker);
            await db.SaveChangesAsync();
        }
        return (ev.Id, p);
    }

    // ---- service / handler factories (the host wires these via DI in production) ----------

    private static HotelFormService HotelSvc(CommunityHubDbContext db) =>
        new(db, new FixedClock(),
            new HotelCalendarInviter(new NoOpEmailSender(), Options.Create(new EmailOptions())),
            new OrganizerActionItemService(db, new FixedClock()),
            Loc(), NullLogger<HotelFormService>.Instance);

    private static DinnerFormService DinnerSvc(CommunityHubDbContext db) =>
        new(db, new FixedClock(), new NoOpEmailSender(), Options.Create(new EmailOptions()),
            new OrganizerActionItemService(db, new FixedClock()), Loc());

    private static WizardModel Host(
        CommunityHubDbContext db, HttpContext http, params IWizardStepHandler[] handlers) =>
        new WizardModel(Accessor(http), new SpeakerWizardService(db, Signal()),
            new RoleWizardService(db, Signal()), handlers)
        .Bind(http);

    private static SpeakerProfile Supported() =>
        new() { SpeakerFunding = SpeakerFunding.Supported, SpeakingMainDay = true };

    // ===== 1. A step POST advances + persists via the shared service =========

    [Fact]
    public async Task Speaker_hotel_step_post_persists_and_advances_with_side_effects()
    {
        using var db = NewDb();
        var (eventId, me) = await SeedAsync(db, ParticipantRole.Speaker, Supported());

        var http = WizardBindingHarness.PostContext(Session(me), new Dictionary<string, string?>
        {
            ["__step"] = "hotel",
            ["__dir"] = "next",
            ["NeedsRoom"] = "true",
            ["CheckInDate"] = "2027-02-08",
            ["CheckOutDate"] = "2027-02-10",
        });
        // Render the step first (as the GET does) so the auto "Complete the Hotel form" task
        // exists — exactly the LoadAsync the host runs when it renders the inline step.
        await HotelSvc(db).LoadAsync(eventId, me.Id, me.Role, default);

        var host = Host(db, http, new HotelStepHandler(HotelSvc(db)));

        var result = await host.OnPostAsync(default);

        // Advanced via Post/Redirect/Get — the host did NOT link out and did NOT re-render.
        Assert.IsType<RedirectToPageResult>(result);

        // The REAL save ran through the shared service: the booking persisted …
        var booking = Assert.Single(await db.HotelBookings.ToListAsync());
        Assert.True(booking.NeedsRoom);
        Assert.Equal(new DateOnly(2027, 2, 8), booking.CheckInDate);
        // … and its side-effect fired: the auto "Complete the Hotel form" task is Done.
        var task = await db.Tasks.SingleAsync(t => t.SourceKey == $"{HotelFormService.HotelTaskKey}:{me.Id}");
        Assert.Equal(TaskState.Done, task.State);
    }

    [Fact]
    public async Task Volunteer_dinner_step_post_persists_and_advances()
    {
        using var db = NewDb();
        var (eventId, me) = await SeedAsync(db, ParticipantRole.Volunteer);

        var http = WizardBindingHarness.PostContext(Session(me), new Dictionary<string, string?>
        {
            ["__step"] = "dinner",
            ["__dir"] = "next",
            ["Rsvp"] = nameof(DinnerRsvp.No),
        });
        var host = Host(db, http, new DinnerStepHandler(DinnerSvc(db)));

        var result = await host.OnPostAsync(default);

        Assert.IsType<RedirectToPageResult>(result);
        var signup = Assert.Single(await db.DinnerSignups.ToListAsync());
        Assert.Equal(DinnerRsvp.No, signup.Rsvp);
    }

    // ===== 2. A validation error re-renders the SAME step INSIDE the wizard ==

    [Fact]
    public async Task Invalid_step_re_renders_same_step_in_wizard_and_persists_nothing()
    {
        using var db = NewDb();
        var (eventId, me) = await SeedAsync(db, ParticipantRole.Speaker, Supported());

        // Hotel with no Yes/No choice → the shared validator rejects it.
        var http = WizardBindingHarness.PostContext(Session(me), new Dictionary<string, string?>
        {
            ["__step"] = "hotel",
            ["__dir"] = "next",
        });
        var host = Host(db, http, new HotelStepHandler(HotelSvc(db)));

        var result = await host.OnPostAsync(default);

        // Stays IN the wizard: a PageResult (NOT a redirect out), same step, with the error.
        Assert.IsType<PageResult>(result);
        Assert.Equal("hotel", host.CurrentStep!.Key);
        Assert.NotNull(host.CurrentHandler);
        Assert.Equal("hotel", host.CurrentHandler!.Key);
        Assert.False(host.ModelState.IsValid);
        Assert.True(host.ModelState.ContainsKey(nameof(HotelFormModel.NeedsRoom)));
        Assert.Empty(await db.HotelBookings.ToListAsync());
    }

    // ===== 3. Previous navigates back, no save ===============================

    [Fact]
    public async Task Previous_navigates_back_one_step_without_saving()
    {
        using var db = NewDb();
        var (eventId, me) = await SeedAsync(db, ParticipantRole.Speaker, Supported());

        // Speaker plan order starts calendar → details → hotel → … ; Previous from hotel = details.
        var http = WizardBindingHarness.PostContext(Session(me), new Dictionary<string, string?>
        {
            ["__step"] = "hotel",
            ["__dir"] = "prev",
            ["NeedsRoom"] = "true",   // present, but Previous must ignore it (no save)
            ["CheckInDate"] = "2027-02-08",
            ["CheckOutDate"] = "2027-02-10",
        });
        var host = Host(db, http, new HotelStepHandler(HotelSvc(db)));

        var result = await host.OnPostAsync(default);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("details", redirect.RouteValues!["step"]);
        Assert.Empty(await db.HotelBookings.ToListAsync());   // Previous saved nothing
    }

    // ===== 4. Standalone page saves the SAME row as the inline step ==========

    [Fact]
    public async Task Standalone_hotel_page_saves_identically_to_the_inline_step()
    {
        var form = new Dictionary<string, string?>
        {
            ["NeedsRoom"] = "true",
            ["CheckInDate"] = "2027-02-08",
            ["CheckOutDate"] = "2027-02-10",
        };

        // (a) via the inline wizard step.
        using var dbWiz = NewDb();
        var (_, meWiz) = await SeedAsync(dbWiz, ParticipantRole.Speaker, Supported());
        var wizHttp = WizardBindingHarness.PostContext(Session(meWiz),
            new Dictionary<string, string?>(form) { ["__step"] = "hotel", ["__dir"] = "next" });
        await Host(dbWiz, wizHttp, new HotelStepHandler(HotelSvc(dbWiz))).OnPostAsync(default);
        var viaWizard = Assert.Single(await dbWiz.HotelBookings.ToListAsync());

        // (b) via the standalone /Forms/Hotel page (same shared service underneath).
        using var dbPage = NewDb();
        var (_, mePage) = await SeedAsync(dbPage, ParticipantRole.Speaker, Supported());
        var pageHttp = WizardBindingHarness.PostContext(Session(mePage), form);
        var page = new HotelModel(HotelSvc(dbPage), Accessor(pageHttp)).Bind(pageHttp);
        await page.OnPostAsync(default);
        var viaPage = Assert.Single(await dbPage.HotelBookings.ToListAsync());

        Assert.Equal(viaWizard.NeedsRoom, viaPage.NeedsRoom);
        Assert.Equal(viaWizard.CheckInDate, viaPage.CheckInDate);
        Assert.Equal(viaWizard.CheckOutDate, viaPage.CheckOutDate);
    }

    // ===== 5. Sponsor entry resolves to the bespoke host ====================

    [Fact]
    public async Task Sponsor_entering_the_generic_host_is_routed_to_the_sponsor_wizard()
    {
        using var db = NewDb();
        var (_, me) = await SeedAsync(db, ParticipantRole.Sponsor);

        var getHttp = WizardBindingHarness.GetContext(Session(me));
        var getResult = await Host(db, getHttp).OnGetAsync(null, default);
        var get = Assert.IsType<RedirectToPageResult>(getResult);
        Assert.Equal("/Sponsor/GetStarted", get.PageName);

        var postHttp = WizardBindingHarness.PostContext(Session(me),
            new Dictionary<string, string?> { ["__step"] = "anything" });
        var postResult = await Host(db, postHttp).OnPostAsync(default);
        var post = Assert.IsType<RedirectToPageResult>(postResult);
        Assert.Equal("/Sponsor/GetStarted", post.PageName);
    }

    // ===== 6. No step links out: every emitted step key has an inline handler =

    [Fact]
    public async Task Every_wizard_step_key_has_an_inline_handler_so_the_host_never_links_out()
    {
        // The set of step keys the SERVICES can emit (speaker union + generic-role union,
        // incl. the multi-hat travel step) must all be covered by a registered handler —
        // otherwise the host falls back to an outbound <a asp-page="@step.Route"> (the bug).
        using var db = NewDb();
        var (evS, spk) = await SeedAsync(db, ParticipantRole.Speaker, Supported());
        var speakerSteps = (await new SpeakerWizardService(db, Signal()).BuildAsync(evS, spk.Id))
            .Steps.Select(s => s.Key);

        using var db2 = NewDb();
        // Volunteer + supported-speaker hat → the widest generic-role plan (adds travel).
        var (evV, vol) = await SeedAsync(db2, ParticipantRole.Volunteer, Supported());
        var roleSteps = (await new RoleWizardService(db2, Signal()).BuildAsync(evV, vol.Id))
            .Steps.Select(s => s.Key);

        var emitted = speakerSteps.Concat(roleSteps).ToHashSet(StringComparer.Ordinal);

        // All registered inline handlers (reflected exactly as Program.cs discovers them).
        var handlerKeys = typeof(WizardModel).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IWizardStepHandler).IsAssignableFrom(t))
            .Select(t =>
            {
                var ctor = t.GetConstructors().First();
                var args = ctor.GetParameters().Select(_ => (object?)null).ToArray();
                return ((IWizardStepHandler)ctor.Invoke(args)).Key;
            })
            .ToHashSet(StringComparer.Ordinal);

        var uncovered = emitted.Where(k => !handlerKeys.Contains(k)).ToArray();
        Assert.True(uncovered.Length == 0,
            "Wizard step(s) with no inline handler (host would link out): " + string.Join(", ", uncovered));
    }
}
