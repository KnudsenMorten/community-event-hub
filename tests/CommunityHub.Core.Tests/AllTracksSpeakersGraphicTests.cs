using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1384 — the ALL TRACKS · ALL SPEAKERS overview GIF: a NEW graphic category
/// (<see cref="GraphicAssetType.AllTracksSpeakers"/>) built by the existing §767 sweep from the SAME
/// per-track line-ups as the track GIFs, and served at a stable public URL.
/// </summary>
/// <remarks>
/// Renderer (frames, size, layout math), <see cref="GraphicsService.GenerateAllTracksSpeakersAsync"/>
/// (store, override, hash skip, public lookup) and the <see cref="SoMeBundleBuildService"/> wiring.
/// FAKE names only (example.test); no network, no SQL.
/// </remarks>
public sealed class AllTracksSpeakersGraphicTests
{
    private static string P(string key) => TestDocLibrary.PathFor(key);

    private static readonly string TemplateFolder =
        P(CommunityHub.Core.Integrations.DocLibrary.DocLibraryPaths.EventGraphicsTemplate);
    private static readonly string PhotoFolder =
        P(CommunityHub.Core.Integrations.DocLibrary.DocLibraryPaths.SpeakerPhotos);
    private static readonly string TracksFolder =
        P(CommunityHub.Core.Integrations.DocLibrary.DocLibraryPaths.SpeakerTrackGraphics);

    private static byte[] Png(int w, int h)
    {
        using var img = new Image<Rgba32>(w, h, new Rgba32(30, 90, 140));
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static TrackOverviewTrack Track(string name, int speakers, bool photos = true) =>
        new(name, Enumerable.Range(1, speakers)
            .Select(i => (photos ? Png(200, 200) : (byte[]?)null, $"Speaker{i} Example"))
            .ToList());

    // ---- renderer -----------------------------------------------------------------------

    [Fact]
    public void The_overview_has_one_frame_per_track_at_card_size_and_loops()
    {
        var gif = new SoMeGraphicRenderer { EventName = "Community Events Demo 2027" }
            .RenderTrackOverviewGif(new[] { Track("Security", 3), Track("Identity", 7), Track("Azure", 14) });

        using var img = Image.Load<Rgba32>(gif);
        Assert.Equal(3, img.Frames.Count);
        Assert.Equal(SoMeGraphicRenderer.Width, img.Width);
        Assert.Equal(SoMeGraphicRenderer.Height, img.Height);
        Assert.Equal(SoMeGraphicRenderer.OverviewFrameDelayCentiseconds,
            img.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay);
        Assert.Equal(0, img.Metadata.GetGifMetadata().RepeatCount);
    }

    [Fact]
    public void The_600_wide_version_keeps_every_frame()
    {
        var gif = new SoMeGraphicRenderer()
            .RenderTrackOverviewGif(new[] { Track("Security", 2), Track("Intune", 4) });

        var small = SoMeGraphicRenderer.ResizeGif(gif, 600);

        using var img = Image.Load<Rgba32>(small);
        Assert.Equal(2, img.Frames.Count);
        Assert.Equal(600, img.Width);
        Assert.InRange(img.Height, 312, 314);   // 627 / 2, aspect kept
        Assert.True(small.Length < 500_000, $"600 px overview is {small.Length} bytes");
        Assert.True(gif.Length < 2_000_000, $"1200 px overview is {gif.Length} bytes");
    }

    [Fact]
    public void A_speaker_without_a_photo_gets_initials_not_an_error()
    {
        var gif = new SoMeGraphicRenderer()
            .RenderTrackOverviewGif(new[] { Track("Security", 4, photos: false) });

        using var img = Image.Load<Rgba32>(gif);
        Assert.Single(img.Frames);
    }

    [Fact]
    public void A_track_with_no_speakers_is_not_a_frame()
    {
        var gif = new SoMeGraphicRenderer()
            .RenderTrackOverviewGif(new[] { Track("Security", 2), new TrackOverviewTrack("Empty", Array.Empty<(byte[]?, string)>()) });

        using var img = Image.Load<Rgba32>(gif);
        Assert.Single(img.Frames);
    }

    [Theory]
    [InlineData(1, 1, 118f)]
    [InlineData(6, 1, 118f)]
    [InlineData(7, 2, 118f)]
    [InlineData(12, 2, 118f)]
    [InlineData(13, 2, 118f)]    // §1384 follow-up: Security (13) is two rows of full-size faces now
    [InlineData(19, 2, 92f)]     // Intune: 10 cols, cell 112 ⇒ face 92
    [InlineData(20, 2, 92f)]
    [InlineData(21, 3, 76f)]     // three rows only above 20
    [InlineData(24, 3, 76f)]
    public void Rows_and_face_size_follow_the_six_and_twenty_rule(int n, int rows, float diameter)
    {
        var layout = SoMeGraphicRenderer.OverviewLayout(n);
        Assert.Equal(rows, layout.Rows);
        Assert.Equal((int)Math.Ceiling(n / (double)rows), layout.Cols);
        Assert.Equal(diameter, layout.Diameter);
        Assert.Equal(n, layout.Centres.Count);
    }

    [Fact]
    public void No_layout_up_to_30_speakers_reaches_the_footer_or_overlaps_a_face()
    {
        for (var n = 1; n <= 30; n++)
        {
            var layout = SoMeGraphicRenderer.OverviewLayout(n);
            Assert.True(layout.GridTop >= SoMeGraphicRenderer.OverviewGridTop, $"n={n}: grid starts above the subtitle");
            Assert.True(layout.GridBottom <= SoMeGraphicRenderer.OverviewFooterTop, $"n={n}: grid reaches the footer");
            // the last name line of the lowest row
            var lowest = layout.Centres.Max(c => c.Y) + (layout.Diameter / 2f) + 9f + (2 * layout.LineHeight);
            Assert.True(lowest <= SoMeGraphicRenderer.OverviewFooterTop, $"n={n}: names reach the footer");
            Assert.True(layout.PitchX >= layout.Diameter + 6f, $"n={n}: faces overlap horizontally");
            Assert.All(layout.Centres, c =>
            {
                Assert.True(c.X - (layout.Diameter / 2f) >= 0 && c.X + (layout.Diameter / 2f) <= SoMeGraphicRenderer.Width,
                    $"n={n}: a face leaves the canvas");
            });
        }
    }

    /// <summary>
    /// §1384 follow-up — a name line never runs into its neighbour: the font for the cell (15 / 13 /
    /// 12 px), shrunk to fit, and cut with "…" only when even the smallest size is too wide.
    /// </summary>
    [Theory]
    [InlineData(13, 15f)]
    [InlineData(19, 13f)]
    [InlineData(31, 12f)]   // 11 cols, cell < 110
    public void Every_name_line_fits_its_cell(int n, float expectedFont)
    {
        var layout = SoMeGraphicRenderer.OverviewLayout(n);
        Assert.Equal(expectedFont, layout.NameFontSize);
        var max = SoMeGraphicRenderer.OverviewNameWidth(layout);
        Assert.True(max < layout.PitchX, "a name may use the whole cell, never the gap to the next");

        foreach (var line in new[] { "Toms", "Knostenbergs", "Guillemin", "Wolfeschlegelsteinhausenbergerdorff", "de la Cruz-Montgomery" })
        {
            var (text, size) = SoMeGraphicRenderer.FitNameLine(line, layout.NameFontSize, max);
            Assert.True(SoMeGraphicRenderer.MeasureBold(text, size) <= max,
                $"n={n}: '{line}' printed as '{text}' at {size}px is wider than {max}px");
            Assert.True(size <= layout.NameFontSize && size >= SoMeGraphicRenderer.OverviewMinNameFont);
        }

        // An ordinary surname keeps its full text.
        Assert.Equal("Knostenbergs", SoMeGraphicRenderer.FitNameLine("Knostenbergs", layout.NameFontSize, max).Text);
    }

    [Theory]
    [InlineData("AI for Makers (Copilot & Agents)", "AI for Makers")]
    [InlineData("Security", "Security")]
    [InlineData("  Microsoft 365  ", "Microsoft 365")]
    public void The_title_drops_a_trailing_parenthetical(string track, string expected) =>
        Assert.Equal(expected, SoMeGraphicRenderer.OverviewTitle(track));

    [Fact]
    public void Speakers_with_a_photo_come_first_then_alphabetical()
    {
        var photo = Png(10, 10);
        var ordered = SoMeGraphicRenderer.OverviewOrder(new (byte[]?, string)[]
        {
            (null, "Anna Nophoto"), (photo, "Zoe Photo"), (photo, "Bo Photo"), (null, "Carl Nophoto"),
        });

        Assert.Equal(new[] { "Bo Photo", "Zoe Photo", "Anna Nophoto", "Carl Nophoto" }, ordered.Select(s => s.Name));
    }

    [Fact]
    public void Track_order_is_the_fixed_list_then_alphabetical()
    {
        var ordered = SoMeBundleBuildService.AllTracksOrder(new[]
        {
            "Zeta Extras", "AI for Engineers (Build Your Own AI)", "Azure", "Data Compliance", "Intune",
            "AI for Makers (Copilot & Agents)", "Identity", "Microsoft 365", "Security", "Alpha Extras",
        });

        Assert.Equal(new[]
        {
            "Security", "Identity", "Intune", "Azure", "Microsoft 365", "Data Compliance",
            "AI for Makers (Copilot & Agents)", "AI for Engineers (Build Your Own AI)", "Alpha Extras", "Zeta Extras",
        }, ordered);
    }

    // ---- GraphicsService ----------------------------------------------------------------

    private static (GraphicsService Graphics, FakeStore Store) NewGraphics(CommunityHubDbContext db, FakeStore? store = null)
    {
        store ??= new FakeStore();
        var graphics = new GraphicsService(
            db, new GraphicCompositor(), store, new NoPictureFetcher(),
            new DraftOnlySocialShareGateway(), Options.Create(new GraphicsSharePointOptions()),
            TestDocLibrary.Resolver());
        return (graphics, store);
    }

    [Fact]
    public async Task Generate_stores_one_gif_row_of_the_new_category_beside_the_track_gifs()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var (graphics, store) = NewGraphics(db);

        var outcome = await graphics.GenerateAllTracksSpeakersAsync(
            seed.EventId, new[] { Track("Security", 2), Track("Azure", 3) }, "hash-1");

        Assert.True(outcome.Rendered);
        var row = await db.GraphicAssets.AsNoTracking().SingleAsync();
        Assert.Equal(GraphicAssetType.AllTracksSpeakers, row.Type);
        Assert.Equal("tracks:all-speakers", row.StableKey);
        Assert.Equal("tracks-all-speakers.gif", row.FileName);
        Assert.Null(row.ParticipantId);
        Assert.Null(row.SessionId);
        Assert.Equal(GraphicAssetStatus.Released, row.Status);
        Assert.Equal("hash-1", row.InputHash);

        var write = Assert.Single(store.Writes);
        Assert.Equal($"{TracksFolder}/tracks-all-speakers.gif", write.Path);
        Assert.Equal("image/gif", write.ContentType);
        using var img = Image.Load<Rgba32>(write.Content);
        Assert.Equal(2, img.Frames.Count);
    }

    [Fact]
    public async Task An_unchanged_hash_does_not_re_render()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var (graphics, store) = NewGraphics(db);

        await graphics.GenerateAllTracksSpeakersAsync(seed.EventId, new[] { Track("Security", 2) }, "hash-1");
        var again = await graphics.GenerateAllTracksSpeakersAsync(seed.EventId, new[] { Track("Security", 2) }, "hash-1");
        var changed = await graphics.GenerateAllTracksSpeakersAsync(seed.EventId, new[] { Track("Security", 3) }, "hash-2");

        Assert.False(again.Rendered);
        Assert.True(changed.Rendered);
        Assert.Equal(2, store.Writes.Count);
        Assert.Single(await db.GraphicAssets.ToListAsync());
    }

    [Fact]
    public async Task An_organizer_replacement_is_left_alone()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var (graphics, store) = NewGraphics(db);

        var first = await graphics.GenerateAllTracksSpeakersAsync(seed.EventId, new[] { Track("Security", 2) }, "hash-1");
        await graphics.OverruleAsync(seed.EventId, first.Asset.Id, Png(50, 50));
        var writesAfterOverrule = store.Writes.Count;

        var next = await graphics.GenerateAllTracksSpeakersAsync(seed.EventId, new[] { Track("Security", 5) }, "hash-2");

        Assert.False(next.Rendered);
        Assert.Equal(writesAfterOverrule, store.Writes.Count);
        // The overrule wrote the replacement into the SAME folder, not a stray one.
        Assert.Equal($"{TracksFolder}/tracks-all-speakers.gif", store.Writes[^1].Path);
    }

    [Fact]
    public async Task The_public_lookup_needs_a_released_stored_row_in_the_active_edition()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var (graphics, _) = NewGraphics(db);

        Assert.Null(await graphics.FindPublicAllTracksSpeakersAsync());

        await graphics.GenerateAllTracksSpeakersAsync(seed.EventId, new[] { Track("Security", 2) }, "hash-1");
        Assert.NotNull(await graphics.FindPublicAllTracksSpeakersAsync());

        var row = await db.GraphicAssets.SingleAsync();
        row.Status = GraphicAssetStatus.Generated;   // un-released by an organizer
        await db.SaveChangesAsync();
        Assert.Null(await graphics.FindPublicAllTracksSpeakersAsync());
    }

    // ---- SoMeBundleBuildService wiring --------------------------------------------------

    private static async Task<Session> AddTrackSessionAsync(
        CommunityHubDbContext db, int eventId, string track, params int[] speakerIds)
    {
        var session = new Session
        {
            EventId = eventId, SessionizeId = Guid.NewGuid().ToString("N"), Title = $"{track} talk",
            Track = track, Type = SessionType.TechnicalSession, CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        foreach (var pid in speakerIds)
            db.SessionSpeakers.Add(new SessionSpeaker { SessionId = session.Id, ParticipantId = pid });
        await db.SaveChangesAsync();
        return session;
    }

    /// <summary>ScenarioSeed flags every row as a test user; these speakers are real (see SoMePhase2GraphicsScenarioTests).</summary>
    private static async Task MarkRealAsync(CommunityHubDbContext db, int eventId, params int[] testUserIds)
    {
        await db.Participants.Where(p => p.EventId == eventId)
            .ForEachAsync(p => p.IsTestUser = testUserIds.Contains(p.Id));
        await db.SaveChangesAsync();
    }

    private static FakeStore StoreWith(params int[] photoIds)
    {
        var store = new FakeStore();
        store.Put(TemplateFolder, "Template.JPG", Png(1600, 1000));
        foreach (var id in photoIds) store.Put(PhotoFolder, SpeakerPhotoFileName.Build(id, ".jpg"), Png(300, 300));
        return store;
    }

    private static SoMeBundleBuildService NewSweep(CommunityHubDbContext db, FakeStore store)
    {
        var (graphics, _) = NewGraphics(db, store);
        return new SoMeBundleBuildService(
            db, graphics, store, Options.Create(new GraphicsSharePointOptions()), TestDocLibrary.Resolver(),
            NullLogger<SoMeBundleBuildService>.Instance);
    }

    private static byte[] OverviewWrite(FakeStore store) =>
        store.Writes.Last(w => w.Path.EndsWith("/tracks-all-speakers.gif", StringComparison.Ordinal)).Content;

    [Fact]
    public async Task The_sweep_builds_the_overview_from_the_track_line_ups_and_skips_an_unchanged_one()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await AddTrackSessionAsync(db, seed.EventId, "Azure", seed.SpeakerOneId);
        await AddTrackSessionAsync(db, seed.EventId, "Security", seed.SpeakerTwoId, seed.SpeakerOneId);
        await MarkRealAsync(db, seed.EventId);
        var store = StoreWith(seed.SpeakerOneId, seed.SpeakerTwoId);
        var sweep = NewSweep(db, store);

        var first = await sweep.BuildAsync(seed.EventId);
        var second = await sweep.BuildAsync(seed.EventId);

        Assert.Equal(1, first.AllTracksOverview);
        Assert.Equal(0, second.AllTracksOverview);
        Assert.Equal(2, first.TrackBundles);   // the per-track GIFs are unchanged by this
        var row = await db.GraphicAssets.SingleAsync(g => g.Type == GraphicAssetType.AllTracksSpeakers);
        Assert.Equal(GraphicAssetStatus.Released, row.Status);
        using var img = Image.Load<Rgba32>(OverviewWrite(store));
        Assert.Equal(2, img.Frames.Count);
    }

    [Fact]
    public async Task A_new_speaker_rebuilds_the_overview()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var session = await AddTrackSessionAsync(db, seed.EventId, "Security", seed.SpeakerOneId);
        await MarkRealAsync(db, seed.EventId);
        var sweep = NewSweep(db, StoreWith(seed.SpeakerOneId, seed.SpeakerTwoId));
        await sweep.BuildAsync(seed.EventId);

        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = session.Id, ParticipantId = seed.SpeakerTwoId });
        await db.SaveChangesAsync();

        Assert.Equal(1, (await sweep.BuildAsync(seed.EventId)).AllTracksOverview);
    }

    [Fact]
    public async Task A_missing_photo_holds_the_track_gif_but_not_the_overview()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await AddTrackSessionAsync(db, seed.EventId, "Security", seed.SpeakerOneId, seed.SpeakerTwoId);
        await MarkRealAsync(db, seed.EventId);
        var sweep = NewSweep(db, StoreWith(seed.SpeakerOneId));   // speaker two has no photo

        var result = await sweep.BuildAsync(seed.EventId);

        Assert.Equal(0, result.TrackBundles);
        Assert.Equal(1, result.TracksSkippedNoPhoto);
        Assert.Equal(1, result.AllTracksOverview);
    }

    [Fact]
    public async Task Test_speakers_and_test_sessions_are_not_in_the_overview()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await AddTrackSessionAsync(db, seed.EventId, "Security", seed.SpeakerOneId);
        await AddTrackSessionAsync(db, seed.EventId, "Intune", seed.SpeakerTwoId);          // test user only
        var testSession = await AddTrackSessionAsync(db, seed.EventId, "Azure", seed.SpeakerOneId);
        testSession.UsedForTesting = true;                                                   // §299 TEST session
        await db.SaveChangesAsync();
        await MarkRealAsync(db, seed.EventId, testUserIds: seed.SpeakerTwoId);
        var store = StoreWith(seed.SpeakerOneId, seed.SpeakerTwoId);

        await NewSweep(db, store).BuildAsync(seed.EventId);

        using var img = Image.Load<Rgba32>(OverviewWrite(store));
        Assert.Single(img.Frames);   // Security only
    }

    /// <summary>
    /// 🔴 §1384 follow-up (PROD 2026-11-07: Intune showed 16 of 19) — a session kept out of the SoMe
    /// campaign ONLY by <c>ExcludeFromSoMeAnnouncements</c> (the sponsor speaker sessions) is on the
    /// overview but still gets no per-track GIF; IsTestData / UsedForTesting sessions are in neither.
    /// </summary>
    [Fact]
    public async Task A_session_excluded_only_from_announcements_is_in_the_overview_but_not_in_a_track_gif()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await AddTrackSessionAsync(db, seed.EventId, "Intune", seed.SpeakerOneId);
        var sponsor = await AddTrackSessionAsync(db, seed.EventId, "Data Compliance", seed.SpeakerTwoId);
        sponsor.ExcludeFromSoMeAnnouncements = true;
        var testData = await AddTrackSessionAsync(db, seed.EventId, "Azure", seed.MasterclassSpeakerId);
        testData.IsTestData = true;
        var testSession = await AddTrackSessionAsync(db, seed.EventId, "Identity", seed.MasterclassSpeakerId);
        testSession.UsedForTesting = true;
        await db.SaveChangesAsync();
        await MarkRealAsync(db, seed.EventId);
        var store = StoreWith(seed.SpeakerOneId, seed.SpeakerTwoId, seed.MasterclassSpeakerId);

        var result = await NewSweep(db, store).BuildAsync(seed.EventId);

        // Per-track: Intune only — unchanged behaviour.
        Assert.Equal(1, result.TrackBundles);
        Assert.Equal(new[] { "track:intune" }, await db.GraphicAssets
            .Where(g => g.Type == GraphicAssetType.TrackBundle).Select(g => g.StableKey).ToListAsync());

        // Overview: Intune + Data Compliance (the sponsor session); never Azure or Identity.
        using var img = Image.Load<Rgba32>(OverviewWrite(store));
        Assert.Equal(2, img.Frames.Count);
    }

    /// <summary>
    /// 🔴 §1384 follow-up (PROD 2026-11-07: two organizers appeared in the Security frame) — a keynote,
    /// panel or welcome session that carries a Track is NOT part of that track's line-up; only
    /// technical sessions and master classes are, even when the announcement flag is lifted.
    /// </summary>
    [Fact]
    public async Task A_keynote_or_panel_with_a_track_is_not_in_the_overview()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await AddTrackSessionAsync(db, seed.EventId, "Intune", seed.SpeakerOneId);
        var keynote = await AddTrackSessionAsync(db, seed.EventId, "Security", seed.SpeakerTwoId);
        keynote.Type = SessionType.Keynote;
        keynote.ExcludeFromSoMeAnnouncements = true;
        var panel = await AddTrackSessionAsync(db, seed.EventId, "Azure", seed.MasterclassSpeakerId);
        panel.Type = SessionType.PanelDiscussion;
        var masterClass = await AddTrackSessionAsync(db, seed.EventId, "Identity", seed.MasterclassSpeakerId);
        masterClass.Type = SessionType.MasterClass;
        await db.SaveChangesAsync();
        await MarkRealAsync(db, seed.EventId);
        var store = StoreWith(seed.SpeakerOneId, seed.SpeakerTwoId, seed.MasterclassSpeakerId);

        await NewSweep(db, store).BuildAsync(seed.EventId);

        // Intune (technical) + Identity (master class); never the Security keynote or the Azure panel.
        using var img = Image.Load<Rgba32>(OverviewWrite(store));
        Assert.Equal(2, img.Frames.Count);
    }

    /// <summary>
    /// §1384 follow-up — a sponsor speaker whose photo file carries ANOTHER id (only their
    /// <c>SpeakerProfile.PhotoSharePointPath</c> names it) still gets their face on the overview.
    /// </summary>
    [Fact]
    public async Task A_sponsor_speaker_photo_is_found_through_the_profile_photo_path()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var sponsor = await AddTrackSessionAsync(db, seed.EventId, "Intune", seed.SpeakerTwoId);
        sponsor.ExcludeFromSoMeAnnouncements = true;
        var file = SpeakerPhotoFileName.Build(9999, ".png");
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = seed.EventId, ParticipantId = seed.SpeakerTwoId, PhotoSharePointPath = $"{PhotoFolder}/{file}",
        });
        await db.SaveChangesAsync();
        await MarkRealAsync(db, seed.EventId);
        var store = StoreWith();
        store.Put(PhotoFolder, file, Png(300, 300));

        var result = await NewSweep(db, store).BuildAsync(seed.EventId);

        Assert.Equal(1, result.AllTracksOverview);
        Assert.Contains($"{PhotoFolder}/{file}", store.Downloads);
    }

    // ---- fakes -------------------------------------------------------------------------

    private sealed class NoPictureFetcher : ISpeakerPictureFetcher
    {
        public Task<FetchedImage?> FetchAsync(string? pictureUrl, CancellationToken ct = default) =>
            Task.FromResult<FetchedImage?>(null);
    }

    private sealed class FakeStore : ISharePointFileStore
    {
        private readonly Dictionary<string, List<SharePointFileRef>> _folders = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _byItemId = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Path, byte[] Content, string ContentType)> Writes { get; } = new();

        public void Put(string folder, string fileName, byte[] content)
        {
            var itemId = $"{folder}/{fileName}";
            if (!_folders.TryGetValue(folder, out var list)) _folders[folder] = list = new List<SharePointFileRef>();
            list.Add(new SharePointFileRef(itemId, fileName, null!));
            _byItemId[itemId] = content;
        }

        public bool CanRead => true;
        public bool CanStore => true;

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(
                _folders.TryGetValue(relativeFolder, out var files) ? files : Array.Empty<SharePointFileRef>());

        public List<string> Downloads { get; } = new();

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default)
        {
            Downloads.Add(itemId);
            return Task.FromResult(_byItemId.TryGetValue(itemId, out var b) ? b : null);
        }

        public Task<StoredFile> StoreAsync(string relativePath, byte[] content, string contentType, CancellationToken ct = default)
        {
            Writes.Add((relativePath, content, contentType));
            return Task.FromResult(new StoredFile(relativePath, $"https://store.example.test/{relativePath}", "item-" + relativePath));
        }

        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;

        public Task<StoredFile> UploadToFolderAsync(
            string relativeFolder, string fileName, byte[] content, string contentType, CancellationToken ct = default)
        {
            var path = $"{relativeFolder}/{fileName}";
            Writes.Add((path, content, contentType));
            _byItemId["item-" + path] = content;
            return Task.FromResult(new StoredFile(path, $"https://store.example.test/{path}", "item-" + path));
        }

        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
