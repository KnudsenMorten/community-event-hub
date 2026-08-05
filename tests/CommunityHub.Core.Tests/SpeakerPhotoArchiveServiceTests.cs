using System.Net;
using System.Text;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §764 — every community and guest speaker's photo into the ONE SharePoint speakers folder.
/// </summary>
/// <remarks>
/// Operator 2026-08-01: <i>"automatically download the speakers (community + guest) photo and put it
/// here … make sure that the sponsor speaker photo upload lands there as well so all speaker
/// pictures are in 1 place"</i>. The sponsor half was a config move; this is the other half.
/// </remarks>
public sealed class SpeakerPhotoArchiveServiceTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Records what was uploaded, and can be told to fail a download.</summary>
    private sealed class PhotoHandler : HttpMessageHandler
    {
        public List<string> Downloaded { get; } = new();
        public List<string> UploadedTo { get; } = new();
        public HttpStatusCode DownloadStatus { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();

            // The SharePoint client's own traffic — record the target path, answer plausibly.
            if (url.Contains("graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
                || url.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
            {
                UploadedTo.Add(url);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"access_token\":\"t\",\"id\":\"i\",\"webUrl\":\"https://sp/x\",\"name\":\"n\"}",
                        Encoding.UTF8, "application/json"),
                });
            }

            Downloaded.Add(url);
            var resp = new HttpResponseMessage(DownloadStatus);
            if (DownloadStatus == HttpStatusCode.OK)
            {
                resp.Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 });
                resp.Content.Headers.ContentType = new("image/png");
            }
            return Task.FromResult(resp);
        }
    }

    /// <summary>A guard that refuses every write — how DEV is kept off SharePoint (§340-H).</summary>
    private sealed class RefuseAllWrites : IExternalWriteGuard
    {
        public Task<bool> AllowAsync(string system, string operation, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<bool> IsAllowedAsync(CancellationToken ct = default) => Task.FromResult(false);
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"photoarchive-{Guid.NewGuid():N}").Options);

    private static async Task<SpeakerProfile> AddSpeakerAsync(
        CommunityHubDbContext db, string name, SpeakerCategory? category,
        string? photoUrl, string? archivedFrom = null, bool active = true,
        string? storedPath = null)
    {
        var p = new Participant
        {
            EventId = EventId, FullName = name, Email = $"{Guid.NewGuid():N}@example.test",
            Role = ParticipantRole.Speaker, IsActive = active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        // §768.16 — an archived speaker also carries the file it was archived AS. Default: the
        // CURRENT `speaker-photo-{id}` convention, so "already archived" means what it says. A test
        // about the legacy name passes it explicitly.
        var profile = new SpeakerProfile
        {
            EventId = EventId, ParticipantId = p.Id, Category = category,
            PhotoUrl = photoUrl, PhotoArchivedFromUrl = archivedFrom,
            PhotoSharePointPath = storedPath
                ?? (archivedFrom is null ? null : SpeakerPhotoFileName.Build(p.Id, ".png")),
        };
        db.SpeakerProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile;
    }

    private static (SpeakerPhotoArchiveService Svc, PhotoHandler Handler) NewService(
        CommunityHubDbContext db, IExternalWriteGuard? writes = null, string folder = "Events/Photos")
    {
        var handler = new PhotoHandler();
        var http = new HttpClient(handler);
        // The upload seam: these tests are about WHICH speakers get archived and WHEN, not about
        // Graph auth. The file name is captured so the naming convention can still be asserted.
        Task Upload(string fileName, byte[] bytes, string contentType, CancellationToken ct)
        {
            handler.UploadedTo.Add(fileName);
            return Task.CompletedTask;
        }

        var cfgDir = Path.Combine(Path.GetTempPath(), "ceh-photo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cfgDir);
        var cfgPath = Path.Combine(cfgDir, "event.json");
        // 🔑 The key is "sharepoint", ALL LOWERCASE — the loader pulls that sibling object by name
        // (the property itself is [JsonIgnore]), so a camelCased "sharePoint" here parses cleanly
        // and yields a null block, i.e. "not configured" rather than an error.
        File.WriteAllText(cfgPath,
            "{\"sharepoint\":{\"siteUrl\":\"https://sp.test/sites/x\",\"driveName\":\"Documents\"}}");

        // §768.14 — the speaker-photo FOLDER is a registry key now, not an edition-config value, so
        // the test configures it the way production does: a root, under which SpeakerPhotos
        // resolves. An empty `folder` means no root, which is what "not configured" now looks like —
        // the case An_unconfigured_folder_is_inactive_not_an_error pins.
        var paths = new CommunityHub.Core.Integrations.DocLibrary.DocLibraryPathResolver(
            new CommunityHub.Core.Integrations.DocLibrary.DocLibraryOptions
            {
                Enabled = folder.Length > 0,
                SiteUrl = "https://sp.test/sites/x",
                RootFolderPath = folder,
            });

        var svc = new SpeakerPhotoArchiveService(
            db,
            new SharePointUploadClient(http, new SharePointUploadOptions { Enabled = true }),
            new EventEditionConfigLoader(),
            new EventConfigOptions { EventConfigPath = cfgPath },
            writes, new FixedClock(), alerts: null, log: null, http: http, uploadOverride: Upload,
            paths: paths);

        return (svc, handler);
    }

    [Fact]
    public async Task Community_and_guest_photos_are_archived_and_stamped_with_their_SOURCE_url()
    {
        using var db = NewDb();
        await AddSpeakerAsync(db, "Nikki Chapple", SpeakerCategory.Community, "https://cdn.test/nikki.png");
        await AddSpeakerAsync(db, "Per Larsen", SpeakerCategory.Guest, "https://cdn.test/per.png");

        var (svc, handler) = NewService(db);
        var result = await svc.RunAsync(EventId);

        Assert.True(result.Ran);
        Assert.Equal(2, result.Archived);
        Assert.Contains("https://cdn.test/nikki.png", handler.Downloaded);

        var stored = await db.SpeakerProfiles.AsNoTracking().ToListAsync();
        Assert.All(stored, s =>
        {
            Assert.Equal(s.PhotoUrl, s.PhotoArchivedFromUrl);   // stamped with the SOURCE
            Assert.Equal(Now, s.PhotoArchivedAt);
            // 🔒 §768.16 — the ID alone names the file, and it is the SAME shape (and the same
            // function) the sponsor upload writes into this folder. One folder with two naming
            // conventions would read as two systems pointed at one place.
            Assert.Equal(
                SpeakerPhotoFileName.Build(s.ParticipantId, ".png"), s.PhotoSharePointPath);
        });
        // Operator 2026-08-02: "use id only (not speaker name and id)".
        Assert.DoesNotContain(stored, s => s.PhotoSharePointPath!.Contains("Nikki"));
    }

    [Fact]
    public async Task A_LEGACY_named_photo_is_re_archived_ONCE_under_the_id_only_name()
    {
        using var db = NewDb();
        // 🔒 §768.16 — the source URL is UNCHANGED, so the old rule ("same URL ⇒ nothing to do")
        // would have frozen this name for ever: the photo never changes, so the job never touches
        // the file again and the folder keeps two conventions permanently.
        var profile = await AddSpeakerAsync(db, "Nikki Chapple", SpeakerCategory.Community,
            "https://cdn.test/nikki.png", archivedFrom: "https://cdn.test/nikki.png",
            storedPath: "speaker-photo-Nikki-Chapple-42.png");

        var (svc, handler) = NewService(db);
        var result = await svc.RunAsync(EventId);

        Assert.Equal(1, result.Archived);
        Assert.Equal(0, result.Skipped);
        Assert.Contains("https://cdn.test/nikki.png", handler.Downloaded);

        var stored = await db.SpeakerProfiles.AsNoTracking().SingleAsync();
        Assert.Equal(
            SpeakerPhotoFileName.Build(profile.ParticipantId, ".png"), stored.PhotoSharePointPath);

        // ...and ONCE. The second run is back in the cheap steady state.
        var (svc2, handler2) = NewService(db);
        var again = await svc2.RunAsync(EventId);
        Assert.Equal(0, again.Archived);
        Assert.Equal(1, again.Skipped);
        Assert.Empty(handler2.Downloaded);
    }

    [Fact]
    public async Task An_ALREADY_archived_photo_is_not_downloaded_again()
    {
        using var db = NewDb();
        await AddSpeakerAsync(db, "Ada Lovelace", SpeakerCategory.Community,
            "https://cdn.test/ada.png", archivedFrom: "https://cdn.test/ada.png");

        var (svc, handler) = NewService(db);
        var result = await svc.RunAsync(EventId);

        // The steady state. Re-pulling every speaker from someone else's CDN daily would be both
        // wasteful and rude, so the comparison is what the run costs.
        Assert.Equal(0, result.Archived);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(handler.Downloaded);
    }

    [Fact]
    public async Task A_CHANGED_photo_url_is_re_archived()
    {
        using var db = NewDb();
        // 🔒 This is the case the import-time copy could never handle: it keys on "is a stored path
        // set", so a speaker who changed their picture kept the old one in SharePoint for ever.
        await AddSpeakerAsync(db, "Grace Hopper", SpeakerCategory.Community,
            "https://cdn.test/grace-v2.png", archivedFrom: "https://cdn.test/grace-v1.png");

        var (svc, handler) = NewService(db);
        var result = await svc.RunAsync(EventId);

        Assert.Equal(1, result.Archived);
        Assert.Contains("https://cdn.test/grace-v2.png", handler.Downloaded);
    }

    [Fact]
    public async Task A_SPONSOR_speaker_is_left_alone_because_their_photo_is_UPLOADED()
    {
        using var db = NewDb();
        await AddSpeakerAsync(db, "Hans Hansen", SpeakerCategory.Sponsor, "https://cdn.test/hans.png");

        var (svc, handler) = NewService(db);
        var result = await svc.RunAsync(EventId);

        // The sponsor uploads their speaker's photo through CEH and it already lands in this same
        // folder (§764). Fetching one too would be a second writer for a file that exists.
        Assert.Equal(0, result.Archived);
        Assert.Empty(handler.Downloaded);
    }

    [Fact]
    public async Task An_UNCATEGORIZED_speaker_is_counted_not_guessed_at()
    {
        using var db = NewDb();
        await AddSpeakerAsync(db, "Unknown Person", category: null, "https://cdn.test/x.png");

        var (svc, handler) = NewService(db);
        var result = await svc.RunAsync(EventId);

        // CEH does not know whether they are community, guest or sponsor (§299 6.1). Surfaced as a
        // number rather than silently archived under an assumption.
        Assert.Equal(1, result.Uncategorized);
        Assert.Equal(0, result.Archived);
        Assert.Empty(handler.Downloaded);
    }

    [Fact]
    public async Task A_speaker_with_no_photo_at_all_is_counted_as_a_gap()
    {
        using var db = NewDb();
        await AddSpeakerAsync(db, "No Picture", SpeakerCategory.Community, photoUrl: null);

        var (svc, _) = NewService(db);
        var result = await svc.RunAsync(EventId);

        Assert.Equal(1, result.NoPhoto);
        Assert.Equal(0, result.Archived);
    }

    /// <summary>
    /// 🔒 §764.1 — an UPLOADED photo is already in this folder. Never fetch it, never fail on it.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-01, on the first live run: <i>"you try to use same method for speakers
    /// with sponsorCategory - here you must remember that we dont use sessionize and the photo is
    /// uploaded instead, so file exist already in the case"</i> — after receiving
    /// <c>"2linkit Speaker Firstname,Lastname: the photo could not be downloaded"</c> for a file
    /// that was sitting in the destination folder, uploaded three days earlier.</para>
    ///
    /// <para>🔑 The original filter keyed on CATEGORY and treated that as equivalent to "has an
    /// external photo URL". It is not: §665 rewrites <c>PhotoUrl</c> to the hub proxy route on
    /// upload, so the "source" became CEH's own URL. The signal is the URL, not the person.</para>
    /// </remarks>
    [Theory]
    [InlineData("/speaker-photo/speaker-photo-2linkit-100.jpg")]                       // §665 proxy, relative
    [InlineData("https://eldk27.eventhub.expertslive.dk/speaker-photo/x-100.jpg")]     // §665 proxy, absolute
    [InlineData("https://expertslivedk.sharepoint.com/sites/x/photo.jpg")]             // already in SharePoint
    [InlineData("not-a-url-at-all")]
    public async Task An_UPLOADED_photo_is_ALREADY_STORED_never_downloaded_and_never_a_failure(string url)
    {
        using var db = NewDb();
        await AddSpeakerAsync(db, "2linkit Speaker Firstname,Lastname", SpeakerCategory.Community, url);

        var (svc, handler) = NewService(db);
        var result = await svc.RunAsync(EventId);

        Assert.Equal(1, result.AlreadyStored);
        Assert.Equal(0, result.Failed);        // 🔒 he must NOT be mailed about a file that is fine
        Assert.Equal(0, result.Archived);
        Assert.Empty(handler.Downloaded);
    }

    [Fact]
    public async Task A_FAILED_download_is_counted_and_never_stamped_as_archived()
    {
        using var db = NewDb();
        await AddSpeakerAsync(db, "Dead Link", SpeakerCategory.Community, "https://cdn.test/gone.png");

        var (svc, handler) = NewService(db);
        handler.DownloadStatus = HttpStatusCode.NotFound;

        var result = await svc.RunAsync(EventId);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Archived);
        // 🔒 The stamp must NOT move on a failure, or the next run would skip a speaker whose photo
        // was never actually copied — the §598 "a stored path is not proof the file exists" shape.
        var profile = await db.SpeakerProfiles.AsNoTracking().SingleAsync();
        Assert.Null(profile.PhotoArchivedFromUrl);
        Assert.Null(profile.PhotoArchivedAt);
    }

    [Fact]
    public async Task DEV_writes_NOTHING_and_says_so_rather_than_running_silently()
    {
        using var db = NewDb();
        await AddSpeakerAsync(db, "Nikki Chapple", SpeakerCategory.Community, "https://cdn.test/n.png");

        var (svc, handler) = NewService(db, writes: new RefuseAllWrites());
        var result = await svc.RunAsync(EventId);

        // 🔒 The established convention — DEV writes nothing to SharePoint. Reported as an explicit
        // INACTIVE reason so the Jobs page distinguishes it from "there was nothing to do", which is
        // what an empty folder looks like either way.
        Assert.False(result.Ran);
        Assert.Contains("External writes are disabled", result.InactiveReason);
        Assert.Empty(handler.Downloaded);
    }

    [Fact]
    public async Task An_unconfigured_folder_is_inactive_not_an_error()
    {
        using var db = NewDb();
        await AddSpeakerAsync(db, "Nikki Chapple", SpeakerCategory.Community, "https://cdn.test/n.png");

        var (svc, _) = NewService(db, folder: "");
        var result = await svc.RunAsync(EventId);

        Assert.False(result.Ran);
        Assert.Contains("No speaker-photo folder", result.InactiveReason);
    }

    [Fact]
    public async Task A_DEACTIVATED_speaker_is_not_archived()
    {
        using var db = NewDb();
        await AddSpeakerAsync(db, "Left The Event", SpeakerCategory.Community,
            "https://cdn.test/x.png", active: false);

        var (svc, handler) = NewService(db);
        var result = await svc.RunAsync(EventId);

        Assert.Equal(0, result.Archived);
        Assert.Empty(handler.Downloaded);
    }
}
