using CommunityHub.Core.Integrations.Graphics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The reusable, server-proxied VENUE-image service (REQUIREMENTS §146). Reads an
/// ALLOWLISTED Venue subfolder through the <see cref="ISharePointFileStore"/> read seam
/// (app creds), caches list + bytes ~15 min, and sanitizes the requested file name. Uses a
/// FAKE store (no external call). Proves: the allowlist rejects unknown folders, file-name
/// sanitization rejects traversal, the slug→folder map, listing + download-by-name, the
/// inert (not-configured) path, and the cache (folder listed once). NO real data.
/// </summary>
public class VenueImageServiceTests
{
    private const string Root = "General/Events/ELDK 2027/EventHub/Venue";

    private static VenueImageService NewService(FakeVenueStore store, string root = Root) =>
        new(store,
            Options.Create(new GraphicsSharePointOptions
            {
                Enabled = true,
                SiteUrl = "https://contoso.sharepoint.example.test/sites/eldk",
                VenueRootFolderPath = root,
            }),
            new MemoryCache(new MemoryCacheOptions()));

    // ---- allowlist ---------------------------------------------------------

    [Theory]
    [InlineData("wayfinding", true)]
    [InlineData("good-to-know", true)]
    [InlineData("evaluations", true)]
    [InlineData("expo", true)]
    [InlineData("Wayfinding", true)]   // case-insensitive
    [InlineData("speakers", false)]    // not on the allowlist (no open proxy)
    [InlineData("../secret", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowedFolder_only_accepts_the_four_keys(string? key, bool expected)
    {
        Assert.Equal(expected, VenueImageService.IsAllowedFolder(key));
    }

    [Fact]
    public void ResolveFolderPath_maps_key_to_root_plus_subfolder_and_rejects_unknown()
    {
        var svc = NewService(new FakeVenueStore(canRead: true));
        Assert.Equal($"{Root}/Wayfinding", svc.ResolveFolderPath("wayfinding"));
        Assert.Equal($"{Root}/Good to know", svc.ResolveFolderPath("good-to-know"));
        Assert.Equal($"{Root}/Evaluations", svc.ResolveFolderPath("evaluations"));
        Assert.Equal($"{Root}/Expo", svc.ResolveFolderPath("expo"));
        // Unknown folder key is rejected → null (no open proxy).
        Assert.Null(svc.ResolveFolderPath("speakers"));
        // Root unset → inert (null) even for an allowlisted key.
        var inert = NewService(new FakeVenueStore(canRead: true), root: "");
        Assert.Null(inert.ResolveFolderPath("wayfinding"));
    }

    // ---- slug → folder -----------------------------------------------------

    [Theory]
    [InlineData("wayfinding", "wayfinding")]
    [InlineData("good-to-know", null)]                  // §162: good-to-know gallery dropped (no venue folder)
    [InlineData("session-evaluations", "evaluations")] // slug differs from folder key
    [InlineData("addresses", null)]                    // a content slug with no venue folder
    [InlineData("expo", null)]                          // folder key is not an /Info slug
    public void FolderForSlug_maps_info_slugs_to_folder_keys(string slug, string? expected)
    {
        Assert.Equal(expected, VenueImageService.FolderForSlug(slug));
    }

    // ---- file-name sanitization (traversal) --------------------------------

    [Theory]
    [InlineData("wf-01.png", "wf-01.png")]
    [InlineData("Entrance Map.jpg", "Entrance Map.jpg")]
    [InlineData("  expo.PNG  ", "expo.PNG")] // trimmed; case-preserved
    public void SanitizeFileName_accepts_safe_image_leaf_names(string input, string expected)
    {
        Assert.Equal(expected, VenueImageService.SanitizeFileName(input));
    }

    [Theory]
    [InlineData("../secret.png")]
    [InlineData("..\\secret.png")]
    [InlineData("sub/dir/x.png")]
    [InlineData("a\\b.png")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("notanimage.txt")]   // wrong extension
    [InlineData("noextension")]
    [InlineData("")]
    [InlineData(null)]
    public void SanitizeFileName_rejects_traversal_paths_and_non_images(string? input)
    {
        Assert.Null(VenueImageService.SanitizeFileName(input));
    }

    // ---- listing + download ------------------------------------------------

    [Fact]
    public async Task ListFileNames_returns_only_images_sorted_and_caches_the_folder()
    {
        var folder = $"{Root}/Wayfinding";
        var store = new FakeVenueStore(canRead: true)
        {
            [folder] = { File("wf-02.png"), File("notes.txt"), File("wf-01.png") },
        };
        var svc = NewService(store);

        var names = await svc.ListFileNamesAsync("wayfinding");
        Assert.Equal(new[] { "wf-01.png", "wf-02.png" }, names);  // sorted, .txt excluded

        // Second call is served from cache — the store is listed only ONCE.
        await svc.ListFileNamesAsync("wayfinding");
        Assert.Equal(1, store.ListCount(folder));
    }

    [Fact]
    public async Task GetImage_downloads_bytes_by_name_and_rejects_traversal_and_misses()
    {
        var folder = $"{Root}/Expo";
        var store = new FakeVenueStore(canRead: true)
        {
            [folder] = { File("expo-map.png") },
        };
        var svc = NewService(store);

        var hit = await svc.GetImageAsync("expo", "expo-map.png");
        Assert.NotNull(hit);
        Assert.Equal("expo-map.png", hit!.FileName);
        Assert.Equal("image/png", hit.ContentType);
        Assert.NotEmpty(hit.Content);

        // A traversal attempt is rejected before any store call.
        Assert.Null(await svc.GetImageAsync("expo", "../secret.png"));
        // A file that isn't in the folder → null (no fake bytes).
        Assert.Null(await svc.GetImageAsync("expo", "missing.png"));
        // An unknown (non-allowlisted) folder → null.
        Assert.Null(await svc.GetImageAsync("speakers", "expo-map.png"));
    }

    // ---- inert (not configured) -------------------------------------------

    [Fact]
    public async Task Is_inert_when_store_cannot_read()
    {
        var folder = $"{Root}/Wayfinding";
        var store = new FakeVenueStore(canRead: false)
        {
            [folder] = { File("wf-01.png") },
        };
        var svc = NewService(store);

        Assert.False(svc.CanRead);
        Assert.Empty(await svc.ListFileNamesAsync("wayfinding"));
        Assert.Null(await svc.GetImageAsync("wayfinding", "wf-01.png"));
        Assert.Equal(0, store.ListCount(folder));
    }

    [Fact]
    public async Task Is_inert_when_venue_root_is_blank()
    {
        var store = new FakeVenueStore(canRead: true)
        {
            [$"{Root}/Wayfinding"] = { File("wf-01.png") },
        };
        var svc = NewService(store, root: "");

        Assert.False(svc.CanRead);
        Assert.Empty(await svc.ListFileNamesAsync("wayfinding"));
        Assert.Null(await svc.GetImageAsync("wayfinding", "wf-01.png"));
    }

    // ---- helpers -----------------------------------------------------------

    private static SharePointFileRef File(string name) =>
        new("item-" + name, name, "https://store.example.test/" + name);

    /// <summary>In-memory fake store: list/download against folders; counts list calls.</summary>
    private sealed class FakeVenueStore : ISharePointFileStore
    {
        private readonly Dictionary<string, List<SharePointFileRef>> _byFolder = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _listCounts = new(StringComparer.OrdinalIgnoreCase);

        public FakeVenueStore(bool canRead) { CanRead = canRead; }

        public List<SharePointFileRef> this[string folder]
        {
            get
            {
                if (!_byFolder.TryGetValue(folder, out var list)) { list = new(); _byFolder[folder] = list; }
                return list;
            }
        }

        public int ListCount(string folder) => _listCounts.TryGetValue(folder, out var n) ? n : 0;

        public bool CanRead { get; }
        public bool CanStore => false;

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default)
        {
            if (!CanRead) return Task.FromResult<IReadOnlyList<SharePointFileRef>>(Array.Empty<SharePointFileRef>());
            _listCounts[relativeFolder] = ListCount(relativeFolder) + 1;
            return Task.FromResult<IReadOnlyList<SharePointFileRef>>(
                _byFolder.TryGetValue(relativeFolder, out var list) ? list.ToList() : new List<SharePointFileRef>());
        }

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(CanRead ? new byte[] { 1, 2, 3 } : null);

        // Unused write/store seam.
        public Task<StoredFile> StoreAsync(string relativePath, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("not used by §146");
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task<StoredFile> UploadToFolderAsync(string relativeFolder, string fileName, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("not used by §146");
        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default) => Task.CompletedTask;
    }
}
