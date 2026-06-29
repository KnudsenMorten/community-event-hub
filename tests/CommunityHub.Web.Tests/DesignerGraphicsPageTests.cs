using System.Security.Claims;
using System.Text;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §165 organizer trigger page (<see cref="DesignerGraphicsModel"/>). Drives the real handlers
/// over a fake organizer session + a FAKE store (no network): a non-organizer is denied; an
/// unconfigured store reports a clear "not configured" note and writes nothing; the brief GET
/// handler returns a downloadable .xlsx FileResult. FAKE names only.
/// </summary>
public sealed class DesignerGraphicsPageTests
{
    private const int EventId = 55;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"designer-page-{Guid.NewGuid():N}")
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
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static DesignerGraphicsModel NewModel(
        CommunityHubDbContext db, DefaultHttpContext http, ISharePointFileStore store, bool configured)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var svc = new ExternalDesignerGraphicsService(db, store, new NullFetcher(),
            Options.Create(new GraphicsSharePointOptions
            {
                Enabled = true,
                SiteUrl = "https://contoso.sharepoint.example.test/sites/eldk",
                SpeakerPhotosFolderPath = configured ? "EventHub/Speakers/Photos" : "",
            }));
        return new DesignerGraphicsModel(accessor, svc)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private static async Task<Participant> SeedOrganizerAsync(CommunityHubDbContext db)
    {
        var org = new Participant { EventId = EventId, Email = "org@example.test", FullName = "Org Person", Role = ParticipantRole.Organizer };
        db.Participants.Add(org);
        await db.SaveChangesAsync();
        return org;
    }

    [Fact]
    public async Task Non_organizer_is_denied()
    {
        using var db = NewDb();
        var speaker = new Participant { EventId = EventId, Email = "s@example.test", FullName = "A Speaker", Role = ParticipantRole.Speaker };
        db.Participants.Add(speaker);
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(speaker) };
        var model = NewModel(db, http, new FakeStore(canStore: true), configured: true);

        await model.OnGetAsync();
        Assert.True(model.AccessDenied);
    }

    [Fact]
    public async Task Run_is_inert_with_a_clear_note_when_not_configured()
    {
        using var db = NewDb();
        var org = await SeedOrganizerAsync(db);
        var http = new DefaultHttpContext { User = Session(org) };
        // Store can't store ⇒ CanManage false regardless of folder paths.
        var model = NewModel(db, http, new FakeStore(canStore: false), configured: true);

        await model.OnPostRunAsync(default);

        Assert.False(model.Configured);
        Assert.Null(model.LastRun);
        Assert.NotNull(model.Message);
        Assert.Contains("not configured", model.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Brief_handler_returns_a_downloadable_xlsx()
    {
        using var db = NewDb();
        var org = await SeedOrganizerAsync(db);
        var http = new DefaultHttpContext { User = Session(org) };
        var model = NewModel(db, http, new FakeStore(canStore: true), configured: true);

        var result = await model.OnGetBriefAsync(default);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(ExternalDesignerGraphicsService.BriefContentType, file.ContentType);
        Assert.Equal(ExternalDesignerGraphicsService.BriefFileName, file.FileDownloadName);
        Assert.NotEmpty(file.FileContents);
    }

    // ---- fakes ---------------------------------------------------------------

    private sealed class NullFetcher : ISpeakerPictureFetcher
    {
        public Task<FetchedImage?> FetchAsync(string? pictureUrl, CancellationToken ct = default) =>
            Task.FromResult<FetchedImage?>(
                string.IsNullOrWhiteSpace(pictureUrl) ? null : new FetchedImage(Encoding.UTF8.GetBytes("x"), "image/jpeg"));
    }

    private sealed class FakeStore(bool canStore) : ISharePointFileStore
    {
        public bool CanStore { get; } = canStore;
        public bool CanRead => CanStore;
        public Task<StoredFile> UploadToFolderAsync(string relativeFolder, string fileName, byte[] content, string contentType, CancellationToken ct = default) =>
            Task.FromResult(new StoredFile($"{relativeFolder}/{fileName}", "https://x.test/" + fileName, "item"));
        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(Array.Empty<SharePointFileRef>());
        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default) => Task.CompletedTask;
        public Task<StoredFile> StoreAsync(string relativePath, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("unused");
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
    }
}
