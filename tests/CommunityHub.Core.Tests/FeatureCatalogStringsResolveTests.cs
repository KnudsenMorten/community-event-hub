using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CommunityHub.Core.Settings;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1053c — EVERY FEATURE'S NAME AND DESCRIPTION KEY MUST EXIST IN THE RESOURCES.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-10, pointing at the Settings page: <i>"bug: what is this"</i> — the
/// SpeakerGapReport row was rendering the literal strings
/// <c>Settings.Feat.SpeakerGapReport.Name</c> and <c>Settings.Feat.SpeakerGapReport.Desc</c> where
/// its title and description belong. §878.5 added the feature to <see cref="FeatureCatalog"/> and
/// never added its two resx entries.</para>
///
/// <para>🔴 <b>Why this could sit there unnoticed.</b> A missing resource is NOT an error: the
/// localizer returns the KEY as the value, so the page renders, the build passes and every test
/// stays green. The only symptom is a developer-looking string on a page the operator shows to
/// other people. That is the §335 shape — <i>"the only symptom is that nothing happens"</i> — and
/// the fix is to make the absence fail somewhere.</para>
///
/// <para>🔑 Reads the <c>.resx</c> XML directly rather than going through
/// <c>IStringLocalizer</c>, precisely BECAUSE the localizer's fallback is what hides the problem: a
/// test that asked the localizer would get the key back and happily assert it was non-empty.</para>
/// </remarks>
public sealed class FeatureCatalogStringsResolveTests
{
    private static HashSet<string> ResourceKeys()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var p = Path.Combine(
                dir.FullName, "src", "CommunityHub.Core", "Resources", "SharedResource.resx");
            if (File.Exists(p))
            {
                return XDocument.Load(p).Root!
                    .Elements("data")
                    .Select(d => (string?)d.Attribute("name"))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .ToHashSet(StringComparer.Ordinal);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate SharedResource.resx from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Every_feature_name_and_description_key_has_a_resource_entry()
    {
        var keys = ResourceKeys();

        // The scan must be able to fail: an empty/renamed resx would otherwise make every
        // assertion below vacuously... no, it would fail loudly — but a resx that parsed to zero
        // entries would report EVERY feature as broken, which is a different lie. Pin the shape.
        Assert.True(keys.Count > 100, $"Only {keys.Count} resource entries parsed — the resx shape changed.");

        var missing = new List<string>();
        foreach (var f in FeatureCatalog.All)
        {
            if (!keys.Contains(f.DisplayNameKey)) missing.Add($"{f.Key} → {f.DisplayNameKey}");
            if (!keys.Contains(f.DescriptionKey)) missing.Add($"{f.Key} → {f.DescriptionKey}");
        }

        Assert.True(missing.Count == 0,
            "These FeatureCatalog entries name a resource that does not exist, so the Settings page "
            + "renders the KEY itself as the feature's title/description (§1053c). A missing "
            + "resource is not an error — the localizer returns the key — so nothing else catches "
            + "it:\n  " + string.Join("\n  ", missing.OrderBy(x => x)));
    }
}
