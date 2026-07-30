using System.Xml.Linq;
using CommunityHub.Core.Settings;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §700 Batch A — every <see cref="FeatureGroup"/> must render its OWN heading on
/// <c>/Organizer/Settings</c>.
///
/// <para>The gap this closes: <c>GroupHeading()</c> in <c>Settings.cshtml</c> is a switch with a
/// <c>_ =&gt; "Settings.Title"</c> fallback. An unmapped group therefore does not throw and does not
/// render blank — it renders the PAGE TITLE ("Settings") as a section heading, i.e. a chapter that
/// looks deliberate and says nothing. Batch A adds three groups at once and Batch B reorganises the
/// page around them, so the next group added is very likely to arrive without its heading.</para>
///
/// <para>🔒 The same failure mode as §683 and §699: a declared thing wired to nothing. It is
/// invisible to every page-model test (the fallback is a legal value) and invisible to a smoke run
/// (the page returns 200), which is why it is pinned statically here.</para>
/// </summary>
public sealed class FeatureGroupHeadingTests
{
    [Fact]
    public void Every_feature_group_has_a_heading_case_in_the_settings_page()
    {
        var page = ReadSettingsPage();

        foreach (var g in Enum.GetValues<FeatureGroup>())
        {
            Assert.Contains($"FeatureGroup.{g} => \"Settings.Group.{g}\"", page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_feature_group_heading_key_resolves_to_a_real_resource_string()
    {
        var resx = ReadSharedResource();

        foreach (var g in Enum.GetValues<FeatureGroup>())
        {
            var key = $"Settings.Group.{g}";
            var value = resx.Root!
                .Elements("data")
                .FirstOrDefault(d => (string?)d.Attribute("name") == key)
                ?.Element("value")?.Value;

            Assert.False(string.IsNullOrWhiteSpace(value),
                $"FeatureGroup.{g} has no '{key}' string — its section would fall back to the page " +
                "title and render a chapter headed \"Settings\".");
        }
    }

    /// <summary>
    /// The retired group must not creep back via a leftover string. §699's standing rule: if
    /// nothing uses it, delete it — a stale "Incubation (test)" heading is exactly the kind of
    /// dead declaration that invites someone to re-home a shipped feature back into it.
    /// </summary>
    [Fact]
    public void The_retired_incubation_group_leaves_nothing_behind()
    {
        Assert.DoesNotContain("Incubation", ReadSettingsPage(), StringComparison.Ordinal);

        var stale = ReadSharedResource().Root!
            .Elements("data")
            .Select(d => (string?)d.Attribute("name"))
            .Where(n => n is not null && n.Contains("Incubation", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(stale);
    }

    private static string ReadSettingsPage() =>
        File.ReadAllText(Path.Combine(RepoPath("src", "CommunityHub", "Pages"), "Organizer", "Settings.cshtml"));

    private static XDocument ReadSharedResource() =>
        XDocument.Load(Path.Combine(
            RepoPath("src", "CommunityHub.Core", "Resources"), "SharedResource.resx"));

    private static string RepoPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(parts));
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not locate {Path.Combine(parts)} from {AppContext.BaseDirectory}");
    }
}
