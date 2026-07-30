using System.Text.Json;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Navigation;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §667 — the Zoho Backstage deep links must be the SAME string everywhere, fragment included.
///
/// <para>
/// The operator gave the canonical destinations verbatim (2026-07-29):
/// <c>https://eldk27.expertslive.dk/ELDK27-ExpertsLiveDenmark2027#/exhibitor-dashboard/lead-list</c>
/// and <c>.../inquiry-list</c>. What was actually shipped disagreed in three different ways at once:
/// </para>
/// <list type="bullet">
///   <item>NavBuilder had the right ROUTE names but no event-slug path segment.</item>
///   <item>event.&lt;edition&gt;.json had the same omission.</item>
///   <item>/Sponsor/Leads hard-coded <c>leads</c>/<c>inquiries</c>/<c>meetings</c> — routes that do
///         not exist in the SPA at all.</item>
/// </list>
/// <para>
/// 🔒 Every one of those still LOOKS like it works: Backstage loads its portal root and picks an
/// event, so the tab opens and shows a dashboard. That is precisely why this needs a test rather
/// than a click-through — "it opened something" is not "it opened the lead list".
/// </para>
/// </summary>
public class NavZohoUrlsMatchConfigTests
{
    private static string ConfigUrl(string key)
    {
        var path = Path.Combine(FindRepoRoot(), "config", "event.eldk27.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));

        foreach (var section in doc.RootElement.EnumerateObject())
        {
            if (section.Value.ValueKind == JsonValueKind.Object
                && section.Value.TryGetProperty(key, out var value))
            {
                return value.GetString() ?? string.Empty;
            }
        }

        throw new InvalidOperationException($"'{key}' not found in event.eldk27.json");
    }

    private static string NavHref(string resourceKey)
    {
        var nav = NavBuilder.Build(ParticipantRole.Sponsor, isExhibitor: true);

        var item = nav.AllItems.FirstOrDefault(i => i.LabelKey == resourceKey);
        Assert.True(item is not null, $"nav item '{resourceKey}' not found for an exhibitor sponsor");
        return item!.Href;
    }

    [Theory]
    [InlineData("Nav.LeadsZoho", "leadsListUrl")]
    [InlineData("Nav.InquiriesZoho", "inquiriesListUrl")]
    public void The_nav_item_and_the_edition_config_point_at_the_same_place(string navKey, string configKey)
    {
        var navHref = NavHref(navKey);
        var configured = ConfigUrl(configKey);

        // NavBuilder is static and has no config access, so the value is duplicated by necessity.
        // The query string is ours (?lang=en pins Backstage to English) and only the config copy
        // carries it — compare everything up to that.
        var configuredWithoutQuery = configured.Split('?')[0];

        Assert.Equal(configuredWithoutQuery, navHref.Split('?')[0]);
    }

    /// <summary>
    /// The operator's exact strings. If a future edit "tidies" the slug away, this fails with the
    /// URL he typed sitting in the assertion message.
    /// </summary>
    [Theory]
    [InlineData("Nav.LeadsZoho", "https://eldk27.expertslive.dk/ELDK27-ExpertsLiveDenmark2027#/exhibitor-dashboard/lead-list")]
    [InlineData("Nav.InquiriesZoho", "https://eldk27.expertslive.dk/ELDK27-ExpertsLiveDenmark2027#/exhibitor-dashboard/inquiry-list")]
    public void The_nav_item_matches_the_destination_the_operator_specified(string navKey, string expected)
    {
        Assert.Equal(expected, NavHref(navKey).Split('?')[0]);
    }

    /// <summary>
    /// 🔒 The event-slug segment is the whole point of §667 and the easiest thing to lose again:
    /// without it the fragment route is still perfectly well-formed, so nothing looks broken.
    /// </summary>
    [Theory]
    [InlineData("exhibitorSpaceUrl")]
    [InlineData("exhibitorProfileUrl")]
    [InlineData("boothMembersUrl")]
    [InlineData("boothMaterialsUrl")]
    [InlineData("leadsListUrl")]
    [InlineData("inquiriesListUrl")]
    public void Every_configured_exhibitor_dashboard_url_carries_the_event_slug(string key)
    {
        var url = ConfigUrl(key);

        Assert.Contains("/ELDK27-ExpertsLiveDenmark2027#/", url, StringComparison.Ordinal);
        Assert.Contains("exhibitor-dashboard", url, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))
                && Directory.Exists(Path.Combine(dir.FullName, "config")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }
}
