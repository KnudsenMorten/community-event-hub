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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// REQUIREMENTS §21 Participant [H] forms-UX batch — now also REQUIREMENTS §148: drives the
/// REAL standalone Dinner / Hotel / Swag page-model POST handlers over an in-memory DB + a
/// fake participant session and a real model-binding pipeline (<see cref="WizardBindingHarness"/>),
/// so the post binds posted form fields exactly as the live server does. After the §148
/// refactor the pages bind their editable fields with <c>TryUpdateModelAsync(Form, name:"")</c>
/// and delegate validate + persist + side-effects to the shared XxxFormService (the SAME service
/// the inline wizard step calls), so these tests prove the standalone page STILL:
///   1. sets a success flash message after a valid save (every form confirms),
///   2. rejects bad input inline with a field-level error and persists nothing,
///   3. persists structured dietary on Dinner and aggregates it.
/// FAKE names only.
/// </summary>
public sealed class FormsUxBatchTests
{
    private const int EventId = 21;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"formsux-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-15T10:00:00Z");
    }

    private sealed class NoOpEmailSender : IEmailSender
    {
        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithIcsAsync(string toEmail, string subject, string htmlBody, string ics, string icsFileName, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct = default) => Task.CompletedTask;
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

    private static ICurrentParticipantAccessor Accessor(HttpContext http) =>
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

    private static async Task<Participant> SeedAsync(CommunityHubDbContext db, ParticipantRole role = ParticipantRole.Speaker)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "Test Community", Code = "TEST",
            DisplayName = "Test Event",
            StartDate = new DateOnly(2026, 11, 1), EndDate = new DateOnly(2026, 11, 2),
        });
        var p = new Participant
        {
            EventId = EventId, Email = "p@example.test", FullName = "Test Person",
            Role = role, IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        // FEATURE B: the self-service forms are entitlement-gated. A SUPPORTED speaker
        // is entitled to the full appreciation set (hotel/swag/lunch/dinner/travel), so
        // these UX/validation tests exercise the forms as an eligible participant. The
        // entitlement DENY paths are covered separately in FormEntitlementGateTests.
        if (role == ParticipantRole.Speaker)
        {
            db.SpeakerProfiles.Add(new SpeakerProfile
            {
                EventId = EventId, ParticipantId = p.Id,
                SpeakerFunding = SpeakerFunding.Supported,
                SpeakingPreDay = true, SpeakingMainDay = true,
            });
            await db.SaveChangesAsync();
        }
        return p;
    }

    // ----- §148: the page now wraps the shared service; the factory builds the service
    // from the same deps and binds the page to a real model-binding request context. -------

    private static DinnerModel NewDinner(CommunityHubDbContext db, HttpContext http) =>
        new DinnerModel(
            new DinnerFormService(db, new FixedClock(), new NoOpEmailSender(),
                Options.Create(new EmailOptions()),
                new OrganizerActionItemService(db, new FixedClock()), Loc()),
            Accessor(http))
        .Bind(http);

    private static HotelModel NewHotel(CommunityHubDbContext db, HttpContext http) =>
        new HotelModel(
            new HotelFormService(db, new FixedClock(),
                new HotelCalendarInviter(new NoOpEmailSender(), Options.Create(new EmailOptions())),
                new OrganizerActionItemService(db, new FixedClock()),
                Loc(), NullLogger<HotelFormService>.Instance),
            Accessor(http))
        .Bind(http);

    private static SwagModel NewSwag(CommunityHubDbContext db, HttpContext http) =>
        new SwagModel(new SwagFormService(db, new FixedClock(), Loc()), Accessor(http)).Bind(http);

    // ===== 1. Flash on save =================================================

    [Fact]
    public async Task Dinner_valid_save_sets_a_success_flash_message()
    {
        using var db = NewDb();
        var me = await SeedAsync(db);
        var http = WizardBindingHarness.PostContext(Session(me),
            new Dictionary<string, string?> { ["Rsvp"] = nameof(DinnerRsvp.No) });   // valid explicit pick
        var model = NewDinner(db, http);

        var result = await model.OnPostAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.True(model.ModelState.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(model.Form.Message));   // drives the _Flash partial
        Assert.Single(await db.DinnerSignups.ToListAsync());
    }

    [Fact]
    public async Task Swag_valid_save_sets_a_success_flash_message()
    {
        using var db = NewDb();
        var me = await SeedAsync(db);
        var http = WizardBindingHarness.PostContext(Session(me),
            new Dictionary<string, string?> { ["PoloChoice"] = SwagOptions.NoPoloLabel });   // explicit "no polo"
        var model = NewSwag(db, http);

        var result = await model.OnPostAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.True(model.ModelState.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(model.Form.Message));
        Assert.Single(await db.SwagPreferences.ToListAsync());
    }

    // ===== 2. Inline + server validation ====================================

    [Fact]
    public async Task Dinner_blank_rsvp_is_rejected_and_saves_nothing()
    {
        using var db = NewDb();
        var me = await SeedAsync(db);
        var http = WizardBindingHarness.PostContext(Session(me),
            new Dictionary<string, string?> { ["Rsvp"] = nameof(DinnerRsvp.NotAnswered) });   // the blank submit
        var model = NewDinner(db, http);

        var result = await model.OnPostAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.True(model.ModelState.ContainsKey(nameof(DinnerFormModel.Rsvp)));
        Assert.Empty(await db.DinnerSignups.ToListAsync());
        Assert.Null(model.Form.Message);
    }

    [Fact]
    public async Task Hotel_blank_need_is_rejected_and_saves_nothing()
    {
        using var db = NewDb();
        var me = await SeedAsync(db);
        var http = WizardBindingHarness.PostContext(Session(me),
            new Dictionary<string, string?>());   // no Yes/No chosen → NeedsRoom binds to null
        var model = NewHotel(db, http);

        var result = await model.OnPostAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.True(model.ModelState.ContainsKey(nameof(HotelFormModel.NeedsRoom)));
        Assert.Empty(await db.HotelBookings.ToListAsync());
    }

    [Fact]
    public async Task Hotel_checkout_before_checkin_is_rejected()
    {
        using var db = NewDb();
        var me = await SeedAsync(db);
        var http = WizardBindingHarness.PostContext(Session(me), new Dictionary<string, string?>
        {
            ["NeedsRoom"] = "true",
            ["CheckInDate"] = "2026-11-02",
            ["CheckOutDate"] = "2026-11-01",   // before check-in
        });
        var model = NewHotel(db, http);

        var result = await model.OnPostAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.True(model.ModelState.ContainsKey(nameof(HotelFormModel.CheckOutDate)));
        Assert.Empty(await db.HotelBookings.ToListAsync());
    }

    [Fact]
    public async Task Swag_blank_polo_is_rejected_and_saves_nothing()
    {
        using var db = NewDb();
        var me = await SeedAsync(db);
        var http = WizardBindingHarness.PostContext(Session(me),
            new Dictionary<string, string?>());   // nothing picked (the client `required` is bypassed)
        var model = NewSwag(db, http);

        var result = await model.OnPostAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.True(model.ModelState.ContainsKey(nameof(SwagFormModel.PoloChoice)));
        Assert.Empty(await db.SwagPreferences.ToListAsync());
    }

    // ===== 3. Structured dietary persists + aggregates ======================

    [Fact]
    public async Task Dinner_save_persists_structured_dietary_with_dinner_surface()
    {
        using var db = NewDb();
        var me = await SeedAsync(db);
        var http = WizardBindingHarness.PostContext(Session(me), new Dictionary<string, string?>
        {
            ["Rsvp"] = nameof(DinnerRsvp.No),
            ["Dietary.DietChoice"] = "Vegan",
            ["Dietary.Gluten"] = "true",
            ["Dietary.Peanuts"] = "true",
            ["Dietary.OtherAllergens"] = "kiwi",
        });
        var model = NewDinner(db, http);

        var result = await model.OnPostAsync(default);

        Assert.IsType<PageResult>(result);
        var row = Assert.Single(await db.DietaryRequirements.ToListAsync());
        Assert.Equal(DietarySurface.Dinner, row.Surface);
        Assert.Equal("Vegan", row.DietChoice);
        Assert.True(row.Gluten);
        Assert.True(row.Peanuts);
        Assert.False(row.Milk);
        Assert.Equal("kiwi", row.OtherAllergens);
        Assert.Equal(me.Id, row.ParticipantId);
    }

    [Fact]
    public async Task Speaker_save_no_longer_persists_dietary()
    {
        // Operator 2026-06-21: day-catering dietary was removed from the speaker form;
        // saving it must NOT create a SpeakerCatering row (and so can't wipe one either).
        using var db = NewDb();
        var me = await SeedAsync(db);   // Speaker role
        var http = new DefaultHttpContext { User = Session(me) };

        var model = new SpeakerModel(db, Accessor(http),
            new CommunityHub.Core.Integrations.SpeakerEmailPropagationService(db, new CommunityHub.Core.Integrations.NullBackstageSpeakerEmailApi(), new FixedClock()),
            new FixedClock())
        {
            PageContext = new PageContext { HttpContext = http },
        };

        // Dietary was removed from the speaker form entirely (the property no longer
        // exists on SpeakerModel), so a save cannot create a SpeakerCatering row.
        var result = await model.OnPostAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.Empty(await db.DietaryRequirements.ToListAsync());
    }

    [Fact]
    public async Task Dinner_dietary_is_saved_and_speaker_form_no_longer_adds_one()
    {
        // Dinner still captures dietary; the speaker form no longer does (operator
        // 2026-06-21), so only the Dinner row exists after both are posted.
        using var db = NewDb();
        var me = await SeedAsync(db);

        var dinnerHttp = WizardBindingHarness.PostContext(Session(me), new Dictionary<string, string?>
        {
            ["Rsvp"] = nameof(DinnerRsvp.Yes),
            ["Dietary.Gluten"] = "true",
        });
        var dinner = NewDinner(db, dinnerHttp);
        // Yes triggers a calendar invite; the NoOp sender swallows it.
        await dinner.OnPostAsync(default);

        var speakerHttp = new DefaultHttpContext { User = Session(me) };
        var speaker = new SpeakerModel(db, Accessor(speakerHttp),
            new CommunityHub.Core.Integrations.SpeakerEmailPropagationService(db, new CommunityHub.Core.Integrations.NullBackstageSpeakerEmailApi(), new FixedClock()),
            new FixedClock())
        {
            PageContext = new PageContext { HttpContext = speakerHttp },
        };
        // Speaker form no longer captures dietary (property removed), so its save adds nothing.
        await speaker.OnPostAsync(default);

        var rows = await db.DietaryRequirements.ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(DietarySurface.Dinner, row.Surface);
        Assert.True(row.Gluten);
    }

    [Fact]
    public async Task Dinner_resave_updates_the_dietary_row_in_place()
    {
        using var db = NewDb();
        var me = await SeedAsync(db);

        var firstHttp = WizardBindingHarness.PostContext(Session(me), new Dictionary<string, string?>
        {
            ["Rsvp"] = nameof(DinnerRsvp.No),
            ["Dietary.Gluten"] = "true",
        });
        var first = NewDinner(db, firstHttp);
        await first.OnPostAsync(default);

        var secondHttp = WizardBindingHarness.PostContext(Session(me), new Dictionary<string, string?>
        {
            ["Rsvp"] = nameof(DinnerRsvp.No),
            ["Dietary.Gluten"] = "false",
            ["Dietary.Fish"] = "true",
        });
        var second = NewDinner(db, secondHttp);
        await second.OnPostAsync(default);

        var row = Assert.Single(await db.DietaryRequirements.ToListAsync());
        Assert.False(row.Gluten);
        Assert.True(row.Fish);
        Assert.NotNull(row.UpdatedAt);
    }
}
