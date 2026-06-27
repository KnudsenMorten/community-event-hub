using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: PULL operator-uploaded SoMe session graphics FROM SharePoint and surface
/// them through the existing organizer-review → speaker-download flow (REQUIREMENTS §18).
/// Drives the real <see cref="GraphicsService.PullSessionGraphicsAsync"/> + EF in-memory
/// DB with a FAKE <see cref="ISharePointFileStore"/> returning known files — nothing
/// external is touched and the compositor is NOT used. Proves the load-bearing contracts:
///  - a session is matched to the file whose NAME slug == the session-title slug
///    (case-insensitive; spaces OR dashes in the file name both match);
///  - MasterClass-type sessions pull from the MasterClass folder, every other session
///    from the Sessions folder; each folder is listed at most once;
///  - one <see cref="GraphicAsset"/> (Type=Session, Status=Generated) is upserted PER
///    (session, speaker) and flows into the organizer review queue;
///  - the pull is IDEMPOTENT (re-pull updates in place, no duplicate rows);
///  - an unmatched session is counted, not errored;
///  - the pull NO-OPS cleanly when the store cannot read (inert until configured).
///
/// NO real data — example.test + @@expertslive.dk only.
/// </summary>
public sealed class PullSessionGraphicsScenarioTests
{
    private const string MasterClassFolder = "Graphics/MasterClass";
    private const string SessionsFolder = "Graphics/Sessions";

    private static GraphicsService NewService(
        CommunityHubDbContext db, ISharePointFileStore store, GraphicsSharePointOptions options) =>
        new(db, new GraphicCompositor(), store,
            new FakePictureFetcher(null), new DraftOnlySocialShareGateway(),
            Options.Create(options));

    private static GraphicsSharePointOptions ConfiguredOptions() => new()
    {
        Enabled = true,
        SiteUrl = "https://contoso.sharepoint.example.test/sites/eldk",
        MasterClassFolderPath = MasterClassFolder,
        SessionsFolderPath = SessionsFolder,
    };

    private static async Task<Session> AddSessionAsync(
        CommunityHubDbContext db, int eventId, string title, SessionType type, params int[] speakerIds)
    {
        var session = new Session
        {
            EventId = eventId,
            SessionizeId = Guid.NewGuid().ToString("N"),
            Title = title,
            Type = type,
            CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        foreach (var pid in speakerIds)
        {
            db.SessionSpeakers.Add(new SessionSpeaker { SessionId = session.Id, ParticipantId = pid });
        }
        await db.SaveChangesAsync();
        return session;
    }

    // ---- MATCH + folder routing + one row per (session, speaker) -----------

    [Fact]
    public async Task Pull_matches_by_title_slug_routes_folders_and_upserts_one_row_per_speaker()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        // A MasterClass session (single speaker) and a regular session (TWO speakers).
        var mc = await AddSessionAsync(
            db, seed.EventId, "Deep Dive Workshop", SessionType.MasterClass, seed.MasterclassSpeakerId);
        var talk = await AddSessionAsync(
            db, seed.EventId, "Cloud Native Talk", SessionType.TechnicalSession,
            seed.SpeakerOneId, seed.SpeakerTwoId);

        var store = new FakePullStore(canRead: true)
        {
            // MasterClass folder file named with SPACES; Sessions folder file with DASHES
            // + a different-case extension — both must still match the title slug.
            [MasterClassFolder] = { File("Deep Dive Workshop.png") },
            [SessionsFolder] = { File("cloud-native-talk.PNG") },
        };
        var svc = NewService(db, store, ConfiguredOptions());

        var result = await svc.PullSessionGraphicsAsync(seed.EventId);

        Assert.Equal(2, result.Matched);     // both sessions matched a file
        Assert.Equal(0, result.Unmatched);

        // Each folder was listed exactly ONCE (listing cached per folder).
        Assert.Equal(1, store.ListCount(MasterClassFolder));
        Assert.Equal(1, store.ListCount(SessionsFolder));

        // MasterClass speaker → one row from the MasterClass folder file.
        var mcRow = await GetRowAsync(db, seed.EventId, mc.Id, seed.MasterclassSpeakerId);
        Assert.NotNull(mcRow);
        Assert.Equal(GraphicAssetType.Session, mcRow!.Type);
        Assert.Equal(GraphicAssetStatus.Generated, mcRow.Status); // the review gate
        Assert.Equal("Deep Dive Workshop.png", mcRow.FileName);
        Assert.Equal($"{MasterClassFolder}/Deep Dive Workshop.png", mcRow.SharePointPath);
        Assert.Equal("item-Deep Dive Workshop.png", mcRow.StorageItemId);
        Assert.False(string.IsNullOrEmpty(mcRow.SharePointUrl));

        // Regular session → ONE row PER speaker, both from the Sessions folder file.
        var row1 = await GetRowAsync(db, seed.EventId, talk.Id, seed.SpeakerOneId);
        var row2 = await GetRowAsync(db, seed.EventId, talk.Id, seed.SpeakerTwoId);
        Assert.NotNull(row1);
        Assert.NotNull(row2);
        Assert.Equal("cloud-native-talk.PNG", row1!.FileName);
        Assert.Equal("cloud-native-talk.PNG", row2!.FileName);

        // The pulled rows are in the organizer review queue (Generated, not released).
        var queue = await svc.GetReviewQueueAsync(seed.EventId, GraphicAssetType.Session);
        Assert.Equal(3, queue.Count); // 1 MC speaker + 2 talk speakers
    }

    // ---- IDEMPOTENT: re-pull updates in place, no duplicates ----------------

    [Fact]
    public async Task Pull_is_idempotent_no_duplicate_rows_on_re_run()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var talk = await AddSessionAsync(
            db, seed.EventId, "Cloud Native Talk", SessionType.TechnicalSession, seed.SpeakerOneId);

        var store = new FakePullStore(canRead: true)
        {
            [SessionsFolder] = { File("Cloud Native Talk.png") },
        };
        var svc = NewService(db, store, ConfiguredOptions());

        var first = await svc.PullSessionGraphicsAsync(seed.EventId);
        var second = await svc.PullSessionGraphicsAsync(seed.EventId);

        Assert.Equal(1, first.Matched);
        Assert.Equal(1, second.Matched);

        var rows = await db.GraphicAssets
            .Where(g => g.EventId == seed.EventId && g.SessionId == talk.Id)
            .ToListAsync();
        Assert.Single(rows); // upsert by stable key — no second row
    }

    // ---- UNMATCHED session is counted, not errored -------------------------

    [Fact]
    public async Task Pull_counts_a_session_with_no_matching_file_as_unmatched()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var matched = await AddSessionAsync(
            db, seed.EventId, "Cloud Native Talk", SessionType.TechnicalSession, seed.SpeakerOneId);
        var orphan = await AddSessionAsync(
            db, seed.EventId, "No Graphic Here", SessionType.TechnicalSession, seed.SpeakerTwoId);

        var store = new FakePullStore(canRead: true)
        {
            [SessionsFolder] = { File("Cloud Native Talk.png") }, // only the first session has a file
        };
        var svc = NewService(db, store, ConfiguredOptions());

        var result = await svc.PullSessionGraphicsAsync(seed.EventId);

        Assert.Equal(1, result.Matched);
        Assert.Equal(1, result.Unmatched);
        Assert.NotNull(await GetRowAsync(db, seed.EventId, matched.Id, seed.SpeakerOneId));
        Assert.Null(await GetRowAsync(db, seed.EventId, orphan.Id, seed.SpeakerTwoId));
    }

    // ---- INERT: no-op when the store cannot read ---------------------------

    [Fact]
    public async Task Pull_is_a_no_op_when_the_store_cannot_read()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await AddSessionAsync(
            db, seed.EventId, "Cloud Native Talk", SessionType.TechnicalSession, seed.SpeakerOneId);

        // CanRead=false: even with files "present", nothing is listed or pulled.
        var store = new FakePullStore(canRead: false)
        {
            [SessionsFolder] = { File("Cloud Native Talk.png") },
        };
        var svc = NewService(db, store, ConfiguredOptions());

        var result = await svc.PullSessionGraphicsAsync(seed.EventId);

        Assert.Equal(0, result.Matched);
        Assert.Equal(0, result.Unmatched);
        Assert.Equal(0, store.ListCount(SessionsFolder)); // never listed — truly inert
        Assert.Empty(await db.GraphicAssets.Where(g => g.EventId == seed.EventId).ToListAsync());
    }

    [Fact]
    public async Task Pull_is_a_no_op_when_no_folder_is_configured()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await AddSessionAsync(
            db, seed.EventId, "Cloud Native Talk", SessionType.TechnicalSession, seed.SpeakerOneId);

        var store = new FakePullStore(canRead: true)
        {
            [SessionsFolder] = { File("Cloud Native Talk.png") },
        };
        // Store CAN read, but the operator has not set any folder path yet → inert.
        var options = new GraphicsSharePointOptions { Enabled = true, SiteUrl = "https://x.example.test" };
        var svc = NewService(db, store, options);

        var result = await svc.PullSessionGraphicsAsync(seed.EventId);

        Assert.Equal(0, result.Matched);
        Assert.Equal(0, result.Unmatched);
        Assert.Empty(await db.GraphicAssets.Where(g => g.EventId == seed.EventId).ToListAsync());
    }

    // ---- helpers -----------------------------------------------------------

    private static Task<GraphicAsset?> GetRowAsync(
        CommunityHubDbContext db, int eventId, int sessionId, int participantId)
    {
        var key = GraphicStableKey.ForSession(sessionId, participantId);
        return db.GraphicAssets.FirstOrDefaultAsync(
            g => g.EventId == eventId && g.StableKey == key);
    }

    private static SharePointFileRef File(string name) =>
        new("item-" + name, name, "https://store.example.test/" + name);

    /// <summary>
    /// A fake read-side SharePoint store: returns the files registered per folder,
    /// records how many times each folder was listed, and (CanRead=false) refuses to
    /// list anything so the inert path can be proven. Write side is unused.
    /// </summary>
    private sealed class FakePullStore : ISharePointFileStore
    {
        private readonly Dictionary<string, List<SharePointFileRef>> _byFolder = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _listCounts = new(StringComparer.OrdinalIgnoreCase);

        public FakePullStore(bool canRead) => CanRead = canRead;

        /// <summary>Collection-initializer hook: <c>[folder] = { File(...) }</c>.</summary>
        public List<SharePointFileRef> this[string folder]
        {
            get
            {
                if (!_byFolder.TryGetValue(folder, out var list))
                {
                    list = new List<SharePointFileRef>();
                    _byFolder[folder] = list;
                }
                return list;
            }
        }

        public int ListCount(string folder) => _listCounts.TryGetValue(folder, out var n) ? n : 0;

        public bool CanRead { get; }
        public bool CanStore => false;

        public Task<StoredFile> StoreAsync(
            string relativePath, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("write side not used by the pull");

        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(
            string relativeFolder, CancellationToken ct = default)
        {
            if (!CanRead) return Task.FromResult<IReadOnlyList<SharePointFileRef>>(Array.Empty<SharePointFileRef>());
            _listCounts[relativeFolder] = ListCount(relativeFolder) + 1;
            return Task.FromResult<IReadOnlyList<SharePointFileRef>>(
                _byFolder.TryGetValue(relativeFolder, out var list)
                    ? list
                    : new List<SharePointFileRef>());
        }

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(CanRead ? new byte[] { 1, 2, 3 } : null);

        public Task<StoredFile> UploadToFolderAsync(
            string relativeFolder, string fileName, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("write side not used by the pull");
        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>A picture fetcher returning a fixed image (or null) — unused by the pull.</summary>
    private sealed class FakePictureFetcher : ISpeakerPictureFetcher
    {
        private readonly FetchedImage? _image;
        public FakePictureFetcher(FetchedImage? image) => _image = image;
        public Task<FetchedImage?> FetchAsync(string? pictureUrl, CancellationToken ct = default) =>
            Task.FromResult(string.IsNullOrWhiteSpace(pictureUrl) ? null : _image);
    }
}
