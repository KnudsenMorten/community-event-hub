using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Reminders;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §172 — the PUBLIC OpenGraph image for a session's public detail page. Two surfaces:
/// (1) <see cref="GraphicsService.GetPublicSessionOgGraphicAsync"/> (behind the no-auth
/// <c>/og/session-graphic/{id}</c> endpoint) returns the RELEASED, stored session graphic's
/// bytes for an active-edition session, and 404s (null) for an UNRELEASED / unknown session or
/// one with NO graphic — released promo graphics are public, drafts never are; and
/// (2) the public <see cref="CommunityHub.Pages.Sessions.DetailModel"/> emits an ABSOLUTE
/// <c>og:image</c> URL pointing at that endpoint ONLY when a released graphic exists. FAKE names only.
/// </summary>
public sealed class SessionDetailOgImageTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"og-image-{Guid.NewGuid():N}")
            .Options);

    private static GraphicsService Graphics(CommunityHubDbContext db, ISharePointFileStore store) =>
        new(db, new GraphicCompositor(), store, new NullFetcher(), new DraftOnlySocialShareGateway(),
            Options.Create(new GraphicsSharePointOptions()), TestDocLibrary.Resolver());

    private static async Task<(Event evt, Participant speaker)> SeedEventAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "ELDK27", DisplayName = "Community Events Demo 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var speaker = new Participant
        {
            EventId = evt.Id, Email = "speaker.one@example.test",
            FullName = "Session Speaker One", Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(speaker);
        await db.SaveChangesAsync();
        return (evt, speaker);
    }

    private static Session NewSession(Event evt, string title) => new()
    {
        EventId = evt.Id, SessionizeId = Guid.NewGuid().ToString("N"),
        Title = title, Type = SessionType.TechnicalSession, Length = SessionLength.FiftyMin,
    };

    [Fact]
    public async Task Released_session_graphic_streams_image_bytes()
    {
        using var db = NewDb();
        var (evt, speaker) = await SeedEventAsync(db);
        var session = NewSession(evt, "Cloud Native Talk");
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = evt.Id, Type = GraphicAssetType.Session,
            StableKey = GraphicStableKey.ForSession(session.Id, speaker.Id),
            ParticipantId = speaker.Id, SessionId = session.Id,
            Status = GraphicAssetStatus.Released,
            StorageItemId = "item-talk.png", FileName = "Welcome.png",
        });
        await db.SaveChangesAsync();

        var graphics = Graphics(db, new FakeReadStore());

        Assert.True(await graphics.HasPublicSessionOgGraphicAsync(session.Id));
        var file = await graphics.GetPublicSessionOgGraphicAsync(session.Id);
        Assert.NotNull(file);
        Assert.NotEmpty(file!.Content);
        Assert.Equal("image/png", file.ContentType);
    }

    [Fact]
    public async Task Unreleased_unknown_and_graphicless_sessions_return_null()
    {
        using var db = NewDb();
        var (evt, speaker) = await SeedEventAsync(db);

        var draftSession = NewSession(evt, "Still In Review");
        var bareSession = NewSession(evt, "No Graphic Yet");
        db.Sessions.AddRange(draftSession, bareSession);
        await db.SaveChangesAsync();

        // A GENERATED (not released) graphic — must never be served publicly.
        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = evt.Id, Type = GraphicAssetType.Session,
            StableKey = GraphicStableKey.ForSession(draftSession.Id, speaker.Id),
            ParticipantId = speaker.Id, SessionId = draftSession.Id,
            Status = GraphicAssetStatus.Generated,
            StorageItemId = "item-draft.png", FileName = "Draft.png",
        });
        await db.SaveChangesAsync();

        var graphics = Graphics(db, new FakeReadStore());

        Assert.Null(await graphics.GetPublicSessionOgGraphicAsync(draftSession.Id));   // unreleased
        Assert.False(await graphics.HasPublicSessionOgGraphicAsync(draftSession.Id));
        Assert.Null(await graphics.GetPublicSessionOgGraphicAsync(bareSession.Id));    // no graphic
        Assert.Null(await graphics.GetPublicSessionOgGraphicAsync(999999));            // unknown id
    }

    [Fact]
    public async Task Detail_page_emits_absolute_og_image_only_when_a_released_graphic_exists()
    {
        using var db = NewDb();
        var (evt, speaker) = await SeedEventAsync(db);

        var withGfx = NewSession(evt, "Cloud Native Talk");
        var withoutGfx = NewSession(evt, "No Graphic Yet");
        db.Sessions.AddRange(withGfx, withoutGfx);
        await db.SaveChangesAsync();

        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = evt.Id, Type = GraphicAssetType.Session,
            StableKey = GraphicStableKey.ForSession(withGfx.Id, speaker.Id),
            ParticipantId = speaker.Id, SessionId = withGfx.Id,
            Status = GraphicAssetStatus.Released,
            StorageItemId = "item-talk.png", FileName = "Welcome.png",
        });
        await db.SaveChangesAsync();

        var graphics = Graphics(db, new FakeReadStore());

        // With a released graphic → absolute og:image pointing at the public endpoint.
        var withModel = await RunDetailAsync(db, graphics, withGfx.Id);
        Assert.NotNull(withModel.Session);
        Assert.Equal($"https://hub.example.test/og/session-graphic/{withGfx.Id}", withModel.OgImageUrl);
        Assert.Equal($"https://hub.example.test/Sessions/{withGfx.Id}", withModel.OgUrl);

        // §196: without a released graphic → fall back to the DEFAULT event/brand OG image (never
        // blank), so the share card always shows an image. og:url still points at this session.
        var withoutModel = await RunDetailAsync(db, graphics, withoutGfx.Id);
        Assert.NotNull(withoutModel.Session);
        Assert.Equal("https://hub.example.test/img/logo-eldk27-event.png", withoutModel.OgImageUrl);
        Assert.Equal($"https://hub.example.test/Sessions/{withoutGfx.Id}", withoutModel.OgUrl);
    }

    [Fact]
    public async Task Configured_default_og_image_path_overrides_the_builtin_fallback()
    {
        using var db = NewDb();
        var (evt, _) = await SeedEventAsync(db);
        var bare = NewSession(evt, "No Graphic Yet");
        db.Sessions.Add(bare);
        await db.SaveChangesAsync();

        var graphics = Graphics(db, new FakeReadStore());
        // §196: the fallback image is configurable via Branding:DefaultOgImagePath.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Branding:DefaultOgImagePath"] = "/img/brand-social-card.png",
            }).Build();

        var model = await RunDetailAsync(db, graphics, bare.Id, config);
        Assert.Equal("https://hub.example.test/img/brand-social-card.png", model.OgImageUrl);
    }

    private static async Task<CommunityHub.Pages.Sessions.DetailModel> RunDetailAsync(
        CommunityHubDbContext db, GraphicsService graphics, int sessionId,
        IConfiguration? config = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("hub.example.test");
        config ??= new ConfigurationBuilder().Build();
        var model = new CommunityHub.Pages.Sessions.DetailModel(new PublicSessionsService(db), graphics, config)
        {
            PageContext = new PageContext { HttpContext = http },
        };
        await model.OnGetAsync(sessionId, default);
        return model;
    }

    // ---- fakes ---------------------------------------------------------------

    private sealed class NullFetcher : ISpeakerPictureFetcher
    {
        public Task<FetchedImage?> FetchAsync(string? pictureUrl, CancellationToken ct = default) =>
            Task.FromResult<FetchedImage?>(null);
    }

    private sealed class FakeReadStore : ISharePointFileStore
    {
        public bool CanStore => false;
        public bool CanRead => true;
        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(Array.Empty<SharePointFileRef>());
        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(new byte[] { 1, 2, 3 });
        public Task<StoredFile> StoreAsync(string relativePath, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("write side not used");
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task<StoredFile> UploadToFolderAsync(string relativeFolder, string fileName, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("write side not used");
        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default) => Task.CompletedTask;
    }
}
