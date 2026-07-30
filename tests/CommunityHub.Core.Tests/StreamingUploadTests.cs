using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §455 (operator 2026-07-27) — big uploads must STREAM, not be buffered whole in memory.
///
/// <para>A speaker's slide upload failed in production with
/// <c>TaskCanceledException</c> inside <c>FormFile.CopyToAsync</c>. The web layer was copying the
/// entire deck into a <c>MemoryStream</c> and then calling <c>ToArray()</c> — holding a
/// multi-hundred-MB file in RAM <b>twice</b>, on a shared App Service instance, before a single
/// byte reached SharePoint. The operator's concern was the right one: <i>"i am worried speakers
/// and others will get faiures"</i>, and the sponsor exhibitor-wall upload (also capped at 1 GB)
/// had the same shape.</para>
///
/// <para>These pin the behaviour that matters: the upload path hands the store a STREAM and the
/// declared length, and every rule that guards an upload still applies on that path — the
/// file-type gate, the §428 own-session check and the §68 version numbering are shared, not
/// duplicated, so the streaming route cannot quietly skip one.</para>
/// </summary>
public sealed class StreamingUploadTests
{
    /// <summary>Records HOW it was called: streamed (and with what declared length) vs buffered.</summary>
    private sealed class RecordingStore : ISharePointFileStore
    {
        public string? StreamedFileName;
        public long StreamedLength = -1;
        public int StreamedBytesRead;
        public string? BufferedFileName;

        public bool CanStore => true;
        public bool CanRead => true;

        public Task<StoredFile> StoreAsync(string relativePath, byte[] content, string contentType, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SharePointFileRef>>(Array.Empty<SharePointFileRef>());
        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default)
            => Task.FromResult<byte[]?>(null);
        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<StoredFile> UploadToFolderAsync(
            string relativeFolder, string fileName, byte[] content, string contentType, CancellationToken ct = default)
        {
            BufferedFileName = fileName;
            return Task.FromResult(new StoredFile($"{relativeFolder}/{fileName}", "https://sp.example/x", "item-1"));
        }

        public async Task<StoredFile> UploadStreamToFolderAsync(
            string relativeFolder, string fileName, Stream content, long contentLength,
            string contentType, CancellationToken ct = default)
        {
            StreamedFileName = fileName;
            StreamedLength = contentLength;
            var buf = new byte[8192];
            int n;
            while ((n = await content.ReadAsync(buf, ct)) > 0) StreamedBytesRead += n;
            return new StoredFile($"{relativeFolder}/{fileName}", "https://sp.example/x", "item-1");
        }
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"stream-upload-{Guid.NewGuid():N}").Options);

    private static SpeakerPresentationService NewService(CommunityHubDbContext db, RecordingStore store) =>
        new(store,
            Options.Create(new GraphicsSharePointOptions
            {
                Enabled = true, SiteUrl = "https://sp.example/site",
                PresentationPreviewFolderPath = $"Ev/{Guid.NewGuid():N}/Preview",
                PresentationFinalFolderPath = $"Ev/{Guid.NewGuid():N}/Final",
            }),
            db, TimeProvider.System);

    private static async Task<(int ev, int pid, int sid)> SeedAsync(CommunityHubDbContext db)
    {
        var ev = new Event { Code = "e", DisplayName = "E", CommunityName = "C", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var p = new Participant
        {
            EventId = ev.Id, FullName = "Speaker One", Email = "s@example.test",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(p);
        var s = new Session { EventId = ev.Id, Title = "My Session" };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s.Id, ParticipantId = p.Id });
        await db.SaveChangesAsync();
        return (ev.Id, p.Id, s.Id);
    }

    [Fact]
    public async Task The_deck_is_streamed_to_the_store_never_buffered()
    {
        using var db = NewDb();
        var (ev, pid, sid) = await SeedAsync(db);
        var store = new RecordingStore();
        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);
        using var src = new MemoryStream(payload);

        var name = await NewService(db, store).UploadStreamAsync(
            ev, pid, sid, PresentationKind.Preview, "deck.pptx", src, payload.Length);

        Assert.Equal($"{sid} - My Session_v1.pptx", name);
        Assert.Equal(name, store.StreamedFileName);
        Assert.Null(store.BufferedFileName);                 // the byte[] path was NOT used
        Assert.Equal(payload.Length, store.StreamedLength);  // exact declared length (Graph needs it)
        Assert.Equal(payload.Length, store.StreamedBytesRead);
    }

    /// <summary>
    /// The streaming route must not become a way around the upload rules. Same file-type gate.
    /// </summary>
    [Fact]
    public async Task The_streaming_path_still_rejects_a_disallowed_file_type()
    {
        using var db = NewDb();
        var (ev, pid, sid) = await SeedAsync(db);
        var store = new RecordingStore();
        using var src = new MemoryStream(new byte[16]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(db, store).UploadStreamAsync(
                ev, pid, sid, PresentationKind.Preview, "deck.exe", src, 16));

        Assert.Null(store.StreamedFileName);
    }

    /// <summary>And the §428 own-session gate — a deck cannot be filed against someone else's session.</summary>
    [Fact]
    public async Task The_streaming_path_still_enforces_the_own_session_gate()
    {
        using var db = NewDb();
        var (ev, pid, _) = await SeedAsync(db);
        var other = new Session { EventId = ev, Title = "Not Mine" };
        db.Sessions.Add(other);
        await db.SaveChangesAsync();
        var store = new RecordingStore();
        using var src = new MemoryStream(new byte[16]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(db, store).UploadStreamAsync(
                ev, pid, other.Id, PresentationKind.Preview, "deck.pdf", src, 16));

        Assert.Null(store.StreamedFileName);
    }

    /// <summary>
    /// The interface's DEFAULT streaming implementation must still work for stores that do not
    /// override it (the null store, test fakes) — otherwise adding the stream path would break
    /// every non-Graph implementation.
    /// </summary>
    [Fact]
    public async Task A_store_without_a_streaming_override_still_uploads_via_the_buffered_default()
    {
        var store = new BufferOnlyStore();
        using var src = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });

        // Through the INTERFACE — a default interface method is not visible on the concrete type,
        // which is also exactly how the production callers reach it (they hold ISharePointFileStore).
        ISharePointFileStore seam = store;
        var stored = await seam.UploadStreamToFolderAsync("Folder", "f.pdf", src, 5, "application/pdf");

        Assert.Equal("Folder/f.pdf", stored.Path);
        Assert.Equal(5, store.ReceivedBytes);
    }

    /// <summary>A store that implements ONLY the byte[] contract — i.e. the pre-§455 shape.</summary>
    private sealed class BufferOnlyStore : ISharePointFileStore
    {
        public int ReceivedBytes;
        public bool CanStore => true;
        public bool CanRead => false;
        public Task<StoredFile> StoreAsync(string relativePath, byte[] content, string contentType, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SharePointFileRef>>(Array.Empty<SharePointFileRef>());
        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default)
            => Task.FromResult<byte[]?>(null);
        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<StoredFile> UploadToFolderAsync(
            string relativeFolder, string fileName, byte[] content, string contentType, CancellationToken ct = default)
        {
            ReceivedBytes = content.Length;
            return Task.FromResult(new StoredFile($"{relativeFolder}/{fileName}", "u", "i"));
        }
    }
}
