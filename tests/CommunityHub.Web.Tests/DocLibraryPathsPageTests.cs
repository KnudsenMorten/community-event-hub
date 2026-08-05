using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §769 / work-order §6.1 — the organizer "Document library paths" page.
/// </summary>
/// <remarks>
/// Drives the real page model over a fake HttpContext and a real store on EF in-memory. What is
/// pinned: organizer-only, a bad path is refused with nothing written, a good one takes effect
/// through the resolver immediately, restore returns to the default, and the ROOT is not writable
/// from here at all (§769.1 D2 — it is the boundary between this environment and the other one).
/// </remarks>
public sealed class DocLibraryPathsPageTests
{
    private static CommunityHubDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private sealed class StaticOptions(DocLibraryOptions value) : IOptionsMonitor<DocLibraryOptions>
    {
        public DocLibraryOptions CurrentValue => value;
        public DocLibraryOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<DocLibraryOptions, string?> listener) => null;
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

    private static DocLibraryOptions Options() => new()
    {
        Enabled = true,
        SiteUrl = "https://sp.test/sites/x",
        DriveName = "Documents",
        RootFolderPath = "General/Test/EventHub",
    };

    private sealed class Harness : IDisposable
    {
        public required CommunityHubDbContext Db { get; init; }
        public required DocLibraryPathsModel Model { get; init; }
        public required IDocLibraryPathResolver Resolver { get; init; }
        public required DocLibraryOverrideStore Store { get; init; }
        public required ServiceProvider Sp { get; init; }
        public required Participant Organizer { get; init; }

        public void Dispose() { Db.Dispose(); Sp.Dispose(); }
    }

    private static async Task<Harness> NewAsync(ParticipantRole role = ParticipantRole.Organizer)
    {
        var dbName = $"doclibpage-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<CommunityHubDbContext>(b => b.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();

        var db = NewDb(dbName);
        var evt = new Event
        {
            Code = "DLP27", CommunityName = "C", DisplayName = "DocLib 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var person = new Participant
        {
            EventId = evt.Id, FullName = "Olivia Organizer", Email = "olivia@example.test",
            Role = role, IsActive = true,
        };
        db.Participants.Add(person);
        await db.SaveChangesAsync();

        var options = Options();
        var cache = new DocLibraryOverrideCache(sp.GetRequiredService<IServiceScopeFactory>());
        var resolver = new DocLibraryPathResolver(options, cache);
        var store = new DocLibraryOverrideStore(db, cache);
        var client = new SharePointUploadClient(new HttpClient(), new SharePointUploadOptions());
        var tester = new DocLibraryPathTester(resolver, client, new StaticOptions(options));

        var http = new DefaultHttpContext { User = Session(person) };
        var model = new DocLibraryPathsModel(
            new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http)),
            resolver, store, tester, new StaticOptions(options), db)
        {
            PageContext = new PageContext { HttpContext = http },
        };

        return new Harness
        {
            Db = db, Model = model, Resolver = resolver, Store = store, Sp = sp, Organizer = person,
        };
    }

    [Fact]
    public async Task An_organizer_sees_every_registered_folder_with_where_it_resolves()
    {
        using var h = await NewAsync();

        var result = await h.Model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.False(h.Model.AccessDenied);
        Assert.Equal(DocLibraryPaths.All.Count, h.Model.Rows.Count);

        var photos = h.Model.Rows.Single(r => r.Definition.Key == DocLibraryPaths.SpeakerPhotos);
        Assert.Equal("General/Test/EventHub/Speakers/Photos", photos.FullPath);
        Assert.False(photos.IsOverridden);
        Assert.Equal("General/Test/EventHub", h.Model.Root);
        Assert.Equal("DLP27", h.Model.EventShortName);
    }

    [Fact]
    public async Task A_non_organizer_is_refused()
    {
        using var h = await NewAsync(ParticipantRole.Speaker);

        await h.Model.OnGetAsync(default);

        // 🔒 Organizer-only (work order §6.1). These fields point every read and write in the
        // product at a folder; a speaker must not be able to see or move them.
        Assert.True(h.Model.AccessDenied);
        Assert.Empty(h.Model.Rows);
    }

    [Fact]
    public async Task A_non_organizer_cannot_save_either()
    {
        using var h = await NewAsync(ParticipantRole.Speaker);
        h.Model.Key = DocLibraryPaths.SpeakerPhotos;
        h.Model.Kind = DocLibrarySettingKind.Path;
        h.Model.Value = "Somewhere/Else";

        await h.Model.OnPostSaveAsync(default);

        Assert.True(h.Model.AccessDenied);
        Assert.Empty(await h.Store.AllAsync());          // nothing written
    }

    [Fact]
    public async Task Saving_a_path_takes_effect_through_the_resolver_immediately()
    {
        using var h = await NewAsync();
        h.Model.Key = DocLibraryPaths.SpeakerPhotos;
        h.Model.Kind = DocLibrarySettingKind.Path;
        h.Model.Value = "Speakers/Portraits";

        await h.Model.OnPostSaveAsync(default);

        Assert.Null(h.Model.ErrorMessage);
        Assert.NotNull(h.Model.SavedMessage);
        Assert.True(h.Resolver.TryResolve(DocLibraryPaths.SpeakerPhotos, out var resolved));
        Assert.Equal("General/Test/EventHub/Speakers/Portraits", resolved);

        // ...and the page now says so, so the operator can see the value is not the code's.
        Assert.True(h.Model.Rows
            .Single(r => r.Definition.Key == DocLibraryPaths.SpeakerPhotos).IsOverridden);
        Assert.Equal("olivia@example.test", (await h.Store.HistoryAsync())[0].ChangedByEmail);
    }

    /// <summary>
    /// 🔒 The save is the LAST moment a bad path can be caught with a human present. After it, the
    /// store returns an empty listing and every caller reads "nothing to do" — silently.
    /// </summary>
    [Theory]
    [InlineData("/Speakers/Photos")]
    [InlineData("https://contoso.sharepoint.com/sites/x/Speakers")]
    [InlineData("Speakers/../Escape")]
    [InlineData("Speakers/Photos/")]
    public async Task A_dangerous_path_is_refused_and_NOTHING_is_written(string value)
    {
        using var h = await NewAsync();
        h.Model.Key = DocLibraryPaths.SpeakerPhotos;
        h.Model.Kind = DocLibrarySettingKind.Path;
        h.Model.Value = value;

        await h.Model.OnPostSaveAsync(default);

        Assert.NotNull(h.Model.ErrorMessage);
        Assert.Empty(await h.Store.AllAsync());
        Assert.Empty(await h.Store.HistoryAsync());
        Assert.True(h.Resolver.TryResolve(DocLibraryPaths.SpeakerPhotos, out var resolved));
        Assert.Equal("General/Test/EventHub/Speakers/Photos", resolved);   // still the default
    }

    [Fact]
    public async Task An_unregistered_key_cannot_be_saved()
    {
        using var h = await NewAsync();
        h.Model.Key = "SomeFolderNobodyRegistered";
        h.Model.Kind = DocLibrarySettingKind.Path;
        h.Model.Value = "Anywhere";

        await h.Model.OnPostSaveAsync(default);

        // A path that exists only as a string is invisible to this page, to the startup check and
        // to the next audit — the exact thing the registry exists to prevent.
        Assert.NotNull(h.Model.ErrorMessage);
        Assert.Empty(await h.Store.AllAsync());
    }

    [Fact]
    public async Task Restore_puts_a_folder_back_on_the_built_in_default()
    {
        using var h = await NewAsync();
        await h.Store.SetAsync(
            DocLibrarySettingKind.Path, DocLibraryPaths.SpeakerPhotos, "Speakers/Portraits", "o@x.test");

        h.Model.Key = DocLibraryPaths.SpeakerPhotos;
        h.Model.Kind = DocLibrarySettingKind.Path;
        await h.Model.OnPostRestoreAsync(default);

        Assert.True(h.Resolver.TryResolve(DocLibraryPaths.SpeakerPhotos, out var resolved));
        Assert.Equal("General/Test/EventHub/Speakers/Photos", resolved);
        Assert.Empty(await h.Store.AllAsync());
        // The history keeps BOTH events even though the row is gone.
        Assert.Equal(2, (await h.Store.HistoryAsync()).Count);
    }

    [Fact]
    public async Task A_file_name_setting_is_validated_as_a_NAME_not_a_path()
    {
        using var h = await NewAsync();
        h.Model.Key = DocLibraryFileNames.TemplateBackground;
        h.Model.Kind = DocLibrarySettingKind.FileName;
        h.Model.Value = "Event/Graphics/Template.png";

        await h.Model.OnPostSaveAsync(default);

        Assert.NotNull(h.Model.ErrorMessage);
        Assert.Empty(await h.Store.AllAsync());

        h.Model.Value = "Template.JPG";
        await h.Model.OnPostSaveAsync(default);

        Assert.Null(h.Model.ErrorMessage);
        Assert.True(h.Resolver.TryResolveFileName(DocLibraryFileNames.TemplateBackground, out var name));
        Assert.Equal("Template.JPG", name);
    }

    /// <summary>
    /// 🔒 §769.1 D2 — the ROOT is the boundary between this environment and the other one. Point
    /// production's root at the DEV tree and a sweep overwrites live artwork. The page shows it; it
    /// must offer no way at all to write it, including through a crafted post.
    /// </summary>
    [Fact]
    public async Task The_ROOT_cannot_be_changed_from_this_page_even_by_a_crafted_post()
    {
        using var h = await NewAsync();
        h.Model.Key = "RootFolderPath";
        h.Model.Kind = DocLibrarySettingKind.Path;
        h.Model.Value = "General/DEVELOPMENT/EventHub";

        await h.Model.OnPostSaveAsync(default);

        Assert.NotNull(h.Model.ErrorMessage);              // not a registered PATH key
        Assert.Equal("General/Test/EventHub", h.Model.Root);
        Assert.Empty(await h.Store.AllAsync());
    }
}
