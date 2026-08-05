using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>
/// The default <see cref="IDocLibraryPathResolver"/> — joins the configured root to a registered
/// key's relative path, and refuses to guess.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Normalisation is load-bearing, not tidiness.</b> A stray leading slash makes a Graph
/// path absolute-from-drive-root and silently escapes the configured root; a doubled slash produces
/// an empty path segment that Graph resolves to a *different* folder than the eye expects. Both fail
/// as "folder not found", which every caller downstream reports as "nothing to do".</para>
///
/// <para>⚠️ <b>Encoding is NOT done here.</b> Both roots contain spaces (<c>ELDK 2027</c>,
/// <c>Shared Documents</c>) and several folders do too (<c>Good to know</c>, <c>Booth Collateral</c>).
/// Percent-encoding belongs in the Graph adapter, which knows it is building a URL — encoding here
/// would double-encode at the adapter and, again, present as "folder not found".</para>
/// </remarks>
public sealed class DocLibraryPathResolver : IDocLibraryPathResolver
{
    private readonly IOptionsMonitor<DocLibraryOptions>? _monitor;
    private readonly DocLibraryOptions? _fixed;

    // §769 — the operator's saved edits. Null for the fixed/test resolver and for any host that has
    // not wired the cache, in which case every key is its registered default.
    private readonly DocLibraryOverrideCache? _overrides;

    /// <summary>The DI constructor — an options MONITOR, so a runtime edit takes effect.</summary>
    public DocLibraryPathResolver(
        IOptionsMonitor<DocLibraryOptions> options, DocLibraryOverrideCache? overrides = null)
    {
        _monitor = options;
        _overrides = overrides;
    }

    /// <summary>
    /// A fixed-configuration resolver, for tests and one-shot tooling that has no options pipeline.
    /// </summary>
    public DocLibraryPathResolver(DocLibraryOptions options, DocLibraryOverrideCache? overrides = null)
    {
        _fixed = options;
        _overrides = overrides;
    }

    private DocLibraryOptions Current => _fixed ?? _monitor!.CurrentValue;

    /// <summary>
    /// §769 — the persisted override for a key, or null. This is the ONE place the saved layer is
    /// consulted, so the precedence (saved edit ▸ registered default) is stated once.
    /// </summary>
    private string? SavedPath(string key) =>
        _overrides is not null
        && _overrides.Paths.TryGetValue(key, out var v)
        && !string.IsNullOrWhiteSpace(v)
            ? v
            : null;

    /// <inheritdoc />
    public bool IsConfigured =>
        Current.Enabled
        && !string.IsNullOrWhiteSpace(Current.SiteUrl)
        && !string.IsNullOrWhiteSpace(Current.RootFolderPath);

    /// <inheritdoc />
    public string Resolve(string key)
    {
        var definition = DocLibraryPaths.Find(key)
            ?? throw new DocLibraryPathException(key,
                "not a registered path. Add it to DocLibraryPaths before using it — a folder that "
                + "exists only as a string at a call site is invisible to the Paths page, the "
                + "startup check and the next audit.");

        if (!TryResolveCore(definition, out var full, out var _))
        {
            throw new DocLibraryPathException(key,
                $"resolved to nothing. Set DocLibrary:Paths:{key} (or the root, "
                + $"DocLibrary:RootFolderPath, which is currently "
                + $"'{Current.RootFolderPath}').");
        }

        return full;
    }

    /// <inheritdoc />
    public bool TryResolve(string key, out string path)
    {
        path = string.Empty;
        var definition = DocLibraryPaths.Find(key);
        if (definition is null) return false;
        return TryResolveCore(definition, out path, out _);
    }

    /// <inheritdoc />
    public bool TryResolveFileName(string key, out string fileName)
    {
        fileName = string.Empty;
        if (string.IsNullOrWhiteSpace(key)) return false;

        // §769 — the saved edit first, then the shipped configuration. File names KEEP their config
        // layer (unlike paths): they are shipped per edition in the fork's own settings, not a
        // second definition of something the registry already owns.
        if (_overrides is not null
            && _overrides.FileNames.TryGetValue(key, out var saved)
            && !string.IsNullOrWhiteSpace(saved))
        {
            fileName = saved.Trim();
            return true;
        }

        if (!Current.FileNames.TryGetValue(key, out var configured)) return false;
        if (string.IsNullOrWhiteSpace(configured)) return false;
        fileName = configured.Trim();
        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<DocLibraryResolvedPath> All() =>
        DocLibraryPaths.All
            .Select(d =>
            {
                TryResolveCore(d, out var full, out var relative);
                return new DocLibraryResolvedPath(d, relative, full, SavedPath(d.Key) is not null);
            })
            .ToList();

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (!Current.Enabled) return problems;   // deliberately off ⇒ nothing to complain about

        if (string.IsNullOrWhiteSpace(Current.SiteUrl))
            problems.Add($"{DocLibraryOptions.SectionName}:SiteUrl is not set.");

        if (string.IsNullOrWhiteSpace(Current.RootFolderPath))
        {
            // 🔑 Reported once, at the root, rather than as ~30 identical per-key failures. A wall
            // of errors hides which one actually needs fixing.
            problems.Add(
                $"{DocLibraryOptions.SectionName}:RootFolderPath is not set — every path depends "
                + "on it, so nothing can resolve.");
        }
        else
        {
            foreach (var d in DocLibraryPaths.Required)
            {
                if (!TryResolveCore(d, out _, out _))
                    problems.Add(
                        $"{DocLibraryOptions.SectionName}:Paths:{d.Key} resolves to nothing "
                        + $"(status Active, area {d.Area}).");
            }
        }

        // 🔒 §769.1 D1 — THE RETIRED LAYER IS REPORTED, NOT IGNORED.
        //
        // Per-key paths moved into the database (the organizer Paths page), so a surviving
        // `DocLibrary:Paths:*` app setting no longer does anything. Saying nothing about it would be
        // the two-switch trap in its purest form: the Azure portal shows a folder path, it reads as
        // authoritative, and the product uses a different one. Every such key is therefore a named
        // configuration problem until somebody deletes it.
        foreach (var key in Current.Paths.Keys)
        {
            problems.Add(
                $"{DocLibraryOptions.SectionName}:Paths:{key} is an app setting, and app settings no "
                + "longer set paths — the organizer 'Document library paths' page does (§769). This "
                + "setting has NO effect: delete it, and set the path on the page if it differs from "
                + "the default.");
        }

        // A SAVED override for a key nobody registered is either a typo or a folder the code
        // invented. Both are worth saying out loud — silently ignoring one is how an orphan path
        // survives an audit.
        if (_overrides is not null)
        {
            foreach (var key in _overrides.Paths.Keys)
            {
                if (DocLibraryPaths.Find(key) is null)
                    problems.Add(
                        $"A saved path override exists for '{key}', which is not registered in "
                        + "DocLibraryPaths — a typo, or a folder that needs registering.");
            }
        }

        return problems;
    }

    private bool TryResolveCore(DocLibraryPathDefinition definition, out string full, out string relative)
    {
        full = string.Empty;

        // 🔒 §769.1 D1 — TWO layers, not three. The operator's saved edit, else the registered
        // default. `DocLibraryOptions.Paths` (the DocLibrary:Paths:* app settings) is deliberately
        // NOT consulted here: it is retired, and Validate() reports any surviving key as a problem
        // rather than letting it look authoritative in the Azure portal while doing nothing.
        relative = SavedPath(definition.Key) is { } saved
            ? Normalize(saved)
            : Normalize(definition.DefaultRelativePath);

        var root = Normalize(Current.RootFolderPath);
        if (root.Length == 0 || relative.Length == 0) return false;

        full = $"{root}/{relative}";
        return true;
    }

    /// <summary>Trim, collapse doubled separators, drop leading/trailing ones.</summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var parts = value.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('/', parts);
    }
}
