using System.Text;
using CommunityHub.Core.Assistant;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The §152 all-roles SharePoint grounding provider: reads the operator-dropped grounding
/// folder through the <see cref="ISharePointFileStore"/> read seam (app creds), extracts each
/// supported doc's text, caches the assembled sections ~15 min, and emits one section per file.
/// Proves: inert when not configured, supported-only listing + per-file sections, skipping
/// unsupported/corrupt files, the cache (no-deploy refresh window), and the per-doc + total
/// caps. Plus a builder test proving the source is all-roles (no role gate). FAKE store only.
/// </summary>
public class SharePointGroundingProviderTests
{
    private const string Folder = "General/Events/ELDK 2027/EventHub/ExtraAIGroundingInfo";

    private static SharePointGroundingProvider NewProvider(
        FakeSharePointFileStore store, string folder = Folder) =>
        new(store,
            Options.Create(new GraphicsSharePointOptions
            {
                Enabled = true,
                SiteUrl = "https://contoso.sharepoint.example.test/sites/eldk",
                GroundingFolderPath = folder,
            }),
            new MemoryCache(new MemoryCacheOptions()));

    // ---- inert (not configured) -------------------------------------------

    [Fact]
    public async Task Is_inert_when_store_cannot_read()
    {
        var store = new FakeSharePointFileStore(canRead: false);
        store.Add(Folder, "info.md", Text("hello"));
        var provider = NewProvider(store);

        Assert.False(provider.CanRead);
        Assert.Empty(await provider.GetGroundingAsync());
        Assert.Equal(0, store.ListCount(Folder)); // never listed
    }

    [Fact]
    public async Task Is_inert_when_folder_path_blank()
    {
        var store = new FakeSharePointFileStore(canRead: true);
        store.Add(Folder, "info.md", Text("hello"));
        var provider = NewProvider(store, folder: "");

        Assert.False(provider.CanRead);
        Assert.Empty(await provider.GetGroundingAsync());
        Assert.Equal(0, store.ListCount(Folder)); // store never touched when folder is blank
    }

    // ---- listing + extraction ---------------------------------------------

    [Fact]
    public async Task Lists_supported_files_and_emits_one_section_per_file()
    {
        var store = new FakeSharePointFileStore(canRead: true);
        store.Add(Folder, "venue.md", Text("The venue is at the conference centre."));
        store.Add(Folder, "faq.txt", Text("Parking is free."));
        var provider = NewProvider(store);

        var sections = await provider.GetGroundingAsync();

        Assert.Equal(2, sections.Count);
        // Heading is "Reference: <file-name-without-extension>", ordered by name.
        Assert.Equal("Reference: faq", sections[0].Heading);
        Assert.Equal("Reference: venue", sections[1].Heading);
        Assert.Contains("Parking is free.", sections[0].Body);
        Assert.Contains("conference centre", sections[1].Body);
    }

    [Fact]
    public async Task Skips_unsupported_and_corrupt_files()
    {
        var store = new FakeSharePointFileStore(canRead: true);
        store.Add(Folder, "good.md", Text("keep me"));
        store.Add(Folder, "logo.png", new byte[] { 1, 2, 3 });         // unsupported ext
        store.Add(Folder, "broken.pdf", Text("not a real pdf"));        // corrupt → extractor null
        var provider = NewProvider(store);

        var sections = await provider.GetGroundingAsync();

        Assert.Single(sections);
        Assert.Equal("Reference: good", sections[0].Heading);
    }

    // ---- caching (no-deploy refresh window) -------------------------------

    [Fact]
    public async Task Second_call_is_served_from_cache()
    {
        var store = new FakeSharePointFileStore(canRead: true);
        store.Add(Folder, "info.md", Text("hello"));
        var provider = NewProvider(store);

        await provider.GetGroundingAsync();
        await provider.GetGroundingAsync();

        Assert.Equal(1, store.ListCount(Folder)); // folder listed once → 15-min TTL is the refresh window
    }

    // ---- caps --------------------------------------------------------------

    [Fact]
    public async Task Trims_each_document_to_the_per_doc_cap()
    {
        var store = new FakeSharePointFileStore(canRead: true);
        store.Add(Folder, "big.txt", Text(new string('a', SharePointGroundingProvider.PerDocCharCap + 4000)));
        var provider = NewProvider(store);

        var sections = await provider.GetGroundingAsync();

        Assert.Single(sections);
        // Trimmed to cap + the ellipsis marker.
        Assert.Equal(SharePointGroundingProvider.PerDocCharCap + 1, sections[0].Body.Length);
        Assert.EndsWith("…", sections[0].Body);
    }

    [Fact]
    public async Task Respects_the_total_cap_and_drops_overflow_files()
    {
        var store = new FakeSharePointFileStore(canRead: true);
        // 12 files × 5000 chars = 60000; the 30000 total cap keeps only the first 6.
        const int per = 5000;
        for (var i = 0; i < 12; i++)
        {
            store.Add(Folder, $"doc-{i:00}.txt", Text(new string('a', per)));
        }
        var provider = NewProvider(store);

        var sections = await provider.GetGroundingAsync();

        Assert.Equal(SharePointGroundingProvider.TotalCharCap / per, sections.Count); // 6 kept
        var total = sections.Sum(s => s.Body.Length);
        Assert.True(total <= SharePointGroundingProvider.TotalCharCap, $"total {total} exceeded cap");
    }

    // ---- builder integration: all-roles, no gate --------------------------

    [Fact]
    public async Task Builder_adds_sharepoint_grounding_for_a_non_organizer_role()
    {
        var builder = new AiHelperGroundingBuilder(
            content: new NullContentProvider(),
            ownData: new EmptyOwnDataProvider(),
            organizerOps: null,
            publicInfo: null,
            sharePointGrounding: new FixedGroundingProvider(
                new AiHelperGroundingSection("Reference: venue", "The venue is downtown.")));

        // Volunteer (NOT organizer) — the §152 source has NO role gate, so it must appear.
        var ctx = await builder.BuildAsync(eventId: 1, participantId: 42, ParticipantRole.Volunteer);

        Assert.Contains(ctx.Sections, s => s.Heading == "Reference: venue" && s.Body.Contains("downtown"));
    }

    // ---- helpers -----------------------------------------------------------

    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>In-memory fake store: per-folder refs + per-item bytes; counts list calls.</summary>
    private sealed class FakeSharePointFileStore : ISharePointFileStore
    {
        private readonly Dictionary<string, List<SharePointFileRef>> _byFolder = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _listCounts = new(StringComparer.OrdinalIgnoreCase);

        public FakeSharePointFileStore(bool canRead) { CanRead = canRead; }

        public bool CanRead { get; }
        public bool CanStore => false;

        public void Add(string folder, string name, byte[] bytes)
        {
            if (!_byFolder.TryGetValue(folder, out var list)) { list = new(); _byFolder[folder] = list; }
            var itemId = $"item:{folder}:{name}";
            list.Add(new SharePointFileRef(itemId, name, "https://store.example.test/" + name));
            _bytes[itemId] = bytes;
        }

        public int ListCount(string folder) => _listCounts.TryGetValue(folder, out var n) ? n : 0;

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default)
        {
            if (!CanRead) return Task.FromResult<IReadOnlyList<SharePointFileRef>>(Array.Empty<SharePointFileRef>());
            _listCounts[relativeFolder] = ListCount(relativeFolder) + 1;
            return Task.FromResult<IReadOnlyList<SharePointFileRef>>(
                _byFolder.TryGetValue(relativeFolder, out var list) ? list.ToList() : new List<SharePointFileRef>());
        }

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
            Task.FromResult(CanRead && _bytes.TryGetValue(itemId, out var b) ? b : null);

        // Unused write/store seam.
        public Task<StoredFile> StoreAsync(string relativePath, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("not used by §152");
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task<StoredFile> UploadToFolderAsync(string relativeFolder, string fileName, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("not used by §152");
        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullContentProvider : IAiHelperContentProvider
    {
        public string? GetContentMarkdown(string slug) => null;
    }

    private sealed class EmptyOwnDataProvider : IAiHelperOwnDataProvider
    {
        public Task<IReadOnlyList<AiHelperGroundingSection>> GetOwnDataAsync(
            int eventId, int participantId, ParticipantRole role, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AiHelperGroundingSection>>(Array.Empty<AiHelperGroundingSection>());
    }

    private sealed class FixedGroundingProvider : IAiHelperSharePointGroundingProvider
    {
        private readonly IReadOnlyList<AiHelperGroundingSection> _sections;
        public FixedGroundingProvider(params AiHelperGroundingSection[] sections) => _sections = sections;
        public Task<IReadOnlyList<AiHelperGroundingSection>> GetGroundingAsync(CancellationToken ct = default) =>
            Task.FromResult(_sections);
    }
}
