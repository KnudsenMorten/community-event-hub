using CommunityHub.Core.Config;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>One key's test result, ready to render.</summary>
/// <param name="Key">The registered key tested.</param>
/// <param name="ResolvedPath">What it resolved to — shown even on failure, because a wrong path is
/// the most likely cause and the operator cannot judge without seeing it.</param>
public sealed record DocLibraryPathTestResult(
    string Key,
    string ResolvedPath,
    DocLibraryFolderProbe Probe)
{
    public bool Ok => Probe.Exists;
}

/// <summary>
/// §769 / work-order §6.1 — the "Test path" and "Test all" engine: resolve a registered key and go
/// and look, reporting what is really there.
/// </summary>
/// <remarks>
/// <para>🔑 <b>The pre-event health check.</b> Every folder in the product, tested in one click,
/// with a pass/fail line each. The §768 audit had to be done by hand precisely because nothing could
/// answer "do all of these actually exist?" — and by the time a sweep answers it, it answers with
/// silence.</para>
///
/// <para>⚠️ <b>Planned and Reserved keys are tested too, but a miss is not a failure for them.</b>
/// Their folders legitimately may not exist yet; reporting them as red would train the operator to
/// ignore red. They are reported separately.</para>
/// </remarks>
public sealed class DocLibraryPathTester
{
    private readonly IDocLibraryPathResolver _paths;
    private readonly SharePointUploadClient _client;
    private readonly IOptionsMonitor<DocLibraryOptions> _options;

    public DocLibraryPathTester(
        IDocLibraryPathResolver paths, SharePointUploadClient client,
        IOptionsMonitor<DocLibraryOptions> options)
    {
        _paths = paths;
        _client = client;
        _options = options;
    }

    /// <summary>Test ONE registered key.</summary>
    public async Task<DocLibraryPathTestResult> TestAsync(string key, CancellationToken ct = default)
    {
        var o = _options.CurrentValue;

        if (!_paths.TryResolve(key, out var resolved) || string.IsNullOrWhiteSpace(resolved))
        {
            return new DocLibraryPathTestResult(key, string.Empty,
                DocLibraryFolderProbe.Failed(
                    "This key resolves to nothing — set a path for it, or set the root."));
        }

        var probe = await _client.ProbeFolderAsync(o.SiteUrl, o.DriveName, resolved, ct);
        return new DocLibraryPathTestResult(key, resolved, probe);
    }

    /// <summary>
    /// Test every registered key. Sequential on purpose: twenty-odd Graph calls fired at once earn a
    /// throttling response, and a "Test all" that reports 429s is worse than one that takes longer.
    /// </summary>
    public async Task<IReadOnlyList<DocLibraryPathTestResult>> TestAllAsync(CancellationToken ct = default)
    {
        var results = new List<DocLibraryPathTestResult>();
        foreach (var d in DocLibraryPaths.All)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await TestAsync(d.Key, ct));
        }
        return results;
    }
}
