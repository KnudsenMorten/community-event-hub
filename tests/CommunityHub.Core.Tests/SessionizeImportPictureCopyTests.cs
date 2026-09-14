using System.Net;
using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §768.16 — WHERE the import-time picture copy lands, and what it is called.
/// </summary>
/// <remarks>
/// <para>🔴 <b>This was the fourth speaker-photo writer, and it wrote where nobody looks.</b> It
/// stored <c>Speakers/speaker-{id}.jpg</c> through <c>StoreAsync</c> — relative to the GRAPHICS root,
/// the drive-root <c>Graphics/</c> that §768.7 retired — so the file landed outside both document
/// library roots under a fourth naming convention, while <c>PhotoSharePointPath</c> was set to it.
/// <see cref="SpeakerPhotoUrl.Resolve"/> prefers that path, so the speaker rendered as
/// <c>/speaker-photo/speaker-42.jpg</c>: a leaf the §665 proxy looks for in <c>Speakers/Photos</c>,
/// where it had never been. A broken image that the archive job later healed by overwriting the
/// path — which is exactly why it never looked like a bug.</para>
///
/// <para>🔒 Nothing in the suite had ever asserted where this copy went. That is the §768 failure
/// class in one line: an unasserted write path drifts, and every symptom it produces looks like
/// something else.</para>
/// </remarks>
public sealed class SessionizeImportPictureCopyTests
{
    private const string PhotoUrl = "https://sessionize.example.test/pictures/speaker.png";

    private const string SpeakersJson = """
    [
      { "id": "spk-1", "firstName": "Community", "lastName": "Speaker",
        "fullName": "Community Speaker",
        "profilePicture": "https://sessionize.example.test/pictures/speaker.png", "links": [] }
    ]
    """;

    /// <summary>Answers any picture fetch with a 4-byte PNG.</summary>
    private sealed class PictureHandler : HttpMessageHandler
    {
        public List<string> Fetched { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Fetched.Add(request.RequestUri!.ToString());
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 }),
            };
            resp.Content.Headers.ContentType = new("image/png");
            return Task.FromResult(resp);
        }
    }

    /// <summary>Records the drive-relative folder + file name every upload targeted.</summary>
    private sealed class RecordingStore : ISharePointFileStore
    {
        public List<(string Folder, string FileName)> Uploads { get; } = new();
        public List<string> RootRelativeWrites { get; } = new();

        public bool CanStore => true;
        public bool CanRead => true;

        public Task<StoredFile> UploadToFolderAsync(
            string relativeFolder, string fileName, byte[] content, string contentType,
            CancellationToken ct = default)
        {
            Uploads.Add((relativeFolder, fileName));
            var path = $"{relativeFolder}/{fileName}";
            return Task.FromResult(new StoredFile(path, $"https://sp.test/{path}", "item-" + path));
        }

        // 🔒 The retired route. Recorded rather than thrown so a regression shows up as a NAMED
        // assertion ("it went back to the graphics root") instead of an exception nobody reads.
        public Task<StoredFile> StoreAsync(
            string relativePath, byte[] content, string contentType, CancellationToken ct = default)
        {
            RootRelativeWrites.Add(relativePath);
            return Task.FromResult(new StoredFile(relativePath, "https://sp.test/x", "item"));
        }

        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteFromFolderAsync(
            string relativeFolder, string fileName, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(
            string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(Array.Empty<SharePointFileRef>());

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);
    }

    private static IDocLibraryPathResolver Resolver() =>
        new DocLibraryPathResolver(new DocLibraryOptions
        {
            Enabled = true,
            SiteUrl = "https://sp.test/sites/x",
            RootFolderPath = "General/Test/EventHub",
        });

    private static SessionizeImportService NewImporter(
        CommunityHubDbContext db, ISharePointFileStore? store, IDocLibraryPathResolver? paths,
        PictureHandler handler)
    {
        var templates = new EmailTemplateProvider(
            Options.Create(new EmailTemplateOptions { TemplateDirectory = RepoPaths.EmailTemplates() }));
        var welcome = new WelcomeEmailService(
            db, templates, new CapturingEmailSender(), ScenarioFixture.Clock);
        return new SessionizeImportService(
            db, welcome, ScenarioFixture.Clock, store, paths, new HttpClient(handler));
    }

    private static SessionizeParseResult Parse() =>
        SessionizeApiClient.ParseSpeakers(
            SpeakersJson,
            new Dictionary<string, string> { ["spk-1"] = "community.speaker@example.test" });

    [Fact]
    public async Task The_picture_copy_lands_in_the_RESOLVED_photo_folder_named_by_id_alone()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var store = new RecordingStore();
        var import = NewImporter(db, store, Resolver(), new PictureHandler());

        var parsed = Parse();
        await import.ImportSpeakersAsync(
            seed.EventId, parsed.Speakers, parsed.Warnings, sendWelcome: false);

        var participant = await db.Participants.SingleAsync(
            p => p.EventId == seed.EventId && p.FullName == "Community Speaker");
        var expected = SpeakerPhotoFileName.Build(participant.Id, ".png");

        // §1132 — TWO files now: the authoritative id-named one and its name alias beside it.
        // ⚠️ This assertion was `Assert.Single`. It was right until §1132 deliberately added the
        // second file (operator: *"same folder, just 2 files"*), so it is UPDATED, not deleted —
        // the id file's folder and name are still pinned exactly as before.
        Assert.Equal(2, store.Uploads.Count);

        var upload = Assert.Single(store.Uploads, u => u.FileName == expected);
        // The SAME folder every other writer and the read proxy resolve (§764: "in 1 place only").
        Assert.Equal("General/Test/EventHub/Speakers/Photos", upload.Folder);

        // The alias sits in that same folder and carries the speaker's name AND their id.
        var alias = Assert.Single(store.Uploads, u => u.FileName != expected);
        Assert.Equal("General/Test/EventHub/Speakers/Photos", alias.Folder);
        Assert.Equal(
            SpeakerPhotoFileName.BuildAlias(participant.Id, "Community Speaker", ".png"),
            alias.FileName);
        // 🔒 And NOT through the root-relative route, which is the retired graphics root.
        Assert.Empty(store.RootRelativeWrites);

        // The stored path is the LEAF — what the §665 proxy resolves inside the photo folder.
        var profile = await db.SpeakerProfiles.SingleAsync(p => p.ParticipantId == participant.Id);
        Assert.Equal(expected, profile.PhotoSharePointPath);
        Assert.Equal($"/speaker-photo/{expected}", SpeakerPhotoUrl.Resolve(profile.PhotoUrl, profile.PhotoSharePointPath));
    }

    [Fact]
    public async Task With_NO_resolver_it_stores_nothing_rather_than_guessing_a_folder()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var store = new RecordingStore();
        var handler = new PictureHandler();
        var import = NewImporter(db, store, paths: null, handler);

        var parsed = Parse();
        await import.ImportSpeakersAsync(
            seed.EventId, parsed.Speakers, parsed.Warnings, sendWelcome: false);

        // 🔒 A skipped best-effort copy is a non-event; a file in the wrong place is not — and the
        // §768 lesson is that the wrong place looks identical to the right one until a reader misses.
        Assert.Empty(store.Uploads);
        Assert.Empty(store.RootRelativeWrites);
        Assert.Empty(handler.Fetched);          // not even downloaded: nowhere to put it

        var profile = await db.SpeakerProfiles.SingleAsync(
            p => p.EventId == seed.EventId
                 && db.Participants.Any(x => x.Id == p.ParticipantId && x.FullName == "Community Speaker"));
        Assert.Null(profile.PhotoSharePointPath);
        Assert.Equal(PhotoUrl, profile.PhotoUrl);   // the Sessionize URL still serves it
    }
}
