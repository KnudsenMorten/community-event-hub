using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Doc-lint: guards the screenshots referenced by the published documentation set
/// (README.md, docs/FEATURES.md, docs/DESIGN.md).
///
/// Every Markdown image reference in those three files must resolve to a real,
/// non-empty file under <c>docs/img/</c>, and the key personas must each be
/// illustrated by at least one screenshot. This catches a broken image link or a
/// screenshot deleted/renamed out from under the docs before it ships to the
/// public mirror (where the docs publish verbatim).
///
/// No DB / network — pure filesystem + regex over the repo's docs.
/// </summary>
public sealed class DocImagePresenceTests
{
    // Markdown inline image: ![alt](path)  — captures the path (group "path").
    // Tolerates an optional title:  ![alt](path "title").
    private static readonly Regex ImageRef =
        new(@"!\[[^\]]*\]\(\s*(?<path>[^)\s]+)(?:\s+""[^""]*"")?\s*\)",
            RegexOptions.Compiled);

    // The published doc set. Paths are relative to the repo root; the value is the
    // directory a relative image path in that file is resolved against.
    private static readonly (string DocRelPath, string BaseDirRelPath)[] PublishedDocs =
    {
        ("README.md", ""),                 // README image paths are docs/img/...
        ("docs/FEATURES.md", "docs"),      // a file in docs/ uses img/...
        ("docs/DESIGN.md", "docs"),
    };

    private static string RepoRoot()
    {
        // Walk up from the test assembly until we find the repo root (the dir that
        // holds README.md + docs/). Works under `dotnet test` (bin/Debug/net8.0)
        // and from an IDE test runner alike.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "README.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "docs", "img")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repo root (a directory containing README.md + docs/img) " +
            $"by walking up from '{AppContext.BaseDirectory}'.");
    }

    public static IEnumerable<object[]> PublishedDocPaths()
    {
        foreach (var (doc, _) in PublishedDocs)
        {
            yield return new object[] { doc };
        }
    }

    /// <summary>
    /// Every Markdown image reference in each published doc resolves to a real,
    /// non-empty file. (Local image refs only — http(s) refs, if any, are skipped.)
    /// </summary>
    [Theory]
    [MemberData(nameof(PublishedDocPaths))]
    public void Every_image_reference_resolves_to_a_real_nonempty_file(string docRelPath)
    {
        var root = RepoRoot();
        var baseDir = PublishedDocs.Single(d => d.DocRelPath == docRelPath).BaseDirRelPath;

        var docPath = Path.Combine(root, docRelPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(docPath), $"Published doc not found: {docRelPath}");

        var text = File.ReadAllText(docPath);
        var refs = ImageRef.Matches(text)
            .Select(m => m.Groups["path"].Value)
            .Where(p => !p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !p.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        // Each doc in scope is expected to embed at least one screenshot.
        Assert.True(refs.Count > 0, $"{docRelPath} has no local Markdown image references.");

        var missing = new List<string>();
        foreach (var rel in refs)
        {
            var resolved = Path.GetFullPath(
                Path.Combine(root, baseDir, rel.Replace('/', Path.DirectorySeparatorChar)));

            if (!File.Exists(resolved))
            {
                missing.Add($"{rel}  ->  (file not found: {resolved})");
            }
            else if (new FileInfo(resolved).Length == 0)
            {
                missing.Add($"{rel}  ->  (file is empty: {resolved})");
            }
        }

        Assert.True(missing.Count == 0,
            $"{docRelPath} references image(s) that do not resolve to a real, non-empty file:" +
            Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// Each key persona screenshot exists on disk under docs/img/. This is the
    /// "key personas each have a screenshot" guarantee — independent of which doc
    /// happens to embed it.
    /// </summary>
    [Theory]
    [InlineData("public-landing.png")]            // public site (no login)
    [InlineData("organizer-command-center.png")]  // organizer
    [InlineData("organizer-dashboard.png")]       // organizer
    [InlineData("speaker-hub.png")]               // speaker
    [InlineData("volunteer-schedule.png")]        // volunteer
    [InlineData("sponsor-portal.png")]            // sponsor
    [InlineData("attendee-my-event.png")]         // attendee
    [InlineData("public-speakers.png")]           // public programme
    [InlineData("public-sponsors.png")]           // public sponsors
    public void Key_persona_screenshot_exists_and_is_nonempty(string fileName)
    {
        var imgPath = Path.Combine(RepoRoot(), "docs", "img", fileName);
        Assert.True(File.Exists(imgPath), $"Key persona screenshot missing: docs/img/{fileName}");
        Assert.True(new FileInfo(imgPath).Length > 0, $"Key persona screenshot is empty: docs/img/{fileName}");
    }

    /// <summary>
    /// Each key persona screenshot is actually embedded somewhere in the published
    /// doc set (so the screenshots are illustrated, not just present on disk).
    /// </summary>
    [Theory]
    [InlineData("public-landing.png")]
    [InlineData("organizer-command-center.png")]
    [InlineData("speaker-hub.png")]
    [InlineData("volunteer-schedule.png")]
    [InlineData("sponsor-portal.png")]
    [InlineData("attendee-my-event.png")]
    [InlineData("public-speakers.png")]
    public void Key_persona_screenshot_is_referenced_by_a_published_doc(string fileName)
    {
        var root = RepoRoot();
        var referenced = PublishedDocs.Any(d =>
        {
            var docPath = Path.Combine(root, d.DocRelPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(docPath)) return false;
            var text = File.ReadAllText(docPath);
            return ImageRef.Matches(text)
                .Select(m => m.Groups["path"].Value)
                .Any(p => p.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase) ||
                          p.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        });

        Assert.True(referenced,
            $"Key persona screenshot docs/img/{fileName} is not embedded in any published doc " +
            "(README.md / docs/FEATURES.md / docs/DESIGN.md).");
    }
}
