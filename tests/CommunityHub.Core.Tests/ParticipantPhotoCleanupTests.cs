using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §6.9 — a deactivated speaker's or volunteer's photo leaves the document library.
/// </summary>
/// <remarks>
/// <para>🔒 <b>This is the ONLY service in CEH that deletes a document-library file</b>, which is
/// why the work order asks for dry-run by default and why the tests here are weighted towards what
/// must NOT be deleted. Every other failure mode in this codebase is visible and re-runnable; this
/// one loses data.</para>
///
/// <para>NO real names — Ada / Grace only.</para>
/// </remarks>
public sealed class ParticipantPhotoCleanupTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"photoclean-{Guid.NewGuid():N}")
            .Options);

    private sealed class FakeStore : ISharePointFileStore
    {
        private readonly Dictionary<string, List<string>> _folders = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Folder, string File)> Deleted { get; } = new();

        public void Put(string folder, string name) =>
            (_folders.TryGetValue(folder, out var l) ? l : _folders[folder] = new()).Add(name);

        public IReadOnlyList<string> Files(string folder) =>
            _folders.TryGetValue(folder, out var l) ? l : Array.Empty<string>();

        public bool CanStore => true;
        public bool CanRead => true;

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(
            string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(
                Files(relativeFolder)
                    .Select(n => new SharePointFileRef($"{relativeFolder}/{n}", n, string.Empty))
                    .ToList());

        public Task DeleteFromFolderAsync(string folder, string fileName, CancellationToken ct = default)
        {
            Deleted.Add((folder, fileName));
            if (_folders.TryGetValue(folder, out var l)) l.Remove(fileName);
            return Task.CompletedTask;
        }

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);
        public Task<StoredFile> StoreAsync(string p, byte[] c, string t, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task<StoredFile> UploadToFolderAsync(
            string f, string n, byte[] c, string t, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static async Task<int> AddPersonAsync(
        CommunityHubDbContext db, string name, ParticipantRole role, bool active)
    {
        var p = new Participant
        {
            EventId = EventId, FullName = name, Email = $"{Guid.NewGuid():N}@example.test",
            Role = role, IsActive = active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static (ParticipantPhotoCleanupService Svc, FakeStore Store, string Speakers, string Volunteers)
        NewService(CommunityHubDbContext db, bool dryRun)
    {
        var paths = TestDocLibrary.Resolver();
        paths.TryResolve(CommunityHub.Core.Integrations.DocLibrary.DocLibraryPaths.SpeakerPhotos, out var sp);
        paths.TryResolve(CommunityHub.Core.Integrations.DocLibrary.DocLibraryPaths.VolunteerPhotos, out var vol);
        var store = new FakeStore();
        var svc = new ParticipantPhotoCleanupService(
            db, store, paths, new PhotoCleanupOptions { DryRun = dryRun });
        return (svc, store, sp, vol);
    }

    /// <summary>
    /// 🔒 The shipped default. Work-order §6.9: it logs what it WOULD delete and deletes nothing.
    /// </summary>
    [Fact]
    public async Task DRY_RUN_IS_THE_DEFAULT_and_it_deletes_nothing()
    {
        using var db = NewDb();
        var id = await AddPersonAsync(db, "Ada Lovelace", ParticipantRole.Speaker, active: false);
        var (svc, store, speakers, _) = NewService(db, dryRun: true);
        store.Put(speakers, $"speaker-photo-{id}.jpg");

        Assert.True(new ParticipantPhotoCleanupService(db, store, TestDocLibrary.Resolver()).DryRun);

        var result = await svc.CleanupParticipantAsync(EventId, id);

        Assert.Equal(0, result.Deleted);
        Assert.Equal($"speaker-photo-{id}.jpg", Assert.Single(result.WouldDelete));
        Assert.Empty(store.Deleted);
        Assert.Single(store.Files(speakers));      // still there
    }

    [Fact]
    public async Task With_dry_run_OFF_a_deactivated_speakers_photo_is_deleted()
    {
        using var db = NewDb();
        var id = await AddPersonAsync(db, "Ada Lovelace", ParticipantRole.Speaker, active: false);
        var (svc, store, speakers, _) = NewService(db, dryRun: false);
        store.Put(speakers, $"speaker-photo-{id}.jpg");

        var result = await svc.CleanupParticipantAsync(EventId, id);

        Assert.Equal(1, result.Deleted);
        Assert.Equal((speakers, $"speaker-photo-{id}.jpg"), Assert.Single(store.Deleted));
    }

    /// <summary>
    /// 🔒 The one mistake with no undo. The caller says "clean up this person"; the service re-reads
    /// the flag and refuses if they are still active.
    /// </summary>
    [Fact]
    public async Task An_ACTIVE_participants_photo_is_NEVER_touched()
    {
        using var db = NewDb();
        var id = await AddPersonAsync(db, "Ada Lovelace", ParticipantRole.Speaker, active: true);
        var (svc, store, speakers, _) = NewService(db, dryRun: false);
        store.Put(speakers, $"speaker-photo-{id}.jpg");

        var result = await svc.CleanupParticipantAsync(EventId, id);

        Assert.False(result.Ran);
        Assert.Contains("still active", result.InactiveReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.Deleted);
    }

    /// <summary>
    /// Legacy `speaker-photo-{Name}-{id}` files still exist (a sponsor-uploaded photo is never
    /// re-archived — §768.16), so removing "their photo" means removing BOTH shapes.
    /// </summary>
    [Fact]
    public async Task Both_the_current_and_the_LEGACY_speaker_name_shapes_are_removed()
    {
        using var db = NewDb();
        var id = await AddPersonAsync(db, "Ada Lovelace", ParticipantRole.Speaker, active: false);
        var (svc, store, speakers, _) = NewService(db, dryRun: false);
        store.Put(speakers, $"speaker-photo-{id}.jpg");
        store.Put(speakers, $"speaker-photo-Ada-Lovelace-{id}.png");
        store.Put(speakers, "speaker-photo-999.jpg");          // somebody else

        var result = await svc.CleanupParticipantAsync(EventId, id);

        Assert.Equal(2, result.Deleted);
        Assert.DoesNotContain(store.Deleted, d => d.File == "speaker-photo-999.jpg");
        Assert.Equal("speaker-photo-999.jpg", Assert.Single(store.Files(speakers)));
    }

    [Fact]
    public async Task Only_PHOTOS_are_in_scope_the_other_folders_are_never_listed()
    {
        using var db = NewDb();
        var id = await AddPersonAsync(db, "Ada Lovelace", ParticipantRole.Speaker, active: false);
        var (svc, store, speakers, _) = NewService(db, dryRun: false);
        var paths = TestDocLibrary.Resolver();
        paths.TryResolve(
            CommunityHub.Core.Integrations.DocLibrary.DocLibraryPaths.SessionPresentationsFinal,
            out var decks);
        store.Put(speakers, $"speaker-photo-{id}.jpg");
        store.Put(decks, $"{id} - A talk_v1.pdf");

        await svc.CleanupParticipantAsync(EventId, id);

        // Work order §6.9: presentations, QR codes, evaluation results and session graphics are
        // event material tied to the session, not personal data tied to the person.
        Assert.All(store.Deleted, d => Assert.Equal(speakers, d.Folder));
        Assert.Single(store.Files(decks));
    }

    [Fact]
    public async Task A_deactivated_volunteers_photo_is_matched_by_the_name_the_UPLOAD_writes()
    {
        using var db = NewDb();
        var id = await AddPersonAsync(db, "Ada Lovelace", ParticipantRole.Volunteer, active: false);
        var (svc, store, _, volunteers) = NewService(db, dryRun: false);
        // 🔑 Built by the product's own function, so the test cannot pass against a name no upload
        // would produce — the mistake the first draft of this service actually made.
        store.Put(volunteers, VolunteerPhotoFileName.Build(id, ".jpg"));

        var result = await svc.CleanupParticipantAsync(EventId, id);

        Assert.Equal(1, result.Deleted);
        Assert.Equal($"volunteer-photo-{id}.jpg", Assert.Single(store.Deleted).File);
    }

    /// <summary>
    /// 🔒 §769.9 — the id-named file is an EXACT match, so a namesake is now irrelevant. This is
    /// what the operator's "switch to id to make consistent" bought: the case below (legacy names)
    /// disappears as volunteers re-upload.
    /// </summary>
    [Fact]
    public async Task An_ID_named_volunteer_photo_is_deleted_even_when_a_namesake_is_active()
    {
        using var db = NewDb();
        var leaving = await AddPersonAsync(db, "Ada Lovelace", ParticipantRole.Volunteer, active: false);
        var staying = await AddPersonAsync(db, "Ada Lovelace", ParticipantRole.Volunteer, active: true);
        var (svc, store, _, volunteers) = NewService(db, dryRun: false);
        store.Put(volunteers, VolunteerPhotoFileName.Build(leaving, ".jpg"));
        store.Put(volunteers, VolunteerPhotoFileName.Build(staying, ".jpg"));

        var result = await svc.CleanupParticipantAsync(EventId, leaving);

        Assert.Equal(1, result.Deleted);
        Assert.Equal($"volunteer-photo-{leaving}.jpg", Assert.Single(store.Deleted).File);
        // The active namesake keeps hers — which the NAME-keyed convention could not guarantee.
        Assert.Contains($"volunteer-photo-{staying}.jpg", store.Files(volunteers));
    }

    /// <summary>
    /// 🔒 <b>THE ONE THAT MATTERS — for the files already in the folder.</b> A LEGACY volunteer
    /// photo carries a NAME, not an id, so two volunteers called the same thing share one file and
    /// deleting it would remove the ACTIVE one's photo. Ambiguity is refused, not resolved. This
    /// case shrinks to nothing as volunteers re-upload under §769.9's id naming.
    /// </summary>
    [Fact]
    public async Task A_LEGACY_volunteer_photo_is_NOT_deleted_when_an_active_namesake_exists()
    {
        using var db = NewDb();
        var leaving = await AddPersonAsync(db, "Ada Lovelace", ParticipantRole.Volunteer, active: false);
        await AddPersonAsync(db, "Ada Lovelace", ParticipantRole.Volunteer, active: true);
        var (svc, store, _, volunteers) = NewService(db, dryRun: false);
        store.Put(volunteers, "Ada Lovelace.jpg");        // the pre-§769.9 shape

        var result = await svc.CleanupParticipantAsync(EventId, leaving);

        Assert.Equal(0, result.Deleted);
        Assert.Empty(store.Deleted);
        Assert.Single(result.SkippedAmbiguous);      // reported, so it can be handled by a human
        Assert.Single(store.Files(volunteers));
    }

    [Fact]
    public async Task Nothing_to_delete_is_a_quiet_success_not_a_failure()
    {
        using var db = NewDb();
        var id = await AddPersonAsync(db, "Grace Hopper", ParticipantRole.Speaker, active: false);
        var (svc, store, _, _) = NewService(db, dryRun: false);

        var result = await svc.CleanupParticipantAsync(EventId, id);

        Assert.True(result.Ran);
        Assert.Equal(0, result.Deleted);
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public async Task A_host_that_may_not_write_externally_deletes_nothing()
    {
        using var db = NewDb();
        var id = await AddPersonAsync(db, "Ada Lovelace", ParticipantRole.Speaker, active: false);
        var paths = TestDocLibrary.Resolver();
        paths.TryResolve(CommunityHub.Core.Integrations.DocLibrary.DocLibraryPaths.SpeakerPhotos, out var sp);
        var store = new FakeStore();
        store.Put(sp, $"speaker-photo-{id}.jpg");

        var svc = new ParticipantPhotoCleanupService(
            db, store, paths, new PhotoCleanupOptions { DryRun = false }, new RefuseAllWrites());

        var result = await svc.CleanupParticipantAsync(EventId, id);

        // §340-H — DEV must not reach into the live library, and a DELETE is the last thing that
        // should slip through that guard.
        Assert.False(result.Ran);
        Assert.Empty(store.Deleted);
    }

    private sealed class RefuseAllWrites : CommunityHub.Core.Integrations.IExternalWriteGuard
    {
        public Task<bool> AllowAsync(string system, string operation, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> IsAllowedAsync(CancellationToken ct = default) => Task.FromResult(false);
    }
}
