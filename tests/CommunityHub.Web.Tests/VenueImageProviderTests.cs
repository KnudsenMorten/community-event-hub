using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Venue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// The web-side venue-image façade (REQUIREMENTS §146): SharePoint (live, app-credentialed)
/// is the source of truth when reachable; otherwise the committed images under
/// <c>wwwroot/content/eldk27/{folder}/</c> are the fallback. Every rendered image points at
/// the CEH proxy URL (<c>/venue-image/{folder}/{name}</c>) — never a SharePoint link.
/// Drives the real <see cref="VenueImageProvider"/> over a FAKE store + a temp web root.
/// </summary>
public sealed class VenueImageProviderTests
{
    private const string Root = "General/Events/ELDK 2027/EventHub/Venue";

    private static VenueImageService NewService(FakeStore store, string root = Root) =>
        new(store,
            Options.Create(new GraphicsSharePointOptions
            {
                Enabled = true,
                SiteUrl = "https://contoso.sharepoint.example.test/sites/eldk",
                VenueRootFolderPath = root,
            }),
            new MemoryCache(new MemoryCacheOptions()));

    private static (VenueImageProvider provider, string webRoot) NewProvider(FakeStore store, string root = Root)
    {
        var webRoot = Path.Combine(Path.GetTempPath(), "ceh-venue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(webRoot);
        var env = new FakeEnv(webRoot);
        return (new VenueImageProvider(NewService(store, root), env), webRoot);
    }

    private static void WriteCommitted(string webRoot, string folderKey, string fileName, byte[] bytes)
    {
        var dir = Path.Combine(webRoot, "content", "eldk27", folderKey);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, fileName), bytes);
    }

    [Fact]
    public async Task Gallery_falls_back_to_committed_wwwroot_images_when_sharepoint_empty()
    {
        // Store can't read → no live images → committed fallback applies.
        var (provider, webRoot) = NewProvider(new FakeStore(canRead: false));
        try
        {
            WriteCommitted(webRoot, "wayfinding", "wf-02.png", new byte[] { 7 });
            WriteCommitted(webRoot, "wayfinding", "wf-01.png", new byte[] { 8 });
            WriteCommitted(webRoot, "wayfinding", "readme.txt", new byte[] { 9 }); // non-image ignored

            var gallery = await provider.GetGalleryAsync("wayfinding");

            Assert.Equal(new[] { "wf-01.png", "wf-02.png" }, gallery.Select(g => g.FileName)); // sorted, .txt out
            // Every URL is the CEH proxy — never a SharePoint link.
            Assert.All(gallery, g => Assert.StartsWith("/venue-image/wayfinding/", g.Url));

            // The endpoint resolver serves the committed file bytes.
            var img = await provider.GetImageAsync("wayfinding", "wf-01.png");
            Assert.NotNull(img);
            Assert.Equal(new byte[] { 8 }, img!.Content);
            Assert.Equal("image/png", img.ContentType);
        }
        finally { Directory.Delete(webRoot, recursive: true); }
    }

    [Fact]
    public async Task Live_sharepoint_wins_over_committed_fallback()
    {
        var folder = $"{Root}/Expo";
        var store = new FakeStore(canRead: true) { [folder] = { Ref("expo-live.png") } };
        var (provider, webRoot) = NewProvider(store);
        try
        {
            // A committed file exists, but SharePoint is reachable → live wins.
            WriteCommitted(webRoot, "expo", "expo-old.png", new byte[] { 1 });

            var gallery = await provider.GetGalleryAsync("expo");
            Assert.Equal(new[] { "expo-live.png" }, gallery.Select(g => g.FileName));

            var img = await provider.GetImageAsync("expo", "expo-live.png");
            Assert.NotNull(img);
            Assert.Equal(new byte[] { 1, 2, 3 }, img!.Content); // the fake store's "live" bytes
        }
        finally { Directory.Delete(webRoot, recursive: true); }
    }

    [Fact]
    public async Task GetImage_rejects_unknown_folder_and_traversal()
    {
        var (provider, webRoot) = NewProvider(new FakeStore(canRead: false));
        try
        {
            WriteCommitted(webRoot, "expo", "expo.png", new byte[] { 1 });
            // Plant a "secret" one level above the expo fallback folder.
            File.WriteAllBytes(Path.Combine(webRoot, "content", "eldk27", "secret.png"), new byte[] { 42 });

            Assert.Null(await provider.GetImageAsync("speakers", "expo.png"));      // not allowlisted
            Assert.Null(await provider.GetImageAsync("expo", "../secret.png"));      // traversal
            Assert.Null(await provider.GetImageAsync("expo", "..\\secret.png"));     // traversal (win sep)
            Assert.Empty(await provider.GetGalleryAsync("speakers"));                // unknown folder gallery
        }
        finally { Directory.Delete(webRoot, recursive: true); }
    }

    // ---- helpers -----------------------------------------------------------

    private static SharePointFileRef Ref(string name) =>
        new("item-" + name, name, "https://store.example.test/" + name);

    private sealed class FakeEnv(string webRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = webRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "CommunityHub.Web.Tests";
        public string ContentRootPath { get; set; } = webRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string EnvironmentName { get; set; } = "Development";
    }

    private sealed class FakeStore : ISharePointFileStore
    {
        private readonly Dictionary<string, List<SharePointFileRef>> _byFolder = new(StringComparer.OrdinalIgnoreCase);
        public FakeStore(bool canRead) { CanRead = canRead; }

        public List<SharePointFileRef> this[string folder]
        {
            get
            {
                if (!_byFolder.TryGetValue(folder, out var list)) { list = new(); _byFolder[folder] = list; }
                return list;
            }
        }

        public bool CanRead { get; }
        public bool CanStore => false;

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(
                CanRead && _byFolder.TryGetValue(relativeFolder, out var list) ? list.ToList() : new List<SharePointFileRef>());

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(CanRead ? new byte[] { 1, 2, 3 } : null);

        public Task<StoredFile> StoreAsync(string relativePath, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("unused");
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task<StoredFile> UploadToFolderAsync(string relativeFolder, string fileName, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("unused");
        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default) => Task.CompletedTask;
    }
}
