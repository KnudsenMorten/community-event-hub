using CommunityHub.Api;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1384 — <c>GET /graphics/track-speakers.gif</c>, the public stable URL of the all-tracks overview.
/// Driven over a fake <see cref="HttpContext"/> (Program.cs cannot boot in-test — it migrates SQL at
/// startup), plus a source check that the route is mapped anonymously. FAKE data only.
/// </summary>
public sealed class AllTracksSpeakersGifEndpointTests
{
    private const int EventId = 7;

    private static byte[] OverviewGif()
    {
        using var face = new Image<Rgba32>(100, 100, new Rgba32(40, 120, 200));
        using var ms = new MemoryStream();
        face.SaveAsPng(ms);
        var photo = ms.ToArray();
        return new SoMeGraphicRenderer().RenderTrackOverviewGif(new[]
        {
            new TrackOverviewTrack("Security", new (byte[]?, string)[] { (photo, "Ada Example"), (null, "Bo Example") }),
            new TrackOverviewTrack("Azure", new (byte[]?, string)[] { (photo, "Cy Example") }),
        });
    }

    private static async Task<(GraphicsService Graphics, CommunityHubDbContext Db)> SeedAsync(
        GraphicAssetStatus status = GraphicAssetStatus.Released)
    {
        var db = new CommunityHubDbContext(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"all-tracks-gif-{Guid.NewGuid():N}").Options);
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "Community Events Demo 2027", Code = "DEMO27",
            IsActive = true, StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = EventId, Type = GraphicAssetType.AllTracksSpeakers,
            StableKey = GraphicStableKey.ForAllTracksSpeakers(), FileName = "tracks-all-speakers.gif",
            Status = status, StorageItemId = "item-overview", UpdatedAt = DateTimeOffset.UnixEpoch,
        });
        await db.SaveChangesAsync();

        var store = new OneFileStore("item-overview", OverviewGif());
        var graphics = new GraphicsService(
            db, new GraphicCompositor(), store, new NoPictureFetcher(), new DraftOnlySocialShareGateway(),
            Options.Create(new GraphicsSharePointOptions()), TestDocLibrary.Resolver());
        return (graphics, db);
    }

    private static IMemoryCache NewCache() => new MemoryCache(new MemoryCacheOptions());

    [Fact]
    public async Task It_returns_the_stored_gif_with_a_public_one_hour_cache_header()
    {
        var (graphics, db) = await SeedAsync();
        using var _ = db;
        var http = new DefaultHttpContext();

        var result = await AllTracksSpeakersGifEndpoint.HandleAsync(null, graphics, NewCache(), http, default);

        var file = Assert.IsType<FileContentHttpResult>(result);
        Assert.Equal("image/gif", file.ContentType);
        Assert.Equal("public, max-age=3600", http.Response.Headers.CacheControl.ToString());
        using var img = Image.Load<Rgba32>(file.FileContents.ToArray());
        Assert.Equal(1200, img.Width);
        Assert.Equal(2, img.Frames.Count);
    }

    [Fact]
    public async Task W600_returns_the_mail_width_with_every_frame()
    {
        var (graphics, db) = await SeedAsync();
        using var _ = db;

        var result = await AllTracksSpeakersGifEndpoint.HandleAsync(600, graphics, NewCache(), new DefaultHttpContext(), default);

        var file = Assert.IsType<FileContentHttpResult>(result);
        using var img = Image.Load<Rgba32>(file.FileContents.ToArray());
        Assert.Equal(600, img.Width);
        Assert.Equal(2, img.Frames.Count);
    }

    [Fact]
    public async Task An_unreleased_overview_is_404()
    {
        var (graphics, db) = await SeedAsync(GraphicAssetStatus.Generated);
        using var _ = db;

        var result = await AllTracksSpeakersGifEndpoint.HandleAsync(null, graphics, NewCache(), new DefaultHttpContext(), default);

        Assert.IsType<NotFound>(result);
    }

    [Theory]
    [InlineData(null, 1200)]
    [InlineData(600, 600)]
    [InlineData(1200, 1200)]
    [InlineData(333, 1200)]
    [InlineData(5000, 1200)]
    public void Only_600_and_1200_are_served(int? w, int expected) =>
        Assert.Equal(expected, AllTracksSpeakersGifEndpoint.WidthFor(w));

    [Fact]
    public void Program_maps_the_route_anonymously()
    {
        var program = File.ReadAllText(Path.Combine(WebSrcDir(), "Program.cs"));
        var at = program.IndexOf("app.MapGet(CommunityHub.Api.AllTracksSpeakersGifEndpoint.Route", StringComparison.Ordinal);
        Assert.True(at >= 0, "the /graphics/track-speakers.gif route is not mapped in Program.cs");
        var next = program.IndexOf("app.Map", at + 10, StringComparison.Ordinal);
        var block = program[at..(next < 0 ? program.Length : next)];
        Assert.Contains(".AllowAnonymous()", block);
        Assert.Equal("/graphics/track-speakers.gif", AllTracksSpeakersGifEndpoint.Route);
    }

    private static string WebSrcDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CommunityHub");
            if (File.Exists(Path.Combine(candidate, "Program.cs"))) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate src/CommunityHub/Program.cs from " + AppContext.BaseDirectory);
    }

    private sealed class NoPictureFetcher : ISpeakerPictureFetcher
    {
        public Task<FetchedImage?> FetchAsync(string? pictureUrl, CancellationToken ct = default) =>
            Task.FromResult<FetchedImage?>(null);
    }

    /// <summary>A read-only store holding the one stored overview file.</summary>
    private sealed class OneFileStore(string itemId, byte[] content) : ISharePointFileStore
    {
        public bool CanStore => false;
        public bool CanRead => true;

        public Task<byte[]?> DownloadAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(id == itemId ? content : null);

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(Array.Empty<SharePointFileRef>());

        public Task<StoredFile> StoreAsync(string relativePath, byte[] c, string contentType, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;

        public Task<StoredFile> UploadToFolderAsync(
            string relativeFolder, string fileName, byte[] c, string contentType, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
