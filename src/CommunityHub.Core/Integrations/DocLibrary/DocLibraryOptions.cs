namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>
/// THE document-library configuration — site, drive, ONE root, and the per-key relative paths.
/// </summary>
/// <remarks>
/// <para>🔒 <b>One root, one place.</b> Before this, folders were configured in two rival systems —
/// eighteen discrete <c>Graphics__SharePoint__*</c> app settings that live only in Azure, and a
/// second set in the edition config that ships in the repo. They defined some of the same folders
/// twice, so changing one silently left the other stale, and an Azure-side change left no diff to
/// review. That is how the paths drifted out of step with the library (REQUIREMENTS §768).</para>
///
/// <para>🔑 <b>PROD and DEV differ in <see cref="RootFolderPath"/> and nothing else.</b> Every
/// registered folder carries the same name under both roots, so one value moves the whole product
/// between environments. A path that carries its own root cannot do that.</para>
///
/// <para>Upstream ships the keys with <b>empty</b> values; an edition's fork ships the values. No
/// event name, address or absolute URL belongs in shared code.</para>
/// </remarks>
public sealed class DocLibraryOptions
{
    /// <summary>Configuration section: <c>DocLibrary</c>.</summary>
    public const string SectionName = "DocLibrary";

    /// <summary>Master switch. False ⇒ the whole integration is inert and says so.</summary>
    public bool Enabled { get; set; }

    /// <summary>The site the library lives on.</summary>
    public string SiteUrl { get; set; } = string.Empty;

    /// <summary>The document library / drive name. Empty ⇒ the site's default drive.</summary>
    public string DriveName { get; set; } = string.Empty;

    /// <summary>
    /// 🔒 THE ROOT. Every registered path is relative to it.
    /// PROD <c>General/Events/ELDK 2027/EventHub</c> · DEV <c>General/DEVELOPMENT/EventHub</c>.
    /// </summary>
    public string RootFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Per-key relative paths, overriding <see cref="DocLibraryPathDefinition.DefaultRelativePath"/>.
    /// Bound from <c>DocLibrary:Paths:{Key}</c>.
    /// </summary>
    /// <remarks>
    /// A key absent here falls back to its registered default — which is a *documented* default, not
    /// a silent one: the default is visible in <see cref="DocLibraryPaths"/>, shown on the organizer
    /// Paths page, and an Active key that still resolves to nothing fails startup.
    /// </remarks>
    public Dictionary<string, string> Paths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Static FILE names that must be editable, not compiled in — e.g. the template background and
    /// the white wordmark. Bound from <c>DocLibrary:FileNames:{Key}</c>.
    /// </summary>
    public Dictionary<string, string> FileNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The single place permitted to turn a registered key into a library path.</summary>
/// <remarks>
/// 🔒 <b>Nothing else may construct a path.</b> Not string concatenation, not a literal at a call
/// site, not a second copy of the rules in a page model. Every folder the product touches is
/// reachable from here, which is what makes the organizer Paths page complete, the startup check
/// meaningful, and the next audit finite.
/// </remarks>
public interface IDocLibraryPathResolver
{
    /// <summary>True when the library is configured enough to be used at all.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// The full drive-relative folder path for a registered key.
    /// </summary>
    /// <exception cref="DocLibraryPathException">
    /// The key is not registered, or resolves to nothing. ⚠️ Deliberately an exception rather than
    /// an empty string: an empty path silently becomes "the drive root", and a sweep that quietly
    /// enumerates the wrong folder is the failure this whole exercise exists to remove.
    /// </exception>
    string Resolve(string key);

    /// <summary>
    /// The path, or false when the key resolves to nothing. Use where a folder is legitimately
    /// optional and the caller reports being inert.
    /// </summary>
    bool TryResolve(string key, out string path);

    /// <summary>A configured static file name (see <see cref="DocLibraryOptions.FileNames"/>).</summary>
    bool TryResolveFileName(string key, out string fileName);

    /// <summary>Every registered key with its resolved path — the organizer Paths page reads this.</summary>
    IReadOnlyList<DocLibraryResolvedPath> All();

    /// <summary>
    /// Problems that should stop the host: an Active key that resolves to nothing, a configured key
    /// that is not registered, or a path that is malformed. Empty ⇒ configuration is sound.
    /// </summary>
    IReadOnlyList<string> Validate();
}

/// <summary>A registered key, resolved.</summary>
/// <param name="IsOverridden">
/// True when an operator's saved edit (§769) is in force rather than the registered default. The
/// organizer page shows this per row: "why is this folder not what the code says?" must be
/// answerable without opening the database.
/// </param>
public sealed record DocLibraryResolvedPath(
    DocLibraryPathDefinition Definition,
    string RelativePath,
    string FullPath,
    bool IsOverridden);

/// <summary>A path could not be resolved. Names the key, always.</summary>
public sealed class DocLibraryPathException : Exception
{
    public DocLibraryPathException(string key, string message)
        : base($"DocLibrary path '{key}': {message}") => Key = key;

    public string Key { get; }
}
