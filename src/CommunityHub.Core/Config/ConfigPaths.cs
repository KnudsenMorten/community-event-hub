namespace CommunityHub.Core.Config;

/// <summary>
/// Resolves a (possibly relative) config-file path so the shipped per-edition JSON
/// is found regardless of the host's working directory. The classic App Service web
/// app runs with the content root as its working dir (so "config/x.json" resolves),
/// but the Flex-Consumption Functions host runs from a mounted package with a
/// different working dir — there the relative path misses and a loader throws
/// (e.g. the sponsor order-pull's "Sponsor config not found: config/sponsor.eldk27.json").
/// Resolving against <see cref="AppContext.BaseDirectory"/> (where the shipped
/// <c>config/</c> sits next to the DLLs) makes file-config load in BOTH hosts.
/// </summary>
public static class ConfigPaths
{
    /// <summary>
    /// Return <paramref name="path"/> if it already exists; otherwise, for a
    /// relative path, try it under <see cref="AppContext.BaseDirectory"/>. Falls
    /// through to the original path when neither exists so the caller's own
    /// missing-file handling still applies.
    /// </summary>
    public static string Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path ?? string.Empty;
        if (File.Exists(path)) return path;
        if (!Path.IsPathRooted(path))
        {
            var underBase = Path.Combine(AppContext.BaseDirectory, path);
            if (File.Exists(underBase)) return underBase;
        }
        return path;
    }
}
