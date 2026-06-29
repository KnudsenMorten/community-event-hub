using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// EXTERNAL-DESIGNER graphics pipeline (REQUIREMENTS §165). Uses a FAKE store (records bytes per
/// folder) + a FAKE picture fetcher (url → deterministic bytes). Proves: photos NAMED BY SPEAKER;
/// SPEAKER-UPLOAD-WINS over Sessionize (the pull uses the speaker's own URL and a later upload
/// supersedes a prior Sessionize pull); per-session / master-class / track build folders are
/// idempotent (re-run = reconcile, no dupes); the brief rows carry the right fields + folder link;
/// inert when options are unset. NO real names.
/// </summary>
public sealed class ExternalDesignerGraphicsServiceTests
{
    private const int EventId = 7;
    private const string PhotosFolder = "EventHub/Speakers/Photos";
    private const string SessionsFolder = "EventHub/Build/Sessions";
    private const string MasterClassFolder = "EventHub/Build/MasterClasses";
    private const string TracksFolder = "EventHub/Build/Tracks";

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"designer-{Guid.NewGuid():N}")
            .Options);

    private static ExternalDesignerGraphicsService NewService(
        CommunityHubDbContext db, FakeStore store, FakeFetcher fetcher,
        string photos = PhotosFolder, string sessions = SessionsFolder,
        string masterClass = MasterClassFolder, string tracks = TracksFolder) =>
        new(db, store, fetcher, Options.Create(new GraphicsSharePointOptions
        {
            Enabled = true,
            SiteUrl = "https://contoso.sharepoint.example.test/sites/eldk",
            SpeakerPhotosFolderPath = photos,
            SessionsFolderPath = sessions,
            MasterClassFolderPath = masterClass,
            TracksFolderPath = tracks,
        }));

    // ---- helpers to seed -----------------------------------------------------

    private static async Task<int> AddSpeakerAsync(
        CommunityHubDbContext db, string fullName, string? photoUrl, bool speakerEdited)
    {
        var p = new Participant { EventId = EventId, Email = $"{Guid.NewGuid():N}@example.test", FullName = fullName, Role = ParticipantRole.Speaker };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        var prof = new SpeakerProfile { EventId = EventId, ParticipantId = p.Id, PhotoUrl = photoUrl };
        if (speakerEdited) prof.MarkSpeakerEdited(SpeakerProfile.BioFields.PhotoUrl, DateTimeOffset.UtcNow);
        db.SpeakerProfiles.Add(prof);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static async Task<int> AddSessionAsync(
        CommunityHubDbContext db, string title, SessionType type, string? track, params int[] speakerIds)
    {
        var s = new Session { EventId = EventId, Title = title, Type = type, Track = track, SessionizeId = Guid.NewGuid().ToString() };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        foreach (var pid in speakerIds)
            db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s.Id, ParticipantId = pid });
        await db.SaveChangesAsync();
        return s.Id;
    }

    // ---- 1. photo named by speaker ------------------------------------------

    [Fact]
    public async Task Pull_writes_each_photo_named_by_the_speaker()
    {
        using var db = NewDb();
        await AddSpeakerAsync(db, "Ada Lovelace", "https://sessionize.example.test/ada.png", speakerEdited: false);
        var store = new FakeStore(canRead: true, canStore: true);
        var svc = NewService(db, store, new FakeFetcher());

        var result = await svc.RunPipelineAsync(EventId);

        Assert.Equal(1, result.PhotosPulled);
        var file = Assert.Single(store.Files(PhotosFolder));
        Assert.Equal("Ada-Lovelace.png", file.Name); // named by the speaker, source extension carried
    }

    [Fact]
    public async Task Same_name_speakers_get_a_deterministic_unique_file_name()
    {
        using var db = NewDb();
        var a = await AddSpeakerAsync(db, "Sam Speaker", "https://x.example.test/a.jpg", false);
        var b = await AddSpeakerAsync(db, "Sam Speaker", "https://x.example.test/b.jpg", false);
        var store = new FakeStore(canRead: true, canStore: true);
        var svc = NewService(db, store, new FakeFetcher());

        await svc.RunPipelineAsync(EventId);

        var names = store.Files(PhotosFolder).Select(f => f.Name).OrderBy(n => n).ToList();
        Assert.Equal(2, names.Count);
        Assert.All(names, n => Assert.StartsWith("Sam-Speaker-", n)); // both got the -{id} suffix
        Assert.Equal(2, names.Distinct().Count());
        Assert.Contains($"Sam-Speaker-{a}.jpg", names);
        Assert.Contains($"Sam-Speaker-{b}.jpg", names);
    }

    // ---- 2. speaker-upload-wins ---------------------------------------------

    [Fact]
    public void Precedence_is_derived_from_existing_profile_state()
    {
        Assert.Equal(PhotoSource.None, ExternalDesignerGraphicsService.ResolvePhotoSource(null).Source);

        var sessionize = new SpeakerProfile { PhotoUrl = "https://s.example.test/p.jpg" };
        Assert.Equal(PhotoSource.Sessionize, ExternalDesignerGraphicsService.ResolvePhotoSource(sessionize).Source);

        var edited = new SpeakerProfile { PhotoUrl = "https://own.example.test/p.jpg" };
        edited.MarkSpeakerEdited(SpeakerProfile.BioFields.PhotoUrl, DateTimeOffset.UtcNow);
        var r = ExternalDesignerGraphicsService.ResolvePhotoSource(edited);
        Assert.Equal(PhotoSource.SpeakerUpload, r.Source);
        Assert.Equal("https://own.example.test/p.jpg", r.Url);
    }

    [Fact]
    public async Task Speaker_upload_wins_pull_uses_the_speakers_own_photo_not_sessionize()
    {
        using var db = NewDb();
        const string ownUrl = "https://own.example.test/me.png";
        await AddSpeakerAsync(db, "Edited Speaker", ownUrl, speakerEdited: true);
        var store = new FakeStore(canRead: true, canStore: true);
        var fetcher = new FakeFetcher();
        var svc = NewService(db, store, fetcher);

        var result = await svc.RunPipelineAsync(EventId);

        Assert.Equal(1, result.PhotosPulled);
        Assert.Equal(1, result.UploadsPreserved); // the speaker's own upload was honoured
        var file = Assert.Single(store.Files(PhotosFolder));
        Assert.Equal(FakeFetcher.BytesFor(ownUrl), file.Bytes); // bytes came from the SPEAKER's URL
    }

    [Fact]
    public async Task A_later_speaker_upload_supersedes_a_previously_pulled_sessionize_photo()
    {
        using var db = NewDb();
        const string sessionizeUrl = "https://sessionize.example.test/old.jpg";
        var pid = await AddSpeakerAsync(db, "Switch Speaker", sessionizeUrl, speakerEdited: false);
        var store = new FakeStore(canRead: true, canStore: true);
        var svc = NewService(db, store, new FakeFetcher());

        // First run: Sessionize photo is pulled.
        var first = await svc.RunPipelineAsync(EventId);
        Assert.Equal(0, first.UploadsPreserved);
        var afterFirst = Assert.Single(store.Files(PhotosFolder));
        Assert.Equal(FakeFetcher.BytesFor(sessionizeUrl), afterFirst.Bytes);

        // The speaker now uploads their own photo (edits the field).
        const string ownUrl = "https://own.example.test/new.jpg";
        var prof = await db.SpeakerProfiles.FirstAsync(p => p.ParticipantId == pid);
        prof.PhotoUrl = ownUrl;
        prof.MarkSpeakerEdited(SpeakerProfile.BioFields.PhotoUrl, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        // Second run supersedes in place — same file name, the speaker's bytes now win.
        var second = await svc.RunPipelineAsync(EventId);
        Assert.Equal(1, second.UploadsPreserved);
        var afterSecond = Assert.Single(store.Files(PhotosFolder)); // still ONE file (replaced, not duped)
        Assert.Equal(FakeFetcher.BytesFor(ownUrl), afterSecond.Bytes);
    }

    // ---- 3. build folders, idempotent ---------------------------------------

    [Fact]
    public async Task Builds_per_session_master_class_and_track_folders_idempotently()
    {
        using var db = NewDb();
        var a = await AddSpeakerAsync(db, "Talk Speaker", "https://x.example.test/a.jpg", false);
        var b = await AddSpeakerAsync(db, "Class Speaker", "https://x.example.test/b.jpg", false);
        await AddSessionAsync(db, "Securing Your Cloud", SessionType.TechnicalSession, "Security", a);
        await AddSessionAsync(db, "Hands-on Workshop", SessionType.MasterClass, "Security", b);
        var store = new FakeStore(canRead: true, canStore: true);
        var svc = NewService(db, store, new FakeFetcher());

        var first = await svc.RunPipelineAsync(EventId);
        Assert.Equal(1, first.SessionFolders);
        Assert.Equal(1, first.MasterClassFolders);
        Assert.Equal(1, first.TrackFolders);

        // Per-session folder holds the session's speaker photo.
        Assert.Equal("Talk-Speaker.jpg", Assert.Single(store.Files($"{SessionsFolder}/Securing Your Cloud")).Name);
        // Master class went under the MASTER-CLASS root, not the sessions root.
        Assert.Equal("Class-Speaker.jpg", Assert.Single(store.Files($"{MasterClassFolder}/Hands-on Workshop")).Name);
        // The track folder gathers both sessions' speakers.
        Assert.Equal(2, store.Files($"{TracksFolder}/Security").Count);

        // Re-run: reconcile in place — no duplicates.
        await svc.RunPipelineAsync(EventId);
        Assert.Single(store.Files($"{SessionsFolder}/Securing Your Cloud"));
        Assert.Single(store.Files($"{MasterClassFolder}/Hands-on Workshop"));
        Assert.Equal(2, store.Files($"{TracksFolder}/Security").Count);
    }

    // ---- 4. brief rows -------------------------------------------------------

    [Fact]
    public async Task Brief_rows_carry_the_session_facts_photo_names_and_folder_link()
    {
        using var db = NewDb();
        var a = await AddSpeakerAsync(db, "Brief Speaker", "https://x.example.test/a.png", false);
        await AddSessionAsync(db, "Cloud Native Talk", SessionType.TechnicalSession, "Cloud", a);
        var store = new FakeStore(canRead: true, canStore: true);
        var svc = NewService(db, store, new FakeFetcher());

        // Run first so the folder has a file (folder link is then resolvable).
        await svc.RunPipelineAsync(EventId);

        var rows = await svc.BuildBriefRowsAsync(EventId);
        var row = Assert.Single(rows);
        Assert.Equal("Cloud Native Talk", row.Title);
        Assert.Equal("Technical Session", row.Type);
        Assert.Equal("Cloud", row.Track);
        Assert.Equal(new[] { "Brief Speaker" }, row.SpeakerNames);
        Assert.Equal(new[] { "Brief-Speaker.png" }, row.PhotoFileNames);
        Assert.Equal($"{SessionsFolder}/Cloud Native Talk", row.FolderPath);
        Assert.False(string.IsNullOrEmpty(row.FolderUrl));
        Assert.EndsWith("/Cloud Native Talk", row.FolderUrl); // parent-folder link derived from a file URL
        Assert.DoesNotContain(".png", row.FolderUrl!); // it's the FOLDER, not a file

        // The brief renders to a valid, non-empty .xlsx.
        var bytes = await svc.BuildBriefAsync(EventId);
        Assert.NotEmpty(bytes);
    }

    // ---- 5. inert when not configured ---------------------------------------

    [Fact]
    public async Task Is_inert_when_the_store_cannot_write_or_no_folder_is_set()
    {
        using var db = NewDb();
        await AddSpeakerAsync(db, "Inert Speaker", "https://x.example.test/a.jpg", false);

        // Store can't write → nothing happens, nothing faked.
        var noStore = NewService(db, new FakeStore(canRead: false, canStore: false), new FakeFetcher());
        Assert.False(noStore.CanManage);
        var r = await noStore.RunPipelineAsync(EventId);
        Assert.Equal(0, r.PhotosPulled);
        Assert.Equal(0, r.SessionFolders);

        // Capable store but EVERY folder path blank → CanManage false, run writes nothing.
        var store = new FakeStore(canRead: true, canStore: true);
        var blank = NewService(db, store, new FakeFetcher(), photos: "", sessions: "", masterClass: "", tracks: "");
        Assert.False(blank.CanManage);
        var r2 = await blank.RunPipelineAsync(EventId);
        Assert.Equal(0, r2.PhotosPulled);
        Assert.Empty(store.AllFolders());
    }

    // ---- fakes ---------------------------------------------------------------

    /// <summary>url → deterministic bytes, so a test can assert WHICH url's bytes were stored.</summary>
    private sealed class FakeFetcher : ISpeakerPictureFetcher
    {
        public static byte[] BytesFor(string url) => Encoding.UTF8.GetBytes("img:" + url);

        public Task<FetchedImage?> FetchAsync(string? pictureUrl, CancellationToken ct = default) =>
            Task.FromResult<FetchedImage?>(
                string.IsNullOrWhiteSpace(pictureUrl) ? null : new FetchedImage(BytesFor(pictureUrl), "image/jpeg"));
    }

    private sealed record StoredEntry(string Name, byte[] Bytes, string ItemId, string WebUrl);

    /// <summary>In-memory fake: records uploaded bytes per drive-relative folder; lists with web URLs.</summary>
    private sealed class FakeStore : ISharePointFileStore
    {
        private readonly Dictionary<string, List<StoredEntry>> _byFolder = new(StringComparer.OrdinalIgnoreCase);

        public FakeStore(bool canRead, bool canStore) { CanRead = canRead; CanStore = canStore; }

        public bool CanRead { get; }
        public bool CanStore { get; }

        public IReadOnlyList<StoredEntry> Files(string folder) =>
            _byFolder.TryGetValue(folder, out var list) ? list : new List<StoredEntry>();

        public IReadOnlyCollection<string> AllFolders() => _byFolder.Keys.ToList();

        public Task<StoredFile> UploadToFolderAsync(
            string relativeFolder, string fileName, byte[] content, string contentType, CancellationToken ct = default)
        {
            if (!CanStore) throw new InvalidOperationException("cannot write");
            if (!_byFolder.TryGetValue(relativeFolder, out var list)) { list = new(); _byFolder[relativeFolder] = list; }
            list.RemoveAll(f => f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            var webUrl = $"https://contoso.sharepoint.example.test/sites/eldk/Shared%20Documents/{relativeFolder}/{fileName}";
            list.Add(new StoredEntry(fileName, content, "item-" + Guid.NewGuid().ToString("N"), webUrl));
            return Task.FromResult(new StoredFile($"{relativeFolder}/{fileName}", webUrl, "item"));
        }

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(
                CanRead && _byFolder.TryGetValue(relativeFolder, out var list)
                    ? list.Select(e => new SharePointFileRef(e.ItemId, e.Name, e.WebUrl)).ToList()
                    : new List<SharePointFileRef>());

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(
                _byFolder.Values.SelectMany(v => v).FirstOrDefault(e => e.ItemId == itemId)?.Bytes);

        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default)
        {
            if (_byFolder.TryGetValue(relativeFolder, out var list))
                list.RemoveAll(f => f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }

        // Root write-side unused by §165.
        public Task<StoredFile> StoreAsync(string relativePath, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("root store not used by §165");
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
    }
}
