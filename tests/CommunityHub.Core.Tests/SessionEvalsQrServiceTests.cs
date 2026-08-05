using CommunityHub.Core.Integrations.Graphics;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The per-ROOM session-evaluation QR service (REQUIREMENTS §124): reads the operator's
/// SharePoint QR folder through the <see cref="ISharePointFileStore"/> read seam and
/// matches each session to its room's QR by ROOM NAME parsed from the file name. Uses a
/// FAKE store (no external call). Proves: file-name → room parsing, exact + tolerant
/// matching, the inert (not-configured) path, download-by-room, and the org-admin
/// upload/delete delegation. NO real data.
/// </summary>
public class SessionEvalsQrServiceTests
{
    // §768: the folder is no longer an app setting — it resolves from the DocLibrary registry.
    // ⚠️ The literal that used to live here was `…/Speakers/SessionEvals-QR`, which the library
    // reorganisation renamed to `SessionEvaluations/QR`. A test carrying the stale name would have
    // gone on passing against a folder that no longer exists.
    private static readonly string Folder = TestDocLibrary.PathFor(
        CommunityHub.Core.Integrations.DocLibrary.DocLibraryPaths.SessionEvaluationQr);

    /// <param name="folder">Pass <c>""</c> to model an UNCONFIGURED library — the inert case.</param>
    private static SessionEvalsQrService NewService(FakeQrStore store, string? folder = null) =>
        new(store, Options.Create(new GraphicsSharePointOptions
        {
            Enabled = true,
            SiteUrl = "https://contoso.sharepoint.example.test/sites/eldk",
        }),
        TestDocLibrary.Resolver(folder == string.Empty ? string.Empty : TestDocLibrary.Root));

    // ---- file-name → room parsing -----------------------------------------

    [Theory]
    [InlineData("Room-16-Floor-1-Device13.png", "Room 16")]
    [InlineData("Room-6-7-Floor-1-Device2.png", "Room 6 7")]
    [InlineData("Hall-A1-Keynote-Floor-2-Device3.png", "Hall A1 Keynote")]
    [InlineData("Room-Treehouse-North-Floor-1-Device9.png", "Room Treehouse North")]
    [InlineData("Room-Auditorium-15-Floor-1-Device1.png", "Room Auditorium 15")]
    public void RoomDisplayFromFileName_parses_room_before_floor_marker(string file, string expected)
    {
        Assert.Equal(expected, SessionEvalsQrService.RoomDisplayFromFileName(file));
    }

    [Fact]
    public void RoomDisplayFromFileName_falls_back_to_the_whole_stem_when_no_marker()
    {
        Assert.Equal("Some Custom Room", SessionEvalsQrService.RoomDisplayFromFileName("Some-Custom-Room.png"));
    }

    // ---- matching ----------------------------------------------------------

    /// <summary>
    /// 🔑 §749.2 — matching is by SESSION now (operator 2026-07-31: <i>"current is linked to room
    /// name but now we use sessionname"</i>). §748 had already made the QR a property of the session
    /// rather than the room; this lookup was the last piece still describing the retired model.
    /// </summary>
    [Fact]
    public async Task MatchSessions_matches_each_session_to_ITS_OWN_file()
    {
        var store = new FakeQrStore(canRead: true, canStore: true)
        {
            [Folder] =
            {
                File("session-1-morning-keynote-qr.png"),
                File("session-2-afternoon-panel-qr.png"),
                File("session-3-qr.png"),                 // a title that slugged to nothing
            },
        };
        var svc = NewService(store);

        // 🔑 Sessions 1 and 2 share a ROOM. Under the old room-keyed rule they collapsed onto one
        // file; each must now get its own — that collapse is the whole reason for the change.
        var sessions = new[]
        {
            new SessionRoomRef(1, "Room 16"),
            new SessionRoomRef(2, "Room 16"),
            new SessionRoomRef(3, null),        // no room at all, and it still matches
            new SessionRoomRef(4, "Room 16"),   // no file of its own
        };

        var map = await svc.MatchSessionsAsync(sessions);

        Assert.Equal("session-1-morning-keynote-qr.png", map[1].FileName);
        Assert.Equal("session-2-afternoon-panel-qr.png", map[2].FileName);
        Assert.Equal("session-3-qr.png", map[3].FileName);
        Assert.False(map.ContainsKey(4));

        // The folder is listed at most once per match call.
        Assert.Equal(1, store.ListCount(Folder));
    }

    /// <summary>
    /// 🔒 The deliberate break, pinned so nobody "fixes" it. Room-named files from §124 are still
    /// sitting in the folder and must now match NOTHING — a room fallback would hand a speaker the
    /// code for whoever ELSE was scheduled in their room, and a QR opening the wrong session's
    /// feedback form is worse than one that does not resolve at all.
    /// </summary>
    [Fact]
    public async Task MatchSessions_does_NOT_fall_back_to_a_legacy_room_named_file()
    {
        var store = new FakeQrStore(canRead: true, canStore: true)
        {
            [Folder] = { File("Room-16-Floor-1-Device13.png"), File("Hall-A1-Keynote-Floor-2-Device3.png") },
        };
        var svc = NewService(store);

        var map = await svc.MatchSessionsAsync(new[]
        {
            new SessionRoomRef(1, "Room 16"),
            new SessionRoomRef(2, "Hall A1"),
        });

        Assert.Empty(map);
    }

    [Fact]
    public async Task MatchSessions_is_inert_when_not_configured()
    {
        // Store can't read → nothing listed, nothing matched, nothing faked.
        var store = new FakeQrStore(canRead: false, canStore: false)
        {
            [Folder] = { File("Room-16-Floor-1-Device13.png") },
        };
        var svc = NewService(store);

        Assert.False(svc.CanRead);
        var map = await svc.MatchSessionsAsync(new[] { new SessionRoomRef(1, "Room 16") });
        Assert.Empty(map);
        Assert.Equal(0, store.ListCount(Folder));
    }

    [Fact]
    public async Task MatchSessions_is_inert_when_folder_blank()
    {
        var store = new FakeQrStore(canRead: true, canStore: true)
        {
            [Folder] = { File("Room-16-Floor-1-Device13.png") },
        };
        var svc = NewService(store, folder: "");

        Assert.False(svc.CanRead);
        var map = await svc.MatchSessionsAsync(new[] { new SessionRoomRef(1, "Room 16") });
        Assert.Empty(map);
    }

    // ---- download ----------------------------------------------------------

    [Fact]
    public async Task DownloadForRoom_returns_bytes_for_a_match_and_null_otherwise()
    {
        var store = new FakeQrStore(canRead: true, canStore: true)
        {
            [Folder] = { File("Room-16-Floor-1-Device13.png") },
        };
        var svc = NewService(store);

        var hit = await svc.DownloadForRoomAsync("Room 16");
        Assert.NotNull(hit);
        Assert.Equal("Room-16-Floor-1-Device13.png", hit!.FileName);
        Assert.Equal("image/png", hit.ContentType);
        Assert.NotEmpty(hit.Content);

        Assert.Null(await svc.DownloadForRoomAsync("Room 99"));
        Assert.Null(await svc.DownloadForRoomAsync(null));
    }

    // ---- org-admin write ---------------------------------------------------

    [Fact]
    public async Task Upload_and_Delete_delegate_to_the_store()
    {
        var store = new FakeQrStore(canRead: true, canStore: true);
        var svc = NewService(store);

        Assert.True(svc.CanManage);

        await svc.UploadAsync("Room-5-Floor-1-Device7.png", new byte[] { 9 }, "image/png");
        var listed = await svc.ListAllAsync();
        var only = Assert.Single(listed);
        Assert.Equal("Room 5", only.RoomDisplay);

        await svc.DeleteAsync("Room-5-Floor-1-Device7.png");
        Assert.Empty(await svc.ListAllAsync());
    }

    // ---- helpers -----------------------------------------------------------

    private static SharePointFileRef File(string name) =>
        new("item-" + name, name, "https://store.example.test/" + name);

    /// <summary>In-memory fake store: list/download/upload/delete against one folder.</summary>
    private sealed class FakeQrStore : ISharePointFileStore
    {
        private readonly Dictionary<string, List<SharePointFileRef>> _byFolder = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _listCounts = new(StringComparer.OrdinalIgnoreCase);

        public FakeQrStore(bool canRead, bool canStore) { CanRead = canRead; CanStore = canStore; }

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
        public bool CanStore { get; }

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default)
        {
            if (!CanRead) return Task.FromResult<IReadOnlyList<SharePointFileRef>>(Array.Empty<SharePointFileRef>());
            _listCounts[relativeFolder] = ListCount(relativeFolder) + 1;
            return Task.FromResult<IReadOnlyList<SharePointFileRef>>(
                _byFolder.TryGetValue(relativeFolder, out var list) ? list.ToList() : new List<SharePointFileRef>());
        }

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(CanRead ? new byte[] { 1, 2, 3 } : null);

        public Task<StoredFile> UploadToFolderAsync(
            string relativeFolder, string fileName, byte[] content, string contentType, CancellationToken ct = default)
        {
            if (!CanStore) throw new InvalidOperationException("cannot write");
            this[relativeFolder].RemoveAll(f => f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            this[relativeFolder].Add(File(fileName));
            return Task.FromResult(new StoredFile($"{relativeFolder}/{fileName}", "https://store.example.test/" + fileName, "item-" + fileName));
        }

        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default)
        {
            this[relativeFolder].RemoveAll(f => f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }

        // Write-to-root side unused by §124.
        public Task<StoredFile> StoreAsync(string relativePath, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("root store not used by §124");
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
    }
}
