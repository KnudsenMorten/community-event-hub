using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Pages.Sponsor;
using CommunityHub.Venue;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// "Our Booth" sponsor page (REQUIREMENTS §146): drives the real <see cref="BoothModel"/>
/// and proves the booth number is sourced from <see cref="SponsorInfo.BoothLabel"/> for the
/// signed-in sponsor's company, that it degrades to "Booth TBD" when unassigned, and that the
/// page is SPONSOR-gated (a non-sponsor is denied). FAKE names only.
/// </summary>
public sealed class SponsorBoothPageTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"booth-{Guid.NewGuid():N}")
            .Options);

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

    // An INERT venue provider (no SharePoint, empty web root) — the expo gallery is empty,
    // which is fine: these tests focus on booth-number sourcing + the sponsor gate.
    private static VenueImageProvider InertVenue()
    {
        var svc = new VenueImageService(
            new NoReadStore(),
            Options.Create(new GraphicsSharePointOptions()),  // VenueRootFolderPath blank → inert
            new MemoryCache(new MemoryCacheOptions()));
        var webRoot = Path.Combine(Path.GetTempPath(), "ceh-booth-" + Guid.NewGuid().ToString("N"));
        return new VenueImageProvider(svc, new FakeEnv(webRoot));
    }

    private static BoothModel NewModel(CommunityHubDbContext db, DefaultHttpContext http)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var model = new BoothModel(accessor, db, InertVenue());
        var actionContext = new ActionContext(
            http, new RouteData(), new PageActionDescriptor(), new ModelStateDictionary());
        model.PageContext = new PageContext(actionContext);
        return model;
    }

    private static async Task<int> NewEventAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "BTH27", CommunityName = "C", DisplayName = "BTH 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        return evt.Id;
    }

    private static async Task<Participant> NewSponsorAsync(
        CommunityHubDbContext db, int eventId, string? companyId, ParticipantRole role = ParticipantRole.Sponsor)
    {
        var p = new Participant
        {
            EventId = eventId, FullName = "Sue Sponsor", Email = "sue@example.test",
            Role = role, IsActive = true, SponsorCompanyId = companyId,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    [Fact]
    public async Task Booth_number_is_sourced_from_sponsor_info_booth_label()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        var sponsor = await NewSponsorAsync(db, eventId, companyId: "7001");
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = eventId, SponsorCompanyId = "7001",
            SponsorPackage = SponsorPackage.Gold, BoothLabel = "E-26",
        });
        await db.SaveChangesAsync();

        var model = NewModel(db, new DefaultHttpContext { User = Session(sponsor) });
        await model.OnGetAsync(default);

        Assert.False(model.AccessDenied);
        Assert.Equal("E-26", model.BoothNumber);
        Assert.True(model.HasBooth);
    }

    [Fact]
    public async Task Booth_is_tbd_when_no_label_or_no_sponsor_info()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);

        // (a) SponsorInfo exists but no booth label yet → TBD.
        var s1 = await NewSponsorAsync(db, eventId, companyId: "7002");
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = eventId, SponsorCompanyId = "7002",
            SponsorPackage = SponsorPackage.Gold, BoothLabel = null,
        });
        await db.SaveChangesAsync();
        var m1 = NewModel(db, new DefaultHttpContext { User = Session(s1) });
        await m1.OnGetAsync(default);
        Assert.False(m1.AccessDenied);
        Assert.Null(m1.BoothNumber); // view renders "Booth TBD"

        // (b) No SponsorInfo row + no company link → TBD, no crash.
        var s2 = await NewSponsorAsync(db, eventId, companyId: null);
        var m2 = NewModel(db, new DefaultHttpContext { User = Session(s2) });
        await m2.OnGetAsync(default);
        Assert.False(m2.AccessDenied);
        Assert.Null(m2.BoothNumber);
        Assert.False(m2.HasBooth);
    }

    [Fact]
    public async Task Page_is_sponsor_gated()
    {
        using var db = NewDb();
        var eventId = await NewEventAsync(db);
        var speaker = await NewSponsorAsync(db, eventId, companyId: "7003", role: ParticipantRole.Speaker);

        var model = NewModel(db, new DefaultHttpContext { User = Session(speaker) });
        await model.OnGetAsync(default);

        Assert.True(model.AccessDenied);   // non-sponsor is denied (server-side, not CSS)
        Assert.Null(model.BoothNumber);
    }

    // ---- helpers -----------------------------------------------------------

    private sealed class FakeEnv(string webRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = webRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "CommunityHub.Web.Tests";
        public string ContentRootPath { get; set; } = webRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string EnvironmentName { get; set; } = "Development";
    }

    private sealed class NoReadStore : ISharePointFileStore
    {
        public bool CanRead => false;
        public bool CanStore => false;
        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(Array.Empty<SharePointFileRef>());
        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task<StoredFile> StoreAsync(string relativePath, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("unused");
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task<StoredFile> UploadToFolderAsync(string relativeFolder, string fileName, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("unused");
        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default) => Task.CompletedTask;
    }
}
