using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1390 — an organizer uploads a picture in the SoMe post editor (organizer request 2026-11-09:
/// "it would be good if we could upload a picture"). It lands in the graphics folder for the post's type
/// under a safe, unique name, and only real PNG / JPG / GIF content is accepted.
/// </summary>
public class SoMePictureUploadTests
{
    private static readonly byte[] PngBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
    private static readonly byte[] GifBytes = { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 1, 2 };
    private static readonly byte[] JpgBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2 };

    private sealed class Paths : IDocLibraryPathResolver
    {
        public bool IsConfigured => true;
        public string Resolve(string key) => "Lib/" + key;
        public bool TryResolve(string key, out string path) { path = "Lib/" + key; return true; }
        public bool TryResolveFileName(string key, out string fileName) { fileName = string.Empty; return false; }
        public IReadOnlyList<DocLibraryResolvedPath> All() => Array.Empty<DocLibraryResolvedPath>();
        public IReadOnlyList<string> Validate() => Array.Empty<string>();
    }

    private sealed class Store : ISharePointFileStore
    {
        public bool Writable { get; init; } = true;
        public List<(string Folder, string Name, byte[] Bytes, string Type)> Uploads { get; } = new();
        public List<string> Existing { get; } = new();
        public bool CanStore => Writable;
        public bool CanRead => true;
        public Task<StoredFile> StoreAsync(string relativePath, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("not used");
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(
                Existing.Concat(Uploads.Select(u => u.Name)).Select(n => new SharePointFileRef("id-" + n, n, "https://x/" + n, 10)).ToList());
        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task<StoredFile> UploadToFolderAsync(string relativeFolder, string fileName, byte[] content, string contentType, CancellationToken ct = default)
        {
            Uploads.Add((relativeFolder, fileName, content, contentType));
            return Task.FromResult(new StoredFile(relativeFolder + "/" + fileName, "https://x/" + fileName, "id"));
        }
        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Upload_goes_to_the_graphics_folder_of_the_post_type_and_returns_the_name_to_link()
    {
        var store = new Store();
        var lib = new SoMeGraphicLibrary(store, new Paths());

        var r = await lib.UploadPictureAsync(SoMeTemplateKind.SpeakerTracks, "All tracks.gif", GifBytes);

        Assert.True(r.Ok, r.Error);
        Assert.Equal("All-tracks.gif", r.FileName);
        var up = Assert.Single(store.Uploads);
        Assert.Equal("Lib/" + DocLibraryPaths.SpeakerTrackGraphics, up.Folder);
        Assert.Equal("image/gif", up.Type);

        // A hand-written post (no type) uses the event graphics folder, like the picker.
        await lib.UploadPictureAsync(null, "x.png", PngBytes);
        Assert.Equal("Lib/" + DocLibraryPaths.EventSoMeGraphics, store.Uploads[^1].Folder);
    }

    [Fact]
    public async Task A_taken_name_gets_a_number_instead_of_replacing_another_posts_picture()
    {
        var store = new Store();
        store.Existing.Add("banner.png");
        store.Existing.Add("banner-2.png");
        var lib = new SoMeGraphicLibrary(store, new Paths());

        var r = await lib.UploadPictureAsync(null, "Banner.png", PngBytes);

        Assert.Equal("Banner-3.png", r.FileName);
    }

    [Theory]
    [InlineData("notes.pdf")]          // not a picture name
    [InlineData("evil.png")]           // JPEG bytes under a PNG name
    [InlineData("clip.mp4")]
    public async Task Only_real_png_jpg_gif_content_is_accepted(string name)
    {
        var store = new Store();
        var lib = new SoMeGraphicLibrary(store, new Paths());

        var r = await lib.UploadPictureAsync(null, name, JpgBytes);

        Assert.False(r.Ok);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
        Assert.Empty(store.Uploads);
    }

    [Fact]
    public async Task Too_large_empty_or_unwritable_is_refused_with_a_reason()
    {
        var lib = new SoMeGraphicLibrary(new Store(), new Paths());
        var big = new byte[SoMeGraphicLibrary.MaxUploadBytes + 1];
        PngBytes.CopyTo(big, 0);
        Assert.Contains("limit", (await lib.UploadPictureAsync(null, "a.png", big)).Error);
        Assert.Contains("empty", (await lib.UploadPictureAsync(null, "a.png", Array.Empty<byte>())).Error);

        var readOnly = new SoMeGraphicLibrary(new Store { Writable = false }, new Paths());
        Assert.False(readOnly.CanUpload);
        Assert.False((await readOnly.UploadPictureAsync(null, "a.png", PngBytes)).Ok);
    }

    [Theory]
    [InlineData(@"C:\Users\x\My Photo.JPG", "My-Photo.jpg")]
    [InlineData("../../secret/../a b.png", "a-b.png")]
    [InlineData("æøå.png", "picture.png")]
    [InlineData("tracks-all-speakers.gif", "tracks-all-speakers.gif")]
    public void Upload_names_are_a_safe_leaf(string original, string expected) =>
        Assert.Equal(expected, SoMeGraphicLibrary.SafeUploadName(original));

    [Fact]
    public void Content_is_sniffed_from_the_first_bytes()
    {
        Assert.Equal("png", SoMeGraphicLibrary.SniffPicture(PngBytes));
        Assert.Equal("gif", SoMeGraphicLibrary.SniffPicture(GifBytes));
        Assert.Equal("jpeg", SoMeGraphicLibrary.SniffPicture(JpgBytes));
        Assert.Null(SoMeGraphicLibrary.SniffPicture(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    }
}
