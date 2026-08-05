using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §769.4 / work-order §6.2 — per-session deck state for the organizer's session list.
/// </summary>
/// <remarks>
/// The question the column answers is <i>"which sessions still have no deck?"</i>, asked before a
/// deadline about sessions that are not the asker's own. What is pinned here is the part that is
/// easy to get subtly wrong: the LATEST version wins, a renamed session still matches its files,
/// and one session's deck never counts for another (id 1 vs id 12).
/// </remarks>
public sealed class SessionDeckStateTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"decks-{Guid.NewGuid():N}")
            .Options);

    private sealed class FakeStore : ISharePointFileStore
    {
        private readonly Dictionary<string, List<SharePointFileRef>> _folders =
            new(StringComparer.OrdinalIgnoreCase);

        public void Put(string folder, string name, DateTimeOffset? modified = null) =>
            (_folders.TryGetValue(folder, out var l) ? l : _folders[folder] = new())
                .Add(new SharePointFileRef($"{folder}/{name}", name, string.Empty, 10, modified));

        public bool CanStore => true;
        public bool CanRead => true;

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(
            string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(
                _folders.TryGetValue(relativeFolder, out var f) ? f : Array.Empty<SharePointFileRef>());

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);
        public Task<StoredFile> StoreAsync(string p, byte[] c, string t, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task<StoredFile> UploadToFolderAsync(
            string f, string n, byte[] c, string t, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteFromFolderAsync(string f, string n, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <remarks>
    /// ⚠️ <b>A UNIQUE ROOT PER TEST, and it is load-bearing.</b> <c>SpeakerPresentationService</c>
    /// caches folder listings in a <b>static</b> dictionary keyed by folder path, for 90 seconds —
    /// so two tests sharing a root share a cache entry, and whichever ran first decides what the
    /// second one sees. Three tests here failed for exactly that reason before the root was made
    /// unique: they were asserting against the FIRST test's empty listing.
    /// </remarks>
    private static (SpeakerPresentationService Svc, FakeStore Store, CommunityHubDbContext Db,
                    string PreviewFolder, string FinalFolder) NewService(CommunityHubDbContext db)
    {
        var paths = TestDocLibrary.Resolver($"General/TEST/decks-{Guid.NewGuid():N}");
        paths.TryResolve(CommunityHub.Core.Integrations.DocLibrary.DocLibraryPaths.SessionPresentationsPreview,
            out var previewFolder);
        paths.TryResolve(CommunityHub.Core.Integrations.DocLibrary.DocLibraryPaths.SessionPresentationsFinal,
            out var finalFolder);

        var store = new FakeStore();
        var svc = new SpeakerPresentationService(
            store,
            Options.Create(new GraphicsSharePointOptions()),
            db,
            TimeProvider.System,
            paths);
        return (svc, store, db, previewFolder, finalFolder);
    }

    private static async Task<int> AddSessionAsync(CommunityHubDbContext db, string title)
    {
        var s = new Session
        {
            EventId = EventId, Title = title, SessionizeId = Guid.NewGuid().ToString("N"),
            Type = SessionType.TechnicalSession,
        };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    [Fact]
    public async Task A_session_with_no_files_reports_NOTHING_UPLOADED()
    {
        using var db = NewDb();
        var id = await AddSessionAsync(db, "Running Azure at Night");
        var (svc, _, _, _, _) = NewService(db);

        var states = await svc.GetDeckStatesAsync(EventId);

        Assert.True(states[id].NothingUploaded);
        Assert.Null(states[id].Final);
        Assert.Null(states[id].Preview);
    }

    [Fact]
    public async Task The_LATEST_version_wins_and_carries_its_upload_date()
    {
        using var db = NewDb();
        var id = await AddSessionAsync(db, "Running Azure at Night");
        var (svc, store, _, _, final) = NewService(db);

        store.Put(final, $"{id} - Running Azure at Night_v1.pdf",
            new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero));
        store.Put(final, $"{id} - Running Azure at Night_v3.pdf",
            new DateTimeOffset(2026, 8, 2, 14, 30, 0, TimeSpan.Zero));
        store.Put(final, $"{id} - Running Azure at Night_v2.pdf",
            new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero));

        var deck = (await svc.GetDeckStatesAsync(EventId))[id];

        // 🔑 Highest version, NOT newest-listed: the folder order is Graph's, not ours.
        Assert.Equal(3, deck.Final!.Version);
        Assert.Equal(new DateTimeOffset(2026, 8, 2, 14, 30, 0, TimeSpan.Zero), deck.Final.UploadedAt);
        // The "{id} - " prefix is a storage token; the organizer sees the human name.
        Assert.Equal("Running Azure at Night_v3.pdf", deck.Final.DisplayName);
    }

    /// <summary>
    /// 🔒 The id prefix is the stable token precisely so a RENAMED session keeps its decks. Matching
    /// on the title would silently orphan every deck the moment a speaker retitles their talk.
    /// </summary>
    [Fact]
    public async Task A_RENAMED_session_still_finds_its_deck()
    {
        using var db = NewDb();
        var id = await AddSessionAsync(db, "The Old Title");
        var (svc, store, _, _, final) = NewService(db);
        store.Put(final, $"{id} - The Old Title_v1.pptx");

        var session = await db.Sessions.SingleAsync(s => s.Id == id);
        session.Title = "A Completely Different Title";
        await db.SaveChangesAsync();

        var deck = (await svc.GetDeckStatesAsync(EventId))[id];
        Assert.NotNull(deck.Final);
        Assert.Equal(1, deck.Final!.Version);
    }

    /// <summary>
    /// ⚠️ Session 1 and session 12 both start "1". A prefix match without the separator would show
    /// session 1's organizer that session 12 had uploaded a deck.
    /// </summary>
    [Fact]
    public async Task One_sessions_deck_never_counts_for_another()
    {
        using var db = NewDb();
        var first = await AddSessionAsync(db, "First");
        var second = await AddSessionAsync(db, "Second");
        var (svc, store, _, _, final) = NewService(db);

        store.Put(final, $"{second} - Second_v1.pdf");

        var states = await svc.GetDeckStatesAsync(EventId);
        Assert.True(states[first].NothingUploaded);
        Assert.NotNull(states[second].Final);
    }

    [Fact]
    public async Task A_preview_without_a_final_is_visible_as_such()
    {
        using var db = NewDb();
        var id = await AddSessionAsync(db, "Half Way");
        var (svc, store, _, preview, _) = NewService(db);
        store.Put(preview, $"{id} - Half Way_v2.pdf");

        var deck = (await svc.GetDeckStatesAsync(EventId))[id];

        // The organizer's actual question before the final deadline: who has SOMETHING but not the
        // final? "none" would be wrong here, and so would treating it as done.
        Assert.False(deck.NothingUploaded);
        Assert.Null(deck.Final);
        Assert.Equal(2, deck.Preview!.Version);
    }

    [Fact]
    public async Task Every_session_in_the_edition_is_covered_including_the_empty_ones()
    {
        using var db = NewDb();
        var a = await AddSessionAsync(db, "A");
        var b = await AddSessionAsync(db, "B");
        var c = await AddSessionAsync(db, "C");
        var (svc, store, _, _, final) = NewService(db);
        store.Put(final, $"{b} - B_v1.pdf");

        var states = await svc.GetDeckStatesAsync(EventId);

        // A dictionary MISS and "no deck" must not be the same thing for the caller — every session
        // gets an entry, so the page never has to guess what a missing key means.
        Assert.Equal(3, states.Count);
        Assert.All(new[] { a, b, c }, id => Assert.True(states.ContainsKey(id)));
    }
}
