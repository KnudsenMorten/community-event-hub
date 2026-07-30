using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §322/§322c — speakers upload their session decks IN THE HUB and the app registration
/// writes them to SharePoint (speakers never see a SharePoint login); attendees browse the
/// LATEST deck anonymously. These pin: §68-CONSISTENT versioned naming
/// ("{sessionId} - {Title}_v{N}.{ext}", latest wins, nothing deleted), the own-session
/// gate, the PDF/PPTX/ZIP gate, the speakerdl task auto-complete, the public listing and
/// the inert-until-configured contract.
/// </summary>
public sealed class SpeakerPresentationServiceTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"spkpres-{Guid.NewGuid():N}").Options);

    private sealed class FakeStore : ISharePointFileStore
    {
        public readonly List<(string Folder, string FileName)> Uploaded = new();
        public readonly List<(string Folder, string FileName)> Deleted = new();
        public readonly List<SharePointFileRef> Existing = new();

        public bool CanStore => true;
        public bool CanRead => true;

        public Task<StoredFile> StoreAsync(string relativePath, byte[] content, string contentType, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SharePointFileRef>>(Existing.ToList());
        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default)
            => Task.FromResult<byte[]?>(new byte[] { 9, 9 });
        public Task<StoredFile> UploadToFolderAsync(string relativeFolder, string fileName, byte[] content, string contentType, CancellationToken ct = default)
        {
            Uploaded.Add((relativeFolder, fileName));
            return Task.FromResult(new StoredFile($"{relativeFolder}/{fileName}", "https://sp.example/" + fileName, "item-1"));
        }
        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default)
        {
            Deleted.Add((relativeFolder, fileName));
            return Task.CompletedTask;
        }
    }

    private static SpeakerPresentationService NewService(
        CommunityHubDbContext db, FakeStore store, bool configured = true) =>
        new(store,
            Options.Create(new GraphicsSharePointOptions
            {
                Enabled = true, SiteUrl = "https://sp.example/site",
                // Unique per-test folder names — the listing cache is static.
                PresentationPreviewFolderPath = configured ? $"Ev/{Guid.NewGuid():N}/Preview" : "",
                PresentationFinalFolderPath = configured ? $"Ev/{Guid.NewGuid():N}/Final" : "",
            }),
            db, TimeProvider.System);

    private static async Task<(int ev, int pid, int sessionId)> SeedAsync(CommunityHubDbContext db)
    {
        var ev = new Event { Code = "e", DisplayName = "E", CommunityName = "C", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var p = new Participant
        {
            EventId = ev.Id, FullName = "Morten Knudsen", Email = "s@x.dk",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(p);
        var s = new Session { EventId = ev.Id, Title = "ELDK27 Welcome" };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s.Id, ParticipantId = p.Id });
        await db.SaveChangesAsync();
        return (ev.Id, p.Id, s.Id);
    }

    [Fact]
    public async Task Upload_writes_v1_and_marks_the_task_done()
    {
        using var db = NewDb();
        var (ev, pid, sid) = await SeedAsync(db);
        db.Tasks.Add(new ParticipantTask
        {
            EventId = ev, AssignedParticipantId = pid, Title = "Upload preview presentation",
            State = TaskState.Open, SourceKey = $"speakerdl:{pid}:upload-preview-presentation",
        });
        await db.SaveChangesAsync();
        var store = new FakeStore();
        var svc = NewService(db, store);

        var name = await svc.UploadAsync(ev, pid, sid, PresentationKind.Preview,
            "My Deck.PPTX", new byte[] { 1, 2, 3 });

        Assert.Equal($"{sid} - ELDK27 Welcome_v1.pptx", name);
        Assert.Equal(name, Assert.Single(store.Uploaded).FileName);
        Assert.Empty(store.Deleted);   // §68 consistency: versions are KEPT, never deleted
        var task = await db.Tasks.SingleAsync();
        Assert.Equal(TaskState.Done, task.State);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public async Task Reupload_becomes_the_next_version_and_older_versions_stay()
    {
        using var db = NewDb();
        var (ev, pid, sid) = await SeedAsync(db);
        var store = new FakeStore();
        store.Existing.Add(new SharePointFileRef("i1", $"{sid} - Old Title_v1.pdf", ""));
        store.Existing.Add(new SharePointFileRef("i2", $"{sid} - Old Title_v2.pdf", ""));
        store.Existing.Add(new SharePointFileRef("i3", "999 - Other Session_v7.pdf", ""));
        var svc = NewService(db, store);

        var name = await svc.UploadAsync(ev, pid, sid, PresentationKind.Preview,
            "deck.pptx", new byte[] { 1 });

        Assert.Equal($"{sid} - ELDK27 Welcome_v3.pptx", name);   // max own version (2) + 1
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public async Task Upload_is_gated_to_the_speakers_own_sessions()
    {
        using var db = NewDb();
        var (ev, pid, _) = await SeedAsync(db);
        var other = new Session { EventId = ev, Title = "Not Mine" };
        db.Sessions.Add(other);
        await db.SaveChangesAsync();
        var svc = NewService(db, new FakeStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UploadAsync(ev, pid, other.Id, PresentationKind.Preview, "deck.pdf", new byte[] { 1 }));
    }

    /// <summary>
    /// §428 — the divergence that made a deck filable against a session the speaker was told
    /// does not exist. The upload gate used to ask "is there a SessionSpeaker row?" while every
    /// LISTING surface additionally required <c>!IsServiceSession</c>, so a service-flagged
    /// session was uploadable but invisible. Both now go through
    /// <see cref="CommunityHub.Core.Data.SpeakerSessionScope.MineAsSpeaker"/>, and this test is
    /// what keeps them together: it asserts the SAME session is absent from the slots AND
    /// rejected by the upload. Add the clause back to only one of them and this fails.
    /// </summary>
    [Fact]
    public async Task A_service_session_is_neither_listed_nor_uploadable()
    {
        using var db = NewDb();
        var (ev, pid, sid) = await SeedAsync(db);

        var service = new Session { EventId = ev, Title = "Lunch", IsServiceSession = true };
        db.Sessions.Add(service);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = service.Id, ParticipantId = pid });
        await db.SaveChangesAsync();

        var svc = NewService(db, new FakeStore());

        // Not offered: the speaker's upload page shows only the real session.
        var slots = await svc.GetSessionSlotsAsync(ev, pid);
        Assert.Equal(sid, Assert.Single(slots).SessionId);

        // And not accepted either — the two checks now give the same answer.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UploadAsync(ev, pid, service.Id, PresentationKind.Preview, "deck.pdf", new byte[] { 1 }));
    }

    [Fact]
    public async Task Non_pdf_pptx_zip_uploads_are_rejected()
    {
        using var db = NewDb();
        var (ev, pid, sid) = await SeedAsync(db);
        var svc = NewService(db, new FakeStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UploadAsync(ev, pid, sid, PresentationKind.Final, "deck.exe", new byte[] { 1 }));
    }

    [Fact]
    public async Task Public_listing_serves_the_latest_version_per_session()
    {
        using var db = NewDb();
        var (ev, pid, sid) = await SeedAsync(db);
        _ = pid;
        var store = new FakeStore();
        store.Existing.Add(new SharePointFileRef("i1", $"{sid} - ELDK27 Welcome_v1.pdf", ""));
        store.Existing.Add(new SharePointFileRef("i2", $"{sid} - ELDK27 Welcome_v2.pptx", ""));
        var svc = NewService(db, store);

        var rows = await svc.ListPublicAsync();
        var row = Assert.Single(rows);
        Assert.Equal($"{sid} - ELDK27 Welcome_v2.pptx", row.PreviewFileName);
        Assert.Contains("Morten Knudsen", row.SpeakerNames);

        var deck = await svc.GetDeckAsync(sid, PresentationKind.Preview);
        Assert.NotNull(deck);
        Assert.Equal($"{sid} - ELDK27 Welcome_v2.pptx", deck!.FileName);
        Assert.Equal("application/vnd.openxmlformats-officedocument.presentationml.presentation", deck.ContentType);
    }

    [Fact]
    public async Task Hits_count_one_number_per_session_plus_the_edition_total()
    {
        // §322h (operator): "tracking should count number of views or downloads (=1
        // number)" — "per session + 1 total number".
        using var db = NewDb();
        var (ev, pid, sid) = await SeedAsync(db);
        var other = new Session { EventId = ev, Title = "Second" };
        db.Sessions.Add(other);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = other.Id, ParticipantId = pid });
        await db.SaveChangesAsync();
        var svc = NewService(db, new FakeStore());

        await svc.RecordHitAsync(sid);
        await svc.RecordHitAsync(sid);
        await svc.RecordHitAsync(other.Id);
        await svc.RecordHitAsync(999_999);   // unknown session — silently ignored

        var stats = await svc.GetStatsAsync(ev, new[] { sid, other.Id });
        Assert.Equal(2, stats[sid]);
        Assert.Equal(1, stats[other.Id]);
        Assert.Equal(3, await svc.GetTotalAsync(ev));
    }

    [Fact]
    public void Unconfigured_folders_keep_the_upload_inert()
    {
        using var db = NewDb();
        var svc = NewService(db, new FakeStore(), configured: false);
        Assert.False(svc.CanUpload(PresentationKind.Preview));
        Assert.False(svc.CanUpload(PresentationKind.Final));
        Assert.False(svc.CanRead(PresentationKind.Preview));
    }
}
