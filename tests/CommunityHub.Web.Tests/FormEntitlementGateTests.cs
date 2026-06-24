using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Resources;
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
/// (<see cref="CommunityHub.Core.Entitlements.OrderEntitlements"/>), not role alone.
/// A SPONSOR-SELF-FUNDED speaker must be DENIED Hotel / Travel / Swag (and the
/// non-existent Award) but ALLOWED Lunch + the appreciation dinner; a SUPPORTED
/// speaker is allowed all. Drives the real page-model OnGet handlers over an
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
        CommunityHubDbContext db, SpeakerFunding funding)
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
            SpeakerFunding = funding,
            SpeakingPreDay = true, SpeakingMainDay = true,
        });
        await db.SaveChangesAsync();
        return p;
    }

    // ---- page-model factories -------------------------------------------------

    private static (T model, CommunityHubDbContext db) Build<T>(
        SpeakerFunding funding, Func<CommunityHubDbContext, DefaultHttpContext, T> make)
        where T : PageModel
    {
        var db = NewDb();
        var speaker = SeedSpeakerAsync(db, funding).GetAwaiter().GetResult();
        var http = new DefaultHttpContext { User = Session(speaker) };
        var model = make(db, http);
        model.PageContext = new PageContext { HttpContext = http };
        return (model, db);
    }

    private static HotelModel NewHotel(CommunityHubDbContext db, DefaultHttpContext http) =>
        new(db, Accessor(http), new FixedClock(),
            new HotelCalendarInviter(new NoOpEmailSender(), Options.Create(new EmailOptions())),
            new OrganizerActionItemService(db, new FixedClock()),
            NullLogger<HotelModel>.Instance, Loc());

    private static TravelModel NewTravel(CommunityHubDbContext db, DefaultHttpContext http) =>
        new(db, Accessor(http), new FixedClock(), Loc());

    private static SwagModel NewSwag(CommunityHubDbContext db, DefaultHttpContext http) =>
        new(db, Accessor(http), new FixedClock(), Loc());

    private static LunchModel NewLunch(CommunityHubDbContext db, DefaultHttpContext http) =>
        new(db, Accessor(http), new FixedClock());

    private static DinnerModel NewDinner(CommunityHubDbContext db, DefaultHttpContext http) =>
        new(db, Accessor(http), new FixedClock(), new NoOpEmailSender(),
            Options.Create(new EmailOptions()),
            new OrganizerActionItemService(db, new FixedClock()), Loc());

    // ===== Sponsor-self-funded speaker: DENIED hotel/travel/swag ==============

    [Fact]
    public async Task Sponsor_self_funded_speaker_is_denied_hotel()
    {
        var (m, db) = Build(SpeakerFunding.SponsorSelfFunded, NewHotel);
        using (db) { await m.OnGetAsync(default); Assert.False(m.HotelRelevant); }
    }

    [Fact]
    public async Task Sponsor_self_funded_speaker_is_denied_travel()
    {
        var (m, db) = Build(SpeakerFunding.SponsorSelfFunded, NewTravel);
        using (db) { await m.OnGetAsync(default); Assert.True(m.AccessDenied); }
    }

    [Fact]
    public async Task Sponsor_self_funded_speaker_is_denied_swag()
    {
        var (m, db) = Build(SpeakerFunding.SponsorSelfFunded, NewSwag);
        using (db) { await m.OnGetAsync(default); Assert.True(m.AccessDenied); }
    }

    // ===== Sponsor-self-funded speaker: ALLOWED lunch + dinner ================

    [Fact]
    public async Task Sponsor_self_funded_master_class_speaker_lunch_is_auto_counted()
    {
        // Master-class speakers (SpeakingPreDay) are AUTO-COUNTED for the pre-day
        // lunch now (operator 2026-06-24) — they don't fill the form.
        var (m, db) = Build(SpeakerFunding.SponsorSelfFunded, NewLunch);
        using (db) { await m.OnGetAsync(default); Assert.True(m.AccessDenied); Assert.True(m.PreDayAutoCounted); }
    }

    [Fact]
    public async Task Sponsor_self_funded_speaker_is_allowed_dinner()
    {
        var (m, db) = Build(SpeakerFunding.SponsorSelfFunded, NewDinner);
        using (db) { await m.OnGetAsync(default); Assert.False(m.AccessDenied); }
    }

    // ===== Supported speaker: ALLOWED everything =============================

    [Fact]
    public async Task Supported_speaker_is_allowed_hotel()
    {
        var (m, db) = Build(SpeakerFunding.Supported, NewHotel);
        using (db) { await m.OnGetAsync(default); Assert.True(m.HotelRelevant); }
    }

    [Fact]
    public async Task Supported_speaker_is_allowed_travel()
    {
        var (m, db) = Build(SpeakerFunding.Supported, NewTravel);
        using (db) { await m.OnGetAsync(default); Assert.False(m.AccessDenied); }
    }

    [Fact]
    public async Task Supported_speaker_is_allowed_swag()
    {
        var (m, db) = Build(SpeakerFunding.Supported, NewSwag);
        using (db) { await m.OnGetAsync(default); Assert.False(m.AccessDenied); }
    }

    [Fact]
    public async Task Supported_master_class_speaker_lunch_auto_counted_dinner_allowed()
    {
        // Pre-day lunch is auto-counted for the master-class speaker (operator
        // 2026-06-24); dinner is unaffected and still shown.
        var (lunch, db1) = Build(SpeakerFunding.Supported, NewLunch);
        using (db1) { await lunch.OnGetAsync(default); Assert.True(lunch.AccessDenied); Assert.True(lunch.PreDayAutoCounted); }

        var (dinner, db2) = Build(SpeakerFunding.Supported, NewDinner);
        using (db2) { await dinner.OnGetAsync(default); Assert.False(dinner.AccessDenied); }
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
    public async Task Organizer_keeps_hotel_even_though_not_hotel_entitled()
    {
        // Organizer is NOT entitled to OrderItem.Hotel, but historically had the form;
        // Feature B must never silently remove a non-speaker role's access.
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
