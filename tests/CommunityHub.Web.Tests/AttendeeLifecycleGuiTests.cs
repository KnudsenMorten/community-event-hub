using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Navigation;
using CommunityHub.Core.Reminders;
using CommunityHub.Forms;
using CommunityHub.Pages;
using CommunityHub.Pages.Forms;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AttendeeIndexModel = CommunityHub.Pages.Attendee.IndexModel;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Attendee-lifecycle GUI + NAV validation (headless, offline page-model render — NO browser).
/// Covers (§206/§207/§208) the attendee menus each ticket type sees AND that the attendee-facing
/// pages — Get-Started stepper, Party form, Master-Class chooser — render without error and bind
/// the right state for BOTH a 2-day and a 1-day attendee. Pairs with
/// <c>AttendeeLifecycleEndToEndTests</c> (the DB/email/task/reminder journey).
/// </summary>
public sealed class AttendeeLifecycleGuiTests
{
    private const string LogisticsSection = "Nav.SectionEventLogistics";

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"attendee-gui-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-30T10:00:00Z");
    }

    private sealed class NoOpSender : IEmailSender
    {
        public Task SendAsync(string t, string s, string h, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string t, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string t, string s, string h, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithIcsAsync(string t, string s, string h, string ics, string fn, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithAttachmentsAsync(string t, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpContext : IEmailContextAccessor
    {
        public EmailContext? Current => null;
        private sealed class D : IDisposable { public void Dispose() { } }
        public IDisposable Set(EmailContext c) => new D();
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static ClaimsPrincipal Session(Participant p) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new Claim(ClaimTypes.Email, p.Email),
            new Claim(ClaimTypes.Name, p.FullName),
            new Claim(ClaimTypes.Role, p.Role.ToString()),
            new Claim("EventId", p.EventId.ToString()),
        }, CookieAuthenticationDefaults.AuthenticationScheme));

    private static async Task<(int ev, Participant p)> SeedAttendeeAsync(
        CommunityHubDbContext db, TicketStatus ticket)
    {
        var ev = new Event
        {
            CommunityName = "C", DisplayName = "T 2027", Code = "T27",
            IsActive = true, StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var p = new Participant
        {
            EventId = ev.Id, Email = "att@x.dk", FullName = "Att Endee",
            Role = ParticipantRole.Attendee, IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        db.Attendees.Add(new Attendee
        {
            EventId = ev.Id, BackstageTicketId = "t1", Email = p.Email, FullName = p.FullName,
            FirstName = "Att", LastName = "Endee", TicketStatus = ticket, MirrorState = MirrorState.Active,
        });
        // Two master classes so the chooser has options to render.
        db.Sessions.Add(new Session { EventId = ev.Id, Title = "Intune", Type = SessionType.MasterClass, MasterClassCapacity = 30 });
        db.Sessions.Add(new Session { EventId = ev.Id, Title = "Security", Type = SessionType.MasterClass, MasterClassCapacity = 30 });
        await db.SaveChangesAsync();
        return (ev.Id, p);
    }

    private static PageContext PageCtxFor(Participant p, out HttpContext http)
    {
        http = new DefaultHttpContext { User = Session(p) };
        return new PageContext { HttpContext = http };
    }

    // =====================================================================
    //  NAV / MENUS — what each attendee sees
    // =====================================================================

    [Fact]
    public void Attendee_nav_has_party_as_single_main_item_plus_get_started()
    {
        // §234 UX: the Master-Class chooser is gated on the 2-day ticket, so build the
        // nav as a 2-day holder (a 1-day attendee's menu is asserted separately below).
        var nav = NavBuilder.Build(ParticipantRole.Attendee, attendeeIsTwoDay: true);
        var items = nav.Groups[0].Items;

        // §353 (operator 2026-07-26) REVERSES §297: attendees get the prominent MAIN-nav entry
        // ("Party Preday") AND a duplicate under Register/Update — the operator asked for both.
        // §326am: both deep-link into the wizard's INLINE party step, not the standalone page.
        var partyItems = items.Where(i => i.Href == "/Forms/Wizard?step=party").ToList();
        Assert.Equal(2, partyItems.Count);
        var party = Assert.Single(partyItems, i => i.SectionKey is null);
        Assert.Equal("Nav.PartyPreday", party.LabelKey);
        Assert.False(party.External, "§326am: the party step opens inside the hub, not a new tab.");
        Assert.Single(partyItems, i => i.SectionKey == "Nav.SectionRegister");
        Assert.DoesNotContain(items, i => i.Href == "/Party");
        // The Get-Started stepper entry (§285: now the inline /Forms/Wizard) + the
        // Master-Class chooser.
        Assert.Contains(items, i => i.Href == "/Forms/Wizard");
        Assert.Contains(items, i => i.Href == "/Forms/Wizard?step=masterclass");
    }

    [Fact]
    public void One_day_attendee_nav_hides_the_master_class_entries_but_keeps_party()
    {
        // §234 UX: a 1-DAY ticket excludes Master Classes — the chooser, waitlist and
        // Q&A menu entries would be dead ends, so they are hidden (the pages stay
        // reachable by direct URL). The Party entry + Get Started remain.
        var nav = NavBuilder.Build(ParticipantRole.Attendee /* attendeeIsTwoDay: false */);
        var hrefs = nav.AllItems.Select(i => i.Href).ToList();

        Assert.DoesNotContain("/Forms/Wizard?step=masterclass", hrefs);
        Assert.DoesNotContain("/Attendee/Waitlist", hrefs);
        Assert.DoesNotContain("/Attendee/MasterClassQa", hrefs);
        Assert.Contains("/Forms/Wizard?step=party", hrefs);   // §326am: inline party step
        Assert.Contains("/Forms/Wizard", hrefs);   // §285: the inline Get-Started wizard
    }

    [Fact]
    public void Attendee_nav_has_no_organizer_or_crew_only_items()
    {
        var nav = NavBuilder.Build(ParticipantRole.Attendee);

        // No server-side management group for an attendee.
        Assert.DoesNotContain(nav.Groups, g => g.IsManagement);
        Assert.Null(nav.ManagementGroup);

        var hrefs = nav.AllItems.Select(i => i.Href).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Crew/organizer-only surfaces must never leak into the attendee menu.
        Assert.DoesNotContain("/Tasks", hrefs);              // crew "My tasks"
        Assert.DoesNotContain("/Profile", hrefs);            // not in the minimal attendee menu
        Assert.DoesNotContain("/Forms/Hotel", hrefs);
        Assert.DoesNotContain("/Sponsor/Tasks", hrefs);
        Assert.DoesNotContain("/Speaker", hrefs);
        Assert.StartsWith("/", nav.Groups[0].Items[0].Href);
        Assert.Equal("/", nav.Groups[0].Items[0].Href);      // Home is first
    }

    // =====================================================================
    //  GET-STARTED stepper page (render)
    // =====================================================================

    [Fact]
    public async Task GetStarted_page_renders_two_day_stepper()
    {
        using var db = NewDb();
        var (ev, p) = await SeedAttendeeAsync(db, TicketStatus.TwoDay);
        var model = NewGetStarted(db, p);

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.False(model.AccessDenied);
        Assert.NotNull(model.View);
        Assert.Equal(new[] { "masterclass", "party" }, model.View!.Steps.Select(s => s.Key).ToArray());
    }

    [Fact]
    public async Task GetStarted_page_renders_one_day_stepper_party_only()
    {
        using var db = NewDb();
        var (ev, p) = await SeedAttendeeAsync(db, TicketStatus.Other);
        var model = NewGetStarted(db, p);

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.View);
        Assert.Equal(new[] { "party" }, model.View!.Steps.Select(s => s.Key).ToArray());
    }

    private static GetStartedModel NewGetStarted(CommunityHubDbContext db, Participant p)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(
            new DefaultHttpContext { User = Session(p) }));
        return new GetStartedModel(accessor, new RoleWizardService(db), new AttendeeWizardService(db))
        {
            PageContext = PageCtxFor(p, out _),
        };
    }

    // =====================================================================
    //  PARTY page (render + explicit Yes/No, no default)
    // =====================================================================

    [Fact]
    public async Task Party_page_renders_for_both_ticket_types()
    {
        foreach (var ticket in new[] { TicketStatus.TwoDay, TicketStatus.Other })
        {
            using var db = NewDb();
            var (_, p) = await SeedAttendeeAsync(db, ticket);
            var model = NewParty(db, p);

            await model.OnGetAsync(default);

            Assert.NotNull(model.Party);                 // the 16:00–18:30 window resolved
            Assert.Equal(16, model.Party!.StartHour);
            Assert.Equal(18, model.Party.EndHour);
        }
    }

    [Fact]
    public async Task Party_page_rejects_neither_choice_with_no_default_row()
    {
        using var db = NewDb();
        var (ev, p) = await SeedAttendeeAsync(db, TicketStatus.TwoDay);
        var model = NewParty(db, p);
        model.Attending = null;   // neither Yes nor No chosen

        await model.OnPostAsync(default);

        Assert.False(model.SubmittedOk);
        Assert.NotNull(model.ErrorMessage);
        Assert.False(await db.PartyRsvps.AnyAsync(r => r.EventId == ev));
    }

    [Fact]
    public async Task Party_page_yes_creates_attending_row_and_enables_invite()
    {
        using var db = NewDb();
        var (ev, p) = await SeedAttendeeAsync(db, TicketStatus.Other);
        var model = NewParty(db, p);
        model.Attending = true;

        await model.OnPostAsync(default);

        Assert.True(model.SubmittedOk);
        Assert.True(model.Attending);   // drives the "send calendar invite" button
        var row = await db.PartyRsvps.SingleAsync(r => r.EventId == ev && r.ParticipantId == p.Id);
        Assert.True(row.Attending);
    }

    private static PartyModel NewParty(CommunityHubDbContext db, Participant p)
    {
        var http = new DefaultHttpContext { User = Session(p) };
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var calendar = new CalendarInviteEmailService(db, new NoOpSender(), new NoOpContext(), new FixedClock());
        return new PartyModel(new PartyRsvpService(db), accessor, calendar, NullLogger<PartyModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    // =====================================================================
    //  MASTER-CLASS chooser page (render) — eligibility per ticket type
    // =====================================================================

    [Fact]
    public async Task MasterClass_chooser_renders_options_and_is_eligible_for_two_day()
    {
        using var db = NewDb();
        var (_, p) = await SeedAttendeeAsync(db, TicketStatus.TwoDay);
        var model = NewAttendeeIndex(db, p);

        var result = await model.OnGetAsync(null, null, default);

        Assert.IsType<PageResult>(result);
        Assert.True(model.HasAttendeeRecord);
        Assert.True(model.Eligible);                 // 2-day → Master Class access
        Assert.Equal(2, model.Options.Count);        // both seeded classes render
        Assert.Null(model.Confirmed);                // nothing chosen yet
    }

    [Fact]
    public async Task MasterClass_chooser_marks_one_day_attendee_not_eligible()
    {
        using var db = NewDb();
        var (_, p) = await SeedAttendeeAsync(db, TicketStatus.Other);
        var model = NewAttendeeIndex(db, p);

        var result = await model.OnGetAsync(null, null, default);

        Assert.IsType<PageResult>(result);
        Assert.True(model.HasAttendeeRecord);
        Assert.False(model.Eligible);                // 1-day → no Master Class access
    }

    private static AttendeeIndexModel NewAttendeeIndex(CommunityHubDbContext db, Participant p)
    {
        var http = new DefaultHttpContext { User = Session(p) };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("hub.example");
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var mc = new MasterClassSignupService(db);
        var ctx = new NoOpContext();
        var promo = new MasterClassPromotionEmailService(db, new NoOpSender(), ctx, mc);
        var email = new MasterClassEmailService(db, new NoOpSender(), ctx, mc);
        var logistics = new MasterClassLogisticsService(db, new FixedClock());
        return new AttendeeIndexModel(mc, accessor, promo, email, logistics)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }
}
