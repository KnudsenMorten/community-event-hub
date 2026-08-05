using System.Net;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.DocLibrary;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §769 — the settings half of the document-library paths page: what may be typed, which layer
/// wins, what the history records, and what "Test path" is able to say.
/// </summary>
/// <remarks>
/// 🔒 <b>The rule these tests exist to protect.</b> A wrong document-library path never throws — the
/// store returns an empty listing and every caller reads that as "nothing to do" (§767: four green
/// production runs against a folder full of photos). So the save is the last moment a human can be
/// told, and the probe is the only reading that can tell EMPTY from MISSING. Both are pinned here.
/// </remarks>
public sealed class DocLibraryPathSettingsTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"doclib-{Guid.NewGuid():N}")
            .Options);

    private static DocLibraryOptions Options() => new()
    {
        Enabled = true,
        SiteUrl = "https://sp.test/sites/x",
        DriveName = "Documents",
        RootFolderPath = "General/Test/EventHub",
    };

    // ---- what an operator may type ---------------------------------------------------------

    [Theory]
    // 🔒 Each of these is a silent failure, not a style preference.
    [InlineData("/Speakers/Photos", "leading slash")]          // escapes the root to the drive root
    [InlineData("Speakers/Photos/", "trailing slash")]
    [InlineData("https://x.sharepoint.com/Shared%20Documents/Speakers", "URL")]
    [InlineData("Speakers/../../Other", "'..'")]
    [InlineData("Speakers//Photos", "doubled slash")]          // an empty segment ⇒ another folder
    [InlineData("Speakers/Ph<o>tos", "illegal character")]
    [InlineData("Speakers/ Photos", "leading space")]          // SharePoint trims it silently
    [InlineData("", "blank")]
    public void A_path_that_would_fail_silently_is_refused_at_save(string value, string _)
    {
        Assert.NotNull(DocLibraryValueValidator.ValidatePath(value));
    }

    [Theory]
    [InlineData("Speakers/Photos")]
    [InlineData("Event/Evaluations/During the event")]         // spaces INSIDE a name are fine
    [InlineData("Sponsors/Booth Collateral")]
    [InlineData("Speakers/Graphics-SoMe/SpeakerTracks")]
    public void An_ordinary_relative_path_is_accepted(string value)
    {
        Assert.Null(DocLibraryValueValidator.ValidatePath(value));
    }

    [Theory]
    [InlineData("Template.JPG", true)]
    [InlineData("LOGO EXPERTS LIVE denmark WHITE_no shadow.png", true)]   // the real file name
    [InlineData("Template", false)]                                       // no extension
    [InlineData("Graphics/Template.png", false)]                          // a path, not a name
    [InlineData("", false)]
    public void A_file_name_is_a_NAME(string value, bool ok)
    {
        Assert.Equal(ok, DocLibraryValueValidator.ValidateFileName(value) is null);
    }

    // ---- which layer wins -------------------------------------------------------------------

    private static (DocLibraryPathResolver Resolver, DocLibraryOverrideCache Cache,
                    DocLibraryOverrideStore Store, ServiceProvider Sp) NewStack(DocLibraryOptions? o = null)
    {
        var dbName = $"doclib-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<CommunityHubDbContext>(b => b.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();

        var cache = new DocLibraryOverrideCache(sp.GetRequiredService<IServiceScopeFactory>());
        var resolver = new DocLibraryPathResolver(o ?? Options(), cache);
        var store = new DocLibraryOverrideStore(
            sp.GetRequiredService<CommunityHubDbContext>(), cache);
        return (resolver, cache, store, sp);
    }

    [Fact]
    public async Task A_saved_edit_BEATS_the_registered_default_and_takes_effect_without_a_restart()
    {
        var (resolver, _, store, sp) = NewStack();
        using var _sp = sp;

        Assert.True(resolver.TryResolve(DocLibraryPaths.SpeakerPhotos, out var before));
        Assert.Equal("General/Test/EventHub/Speakers/Photos", before);

        await store.SetAsync(
            DocLibrarySettingKind.Path, DocLibraryPaths.SpeakerPhotos,
            "Speakers/Portraits", "olivia@example.test");

        // 🔑 No restart, no options reload — SetAsync invalidates the snapshot the resolver reads.
        Assert.True(resolver.TryResolve(DocLibraryPaths.SpeakerPhotos, out var after));
        Assert.Equal("General/Test/EventHub/Speakers/Portraits", after);

        var row = resolver.All().Single(r => r.Definition.Key == DocLibraryPaths.SpeakerPhotos);
        Assert.True(row.IsOverridden);          // the page shows WHY it differs from the code
    }

    [Fact]
    public async Task Restoring_the_default_DELETES_the_row_rather_than_writing_the_default_in()
    {
        var (resolver, _, store, sp) = NewStack();
        using var _sp = sp;

        await store.SetAsync(
            DocLibrarySettingKind.Path, DocLibraryPaths.SpeakerPhotos, "Speakers/Portraits", "o@x.test");
        await store.SetAsync(
            DocLibrarySettingKind.Path, DocLibraryPaths.SpeakerPhotos, null, "o@x.test");

        // 🔒 Writing the default's TEXT would freeze it: a later change to the registry would then
        // never reach an installation that had once "restored" the value.
        Assert.Empty(await store.AllAsync());
        Assert.True(resolver.TryResolve(DocLibraryPaths.SpeakerPhotos, out var path));
        Assert.Equal("General/Test/EventHub/Speakers/Photos", path);
        Assert.False(resolver.All()
            .Single(r => r.Definition.Key == DocLibraryPaths.SpeakerPhotos).IsOverridden);
    }

    [Fact]
    public async Task The_history_survives_the_restore_and_names_who_changed_what()
    {
        var (_, _, store, sp) = NewStack();
        using var _sp = sp;

        await store.SetAsync(
            DocLibrarySettingKind.Path, DocLibraryPaths.SpeakerPhotos, "Speakers/One", "olivia@example.test");
        await store.SetAsync(
            DocLibrarySettingKind.Path, DocLibraryPaths.SpeakerPhotos, "Speakers/Two", "olivia@example.test");
        await store.SetAsync(
            DocLibrarySettingKind.Path, DocLibraryPaths.SpeakerPhotos, null, "morten@example.test");

        var history = await store.HistoryAsync();
        Assert.Equal(3, history.Count);

        // Newest first: the restore, then the edit, then the first move off the default.
        Assert.Null(history[0].NewValue);                       // null NewValue IS "restored"
        Assert.Equal("Speakers/Two", history[0].OldValue);
        Assert.Equal("morten@example.test", history[0].ChangedByEmail);

        Assert.Equal("Speakers/One", history[1].OldValue);
        Assert.Equal("Speakers/Two", history[1].NewValue);

        Assert.Null(history[2].OldValue);                       // null OldValue IS "was the default"
        Assert.Equal("Speakers/One", history[2].NewValue);
    }

    [Fact]
    public async Task Saving_the_same_value_twice_records_nothing()
    {
        var (_, _, store, sp) = NewStack();
        using var _sp = sp;

        Assert.True(await store.SetAsync(
            DocLibrarySettingKind.Path, DocLibraryPaths.SpeakerPhotos, "Speakers/One", "o@x.test"));
        // A second identical save is a no-op — otherwise the history fills with events that did
        // nothing, and the one entry that mattered is buried.
        Assert.False(await store.SetAsync(
            DocLibrarySettingKind.Path, DocLibraryPaths.SpeakerPhotos, "Speakers/One", "o@x.test"));
        Assert.Single(await store.HistoryAsync());
    }

    /// <summary>
    /// 🔒 §769.1 D1 — the retired app-setting layer has NO effect, and is REPORTED rather than
    /// silently ignored. A setting that reads as authoritative in the Azure portal while the product
    /// uses something else is the two-switch trap, and it is how §768 started.
    /// </summary>
    [Fact]
    public void A_surviving_DocLibrary_Paths_app_setting_does_nothing_AND_is_reported()
    {
        var o = Options();
        o.Paths[DocLibraryPaths.SpeakerPhotos] = "Legacy/AppSetting/Folder";
        var (resolver, _, _, sp) = NewStack(o);
        using var _sp = sp;

        Assert.True(resolver.TryResolve(DocLibraryPaths.SpeakerPhotos, out var path));
        Assert.Equal("General/Test/EventHub/Speakers/Photos", path);   // the DEFAULT, not the setting

        var problems = resolver.Validate();
        Assert.Contains(problems, p =>
            p.Contains("SpeakerPhotos", StringComparison.Ordinal)
            && p.Contains("NO effect", StringComparison.Ordinal));
    }

    // ---- what "Test path" can say ------------------------------------------------------------

    private sealed class GraphHandler : HttpMessageHandler
    {
        public HttpStatusCode FolderStatus { get; set; } = HttpStatusCode.OK;
        public string ChildrenJson { get; set; } = """{"value":[]}""";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
            {
                return Json("""{"access_token":"t"}""");
            }
            // ⚠️ ORDER MATTERS, and getting it wrong made two of these tests pass for the wrong
            // reason: the children URL contains "/drives/" and the drive-listing URL contains
            // "/sites/", so the most specific match has to be tested first.
            if (url.Contains(":/children", StringComparison.Ordinal))
            {
                return Json(ChildrenJson);
            }
            if (url.EndsWith("/drives", StringComparison.Ordinal))
            {
                return Json("""{"value":[{"id":"drive-1","name":"Documents"}]}""");
            }
            // 🔑 The SITE lookup always succeeds: FolderStatus is about the FOLDER. Letting it answer
            // this call too made "missing folder" pass because the SITE 404'd — the code never
            // reached the folder at all, so the test proved nothing about the folder.
            if (!url.Contains("/root:", StringComparison.Ordinal))
            {
                return Json("""{"id":"site-1"}""");
            }

            // The folder item itself.
            if (FolderStatus != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(FolderStatus)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                });

            return Json("""{"id":"folder-1","lastModifiedDateTime":"2026-08-01T10:00:00Z"}""");
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private static DocLibraryPathTester NewTester(GraphHandler handler, DocLibraryOptions o)
    {
        var client = new SharePointUploadClient(
            new HttpClient(handler),
            new SharePointUploadOptions
            {
                Enabled = true, TenantId = "t", ClientId = "c", ClientSecret = "s",
            });
        return new DocLibraryPathTester(
            new DocLibraryPathResolver(o), client, new StaticOptions(o));
    }

    private sealed class StaticOptions(DocLibraryOptions value) : IOptionsMonitor<DocLibraryOptions>
    {
        public DocLibraryOptions CurrentValue => value;
        public DocLibraryOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<DocLibraryOptions, string?> listener) => null;
    }

    /// <summary>
    /// 🔒 THE POINT OF THE WHOLE PAGE. Everywhere else a missing folder and an empty folder are the
    /// same thing — an empty listing — which is what let §767 report health for four production runs.
    /// The probe must tell them apart, and say which one it is in words.
    /// </summary>
    [Fact]
    public async Task An_EMPTY_folder_and_a_MISSING_folder_are_not_the_same_answer()
    {
        var o = Options();

        var empty = await NewTester(new GraphHandler(), o).TestAsync(DocLibraryPaths.SpeakerPhotos);
        Assert.True(empty.Ok);
        Assert.True(empty.Probe.IsEmpty);
        Assert.Contains("exists and is empty", empty.Probe.Summary, StringComparison.OrdinalIgnoreCase);

        var missing = await NewTester(
            new GraphHandler { FolderStatus = HttpStatusCode.NotFound }, o)
            .TestAsync(DocLibraryPaths.SpeakerPhotos);
        Assert.False(missing.Ok);
        Assert.False(missing.Probe.Exists);
        Assert.Contains("does not exist", missing.Probe.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_populated_folder_reports_its_count_and_the_newest_file()
    {
        var handler = new GraphHandler
        {
            ChildrenJson = """
            {"value":[
              {"name":"speaker-photo-73.jpg","lastModifiedDateTime":"2026-08-02T17:20:21Z"},
              {"name":"speaker-photo-8.png","lastModifiedDateTime":"2026-08-02T17:20:02Z"},
              {"name":"Archive","folder":{"childCount":2},"lastModifiedDateTime":"2026-07-01T09:00:00Z"}
            ]}
            """,
        };

        var result = await NewTester(handler, Options()).TestAsync(DocLibraryPaths.SpeakerPhotos);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Probe.FileCount);            // the sub-folder is not a file
        Assert.Equal(1, result.Probe.FolderCount);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 2, 17, 20, 21, TimeSpan.Zero), result.Probe.LastModified);
        Assert.Contains("2 file(s)", result.Probe.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A test button that throws tells the operator less than one that says "HTTP 403". The real
    /// error is DATA — work order §6.1: "surfaces the real error on failure".
    /// </summary>
    [Fact]
    public async Task A_permission_failure_is_REPORTED_not_thrown()
    {
        var result = await NewTester(
            new GraphHandler { FolderStatus = HttpStatusCode.Forbidden }, Options())
            .TestAsync(DocLibraryPaths.SpeakerPhotos);

        Assert.False(result.Ok);
        Assert.NotNull(result.Probe.Error);
        Assert.Contains("403", result.Probe.Error!, StringComparison.Ordinal);
        // The path is shown even on failure — a typo is the likeliest cause and the operator
        // cannot judge that without seeing what it resolved to.
        Assert.Equal("General/Test/EventHub/Speakers/Photos", result.ResolvedPath);
    }

    [Fact]
    public async Task Test_all_covers_every_registered_key()
    {
        var results = await NewTester(new GraphHandler(), Options()).TestAllAsync();

        Assert.Equal(DocLibraryPaths.All.Count, results.Count);
        Assert.All(results, r => Assert.False(string.IsNullOrWhiteSpace(r.Key)));
    }
}
