using CommunityHub.Core.Documents;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Assistant;

/// <summary>
/// Builds all-roles AI-helper grounding from the operator-dropped documents in the
/// configured SharePoint grounding folder (REQUIREMENTS §152). Reads the folder through
/// the EXISTING <see cref="ISharePointFileStore"/> seam with the app's own SharePoint
/// credentials (the same §18/§124/§146 read path), extracts each supported document's
/// text via <see cref="DocumentTextExtractor"/>, and emits one grounding section per file.
///
/// <para>CONCRETE in Core (like <see cref="VenueImageService"/>) — no EF / web deps — so it
/// can be registered straight from Program.cs.</para>
///
/// <para>CACHING (the no-deploy refresh window): the assembled sections are held in
/// <see cref="IMemoryCache"/> for <see cref="CacheTtl"/> (~15 min). Replacing a file on
/// SharePoint auto-updates within that window without a redeploy — exactly the §152
/// requirement. Caching the empty result too is fine; it bounds the Graph calls.</para>
///
/// <para>INERT until configured: with no wired store
/// (<see cref="ISharePointFileStore.CanRead"/> false) or a blank
/// <see cref="GraphicsSharePointOptions.GroundingFolderPath"/>, <see cref="GetGroundingAsync"/>
/// returns empty and the folder is never listed — nothing is faked, nothing throws.</para>
///
/// <para>LIMITS bound the prompt cost: at most <see cref="MaxFiles"/> files (alphabetical),
/// each trimmed to <see cref="PerDocCharCap"/> chars, and the combined body capped at
/// <see cref="TotalCharCap"/> chars (overflow files dropped). One bad file can't sink the
/// batch — each file is wrapped in try/catch.</para>
/// </summary>
public sealed class SharePointGroundingProvider : IAiHelperSharePointGroundingProvider
{
    /// <summary>Max characters kept from any single document (longer text is trimmed + "…").</summary>
    public const int PerDocCharCap = 6000;

    /// <summary>Max combined characters across all kept documents (the prompt budget).</summary>
    public const int TotalCharCap = 30000;

    /// <summary>Max number of documents grounded (alphabetical by file name).</summary>
    public const int MaxFiles = 12;

    /// <summary>How long the assembled sections stay cached — the live-replace / no-deploy window.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    private readonly ISharePointFileStore _store;
    private readonly GraphicsSharePointOptions _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SharePointGroundingProvider>? _log;

    public SharePointGroundingProvider(
        ISharePointFileStore store,
        IOptions<GraphicsSharePointOptions> options,
        IMemoryCache cache,
        ILogger<SharePointGroundingProvider>? log = null)
    {
        _store = store;
        _options = options.Value;
        _cache = cache;
        _log = log;
    }

    /// <summary>True when the store can read AND a grounding folder is configured (else inert).</summary>
    public bool CanRead =>
        _store.CanRead && !string.IsNullOrWhiteSpace(_options.GroundingFolderPath);

    public async Task<IReadOnlyList<AiHelperGroundingSection>> GetGroundingAsync(
        CancellationToken ct = default)
    {
        if (!CanRead) return Array.Empty<AiHelperGroundingSection>();

        var folder = _options.GroundingFolderPath.Trim();
        var cacheKey = $"ai-grounding:sp:{folder}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<AiHelperGroundingSection>? cached) &&
            cached is not null)
        {
            return cached;
        }

        var sections = await BuildAsync(folder, ct);
        _cache.Set(cacheKey, sections, CacheTtl);
        return sections;
    }

    private async Task<IReadOnlyList<AiHelperGroundingSection>> BuildAsync(
        string folder, CancellationToken ct)
    {
        IReadOnlyList<SharePointFileRef> files;
        try
        {
            files = await _store.ListAsync(folder, ct);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex,
                "§152 SharePoint grounding: listing folder {Folder} failed; treating as empty.", folder);
            return Array.Empty<AiHelperGroundingSection>();
        }

        var candidates = files
            .Where(f => DocumentTextExtractor.IsSupported(f.Name))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxFiles)
            .ToList();

        var sections = new List<AiHelperGroundingSection>();
        var runningTotal = 0;

        foreach (var file in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var bytes = await _store.DownloadAsync(file.ItemId, ct);
                if (bytes is null || bytes.Length == 0) continue;

                var text = DocumentTextExtractor.Extract(file.Name, bytes);
                if (string.IsNullOrWhiteSpace(text)) continue;

                text = TrimToCap(text, PerDocCharCap);

                // Respect the TOTAL cap: stop adding once this file would overflow the budget.
                if (runningTotal + text.Length > TotalCharCap) break;
                runningTotal += text.Length;

                var name = Path.GetFileNameWithoutExtension(file.Name);
                sections.Add(new AiHelperGroundingSection($"Reference: {name}", text));
            }
            catch (Exception ex)
            {
                // One bad file (corrupt / download hiccup) must not sink the batch.
                _log?.LogWarning(ex,
                    "§152 SharePoint grounding: file {File} skipped (extract/download failed).", file.Name);
            }
        }

        return sections;
    }

    private static string TrimToCap(string text, int cap)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= cap ? trimmed : trimmed[..cap].TrimEnd() + "…";
    }
}
