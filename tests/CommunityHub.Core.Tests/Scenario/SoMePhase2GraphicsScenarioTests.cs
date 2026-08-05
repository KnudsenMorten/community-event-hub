using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: §767 PHASE 2 — the per-SESSION graphic (PNG single / GIF multi) and the per-SPONSOR
/// graphic, built by the quarter-hourly sweep and surfaced to the speakers of that session.
/// </summary>
/// <remarks>
/// <para>Drives the real <see cref="SoMeBundleBuildService"/> + <see cref="GraphicsService"/>
/// against EF in-memory with a fake SharePoint store, so the whole chain runs: list the folders →
/// match photos → render → store → upsert → who can see it.</para>
///
/// <para>🔑 <b>What is worth pinning here</b> (each of these is a way the feature has already failed
/// or could fail silently):</para>
/// <list type="bullet">
/// <item>ONE row per session, keyed <c>session:{id}</c> with NO participant — the whole point of
///   phase 2, and the thing every visibility surface then had to be taught;</item>
/// <item>single ⇒ <c>.png</c>, multi ⇒ <c>.gif</c>, and a 1→2-speaker flip UPDATES that one row
///   rather than creating a second graphic;</item>
/// <item>an unchanged line-up does NOT re-render (the hash guard) — a "built" count that never
///   changes reports nothing, which is the §767.3 lesson;</item>
/// <item>EVERY speaker on the session sees it once released, and nobody else does;</item>
/// <item>🔒 the quarter-hourly sync must NOT auto-release engine-rendered artwork — releasing stays
///   the organizer's click — while still releasing the operator's own PULLED files;</item>
/// <item>🔒 a session the operator uploaded artwork for is LEFT ALONE by the generator.</item>
/// </list>
///
/// NO real data — example.test + @@expertslive.dk only.
/// </remarks>
public sealed class SoMePhase2GraphicsScenarioTests
{
    // §768 — every folder the sweep touches now resolves from the DocLibrary registry, so the fake
    // store must stage its files at the RESOLVED paths. Staging them under the old names would make
    // the sweep find nothing and report a healthy zero, which is precisely the failure mode these
    // tests exist to catch.
    private static string P(string key) => TestDocLibrary.PathFor(key);

    private static readonly string TemplateFolder =
        P(CommunityHub.Core.Integrations.DocLibrary.DocLibraryPaths.EventGraphicsTemplate);
    private static readonly string PhotoFolder =
        P(CommunityHub.Core.Integrations.DocLibrary.DocLibraryPaths.SpeakerPhotos);
    private static readonly string SponsorLogoFolder =
        P(CommunityHub.Core.Integrations.DocLibrary.DocLibraryPaths.SponsorLogoWeb);

    /// <summary>Where generated session graphics now land — INSIDE the configured root (§768).</summary>
    private static readonly string SessionsFolder =
        P(CommunityHub.Core.Integrations.DocLibrary.DocLibraryPaths.SpeakerSessionGraphics);

    // ---- harness -----------------------------------------------------------------------

    private static byte[] Image(int w, int h) =>
        ToPng(new Image<Rgba32>(w, h, new Rgba32(20, 60, 100)));

    private static byte[] ToPng(Image<Rgba32> img)
    {
        using (img)
        {
            using var ms = new MemoryStream();
            img.SaveAsPng(ms);
            return ms.ToArray();
        }
    }

    private static GraphicsSharePointOptions SpOptions(bool withSponsorLogos = false) => new()
    {
        Enabled = true,
        SiteUrl = "https://contoso.sharepoint.example.test/sites/eldk",
        TemplateFolderPath = TemplateFolder,
        SpeakerPhotosFolderPath = PhotoFolder,
        SponsorLogosFolderPath = withSponsorLogos ? SponsorLogoFolder : string.Empty,
    };

    private static (SoMeBundleBuildService Bundles, GraphicsService Graphics, FakeStore Store)
        NewSweep(CommunityHubDbContext db, FakeStore store, GraphicsSharePointOptions options)
    {
        var graphics = new GraphicsService(
            db, new GraphicCompositor(), store, new FakePictureFetcher(null),
            new DraftOnlySocialShareGateway(), Options.Create(options), TestDocLibrary.Resolver());
        var bundles = new SoMeBundleBuildService(
            db, graphics, store, Options.Create(options), TestDocLibrary.Resolver(),
            NullLogger<SoMeBundleBuildService>.Instance);
        return (bundles, graphics, store);
    }

    /// <summary>A store holding the template + one photo per named speaker.</summary>
    private static FakeStore StoreWithPhotos(params (int ParticipantId, string Name)[] speakers)
    {
        var store = new FakeStore();
        store.Put(TemplateFolder, "Template.JPG", Image(1600, 1000));
        store.Put(TemplateFolder, "eldk-logo-white.png", Image(300, 100));
        foreach (var (id, _) in speakers)
        {
            // 🔒 §768.16 — the fixture names the file through the PRODUCT's own function, so it
            // cannot pass with a name no writer produces. That is the §767 failure in test form.
            store.Put(PhotoFolder, SpeakerPhotoFileName.Build(id, ".jpg"), Image(400, 400));
        }
        return store;
    }

    private static async Task<Session> AddSessionAsync(
        CommunityHubDbContext db, int eventId, string title, params int[] speakerIds)
    {
        var session = new Session
        {
            EventId = eventId,
            SessionizeId = Guid.NewGuid().ToString("N"),
            Title = title,
            Type = SessionType.TechnicalSession,
            CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        foreach (var pid in speakerIds)
            db.SessionSpeakers.Add(new SessionSpeaker { SessionId = session.Id, ParticipantId = pid });
        await db.SaveChangesAsync();
        return session;
    }

    // ---- ONE row per session, keyed on the session alone --------------------------------

    [Fact]
    public async Task A_single_speaker_session_builds_ONE_png_keyed_on_the_session_with_no_participant()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var session = await AddSessionAsync(db, seed.EventId, "Running Azure at Night", seed.SpeakerOneId);

        var store = StoreWithPhotos((seed.SpeakerOneId, "Session Speaker One"));
        var (bundles, _, _) = NewSweep(db, store, SpOptions());

        var result = await bundles.BuildAsync(seed.EventId);

        Assert.Equal(1, result.SessionGraphics);

        var row = Assert.Single(await db.GraphicAssets
            .Where(g => g.Type == GraphicAssetType.Session && g.SessionId == session.Id)
            .ToListAsync());

        // The KEY carries the session and nothing else — no speaker in it.
        Assert.Equal($"session:{session.Id}", row.StableKey);
        Assert.Null(row.ParticipantId);
        Assert.Equal($"session-{session.Id}.png", row.FileName);
        // §784.12(a) — RELEASED ON SIGHT. This asserted Generated ("rendered, never auto-released")
        // until the operator retired the gate: a speaker who cannot see their own promo graphic
        // cannot promote the event.
        Assert.Equal(GraphicAssetStatus.Released, row.Status);
        Assert.NotNull(row.ReleasedAt);
        Assert.NotNull(row.InputHash);

        var write = Assert.Single(store.Writes, w => w.Path.StartsWith(SessionsFolder));
        Assert.Equal($"{SessionsFolder}/session-{session.Id}.png", write.Path);
        Assert.Equal("image/png", write.ContentType);
    }

    /// <summary>
    /// 🔒 §768.16 — during the rename the folder holds BOTH names for one speaker. The id-only file
    /// is what a writer produced most recently, so it must win — and it must win by RULE, not by
    /// whichever file Graph happened to list first.
    /// </summary>
    [Fact]
    public async Task The_id_only_photo_WINS_over_a_LEGACY_named_duplicate()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await AddSessionAsync(db, seed.EventId, "Two Names, One Speaker", seed.SpeakerOneId);

        var store = new FakeStore();
        store.Put(TemplateFolder, "Template.JPG", Image(1600, 1000));
        store.Put(TemplateFolder, "eldk-logo-white.png", Image(300, 100));
        // The LEGACY file is staged FIRST — under the old first-wins rule it would have been chosen.
        var legacy = $"speaker-photo-Session-Speaker-One-{seed.SpeakerOneId}.jpg";
        store.Put(PhotoFolder, legacy, Image(400, 400));
        var current = SpeakerPhotoFileName.Build(seed.SpeakerOneId, ".jpg");
        store.Put(PhotoFolder, current, Image(400, 400));

        var (bundles, _, _) = NewSweep(db, store, SpOptions());
        await bundles.BuildAsync(seed.EventId);

        Assert.Contains($"{PhotoFolder}/{current}", store.Downloads);
        Assert.DoesNotContain($"{PhotoFolder}/{legacy}", store.Downloads);
    }

    [Fact]
    public async Task Two_speakers_make_it_a_gif_with_one_frame_each()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var session = await AddSessionAsync(
            db, seed.EventId, "Two Heads", seed.SpeakerOneId, seed.SpeakerTwoId);

        var store = StoreWithPhotos(
            (seed.SpeakerOneId, "Session Speaker One"), (seed.SpeakerTwoId, "Session Speaker Two"));
        var (bundles, _, _) = NewSweep(db, store, SpOptions());

        await bundles.BuildAsync(seed.EventId);

        var row = Assert.Single(await db.GraphicAssets
            .Where(g => g.Type == GraphicAssetType.Session && g.SessionId == session.Id)
            .ToListAsync());
        Assert.Equal($"session-{session.Id}.gif", row.FileName);

        var write = Assert.Single(store.Writes, w => w.Path.StartsWith(SessionsFolder));
        Assert.Equal("image/gif", write.ContentType);
        using var gif = SixLabors.ImageSharp.Image.Load(write.Content);
        Assert.Equal(2, gif.Frames.Count);   // one frame per speaker
    }

    /// <summary>
    /// 🔑 The flip that makes the single-key model worth having: a second speaker joins and the
    /// SAME graphic becomes a GIF. One row, one subject, a new file name.
    /// </summary>
    [Fact]
    public async Task A_second_speaker_joining_updates_the_same_row_from_png_to_gif()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var session = await AddSessionAsync(db, seed.EventId, "Growing Session", seed.SpeakerOneId);

        var store = StoreWithPhotos(
            (seed.SpeakerOneId, "Session Speaker One"), (seed.SpeakerTwoId, "Session Speaker Two"));
        var (bundles, _, _) = NewSweep(db, store, SpOptions());

        await bundles.BuildAsync(seed.EventId);
        var before = await db.GraphicAssets.SingleAsync(g => g.SessionId == session.Id);
        var idBefore = before.Id;
        Assert.EndsWith(".png", before.FileName);

        // Speaker two joins the session.
        db.SessionSpeakers.Add(new SessionSpeaker
        {
            SessionId = session.Id, ParticipantId = seed.SpeakerTwoId,
        });
        await db.SaveChangesAsync();

        var second = await bundles.BuildAsync(seed.EventId);
        Assert.Equal(1, second.SessionGraphics);   // the line-up changed ⇒ it rebuilt

        var rows = await db.GraphicAssets.Where(g => g.SessionId == session.Id).ToListAsync();
        var after = Assert.Single(rows);           // NOT a second graphic
        Assert.Equal(idBefore, after.Id);
        Assert.EndsWith(".gif", after.FileName);
    }

    /// <summary>
    /// The hash guard: an untouched line-up must not re-render. Without this the sweep rewrites
    /// every session's artwork four times an hour and the "rebuilt" count means nothing.
    /// </summary>
    [Fact]
    public async Task An_unchanged_session_is_not_re_rendered_on_the_next_sweep()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await AddSessionAsync(db, seed.EventId, "Steady State", seed.SpeakerOneId);

        var store = StoreWithPhotos((seed.SpeakerOneId, "Session Speaker One"));
        var (bundles, _, _) = NewSweep(db, store, SpOptions());

        var first = await bundles.BuildAsync(seed.EventId);
        Assert.Equal(1, first.SessionGraphics);

        store.Writes.Clear();
        var second = await bundles.BuildAsync(seed.EventId);

        Assert.Equal(0, second.SessionGraphics);
        Assert.DoesNotContain(store.Writes, w => w.Path.StartsWith(SessionsFolder));
    }

    // ---- who can see it ----------------------------------------------------------------

    /// <summary>
    /// 🔒 The visibility widening §767 phase 2 forced: the graphic belongs to the SESSION, so it
    /// has to reach every speaker on it. Under the old <c>ParticipantId == me</c> rule they would
    /// all have seen nothing.
    ///
    /// <para>§784.12(a) changed WHEN, not WHO: the render is visible immediately, so the organizer
    /// release click this test used to make is gone. The ownership half — every speaker on the
    /// session, and nobody else — is the part that still has to hold, and it is why this test is
    /// not just "the status is Released".</para>
    /// </summary>
    [Fact]
    public async Task Every_speaker_on_the_session_sees_the_graphic_at_once_and_nobody_else_does()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var session = await AddSessionAsync(
            db, seed.EventId, "Shared Stage", seed.SpeakerOneId, seed.SpeakerTwoId);

        var store = StoreWithPhotos(
            (seed.SpeakerOneId, "Session Speaker One"), (seed.SpeakerTwoId, "Session Speaker Two"));
        var (bundles, graphics, _) = NewSweep(db, store, SpOptions());
        await bundles.BuildAsync(seed.EventId);

        var asset = await db.GraphicAssets.SingleAsync(g => g.SessionId == session.Id);

        // §784.12(a) — NO RELEASE CLICK HAPPENS HERE. The build alone is enough for both speakers
        // on the session to see it; the assertion that used to sit here was the opposite one
        // ("nothing is visible until an organizer releases it").
        Assert.Contains(
            await graphics.GetSpeakerVisibleAsync(seed.EventId, seed.SpeakerOneId),
            g => g.Id == asset.Id);
        Assert.Contains(
            await graphics.GetSpeakerVisibleAsync(seed.EventId, seed.SpeakerTwoId),
            g => g.Id == asset.Id);

        // A speaker who is NOT on that session sees nothing of it.
        Assert.DoesNotContain(
            await graphics.GetSpeakerVisibleAsync(seed.EventId, seed.MasterclassSpeakerId),
            g => g.Id == asset.Id);
    }

    /// <summary>
    /// §784.12(a) — the ONE rule that decides a new row's status, asserted directly because it is
    /// the whole decision. ⚠️ The SPONSOR exclusion is the load-bearing half: those graphics are
    /// internal-only and they feed <c>BrandingGraphicsProvider</c>, which gates on Released. If a
    /// later reader "finishes the job" by auto-releasing them too, the branding surfaces silently
    /// start picking up artwork nobody decided to publish.
    /// </summary>
    [Theory]
    [InlineData(GraphicAssetType.Session, GraphicAssetStatus.Released)]
    [InlineData(GraphicAssetType.Track, GraphicAssetStatus.Released)]
    [InlineData(GraphicAssetType.Speaker, GraphicAssetStatus.Released)]
    [InlineData(GraphicAssetType.Sponsor, GraphicAssetStatus.Generated)]
    public void The_initial_status_releases_speaker_facing_artwork_and_only_that(
        GraphicAssetType type, GraphicAssetStatus expected)
        => Assert.Equal(expected, GraphicsService.InitialStatusFor(type));

    // ---- §784.12(a): the gate the sweep used to respect is gone -------------------------

    /// <summary>
    /// 🔒 <b>The reversal, pinned.</b> This test asserted the opposite until 2026-08-04: that the
    /// sweep released PULLED artwork (placing the file was the curation) and left ENGINE-RENDERED
    /// artwork alone for the organizer's click.
    ///
    /// <para>Operator 2026-08-03 retired that distinction — generated files "should be published to
    /// speaker right away so they can see them". So the render is Released when it is written, and
    /// the bulk sweep's remaining job is the BACKLOG: rows that were sitting at Generated when the
    /// decision shipped. This test is deliberately kept (rather than deleted) so the next reader
    /// finds the reversal stated where the old rule was.</para>
    /// </summary>
    [Fact]
    public async Task The_render_is_released_on_sight_and_the_sweep_clears_the_backlog()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var session = await AddSessionAsync(db, seed.EventId, "Engine Rendered", seed.SpeakerOneId);

        var store = StoreWithPhotos((seed.SpeakerOneId, "Session Speaker One"));
        var (bundles, graphics, _) = NewSweep(db, store, SpOptions());
        await bundles.BuildAsync(seed.EventId);

        // The engine-rendered row needed NO sweep and NO click: it is already visible.
        var engineRow = await db.GraphicAssets.SingleAsync(g => g.StableKey == $"session:{session.Id}");
        Assert.Equal(GraphicAssetStatus.Released, engineRow.Status);
        Assert.NotNull(engineRow.ReleasedAt);

        // A BACKLOG row — what production held when this shipped: written under the old gate and
        // still sitting at Generated. Nothing new creates one of these.
        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = seed.EventId,
            Type = GraphicAssetType.Session,
            StableKey = "session:999:speaker:" + seed.SpeakerTwoId,
            SessionId = session.Id,
            ParticipantId = seed.SpeakerTwoId,
            Status = GraphicAssetStatus.Generated,
            InputHash = null,
        });
        await db.SaveChangesAsync();

        var released = await graphics.ReleaseAllGeneratedAsync(
            seed.EventId, "system (SharePoint sync)", includeEngineRendered: true);

        Assert.Equal(1, released.Count);   // the backlog row — the engine row was never Generated
        Assert.Equal(
            GraphicAssetStatus.Released,
            (await db.GraphicAssets.SingleAsync(g => g.ParticipantId == seed.SpeakerTwoId
                                                     && g.SessionId == session.Id)).Status);

        // And it is a no-op the second time round — the steady state finds nothing to do.
        Assert.Equal(0, (await graphics.ReleaseAllGeneratedAsync(
            seed.EventId, "system (SharePoint sync)", includeEngineRendered: true)).Count);
    }

    /// <summary>
    /// 🔒 A human's artwork wins. A session the operator has already uploaded a file for is not
    /// given a machine-made rival sitting next to it.
    /// </summary>
    [Fact]
    public async Task A_session_with_operator_uploaded_artwork_is_left_alone()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var session = await AddSessionAsync(db, seed.EventId, "His Own Artwork", seed.SpeakerOneId);

        // What the SharePoint pull writes for an uploaded file: per-speaker, no input hash.
        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = seed.EventId,
            Type = GraphicAssetType.Session,
            StableKey = GraphicStableKey.ForSession(session.Id, seed.SpeakerOneId),
            SessionId = session.Id,
            ParticipantId = seed.SpeakerOneId,
            Status = GraphicAssetStatus.Generated,
        });
        await db.SaveChangesAsync();

        var store = StoreWithPhotos((seed.SpeakerOneId, "Session Speaker One"));
        var (bundles, _, _) = NewSweep(db, store, SpOptions());

        var result = await bundles.BuildAsync(seed.EventId);

        Assert.Equal(0, result.SessionGraphics);
        Assert.Equal(1, result.SessionsLeftToUpload);   // counted, never silent
        Assert.False(await db.GraphicAssets.AnyAsync(g => g.StableKey == $"session:{session.Id}"));
    }

    // ---- sponsors ----------------------------------------------------------------------

    [Fact]
    public async Task Each_sponsor_gets_one_png_keyed_on_the_company()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var sponsor = await db.SponsorInfos.FirstAsync(s => s.EventId == seed.EventId);
        sponsor.Status = SponsorStatus.Active;
        // The sweep matches a logo file by the COMPANY NAME — the seed carries only the id.
        sponsor.CompanyName = ScenarioSeed.SponsorPublicName;
        await db.SaveChangesAsync();

        var store = StoreWithPhotos();
        // The §768.8 sponsor-upload convention: {sponsor}-logo-web-{N}.png, newest version wins.
        // Built through the SAME helper the upload writers use, so this scenario cannot pass with a
        // name the product would never actually produce.
        store.Put(
            SponsorLogoFolder,
            CommunityHub.Uploads.SponsorUploadNaming.Build("some", sponsor.CompanyName, 1, ".png"),
            Image(300, 120));
        store.Put(
            SponsorLogoFolder,
            CommunityHub.Uploads.SponsorUploadNaming.Build("some", sponsor.CompanyName, 2, ".png"),
            Image(300, 120));

        var (bundles, _, _) = NewSweep(db, store, SpOptions(withSponsorLogos: true));

        var result = await bundles.BuildAsync(seed.EventId);

        Assert.Equal(1, result.SponsorGraphics);
        var row = await db.GraphicAssets.SingleAsync(
            g => g.Type == GraphicAssetType.Sponsor
                 && g.StableKey == GraphicStableKey.ForSponsor(sponsor.SponsorCompanyId!));
        Assert.Equal($"sponsor-{sponsor.SponsorCompanyId}.png", row.FileName);
        // INTERNAL-ONLY: a sponsor graphic never reaches a speaker's page.
        Assert.DoesNotContain(
            await db.GraphicsVisibleToSpeaker(seed.EventId, seed.SpeakerOneId).ToListAsync(),
            g => g.Id == row.Id);
    }

    // ---- fakes -------------------------------------------------------------------------

    /// <summary>A SharePoint store that both READS folders and records writes.</summary>
    private sealed class FakeStore : ISharePointFileStore
    {
        private readonly Dictionary<string, List<SharePointFileRef>> _folders = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _byItemId = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Path, byte[] Content, string ContentType)> Writes { get; } = new();

        public void Put(string folder, string fileName, byte[] content)
        {
            var itemId = $"{folder}/{fileName}";
            if (!_folders.TryGetValue(folder, out var list))
                _folders[folder] = list = new List<SharePointFileRef>();
            list.Add(new SharePointFileRef(itemId, fileName, null));
            _byItemId[itemId] = content;
        }

        public bool CanRead => true;
        public bool CanStore => true;

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(
            string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(
                _folders.TryGetValue(relativeFolder, out var files)
                    ? files
                    : Array.Empty<SharePointFileRef>());

        /// <summary>Which items were actually READ — the only way to assert WHICH photo was used.</summary>
        public List<string> Downloads { get; } = new();

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default)
        {
            Downloads.Add(itemId);
            return Task.FromResult(_byItemId.TryGetValue(itemId, out var bytes) ? bytes : null);
        }

        public Task<StoredFile> StoreAsync(
            string relativePath, byte[] content, string contentType, CancellationToken ct = default)
        {
            Writes.Add((relativePath, content, contentType));
            return Task.FromResult(new StoredFile(
                relativePath, $"https://store.example.test/{relativePath}", "item-" + relativePath));
        }

        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;

        public Task<StoredFile> UploadToFolderAsync(
            string relativeFolder, string fileName, byte[] content, string contentType,
            CancellationToken ct = default)
        {
            // §768: generated artwork now writes through THIS method (a drive-relative folder)
            // rather than StoreAsync — so it must RECORD, or every write assertion quietly sees
            // nothing and the test passes for the wrong reason.
            var path = $"{relativeFolder}/{fileName}";
            Writes.Add((path, content, contentType));
            return Task.FromResult(new StoredFile(
                path, $"https://store.example.test/{path}", "item-" + path));
        }

        public Task DeleteFromFolderAsync(
            string relativeFolder, string fileName, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakePictureFetcher : ISpeakerPictureFetcher
    {
        public FakePictureFetcher(byte[]? _) { }
        public Task<FetchedImage?> FetchAsync(string? pictureUrl, CancellationToken ct = default) =>
            Task.FromResult<FetchedImage?>(null);
    }
}
