using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Pages.Organizer.Settings;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Web tests for the Phase 2 organizer Configuration editor (<see cref="ConfigModel"/>):
/// the SCALAR config GUI. Drives the real page model over a fake HttpContext + a
/// real <see cref="ConfigOverrideStore"/> (EF in-memory + MemoryCache) + temp
/// shipped-default JSON files. Proves the end-to-end contract:
///   • an organizer sees the editable scalar fields with effective values;
///   • a non-organizer is access-denied (and acting-as cannot write);
///   • Save → override row → effective config round-trip (value live, no redeploy);
///   • Save deep-merges (a second save does not clobber the first key);
///   • Reset removes the key (and deletes the row when nothing is left);
///   • invalid input (bad URL) is rejected inline, nothing is written;
///   • a secret-bearing path is refused even if posted directly.
/// FAKE names only.
/// </summary>
public sealed class OrganizerConfigEditorPageTests : IDisposable
{
    private readonly string _dir;
    private readonly EventConfigOptions _eventOptions;
    private readonly SponsorConfigOptions _sponsorOptions;
    private readonly IntegrationsConfigOptions _integrationsOptions;

    public OrganizerConfigEditorPageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ceh-cfgui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);

        var eventPath = Path.Combine(_dir, "event.json");
        var sponsorPath = Path.Combine(_dir, "sponsor.json");
        var integrationsPath = Path.Combine(_dir, "integrations.json");

        File.WriteAllText(eventPath, """
        {
          "edition": { "code": "ELDK27", "expectedAttendees": 1250 },
          "community": { "brandColor": "#0a3d62", "logoUrl": "" }
        }
        """);
        File.WriteAllText(sponsorPath, """{ "boothWallSpecs": { "_doc": "x" } }""");
        File.WriteAllText(integrationsPath, """
        {
          "woocommerce": {
            "baseUrl": "https://shop.example.test",
            "consumerKeySecretName": "woo-key"
          }
        }
        """);

        _eventOptions = new EventConfigOptions { EventConfigPath = eventPath };
        _sponsorOptions = new SponsorConfigOptions { SponsorConfigPath = sponsorPath };
        _integrationsOptions =
            new IntegrationsConfigOptions { IntegrationsConfigPath = integrationsPath };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"cfgui-{Guid.NewGuid():N}")
            .Options);

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static ClaimsPrincipal Session(Participant p, bool actingAs = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new(ClaimTypes.Email, p.Email),
            new(ClaimTypes.Name, p.FullName),
            new(ClaimTypes.Role, p.Role.ToString()),
            new("EventId", p.EventId.ToString()),
        };
        if (actingAs)
        {
            claims.Add(new Claim(CommunityHub.Core.Auth.ActingAsClaims.ActorKind, "Organizer"));
            claims.Add(new Claim(CommunityHub.Core.Auth.ActingAsClaims.ActorParticipantId, "999"));
            claims.Add(new Claim(CommunityHub.Core.Auth.ActingAsClaims.ActorLabel, "Olivia"));
        }
        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private (ConfigModel model, ConfigOverrideStore store, int eventId, Participant org)
        NewModel(CommunityHubDbContext db, HttpContext http, int eventId, Participant org)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new ConfigOverrideStore(db, cache, TimeProvider.System);
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var model = new ConfigModel(
            accessor, store, _eventOptions, _sponsorOptions, _integrationsOptions,
            NullLogger<ConfigModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = http },
        };
        return (model, store, eventId, org);
    }

    private static async Task<(int eventId, Participant org)> SeedAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "CFG27", CommunityName = "C", DisplayName = "CFG 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var org = new Participant
        {
            EventId = evt.Id, FullName = "Olivia Organizer", Email = "olivia@example.test",
            Role = ParticipantRole.Organizer, IsActive = true,
        };
        db.Participants.Add(org);
        await db.SaveChangesAsync();
        return (evt.Id, org);
    }

    [Fact]
    public async Task OnGet_lists_scalar_fields_with_effective_values()
    {
        using var db = NewDb();
        var (eventId, org) = await SeedAsync(db);
        var http = new DefaultHttpContext { User = Session(org) };
        var (model, _, _, _) = NewModel(db, http, eventId, org);

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.False(model.AccessDenied);
        var eventSection = model.Sections.Single(s => s.Section == ConfigSection.Event);
        var attendees = eventSection.Fields.Single(f => f.Path == "edition.expectedAttendees");
        Assert.Equal("1250", attendees.EffectiveValue);
        Assert.False(attendees.IsOverridden);

        // Secret-bearing integrations key is never listed.
        var integrations = model.Sections.Single(s => s.Section == ConfigSection.Integrations);
        Assert.DoesNotContain(integrations.Fields,
            f => f.Path.Contains("SecretName", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(integrations.Fields, f => f.Path == "woocommerce.baseUrl");
    }

    [Fact]
    public async Task Non_organizer_is_access_denied()
    {
        using var db = NewDb();
        var (eventId, org) = await SeedAsync(db);
        org.Role = ParticipantRole.Attendee;
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(org) };
        var (model, _, _, _) = NewModel(db, http, eventId, org);

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.True(model.AccessDenied);
        Assert.Empty(model.Sections);
    }

    [Fact]
    public async Task ActingAs_organizer_cannot_save()
    {
        using var db = NewDb();
        var (eventId, org) = await SeedAsync(db);
        var http = new DefaultHttpContext { User = Session(org, actingAs: true) };
        var (model, store, _, _) = NewModel(db, http, eventId, org);

        var result = await model.OnPostSaveAsync(
            "Event", "edition.expectedAttendees", "777", (int)ScalarKind.Number, default);

        Assert.IsType<ForbidResult>(result);
        Assert.Null(await store.GetOverrideJsonAsync(eventId, ConfigSection.Event));
    }

    [Fact]
    public async Task Save_creates_override_and_effective_config_reflects_it_live()
    {
        using var db = NewDb();
        var (eventId, org) = await SeedAsync(db);
        var http = new DefaultHttpContext { User = Session(org) };
        var (model, store, _, _) = NewModel(db, http, eventId, org);

        var result = await model.OnPostSaveAsync(
            "Event", "edition.expectedAttendees", "777", (int)ScalarKind.Number, default);

        Assert.IsType<PageResult>(result);
        Assert.True(model.Saved);

        // Round-trip through the loader the live app uses → effective value live.
        var loader = new EventEditionConfigLoader();
        var effective = loader.Load(_eventOptions.EventConfigPath,
            await store.GetOverrideJsonAsync(eventId, ConfigSection.Event));
        Assert.Equal(777, effective.ExpectedAttendees);
        Assert.Equal("ELDK27", effective.Code); // untouched

        // The field now shows as overridden in the re-rendered model.
        var attendees = model.Sections.Single(s => s.Section == ConfigSection.Event)
            .Fields.Single(f => f.Path == "edition.expectedAttendees");
        Assert.True(attendees.IsOverridden);
        Assert.Equal("777", attendees.EffectiveValue);
    }

    [Fact]
    public async Task Second_save_does_not_clobber_the_first_key()
    {
        using var db = NewDb();
        var (eventId, org) = await SeedAsync(db);
        var http = new DefaultHttpContext { User = Session(org) };
        var (model, store, _, _) = NewModel(db, http, eventId, org);

        await model.OnPostSaveAsync(
            "Event", "edition.expectedAttendees", "777", (int)ScalarKind.Number, default);
        await model.OnPostSaveAsync(
            "Event", "edition.code", "CUSTOM27", (int)ScalarKind.String, default);

        var loader = new EventEditionConfigLoader();
        var effective = loader.Load(_eventOptions.EventConfigPath,
            await store.GetOverrideJsonAsync(eventId, ConfigSection.Event));
        Assert.Equal(777, effective.ExpectedAttendees); // first key survived
        Assert.Equal("CUSTOM27", effective.Code);       // second key applied
    }

    [Fact]
    public async Task Reset_removes_the_key_and_deletes_empty_row()
    {
        using var db = NewDb();
        var (eventId, org) = await SeedAsync(db);
        var http = new DefaultHttpContext { User = Session(org) };
        var (model, store, _, _) = NewModel(db, http, eventId, org);

        await model.OnPostSaveAsync(
            "Event", "edition.expectedAttendees", "777", (int)ScalarKind.Number, default);
        Assert.NotNull(await store.GetOverrideJsonAsync(eventId, ConfigSection.Event));

        var result = await model.OnPostResetAsync("Event", "edition.expectedAttendees", default);

        Assert.IsType<PageResult>(result);
        Assert.True(model.Reset);
        Assert.Null(await store.GetOverrideJsonAsync(eventId, ConfigSection.Event));
        Assert.Empty(db.ConfigOverrides.Where(o => o.EventId == eventId));
    }

    [Fact]
    public async Task Invalid_url_is_rejected_inline_and_nothing_is_written()
    {
        using var db = NewDb();
        var (eventId, org) = await SeedAsync(db);
        var http = new DefaultHttpContext { User = Session(org) };
        var (model, store, _, _) = NewModel(db, http, eventId, org);

        var result = await model.OnPostSaveAsync(
            "Event", "community.logoUrl", "not a url", (int)ScalarKind.Url, default);

        Assert.IsType<PageResult>(result);
        Assert.False(model.Saved);
        Assert.Equal("community.logoUrl", model.ErrorPath);
        Assert.NotNull(model.ErrorMessage);
        Assert.Null(await store.GetOverrideJsonAsync(eventId, ConfigSection.Event));
    }

    [Fact]
    public async Task Posting_a_secret_path_is_refused()
    {
        using var db = NewDb();
        var (eventId, org) = await SeedAsync(db);
        var http = new DefaultHttpContext { User = Session(org) };
        var (model, store, _, _) = NewModel(db, http, eventId, org);

        // Even a hand-crafted POST naming a secret key must never be stored.
        var result = await model.OnPostSaveAsync(
            "Integrations", "woocommerce.consumerKeySecretName", "leaked",
            (int)ScalarKind.String, default);

        Assert.IsType<PageResult>(result);
        Assert.False(model.Saved);
        Assert.Null(await store.GetOverrideJsonAsync(eventId, ConfigSection.Integrations));
    }
}
