using System.Text;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1078 — the media crew's picture/video libraries, run through the APP's credentials.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"Both links give the media team permissions with the app so they
/// have full permissions to add/delete files. it should not run in their user context, but through
/// the app context"</i>.</para>
///
/// <para>🔴 <b>The delete tests are the ones that earn their place.</b> He asked for delete, so the
/// hub can now remove a file from the event's document library on a click — and the file NAME comes
/// from a form. These pin the rule that makes that safe: a delete only ever acts on a name the
/// folder's own listing returned, so a crafted value has nothing to address.</para>
/// </remarks>
public sealed class MediaLibraryServiceTests
{
    private sealed class FakeStore : ISharePointFileStore
    {
        private readonly Dictionary<string, Dictionary<string, byte[]>> _folders =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> Deleted { get; } = new();
        public bool CanStore { get; set; } = true;
        public bool CanRead { get; set; } = true;

        public void Seed(string folder, string name, string content = "x")
        {
            if (!_folders.TryGetValue(folder, out var f))
                _folders[folder] = f = new(StringComparer.OrdinalIgnoreCase);
            f[name] = Encoding.UTF8.GetBytes(content);
        }

        public bool Has(string folder, string name) =>
            _folders.TryGetValue(folder, out var f) && f.ContainsKey(name);

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(
            string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(
                _folders.TryGetValue(relativeFolder, out var f)
                    ? f.Select(kv => new SharePointFileRef(
                        $"{relativeFolder}|{kv.Key}", kv.Key, string.Empty, kv.Value.Length,
                        DateTimeOffset.UnixEpoch.AddDays(kv.Key.Length))).ToList()
                    : []);

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default)
        {
            var parts = itemId.Split('|');
            return Task.FromResult(
                _folders.TryGetValue(parts[0], out var f) && f.TryGetValue(parts[1], out var b)
                    ? b : null);
        }

        public Task<StoredFile> UploadToFolderAsync(
            string relativeFolder, string fileName, byte[] content, string contentType,
            CancellationToken ct = default)
        {
            Seed(relativeFolder, fileName, Encoding.UTF8.GetString(content));
            return Task.FromResult(new StoredFile($"{relativeFolder}/{fileName}", string.Empty, "id"));
        }

        public Task DeleteFromFolderAsync(string folder, string name, CancellationToken ct = default)
        {
            Deleted.Add($"{folder}/{name}");
            if (_folders.TryGetValue(folder, out var f)) f.Remove(name);
            return Task.CompletedTask;
        }

        public Task<StoredFile> StoreAsync(string p, byte[] c, string t, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(string p, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static (MediaLibraryService Svc, FakeStore Store, string Pictures, string Video) New()
    {
        var paths = TestDocLibrary.Resolver();
        var store = new FakeStore();
        return (new MediaLibraryService(store, paths),
                store,
                paths.Resolve(DocLibraryPaths.MediaPictures),
                paths.Resolve(DocLibraryPaths.MediaVideo));
    }

    private static Stream Bytes(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    /// <summary>The two folders are separate libraries — a picture must not land among the video.</summary>
    [Fact]
    public async Task Pictures_and_video_are_two_folders()
    {
        var (svc, store, pictures, video) = New();

        await svc.UploadAsync(MediaLibraryKind.Pictures, "keynote.jpg", Bytes("a"), 1, "image/jpeg");
        await svc.UploadAsync(MediaLibraryKind.Video, "keynote.mp4", Bytes("b"), 1, "video/mp4");

        Assert.True(store.Has(pictures, "keynote.jpg"));
        Assert.True(store.Has(video, "keynote.mp4"));
        Assert.False(store.Has(pictures, "keynote.mp4"));
    }

    /// <summary>Newest first: a crew looks for what was just added, not what sorts first.</summary>
    [Fact]
    public async Task The_listing_puts_the_newest_file_first()
    {
        var (svc, store, pictures, _) = New();
        store.Seed(pictures, "a.jpg");                  // the fake dates by name length
        store.Seed(pictures, "a-much-longer-name.jpg");

        var files = await svc.ListAsync(MediaLibraryKind.Pictures);

        Assert.Equal("a-much-longer-name.jpg", files[0].FileName);
    }

    /// <summary>
    /// 🔒 A browser may send a full client path, and a hostile caller may send a traversal. Neither
    /// decides where a byte lands: only the file's own name survives.
    /// </summary>
    [Theory]
    [InlineData(@"C:\photos\keynote.jpg", "keynote.jpg")]
    [InlineData("../../secrets.jpg", "secrets.jpg")]
    [InlineData("  spaced.png  ", "spaced.png")]
    public void A_supplied_path_is_reduced_to_its_file_name(string raw, string expected)
        => Assert.Equal(expected, MediaLibraryService.SafeName(raw));

    /// <summary>
    /// 🔴 The delete rule. The folder's own LISTING is the allowlist, so a name that is not in the
    /// folder is refused outright rather than passed down to the store.
    /// </summary>
    [Fact]
    public async Task A_delete_only_acts_on_a_file_the_folder_actually_lists()
    {
        var (svc, store, pictures, _) = New();
        store.Seed(pictures, "keynote.jpg");

        Assert.False(await svc.DeleteAsync(MediaLibraryKind.Pictures, "../../elsewhere.jpg"));
        Assert.False(await svc.DeleteAsync(MediaLibraryKind.Pictures, "never-existed.jpg"));
        Assert.Empty(store.Deleted);

        Assert.True(await svc.DeleteAsync(MediaLibraryKind.Pictures, "keynote.jpg"));
        Assert.False(store.Has(pictures, "keynote.jpg"));
    }

    /// <summary>A file in the OTHER library is not this library's to delete.</summary>
    [Fact]
    public async Task A_delete_cannot_reach_into_the_other_library()
    {
        var (svc, store, _, video) = New();
        store.Seed(video, "session.mp4");

        Assert.False(await svc.DeleteAsync(MediaLibraryKind.Pictures, "session.mp4"));
        Assert.True(store.Has(video, "session.mp4"));
    }

    /// <summary>
    /// ⚠️ A small DENYLIST, not an allowlist. A photographer's real work arrives in formats we did
    /// not think of (<c>.cr3</c>, <c>.arw</c>, a <c>.xmp</c> sidecar), and rejecting those teaches
    /// them the hub is broken. What must not land is something a machine would EXECUTE.
    /// </summary>
    [Theory]
    [InlineData("shot.cr3", true)]
    [InlineData("clip.mkv", true)]
    [InlineData("sidecar.xmp", true)]
    [InlineData("evil.exe", false)]
    [InlineData("evil.ps1", false)]
    [InlineData("page.html", false)]
    public void Executables_are_refused_and_unusual_camera_formats_are_not(string name, bool accepted)
        => Assert.Equal(accepted, new MediaLibraryService(new FakeStore(), TestDocLibrary.Resolver())
            .RejectionReason(name) is null);

    [Fact]
    public async Task A_blocked_file_is_never_stored()
    {
        var (svc, store, pictures, _) = New();

        Assert.False(await svc.UploadAsync(
            MediaLibraryKind.Pictures, "evil.exe", Bytes("x"), 1, "application/octet-stream"));
        Assert.False(store.Has(pictures, "evil.exe"));
    }

    /// <summary>
    /// 🔒 INERT when the store cannot write: uploads and deletes refuse rather than pretending, and
    /// the page reads <c>CanManage</c> to say so instead of showing controls that do nothing.
    /// </summary>
    [Fact]
    public async Task With_no_wired_store_nothing_is_faked()
    {
        var (svc, store, pictures, _) = New();
        store.Seed(pictures, "keynote.jpg");
        store.CanStore = false;
        store.CanRead = false;

        Assert.False(svc.CanManage(MediaLibraryKind.Pictures));
        Assert.False(svc.CanRead(MediaLibraryKind.Pictures));
        Assert.Empty(await svc.ListAsync(MediaLibraryKind.Pictures));
        Assert.False(await svc.UploadAsync(
            MediaLibraryKind.Pictures, "a.jpg", Bytes("x"), 1, "image/jpeg"));
        Assert.False(await svc.DeleteAsync(MediaLibraryKind.Pictures, "keynote.jpg"));
        Assert.Null(await svc.DownloadAsync(MediaLibraryKind.Pictures, "keynote.jpg"));
    }

    /// <summary>
    /// The download goes through the hub — bytes, with a content type — because a media person must
    /// never need (or receive) a SharePoint link. That is the §160 rule this whole page exists for.
    /// </summary>
    [Fact]
    public async Task A_download_returns_the_bytes_through_the_hub()
    {
        var (svc, store, pictures, _) = New();
        store.Seed(pictures, "keynote.jpg", "PICTURE");

        var file = await svc.DownloadAsync(MediaLibraryKind.Pictures, "keynote.jpg");

        Assert.NotNull(file);
        Assert.Equal("keynote.jpg", file!.FileName);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal("PICTURE", Encoding.UTF8.GetString(file.Content));
    }

    /// <summary>The two registered folders are the ones he pre-staged in SharePoint.</summary>
    [Fact]
    public void The_registry_points_at_the_folders_he_staged()
    {
        Assert.EndsWith("Event/Media/Pictures", TestDocLibrary.PathFor(DocLibraryPaths.MediaPictures));
        Assert.EndsWith("Event/Media/Video", TestDocLibrary.PathFor(DocLibraryPaths.MediaVideo));
    }

    // =====================================================================
    //  §1078b — "sharepoint goes to diff urls depending on env" (operator)
    // =====================================================================

    private static MediaLibraryService For(
        string root, DocLibraryOptions? docLibrary = null, GraphicsSharePointOptions? graphics = null)
        => new(new FakeStore(),
               TestDocLibrary.Resolver(root),
               docLibrary is null ? null : new StaticMonitor(docLibrary),
               graphics is null ? null : Microsoft.Extensions.Options.Options.Create(graphics));

    private sealed class StaticMonitor(DocLibraryOptions value)
        : Microsoft.Extensions.Options.IOptionsMonitor<DocLibraryOptions>
    {
        public DocLibraryOptions CurrentValue => value;
        public DocLibraryOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<DocLibraryOptions, string?> listener) => null;
    }

    /// <summary>
    /// 🔑 The SAME key resolves to a different folder per environment, because the ROOT is the
    /// environment's. This is the operator's point stated as a test: DEV and PROD are two libraries,
    /// and the page shows which one it is looking at.
    /// </summary>
    [Theory]
    [InlineData("General/DEVELOPMENT/EventHub", "General/DEVELOPMENT/EventHub/Event/Media/Pictures")]
    [InlineData("General/Events/ELDK 2027/EventHub", "General/Events/ELDK 2027/EventHub/Event/Media/Pictures")]
    public void The_folder_resolves_against_this_environments_root(string root, string expected)
        => Assert.Equal(expected, For(root).ResolvedFolder(MediaLibraryKind.Pictures));

    /// <summary>
    /// 🔴 The invariant. The FOLDER comes from <c>DocLibrary:*</c> and the FILE is written through
    /// <c>Graphics:SharePoint:*</c>. If those name different sites, an upload succeeds somewhere
    /// nobody is looking — the §767 failure exactly. The library refuses instead.
    /// </summary>
    [Fact]
    public void A_site_mismatch_between_the_two_sections_closes_the_library()
    {
        var svc = For("General/TEST/EventHub",
            new DocLibraryOptions { Enabled = true, SiteUrl = "https://sp.test/sites/a", DriveName = "Documents" },
            new GraphicsSharePointOptions { Enabled = true, SiteUrl = "https://sp.test/sites/B-DIFFERENT", DriveName = "Documents" });

        Assert.NotNull(svc.ConfigurationProblem);
        Assert.Contains("sites/a", svc.ConfigurationProblem);
        Assert.Contains("B-DIFFERENT", svc.ConfigurationProblem);
        // 🔒 Not merely reported — refused. Writing to a path computed for another site is worse
        // than declining to write at all.
        Assert.False(svc.CanManage(MediaLibraryKind.Pictures));
    }

    /// <summary>The same rule for the DRIVE: the right path in the wrong library is the wrong file.</summary>
    [Fact]
    public void A_drive_mismatch_closes_it_too()
    {
        var svc = For("General/TEST/EventHub",
            new DocLibraryOptions { Enabled = true, SiteUrl = "https://sp.test/sites/a", DriveName = "Documents" },
            new GraphicsSharePointOptions { Enabled = true, SiteUrl = "https://sp.test/sites/a", DriveName = "Archive" });

        Assert.Contains("Archive", svc.ConfigurationProblem);
        Assert.False(svc.CanManage(MediaLibraryKind.Pictures));
    }

    /// <summary>
    /// ⚠️ Agreement is the normal case and must NOT be reported as a problem — a warning that fires
    /// when everything is correct is a warning nobody reads.
    /// </summary>
    [Fact]
    public void Matching_sections_are_not_a_problem()
    {
        var svc = For("General/TEST/EventHub",
            new DocLibraryOptions { Enabled = true, SiteUrl = "https://sp.test/sites/a/", DriveName = "Documents" },
            new GraphicsSharePointOptions { Enabled = true, SiteUrl = "https://sp.test/sites/a", DriveName = "Documents" });

        Assert.Null(svc.ConfigurationProblem);
        Assert.True(svc.CanManage(MediaLibraryKind.Pictures));
    }

    /// <summary>
    /// 🔒 With one side unknown there is nothing to compare, and inventing a mismatch would close a
    /// working library. Silence is the correct answer to a question that was not asked.
    /// </summary>
    [Fact]
    public void An_unknown_section_is_not_treated_as_a_mismatch()
    {
        var svc = For("General/TEST/EventHub");

        Assert.Null(svc.ConfigurationProblem);
        Assert.True(svc.CanManage(MediaLibraryKind.Pictures));
    }
}
