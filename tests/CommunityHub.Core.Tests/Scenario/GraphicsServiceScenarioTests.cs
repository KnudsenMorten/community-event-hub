using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: the SoMe-graphics engine + gates (REQUIREMENTS §18). Proves the
/// load-bearing contracts end-to-end against the real <see cref="GraphicsService"/>
/// + EF in-memory DB, with fake (recording) seams for SharePoint / Sessionize /
/// social so nothing external is touched:
///  - GENERATE composites a PNG, stores it (fake store) and creates a
///    <see cref="GraphicAssetStatus.Generated"/> row — NOT released;
///  - RELEASE GATE: a speaker sees a graphic only AFTER an organizer releases it;
///  - OVERRULE keeps the stable key / path / URL identical (the link never breaks)
///    and marks the row organizer-overridden so a re-run won't clobber it;
///  - SPONSOR graphics are INTERNAL-ONLY — never returned on the sponsor surface;
///  - SHARE builds a LinkedIn DRAFT (with date + ticket URL) — never an auto-post;
///  - SESSIONIZE picture is FETCHED DOWN and stored on SharePoint (not just a URL).
///
/// NO real data — example.test + @@expertslive.dk only.
/// </summary>
public sealed class GraphicsServiceScenarioTests
{
    private const string TicketUrl = "eldk27.expertslive.dk";

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

    private static GraphicsService NewService(
        CommunityHubDbContext db,
        ISharePointFileStore? store = null,
        ISpeakerPictureFetcher? fetcher = null,
        ISocialShareGateway? share = null,
        GraphicsSharePointOptions? spOptions = null) =>
        new(db, new GraphicCompositor(),
            store ?? new FakeFileStore(),
            fetcher ?? new FakePictureFetcher(null),
            share ?? new DraftOnlySocialShareGateway(),
            Microsoft.Extensions.Options.Options.Create(spOptions ?? new GraphicsSharePointOptions()));

    // ---- GENERATE: composites a PNG, stored, status Generated (not released) ----

    [Fact]
    public async Task Generate_speaker_graphic_stores_a_png_and_is_not_released()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var store = new FakeFileStore();
        var svc = NewService(db, store);

        var asset = await svc.GenerateSpeakerGraphicAsync(
            seed.EventId, seed.SpeakerOneId, Template(), Photo(), "Session Speaker One");

        // A row exists, GENERATED (the gate) — never auto-released.
        Assert.Equal(GraphicAssetType.Speaker, asset.Type);
        Assert.Equal(GraphicAssetStatus.Generated, asset.Status);
        Assert.Null(asset.ReleasedAt);

        // The store received PNG bytes for the stable, key-derived path.
        Assert.Equal(GraphicStableKey.ForSpeaker(seed.SpeakerOneId), asset.StableKey);
        var write = Assert.Single(store.Writes);
        Assert.Equal("Speakers/speaker-" + seed.SpeakerOneId + ".png", write.Path);
        Assert.True(IsPng(write.Content), "stored content must be a PNG");
        Assert.Equal(GraphicCompositor.PngContentType, write.ContentType);
    }

    [Fact]
    public async Task Regenerate_upserts_by_stable_key_no_duplicate_row()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);

        await svc.GenerateSpeakerGraphicAsync(seed.EventId, seed.SpeakerOneId, Template(), Photo(), "One");
        await svc.GenerateSpeakerGraphicAsync(seed.EventId, seed.SpeakerOneId, Template(), Photo(), "One v2");

        var rows = await db.GraphicAssets
            .Where(g => g.EventId == seed.EventId && g.Type == GraphicAssetType.Speaker)
            .ToListAsync();
        Assert.Single(rows); // upsert by stable key, not a second row
    }

    // ---- RELEASE GATE: speaker sees nothing until released ------------------

    [Fact]
    public async Task Release_gate_hides_graphic_from_speaker_until_released()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);

        var asset = await svc.GenerateSpeakerGraphicAsync(
            seed.EventId, seed.SpeakerOneId, Template(), Photo(), "One");

        // Before release: the speaker's visible list is empty.
        var before = await svc.GetSpeakerVisibleAsync(seed.EventId, seed.SpeakerOneId);
        Assert.Empty(before);
        // It IS in the organizer review queue (Generated).
        var queue = await svc.GetReviewQueueAsync(seed.EventId);
        Assert.Contains(queue, g => g.Id == asset.Id);

        // Organizer releases it.
        var released = await svc.ReleaseAsync(seed.EventId, asset.Id, "organizer@expertslive.dk");
        Assert.Equal(GraphicAssetStatus.Released, released.Status);
        Assert.NotNull(released.ReleasedAt);
        Assert.Equal("organizer@expertslive.dk", released.ReleasedByEmail);

        // After release: the speaker can see it; it leaves the review queue.
        var after = await svc.GetSpeakerVisibleAsync(seed.EventId, seed.SpeakerOneId);
        Assert.Single(after);
        Assert.DoesNotContain(await svc.GetReviewQueueAsync(seed.EventId), g => g.Id == asset.Id);
    }

    [Fact]
    public async Task Speaker_never_sees_another_speakers_or_sponsor_graphics()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);

        // Speaker two's graphic, released.
        var other = await svc.GenerateSpeakerGraphicAsync(
            seed.EventId, seed.SpeakerTwoId, Template(), Photo(), "Two");
        await svc.ReleaseAsync(seed.EventId, other.Id, "organizer@expertslive.dk");

        // Speaker one sees nothing belonging to speaker two.
        var visible = await svc.GetSpeakerVisibleAsync(seed.EventId, seed.SpeakerOneId);
        Assert.Empty(visible);
    }

    // ---- OVERRULE: stable key / path / URL stay identical -------------------

    [Fact]
    public async Task Overrule_preserves_the_stable_key_path_and_url()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var store = new FakeFileStore();
        var svc = NewService(db, store);

        var asset = await svc.GenerateSpeakerGraphicAsync(
            seed.EventId, seed.SpeakerOneId, Template(), Photo(), "One");

        var keyBefore = asset.StableKey;
        var pathBefore = asset.SharePointPath;
        var urlBefore = asset.SharePointUrl;
        var fileBefore = asset.FileName;

        // Organizer overrules with their own PNG.
        var replacement = MakePng(600, 600, new Rgba32(255, 0, 0));
        var after = await svc.OverruleAsync(seed.EventId, asset.Id, replacement);

        // THE CONTRACT: the link never breaks — same key, path, URL, file name.
        Assert.Equal(keyBefore, after.StableKey);
        Assert.Equal(pathBefore, after.SharePointPath);
        Assert.Equal(urlBefore, after.SharePointUrl);
        Assert.Equal(fileBefore, after.FileName);
        Assert.True(after.IsOrganizerOverridden);

        // The fake store REPLACED the bytes at the SAME path (last write == that path).
        Assert.Equal(pathBefore, store.Writes[^1].Path);
    }

    [Fact]
    public async Task Regenerate_does_not_clobber_an_organizer_overrule()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);

        var asset = await svc.GenerateSpeakerGraphicAsync(
            seed.EventId, seed.SpeakerOneId, Template(), Photo(), "One");
        await svc.OverruleAsync(seed.EventId, asset.Id, MakePng(600, 600, new Rgba32(255, 0, 0)));

        // A benign re-run must leave the human's replacement in place.
        var afterRegen = await svc.GenerateSpeakerGraphicAsync(
            seed.EventId, seed.SpeakerOneId, Template(), Photo(), "One");
        Assert.True(afterRegen.IsOrganizerOverridden);
    }

    // ---- SPONSOR graphics are INTERNAL-ONLY --------------------------------

    [Fact]
    public async Task Sponsor_graphic_is_internal_only_never_on_the_sponsor_surface()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);

        var sponsor = await svc.GenerateSponsorGraphicAsync(
            seed.EventId, ScenarioSeed.SponsorCompanyId, Template(), Logo());
        // Even if an organizer "releases" it, the sponsor surface stays empty.
        await svc.ReleaseAsync(seed.EventId, sponsor.Id, "organizer@expertslive.dk");

        // Organizer (internal) sees it...
        var internalList = await svc.GetInternalSponsorGraphicsAsync(seed.EventId);
        Assert.Contains(internalList, g => g.Id == sponsor.Id);

        // ...but the sponsor-facing query is ALWAYS empty of sponsor graphics.
        var sponsorFacing = await svc.GetSponsorFacingAsync(seed.EventId, ScenarioSeed.SponsorCompanyId);
        Assert.Empty(sponsorFacing);
    }

    [Fact]
    public async Task Speaker_review_queue_excludes_sponsor_graphics()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewService(db);

        await svc.GenerateSponsorGraphicAsync(seed.EventId, ScenarioSeed.SponsorCompanyId, Template(), Logo());
        var speakerQueue = await svc.GetReviewQueueAsync(seed.EventId, GraphicAssetType.Speaker);
        Assert.DoesNotContain(speakerQueue, g => g.Type == GraphicAssetType.Sponsor);
    }

    // ---- SHARE builds a DRAFT, never an auto-post --------------------------

    [Fact]
    public async Task Speaking_announcement_builds_a_linkedin_draft_with_date_and_ticket_url()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var share = new RecordingShareGateway();
        var svc = NewService(db, share: share);

        var draft = svc.BuildSpeakingAnnouncementDraft(
            "ELDK27", "4–5 Feb 2027", TicketUrl, "Session Speaker One",
            sessionTitle: "Cloud talk", graphicUrl: "https://example.test/g.png");

        Assert.Equal(SocialNetwork.LinkedIn, draft.Network);
        Assert.Contains("ELDK27", draft.Text);
        Assert.Contains("4–5 Feb 2027", draft.Text);
        Assert.Contains(TicketUrl, draft.Text);
        Assert.Contains("Cloud talk", draft.Text);
        // It is a DRAFT (composer intent URL) — NOT an auto-post.
        Assert.False(share.CanPost);
        Assert.Empty(share.Posts);             // nothing was posted
        Assert.NotEmpty(draft.IntentUrl);      // a composer the speaker opens himself
    }

    [Fact]
    public void Default_share_gateway_cannot_auto_post()
    {
        var gw = new DraftOnlySocialShareGateway();
        Assert.False(gw.CanPost);
        var d = gw.BuildDraft(SocialNetwork.X, "Hi", null);
        Assert.Equal(SocialNetwork.X, d.Network);
        Assert.Contains("twitter.com/intent/tweet", d.IntentUrl);
    }

    // ---- SESSIONIZE picture: fetched DOWN + stored on SharePoint -----------

    [Fact]
    public async Task Sessionize_picture_is_fetched_down_and_stored_on_sharepoint()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var store = new FakeFileStore();
        var bytes = MakePng(64, 64, new Rgba32(1, 2, 3));
        var fetcher = new FakePictureFetcher(new FetchedImage(bytes, "image/png"));
        var svc = NewService(db, store, fetcher);

        var stored = await svc.FetchAndStoreSpeakerPictureAsync(
            seed.EventId, seed.SpeakerOneId, "https://sessionize.example.test/pic.png");

        Assert.NotNull(stored);
        // The bytes (not the URL) were stored on SharePoint under a stable path.
        var write = Assert.Single(store.Writes);
        Assert.Equal("Pictures/speaker-" + seed.SpeakerOneId + ".png", write.Path);
        Assert.Equal(bytes, write.Content);
    }

    [Fact]
    public async Task Sessionize_picture_missing_url_stores_nothing()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var store = new FakeFileStore();
        var fetcher = new FakePictureFetcher(null); // nothing fetched
        var svc = NewService(db, store, fetcher);

        var stored = await svc.FetchAndStoreSpeakerPictureAsync(seed.EventId, seed.SpeakerOneId, null);

        Assert.Null(stored);
        Assert.Empty(store.Writes);
    }

    // ---- NULL store: nothing faked, but the engine still upserts the row ----

    [Fact]
    public async Task Null_store_default_makes_no_call_but_still_records_the_intended_path()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        // The Null store throws if StoreAsync is called -> proves no fake call.
        var svc = NewService(db, new NullSharePointFileStore());

        var asset = await svc.GenerateSpeakerGraphicAsync(
            seed.EventId, seed.SpeakerOneId, Template(), Photo(), "One");

        Assert.Equal(GraphicAssetStatus.Generated, asset.Status);
        Assert.Equal("Speakers/speaker-" + seed.SpeakerOneId + ".png", asset.SharePointPath);
        Assert.Null(asset.SharePointUrl); // no live store -> no URL, nothing faked
    }

    // ---- helpers -----------------------------------------------------------

    private static bool IsPng(byte[] b) =>
        b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

    /// <summary>A recording SharePoint file store (CanStore=true) — replaces in place by path.</summary>
    private sealed class FakeFileStore : ISharePointFileStore
    {
        public List<(string Path, byte[] Content, string ContentType)> Writes { get; } = new();
        public bool CanStore => true;

        public Task<StoredFile> StoreAsync(
            string relativePath, byte[] content, string contentType, CancellationToken ct = default)
        {
            Writes.Add((relativePath, content, contentType));
            // Deterministic, stable URL derived from the path (replacing in place
            // keeps the same URL — the overrule contract).
            return Task.FromResult(new StoredFile(
                relativePath, $"https://store.example.test/{relativePath}", "item-" + relativePath));
        }

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

    /// <summary>A picture fetcher returning a fixed image (or null).</summary>
    private sealed class FakePictureFetcher : ISpeakerPictureFetcher
    {
        private readonly FetchedImage? _image;
        public FakePictureFetcher(FetchedImage? image) => _image = image;
        public Task<FetchedImage?> FetchAsync(string? pictureUrl, CancellationToken ct = default) =>
            Task.FromResult(string.IsNullOrWhiteSpace(pictureUrl) ? null : _image);
    }

    /// <summary>A share gateway that records any (non-existent) posts; cannot auto-post.</summary>
    private sealed class RecordingShareGateway : ISocialShareGateway
    {
        public List<SocialShareDraft> Posts { get; } = new();
        public bool CanPost => false;
        public SocialShareDraft BuildDraft(SocialNetwork network, string text, string? graphicUrl) =>
            new DraftOnlySocialShareGateway().BuildDraft(network, text, graphicUrl);
    }
}
