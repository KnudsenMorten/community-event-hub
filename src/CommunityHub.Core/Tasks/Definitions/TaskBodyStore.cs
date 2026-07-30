using System.Collections.Concurrent;
using CommunityHub.Core.Config;
using CommunityHub.Core.Tasks.Model;
using CommunityHub.Core.Tasks.Parsing;

namespace CommunityHub.Core.Tasks.Definitions;

/// <summary>
/// §684.8 / §684.14 — loads and caches the authored task bodies from
/// <c>config/tasks/&lt;edition&gt;/…md</c>.
/// </summary>
/// <remarks>
/// <para>Mirrors <c>ContentMarkdownRenderer</c>'s proven pattern (15 content pages under
/// <c>config/content/&lt;edition&gt;/</c>, resolved through <see cref="ConfigPaths"/> so the file is
/// found from both the App Service content root and the Functions host's mounted package).</para>
///
/// <para>🔒 <b>A missing file is an EMPTY body, never an exception</b> (§682). Authored content must
/// never be able to break a runtime path: the reminder job walks every task, and one missing file
/// taking the job down is precisely the incident that widened the description column. The miss is
/// logged and <c>TaskBodyCatalogTests</c> turns it into a build failure — loud where it is cheap.</para>
/// </remarks>
public sealed class TaskBodyStore
{
    private readonly string _editionCode;
    private readonly ConcurrentDictionary<string, string> _rawCache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="editionCode">
    /// The edition folder, e.g. <c>eldk27</c>. A CONSTRUCTOR argument, not a constant: the evergreen
    /// rule (CLAUDE.md) is that a new edition is a new config folder, not a code change.
    /// </param>
    public TaskBodyStore(string editionCode = "eldk27")
    {
        _editionCode = editionCode;
    }

    /// <summary>The on-disk path a body ref resolves to.</summary>
    public string ResolvePath(string bodyRef) =>
        ConfigPaths.Resolve($"config/tasks/{_editionCode}/{bodyRef}.md");

    /// <summary>Load and parse a body with NO placeholder substitution.</summary>
    /// <remarks>
    /// ⚠️ Only for callers that do not have a participant in hand (the catalogue tests, tooling).
    /// Rendering for a real participant must go through <see cref="LoadRaw"/> + substitution first —
    /// see <see cref="TaskBodyService"/> and §688.
    /// </remarks>
    public TaskBody Load(string bodyRef) => TaskBodyParser.Parse(LoadRaw(bodyRef));

    /// <summary>
    /// The RAW authored markdown, cached. The file is read once per process; parsing happens per
    /// render, after placeholders are substituted.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>§688 — SUBSTITUTE, THEN PARSE. The order is the whole fix.</b> Placeholder values were
    /// previously injected as TEXT at render time, i.e. AFTER parsing, which silently broke three
    /// separate things at once on live tasks:
    /// <list type="bullet">
    ///   <item><description>a value containing another <c>{{placeholder}}</c> never resolved —
    ///     <c>{{furnitureSpec}}</c> holds <c>{{couponCode}}</c>, so sponsors saw a literal
    ///     <c>**{{couponCode}}**</c>;</description></item>
    ///   <item><description>a value containing MARKUP was not interpreted — the same
    ///     <c>furnitureSpec</c> dumped its <c>__headings__</c>, <c>**bold**</c> and <c>* bullets</c>
    ///     as one run-on line of raw markers;</description></item>
    ///   <item><description>a value containing an E-MAIL or a URL never became a link, because
    ///     link detection runs in the parser over literal text — hence "email to sabine is not
    ///     mailto format".</description></item>
    /// </list>
    /// Substituting first makes all three work by construction rather than by three special cases.
    /// It also matches what the old seed-time pull did, so migrated copy behaves identically.
    ///
    /// <para>Config values are as trusted as the body files themselves — both are in-repo,
    /// operator-authored — so allowing markup through from config is deliberate, not an oversight.
    /// The renderers still HTML-encode everything at output (§684.17).</para>
    /// </remarks>
    public string LoadRaw(string bodyRef) =>
        _rawCache.GetOrAdd(bodyRef, r =>
        {
            var path = ResolvePath(r);
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        });

    /// <summary>True when the body file exists on disk.</summary>
    public bool Exists(string bodyRef) => File.Exists(ResolvePath(bodyRef));

    /// <summary>Drop the cache (tests, and a future content-reload hook).</summary>
    public void Clear() => _rawCache.Clear();
}
