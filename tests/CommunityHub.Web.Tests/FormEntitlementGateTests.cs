using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Resources;
using CommunityHub.Forms.Steps;
using CommunityHub.Notify;
using CommunityHub.Pages.Forms;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// FEATURE B: the self-service forms are gated by ENTITLEMENT
/// (<see cref="CommunityHub.Core.Entitlements.OrderEntitlements"/>), not role alone
/// — by the §299 6.2 <see cref="SpeakerCategory"/> matrix. A SPONSOR-category
/// speaker must be DENIED Hotel / Travel / Swag (and the non-existent Award) but
/// ALLOWED Lunch + the appreciation dinner; a COMMUNITY speaker is allowed all;
/// a GUEST is Community minus travel; an UNCATEGORIZED (null) speaker gets no
/// speaker forms at all. Drives the real page-model OnGet handlers over an
/// in-memory DB + a fake speaker session. FAKE names only.
/// </summary>
public sealed class FormEntitlementGateTests
{
    private const int EventId = 31;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"entitle-{Guid.NewGuid():N}")
            .Options);

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
        public Task SendWithAttachmentsAsync(string t, string s, string h, IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static IStringLocalizer<SharedResource> Loc()
    {
        var options = Options.Create(new LocalizationOptions { ResourcesPath = "" });
        var factory = new ResourceManagerStringLocalizerFactory(options, NullLoggerFactory.Instance);
        return new StringLocalizer<SharedResource>(factory);
    }

    private static ICurrentParticipantAccessor Accessor(DefaultHttpContext http) =>
        new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));

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
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static async Task<Participant> SeedSpeakerAsync(
        CommunityHubDbContext db, SpeakerCategory? category)
    {
        if (!await db.Events.AnyAsync(e => e.Id == EventId))
        {
            db.Events.Add(new Event
            {
                Id = EventId, CommunityName = "Test Community", Code = "TEST",
                DisplayName = "Test Event",
                StartDate = new DateOnly(2026, 11, 1), EndDate = new DateOnly(2026, 11, 2),
            });
            await db.SaveChangesAsync();
        }
        var p = new Participant
        {
            EventId = EventId, Email = $"spk-{Guid.NewGuid():N}@example.test",
            FullName = "Speaker Person", Role = ParticipantRole.Speaker,
            IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = EventId, ParticipantId = p.Id,
            Category = category,   // §299 6.1: the canonical organizer-set category
        });
        await db.SaveChangesAsync();
        return p;
    }

    // ---- page-model factories -------------------------------------------------

    private static (T model, CommunityHubDbContext db) Build<T>(
        SpeakerCategory? category, Func<CommunityHubDbContext, DefaultHttpContext, T> make)
        where T : PageModel
    {
        var db = NewDb();
        var speaker = SeedSpeakerAsync(db, category).GetAwaiter().GetResult();
        var http = new DefaultHttpContext { User = Session(speaker) };
        var model = make(db, http);
        model.PageContext = new PageContext { HttpContext = http };
        return (model, db);
    }

    // REQUIREMENTS §148: the form pages are now thin shells over the shared XxxFormService
    // (the inline-wizard step calls the SAME service). The factories build the service from the
    // same deps the page used to take, so the entitlement (relevance) gate under test is identical.
    private static HotelModel NewHotel(CommunityHubDbContext db, DefaultHttpContext http) =>
        new(new HotelFormService(db, new FixedClock(),
                new OrganizerActionItemService(db, new FixedClock()),
                Loc(), NullLogger<HotelFormService>.Instance),
            Accessor(http));

    private static TravelModel NewTravel(CommunityHubDbContext db, DefaultHttpContext http) =>
        new(db, Accessor(http), new FixedClock(), Loc(), new NoOpEmailSender(),
            NullLogger<TravelModel>.Instance);

    private static SwagModel NewSwag(CommunityHubDbContext db, DefaultHttpContext http) =>
        new(new SwagFormService(db, new FixedClock(), Loc()), Accessor(http));

    private static LunchModel NewLunch(CommunityHubDbContext db, DefaultHttpContext http) =>
        new(new LunchFormService(db, new FixedClock(), Loc()), Accessor(http));

    private static DinnerModel NewDinner(CommunityHubDbContext db, DefaultHttpContext http) =>
        new(new DinnerFormService(db, new FixedClock(), new NoOpEmailSender(),
                Options.Create(new EmailOptions()),
                new OrganizerActionItemService(db, new FixedClock()), Loc()),
            Accessor(http));

    // ===== Sponsor-category speaker: DENIED hotel/travel/swag =================

    [Fact]
    public async Task Sponsor_category_speaker_is_denied_hotel()
    {
        var (m, db) = Build(SpeakerCategory.Sponsor, NewHotel);
        using (db) { await m.OnGetAsync(default); Assert.False(m.HotelRelevant); }
    }

    [Fact]
    public async Task Sponsor_category_speaker_is_denied_travel()
    {
        var (m, db) = Build(SpeakerCategory.Sponsor, NewTravel);
        using (db) { await m.OnGetAsync(default); Assert.True(m.AccessDenied); }
    }

    [Fact]
    public async Task Sponsor_category_speaker_is_denied_swag()
    {
        var (m, db) = Build(SpeakerCategory.Sponsor, NewSwag);
        using (db) { await m.OnGetAsync(default); Assert.True(m.AccessDenied); }
    }

    // ===== Sponsor-category speaker: ALLOWED lunch + dinner ===================

    [Fact]
    public async Task Sponsor_category_speaker_sees_optin_preday_lunch()
    {
        // §295 (operator 2026-07-11): speakers are NO LONGER auto-counted for the pre-day lunch —
        // any speaker can join but some arrive after lunch, so they SEE the form with an opt-in
        // pre-day checkbox (master-class speakers just default it to Yes).
        var (m, db) = Build(SpeakerCategory.Sponsor, NewLunch);
        using (db) { await m.OnGetAsync(default); Assert.False(m.Form.AccessDenied); Assert.False(m.Form.PreDayAutoCounted); Assert.True(m.Form.ShowPreDay); }
    }

    [Fact]
    public async Task Sponsor_category_speaker_is_allowed_dinner()
    {
        var (m, db) = Build(SpeakerCategory.Sponsor, NewDinner);
        using (db) { await m.OnGetAsync(default); Assert.False(m.AccessDenied); }
    }

    // ===== Community speaker: ALLOWED everything =============================

    [Fact]
    public async Task Community_speaker_is_allowed_hotel()
    {
        var (m, db) = Build(SpeakerCategory.Community, NewHotel);
        using (db) { await m.OnGetAsync(default); Assert.True(m.HotelRelevant); }
    }

    [Fact]
    public async Task Community_speaker_is_allowed_travel()
    {
        var (m, db) = Build(SpeakerCategory.Community, NewTravel);
        using (db) { await m.OnGetAsync(default); Assert.False(m.AccessDenied); }
    }

    [Fact]
    public async Task Community_speaker_is_allowed_swag()
    {
        var (m, db) = Build(SpeakerCategory.Community, NewSwag);
        using (db) { await m.OnGetAsync(default); Assert.False(m.AccessDenied); }
    }

    [Fact]
    public async Task Community_speaker_sees_optin_preday_lunch_dinner_allowed()
    {
        // §295: pre-day lunch is an OPT-IN checkbox for the speaker (not auto-counted); dinner is
        // unaffected and still shown.
        var (lunch, db1) = Build(SpeakerCategory.Community, NewLunch);
        using (db1) { await lunch.OnGetAsync(default); Assert.False(lunch.Form.AccessDenied); Assert.False(lunch.Form.PreDayAutoCounted); Assert.True(lunch.Form.ShowPreDay); }

        var (dinner, db2) = Build(SpeakerCategory.Community, NewDinner);
        using (db2) { await dinner.OnGetAsync(default); Assert.False(dinner.AccessDenied); }
    }

    // ===== Guest speaker (§299 6.2): Community minus travel ==================

    [Fact]
    public async Task Guest_speaker_is_allowed_hotel_and_swag_but_denied_travel()
    {
        // §299 6.2 summary rule: Guest is identical to Community EXCEPT the
        // travel-reimbursement option must never be presented.
        var (hotel, db1) = Build(SpeakerCategory.Guest, NewHotel);
        using (db1) { await hotel.OnGetAsync(default); Assert.True(hotel.HotelRelevant); }

        var (swag, db2) = Build(SpeakerCategory.Guest, NewSwag);
        using (db2) { await swag.OnGetAsync(default); Assert.False(swag.AccessDenied); }

        var (travel, db3) = Build(SpeakerCategory.Guest, NewTravel);
        using (db3) { await travel.OnGetAsync(default); Assert.True(travel.AccessDenied); }
    }

    // ===== Uncategorized speaker (§299 6.1): the hat grants nothing ==========

    [Fact]
    public async Task Uncategorized_speaker_is_denied_the_speaker_forms()
    {
        // A null Category contributes NOTHING — the still-active legacy case (a
        // migrated organizer-funded speaker) gets no hotel/travel/swag forms.
        var (hotel, db1) = Build((SpeakerCategory?)null, NewHotel);
        using (db1) { await hotel.OnGetAsync(default); Assert.False(hotel.HotelRelevant); }

        var (travel, db2) = Build((SpeakerCategory?)null, NewTravel);
        using (db2) { await travel.OnGetAsync(default); Assert.True(travel.AccessDenied); }

        var (swag, db3) = Build((SpeakerCategory?)null, NewSwag);
        using (db3) { await swag.OnGetAsync(default); Assert.True(swag.AccessDenied); }
    }

    // ===== Award form: confirmed NOT to exist (entitlement only) ============

    [Fact]
    public void Award_has_no_self_service_form()
    {
        // There is no Award page/form to gate — OrderItem.Award is entitlement-only.
        // This pins that fact so a future Award form is wired through the same gate.
        var formType = typeof(HotelModel).Assembly
            .GetType("CommunityHub.Pages.Forms.AwardModel");
        Assert.Null(formType);
    }

    // ===== Non-speaker roles keep their historical access ====================

    [Fact]
    public async Task Organizer_is_entitled_to_hotel()
    {
        // Operator 2026-06-26: organizers (and volunteers) ARE entitled to a hotel
        // booking — OrderEntitlements now grants OrderItem.Hotel to the Organizer hat.
        // (They also historically had the form via the non-speaker access guarantee,
        // so access is doubly guaranteed.)
        using var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", Code = "T", DisplayName = "T",
            StartDate = new DateOnly(2026, 11, 1), EndDate = new DateOnly(2026, 11, 2),
        });
        var org = new Participant
        {
            EventId = EventId, Email = "org@x.test", FullName = "Org Person",
            Role = ParticipantRole.Organizer, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(org);
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(org) };
        var model = NewHotel(db, http);
        model.PageContext = new PageContext { HttpContext = http };

        await model.OnGetAsync(default);
        Assert.True(model.HotelRelevant);   // organizer keeps the hotel form
    }
}
