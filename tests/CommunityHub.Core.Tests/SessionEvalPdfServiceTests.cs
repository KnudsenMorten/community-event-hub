using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// FINAL per-session evaluation PDFs (REQUIREMENTS §166): the organizer uploads a PDF, it
/// lands in a SharePoint folder under a DETERMINISTIC name (<c>session-{id}.pdf</c>) and is
/// streamed back to the session's speaker(s) through a HUB PROXY — never a SharePoint URL.
/// Uses a FAKE store (no external call). Proves: the proxy-url contract, the upload/list
/// round-trip + status, the inert (not-configured) path, and — the security-critical part —
/// the proxy ACCESS GATE (organizer + own-session speaker allowed; a foreign speaker / other
/// role denied). NO real data.
/// </summary>
public sealed class SessionEvalPdfServiceTests
{
    private const string Folder = "General/Events/ELDK 2027/EventHub/Speakers/SessionEvals-PDF";
    private const int EventId = 7;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"evalpdf-{Guid.NewGuid():N}")
            .Options);

    private static SessionEvalPdfService NewService(
        CommunityHubDbContext db, FakePdfStore store, string folder = Folder) =>
        new(store, Options.Create(new GraphicsSharePointOptions
        {
            Enabled = true,
            SiteUrl = "https://contoso.sharepoint.example.test/sites/eldk",
            SessionEvalPdfFolderPath = folder,
        }), db);

    /// <summary>Seed an organizer, two speakers, and a session with only the FIRST speaker.</summary>
    private static async Task<(int sessionId, int organizerId, int ownSpeakerId, int otherSpeakerId)>
        SeedAsync(CommunityHubDbContext db)
    {
        var org = new Participant { EventId = EventId, Email = "org@example.test", FullName = "Org Person", Role = ParticipantRole.Organizer };
        var own = new Participant { EventId = EventId, Email = "own@example.test", FullName = "Own Speaker", Role = ParticipantRole.Speaker };
        var other = new Participant { EventId = EventId, Email = "other@example.test", FullName = "Other Speaker", Role = ParticipantRole.Speaker };
        db.Participants.AddRange(org, own, other);
        await db.SaveChangesAsync();

        var session = new Session { EventId = EventId, Title = "Securing Your Cloud", Type = SessionType.TechnicalSession };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = session.Id, ParticipantId = own.Id });
        await db.SaveChangesAsync();

        return (session.Id, org.Id, own.Id, other.Id);
    }

    // ---- the proxy-url contract -------------------------------------------

    [Fact]
    public void ProxyUrl_and_file_name_are_deterministic_and_never_a_sharepoint_url()
    {
        Assert.Equal("/session-eval/42/download", SessionEvalPdfService.ProxyUrlFor(42));
        Assert.Equal("session-42.pdf", SessionEvalPdfService.FileNameFor(42));
        Assert.DoesNotContain("sharepoint", SessionEvalPdfService.ProxyUrlFor(42), StringComparison.OrdinalIgnoreCase);
    }

    // ---- upload + list status round-trip ----------------------------------

    [Fact]
    public async Task Upload_then_list_marks_the_session_as_having_a_pdf()
    {
        using var db = NewDb();
        var (sessionId, _, _, _) = await SeedAsync(db);
        var store = new FakePdfStore(canRead: true, canStore: true);
        var svc = NewService(db, store);

        // Before any upload: listed, but not uploaded.
        var before = await svc.ListSessionsAsync(EventId);
        var row = Assert.Single(before);
        Assert.Equal(sessionId, row.SessionId);
        Assert.False(row.HasPdf);
        Assert.Equal(new[] { "Own Speaker" }, row.SpeakerNames);

        await svc.UploadAsync(sessionId, new byte[] { 1, 2, 3 });

        var after = await svc.ListSessionsAsync(EventId);
        Assert.True(Assert.Single(after).HasPdf);
        Assert.Equal(SessionEvalPdfService.FileNameFor(sessionId), Assert.Single(store[Folder]).Name);
    }

    // ---- the ACCESS GATE (the security-critical part) ---------------------

    [Fact]
    public async Task Proxy_allows_an_organizer_and_the_own_session_speaker_but_denies_a_foreign_speaker()
    {
        using var db = NewDb();
        var (sessionId, organizerId, ownSpeakerId, otherSpeakerId) = await SeedAsync(db);
        var store = new FakePdfStore(canRead: true, canStore: true);
        var svc = NewService(db, store);
        await svc.UploadAsync(sessionId, new byte[] { 9, 9, 9 });

        // Organizer in this edition → allowed.
        var asOrg = await svc.GetPdfForParticipantAsync(EventId, organizerId, ParticipantRole.Organizer, sessionId);
        Assert.NotNull(asOrg);
        Assert.Equal(SessionEvalPdfService.FileNameFor(sessionId), asOrg!.FileName);
        Assert.NotEmpty(asOrg.Content);

        // The speaker ON this session → allowed.
        var asOwn = await svc.GetPdfForParticipantAsync(EventId, ownSpeakerId, ParticipantRole.Speaker, sessionId);
        Assert.NotNull(asOwn);

        // A DIFFERENT speaker, not on this session → denied (→ 404).
        var asOther = await svc.GetPdfForParticipantAsync(EventId, otherSpeakerId, ParticipantRole.Speaker, sessionId);
        Assert.Null(asOther);
    }

    [Fact]
    public async Task Proxy_denies_when_the_session_belongs_to_another_edition()
    {
        using var db = NewDb();
        var (sessionId, organizerId, _, _) = await SeedAsync(db);
        var store = new FakePdfStore(canRead: true, canStore: true);
        var svc = NewService(db, store);
        await svc.UploadAsync(sessionId, new byte[] { 1 });

        // Same organizer id, WRONG event id → denied.
        Assert.Null(await svc.GetPdfForParticipantAsync(EventId + 1, organizerId, ParticipantRole.Organizer, sessionId));
    }

    // ---- inert when not configured ----------------------------------------

    [Fact]
    public async Task Is_inert_when_the_store_cannot_read_or_the_folder_is_blank()
    {
        using var db = NewDb();
        var (sessionId, organizerId, _, _) = await SeedAsync(db);

        // Store can't read/write → nothing uploaded-flagged, nothing streamed, nothing faked.
        var noStore = NewService(db, new FakePdfStore(canRead: false, canStore: false));
        Assert.False(noStore.CanRead);
        Assert.False(noStore.CanManage);
        Assert.False(Assert.Single(await noStore.ListSessionsAsync(EventId)).HasPdf);
        Assert.Null(await noStore.GetPdfForParticipantAsync(EventId, organizerId, ParticipantRole.Organizer, sessionId));

        // Folder blank → also inert even with a capable store.
        var blank = NewService(db, new FakePdfStore(canRead: true, canStore: true), folder: "");
        Assert.False(blank.CanRead);
        Assert.False(blank.CanManage);
    }

    // ---- speaker contacts to notify ---------------------------------------

    [Fact]
    public async Task Speaker_contacts_returns_the_sessions_speakers_with_an_email()
    {
        using var db = NewDb();
        var (sessionId, _, _, _) = await SeedAsync(db);
        var svc = NewService(db, new FakePdfStore(canRead: true, canStore: true));

        var contacts = await svc.GetSpeakerContactsAsync(EventId, sessionId);
        var only = Assert.Single(contacts);
        Assert.Equal("own@example.test", only.Email);
        Assert.Equal("Own Speaker", only.FullName);
    }

    // ---- helpers -----------------------------------------------------------

    private static SharePointFileRef File(string name) =>
        new("item-" + name, name, "https://store.example.test/" + name);

    /// <summary>In-memory fake store: list/download/upload/delete against one folder.</summary>
    private sealed class FakePdfStore : ISharePointFileStore
    {
        private readonly Dictionary<string, List<SharePointFileRef>> _byFolder = new(StringComparer.OrdinalIgnoreCase);

        public FakePdfStore(bool canRead, bool canStore) { CanRead = canRead; CanStore = canStore; }

        public List<SharePointFileRef> this[string folder]
        {
            get
            {
                if (!_byFolder.TryGetValue(folder, out var list)) { list = new(); _byFolder[folder] = list; }
                return list;
            }
        }

        public bool CanRead { get; }
        public bool CanStore { get; }

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(
                CanRead && _byFolder.TryGetValue(relativeFolder, out var list) ? list.ToList() : new List<SharePointFileRef>());

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

        // Write-to-root side unused by §166.
        public Task<StoredFile> StoreAsync(string relativePath, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("root store not used by §166");
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
    }
}
