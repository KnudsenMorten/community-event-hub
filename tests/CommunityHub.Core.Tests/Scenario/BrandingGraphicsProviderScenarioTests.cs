using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: the BRANDING GRAPHICS consumption contract (REQUIREMENTS §18) — the
/// read-only seam that EXPOSES publishable graphics + a ready draft text to a
/// downstream consumer (the §19 SoMe queue, or any other), without graphics
/// referencing a SoMe type. Proves the load-bearing contracts:
///  - a speaker / session graphic is exposed ONLY after the organizer RELEASES it
///    (the gate holds — a still-generated graphic yields null);
///  - a sponsor graphic is exposed (internal-only — no speaker release gate);
///  - the consumable ref carries the image ref a SoMePost expects + a draft text
///    with the event name / ticket URL;
///  - with the NULL store (nothing wired) the provider still exposes the stable
///    PATH as the image ref and never makes an external call.
///
/// NO real data — example.test + @@expertslive.dk only.
/// </summary>
public sealed class BrandingGraphicsProviderScenarioTests
{
    private static byte[] MakePng(int w, int h, Rgba32 color)
    {
        using var img = new Image<Rgba32>(w, h, color);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] Template() => MakePng(600, 600, new Rgba32(10, 50, 90));
    private static byte[] Photo() => MakePng(200, 200, new Rgba32(200, 200, 200));
    private static byte[] Logo() => MakePng(200, 80, new Rgba32(0, 120, 210));

    private static GraphicsService NewGraphics(CommunityHubDbContext db, ISharePointFileStore store) =>
        new(db, new GraphicCompositor(), store,
            new FakePictureFetcher(), new DraftOnlySocialShareGateway(),
            Microsoft.Extensions.Options.Options.Create(new GraphicsSharePointOptions()));

    private static async Task<Session> AddSessionAsync(
        CommunityHubDbContext db, ScenarioSeed.SeedResult s, string title, int speakerId)
    {
        var session = new Session
        {
            EventId = s.EventId,
            SessionizeId = Guid.NewGuid().ToString("N"),
            Title = title,
            Abstract = "An interesting talk.",
            CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = session.Id, ParticipantId = speakerId });
        await db.SaveChangesAsync();
        return session;
    }

    // ---- SPEAKER: gated until released -------------------------------------

    [Fact]
    public async Task Speaker_graphic_is_not_exposed_until_released()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var graphics = NewGraphics(db, new FakeFileStore());
        var provider = new BrandingGraphicsProvider(db, graphics);

        var asset = await graphics.GenerateSpeakerGraphicAsync(
            seed.EventId, seed.SpeakerOneId, Template(), Photo(), "Session Speaker One");

        // Behind the gate -> not exposed to a consumer.
        Assert.Null(await provider.GetSpeakerGraphicAsync(seed.EventId, seed.SpeakerOneId));

        // Organizer releases it -> now exposed.
        await graphics.ReleaseAsync(seed.EventId, asset.Id, "organizer@expertslive.dk");
        var refReleased = await provider.GetSpeakerGraphicAsync(seed.EventId, seed.SpeakerOneId);

        Assert.NotNull(refReleased);
        Assert.Equal(GraphicAssetType.Speaker, refReleased!.Type);
        Assert.Equal(GraphicStableKey.ForSpeaker(seed.SpeakerOneId), refReleased.StableKey);
        // The image ref is the consumable shape SoMePost.ImageRef expects (the live URL).
        Assert.False(string.IsNullOrWhiteSpace(refReleased.ImageRef));
        Assert.Equal("speaker-" + seed.SpeakerOneId + ".png", refReleased.FileName);
        // The draft text carries the event + ticket URL.
        Assert.Contains("ELDK27", refReleased.DraftText);
        Assert.Contains("eldk27.expertslive.dk", refReleased.DraftText);
    }

    [Fact]
    public async Task Unreleasing_pulls_the_graphic_back_out_of_the_consumer_surface()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var graphics = NewGraphics(db, new FakeFileStore());
        var provider = new BrandingGraphicsProvider(db, graphics);

        var asset = await graphics.GenerateSpeakerGraphicAsync(
            seed.EventId, seed.SpeakerOneId, Template(), Photo(), "One");
        await graphics.ReleaseAsync(seed.EventId, asset.Id, "organizer@expertslive.dk");
        Assert.NotNull(await provider.GetSpeakerGraphicAsync(seed.EventId, seed.SpeakerOneId));

        await graphics.UnreleaseAsync(seed.EventId, asset.Id);
        Assert.Null(await provider.GetSpeakerGraphicAsync(seed.EventId, seed.SpeakerOneId));
    }

    // ---- SESSION: gated, and carries the session title in the draft --------

    [Fact]
    public async Task Session_graphic_exposes_only_after_release_with_session_title_in_draft()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var graphics = NewGraphics(db, new FakeFileStore());
        var provider = new BrandingGraphicsProvider(db, graphics);
        var session = await AddSessionAsync(db, seed, "Cloud security deep dive", seed.SpeakerOneId);

        var asset = await graphics.GenerateSessionGraphicAsync(
            seed.EventId, session.Id, seed.SpeakerOneId, Template(), Photo(), "One", "Cloud security deep dive");
        Assert.Null(await provider.GetSessionGraphicAsync(seed.EventId, session.Id, seed.SpeakerOneId));

        await graphics.ReleaseAsync(seed.EventId, asset.Id, "organizer@expertslive.dk");
        var sref = await provider.GetSessionGraphicAsync(seed.EventId, session.Id, seed.SpeakerOneId);

        Assert.NotNull(sref);
        Assert.Equal(GraphicAssetType.Session, sref!.Type);
        Assert.Contains("Cloud security deep dive", sref.DraftText);
        Assert.Contains("eldk27.expertslive.dk", sref.DraftText);
    }

    // ---- SPONSOR: internal-only, exposed without a speaker release gate -----

    [Fact]
    public async Task Sponsor_graphic_is_exposed_to_the_internal_consumer_without_a_release_gate()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var graphics = NewGraphics(db, new FakeFileStore());
        var provider = new BrandingGraphicsProvider(db, graphics);

        // Sponsor graphics are internal-only — generated, NOT released, still exposed
        // to the organizer-internal consumer (the SoMe sponsor post).
        await graphics.GenerateSponsorGraphicAsync(
            seed.EventId, ScenarioSeed.SponsorCompanyId, Template(), Logo());

        var sref = await provider.GetSponsorGraphicAsync(seed.EventId, ScenarioSeed.SponsorCompanyId);
        Assert.NotNull(sref);
        Assert.Equal(GraphicAssetType.Sponsor, sref!.Type);
        Assert.Equal(GraphicStableKey.ForSponsor(ScenarioSeed.SponsorCompanyId), sref.StableKey);
        Assert.False(string.IsNullOrWhiteSpace(sref.ImageRef));
    }

    [Fact]
    public async Task Missing_graphics_yield_null_for_every_kind()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var provider = new BrandingGraphicsProvider(db, NewGraphics(db, new FakeFileStore()));

        Assert.Null(await provider.GetSpeakerGraphicAsync(seed.EventId, seed.SpeakerOneId));
        Assert.Null(await provider.GetSessionGraphicAsync(seed.EventId, 99999, seed.SpeakerOneId));
        Assert.Null(await provider.GetSponsorGraphicAsync(seed.EventId, "no-such-company"));
    }

    // ---- NULL store: stable PATH is the image ref, no external call --------

    [Fact]
    public async Task Null_store_exposes_the_stable_path_as_the_image_ref_and_makes_no_call()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        // NullSharePointFileStore.StoreAsync throws if ever called -> proves no fake call.
        var graphics = NewGraphics(db, new NullSharePointFileStore());
        var provider = new BrandingGraphicsProvider(db, graphics);

        var asset = await graphics.GenerateSpeakerGraphicAsync(
            seed.EventId, seed.SpeakerOneId, Template(), Photo(), "One");
        await graphics.ReleaseAsync(seed.EventId, asset.Id, "organizer@expertslive.dk");

        var sref = await provider.GetSpeakerGraphicAsync(seed.EventId, seed.SpeakerOneId);
        Assert.NotNull(sref);
        // No live store -> no URL -> the stable relative PATH is the consumable ref.
        Assert.Equal("Speakers/speaker-" + seed.SpeakerOneId + ".png", sref!.ImageRef);
    }

    // ---- Caller can override the event context -----------------------------

    [Fact]
    public async Task Caller_supplied_event_context_flows_into_the_draft_text()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var graphics = NewGraphics(db, new FakeFileStore());
        var provider = new BrandingGraphicsProvider(db, graphics)
        {
            Context = new BrandingEventContext("Experts Live Denmark 2027", "4-5 Feb 2027", "tickets.example.test"),
        };

        var asset = await graphics.GenerateSpeakerGraphicAsync(
            seed.EventId, seed.SpeakerOneId, Template(), Photo(), "One");
        await graphics.ReleaseAsync(seed.EventId, asset.Id, "organizer@expertslive.dk");

        var sref = await provider.GetSpeakerGraphicAsync(seed.EventId, seed.SpeakerOneId);
        Assert.NotNull(sref);
        Assert.Contains("Experts Live Denmark 2027", sref!.DraftText);
        Assert.Contains("tickets.example.test", sref.DraftText);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>A recording SharePoint file store (CanStore=true) — returns a stable URL.</summary>
    private sealed class FakeFileStore : ISharePointFileStore
    {
        public bool CanStore => true;
        public Task<StoredFile> StoreAsync(
            string relativePath, byte[] content, string contentType, CancellationToken ct = default) =>
            Task.FromResult(new StoredFile(
                relativePath, $"https://store.example.test/{relativePath}", "item-" + relativePath));
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;

        // Read side unused by these (write-focused) tests.
        public bool CanRead => false;
        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(
            string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(Array.Empty<SharePointFileRef>());
        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);
        public Task<StoredFile> UploadToFolderAsync(
            string relativeFolder, string fileName, byte[] content, string contentType, CancellationToken ct = default) =>
            Task.FromResult(new StoredFile($"{relativeFolder}/{fileName}", $"https://store.example.test/{relativeFolder}/{fileName}", "item"));
        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A picture fetcher that never returns an image (not used by these tests).</summary>
    private sealed class FakePictureFetcher : ISpeakerPictureFetcher
    {
        public Task<FetchedImage?> FetchAsync(string? pictureUrl, CancellationToken ct = default) =>
            Task.FromResult<FetchedImage?>(null);
    }
}
