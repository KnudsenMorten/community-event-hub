using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1053c (widened) — EVERY DOTTED LOCALIZER KEY USED IN SOURCE MUST EXIST IN THE RESOURCES.
/// </summary>
/// <remarks>
/// <para>🔑 <b>Why this exists next to <see cref="FeatureCatalogStringsResolveTests"/> rather than
/// inside it.</b> That test proves every <c>FeatureCatalog</c> entry names a real resource — and it
/// passed, green, while <c>Survey.Submit</c> rendered as the literal text
/// <i>"Survey.Submit"</i> on the attendee-facing survey's submit button. The catalog was never the
/// boundary of the defect; it was only where the operator happened to SEE it. A test of the reported
/// case cannot see the unreported one (§1041's lesson, third time).</para>
///
/// <para>🔴 <b>The failure is silent by construction.</b> A missing resource is not an error:
/// <c>IStringLocalizer</c> returns the KEY as the value, so the page renders, the build passes and
/// every other test stays green. The only symptom is developer text on a customer's screen — and on
/// a survey button, the customer is an attendee, not an organizer who would report it.</para>
///
/// <para>⚠️ <b>Scope, deliberately narrow:</b> only DOTTED keys (<c>Foo.Bar</c>) passed as a literal
/// to a localizer indexer. The dotted shape is this codebase's key convention, and it is what
/// distinguishes a key from the other common ASP.NET pattern — <c>Localizer["Some English
/// sentence"]</c>, where the key IS the fallback text and a missing entry is CORRECT, not a bug.
/// Widening this test to bare sentences would make it fire on working code.</para>
///
/// <para>Reads the <c>.resx</c> XML directly, for the same reason the sibling test does: asking the
/// localizer would return the key and cheerfully assert it was non-empty.</para>
/// </remarks>
public sealed class LocalizerKeysResolveTests
{
    /// <summary>Matches <c>Localizer["Foo.Bar"]</c>, <c>SharedLocalizer[...]</c>, <c>_loc[...]</c>.</summary>
    private static readonly Regex KeyUse = new(
        @"(?:Localizer|_loc|\bL)\[\s*""([A-Za-z0-9]+(?:\.[A-Za-z0-9_]+)+)""",
        RegexOptions.Compiled);

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(
                    dir.FullName, "src", "CommunityHub.Core", "Resources", "SharedResource.resx")))
            {
                return dir;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repo root from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Every_dotted_localizer_key_used_in_source_has_a_resource_entry()
    {
        var root = RepoRoot();

        var resourceKeys = XDocument
            .Load(Path.Combine(root.FullName, "src", "CommunityHub.Core", "Resources", "SharedResource.resx"))
            .Root!
            .Elements("data")
            .Select(d => (string?)d.Attribute("name"))
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);

        var projects = new[] { "CommunityHub", "CommunityHub.Core", "CommunityHub.Jobs" };
        var files = projects
            .Select(p => Path.Combine(root.FullName, "src", p))
            .Where(Directory.Exists)
            .SelectMany(d => Directory
                .EnumerateFiles(d, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)))
            // bin/obj hold generated copies of the very files being scanned — counting them would
            // double every key and make a stale build look like source.
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();

        var used = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (Match m in KeyUse.Matches(File.ReadAllText(file)))
            {
                used.TryAdd(m.Groups[1].Value, Path.GetRelativePath(root.FullName, file));
            }
        }

        // A rename of the source layout, or a regex that stops matching, would make every assertion
        // below vacuous — the test would pass loudest exactly when it had stopped looking. Pin the
        // scan's own shape first. (Same guard as the sibling test's `keys.Count > 100`.)
        Assert.True(files.Count > 200, $"Only {files.Count} source files scanned — the layout changed.");
        Assert.True(used.Count > 500, $"Only {used.Count} localizer keys found — the pattern stopped matching.");
        Assert.True(resourceKeys.Count > 1000,
            $"Only {resourceKeys.Count} resource entries parsed — the resx shape changed.");

        var missing = used
            .Where(kv => !resourceKeys.Contains(kv.Key))
            .Select(kv => $"{kv.Key}  (used in {kv.Value})")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "These localizer keys have no entry in SharedResource.resx, so the page renders the KEY "
            + "ITSELF as its visible text (§1053c). A missing resource is not an error — the "
            + "localizer returns the key — so nothing else catches it:\n  "
            + string.Join("\n  ", missing));
    }
}
