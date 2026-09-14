using System.Text.Json;
using CommunityHub.Core.Config;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1157 — the SHIPPED config, checked against the LIVE webshop categories and the LIVE Zoho
/// headings, so a rename on either side fails here instead of in production.
/// </summary>
/// <remarks>
/// <para>🔑 <b>Why this file exists.</b> §767 shipped a SharePoint sweep whose matcher was written
/// from a guessed naming convention; it matched nothing for four production runs and nobody could
/// see it, because "found no files" and "there are no files" look identical. A category needle has
/// exactly that failure mode — a sponsor is silently filed under the wrong heading, or under none.
/// The strings below were read off the live products list, so they are evidence, not assumptions.</para>
///
/// <para>⚠️ Every Zoho heading the map names MUST have a pinned id in the event config: a heading
/// with no id cannot be created, so the map would promise a record the provisioner can never make.</para>
/// </remarks>
public sealed class SponsorZohoCategoryConfigTests
{
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CommunityHub.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!;
    }

    private static SponsorConfig SponsorConfig()
    {
        var path = Path.Combine(RepoRoot().FullName, "config", "sponsor.eldk27.json");
        Assert.True(File.Exists(path), $"Expected the shipped sponsor config at {path}");
        var cfg = JsonSerializer.Deserialize<SponsorConfig>(
            File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(cfg);
        return cfg!;
    }

    private static IReadOnlyDictionary<string, string> PinnedZohoCategories()
    {
        var path = Path.Combine(RepoRoot().FullName, "config", "event.eldk27.json");
        Assert.True(File.Exists(path), $"Expected the shipped event config at {path}");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("zohoSponsorCategoryIds", out var el)
            && el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
                if (!p.Name.StartsWith('_')) map[p.Name] = p.Value.GetString() ?? string.Empty;
        }
        return map;
    }

    /// <summary>
    /// 🔒 Every heading the map can produce is one Zoho actually has an id for.
    /// </summary>
    [Fact]
    public void Every_mapped_heading_has_a_pinned_zoho_id()
    {
        var pinned = PinnedZohoCategories();
        var rules = SponsorConfig().ZohoSponsorCategoryMap?.Rules ?? new List<ZohoSponsorCategoryRule>();

        Assert.NotEmpty(rules);
        foreach (var rule in rules)
        {
            Assert.True(
                pinned.TryGetValue(rule.ZohoCategory, out var id) && !string.IsNullOrWhiteSpace(id),
                $"zohoSponsorCategoryMap names '{rule.ZohoCategory}', which has no pinned id in "
                + "event.eldk27.json -> zohoSponsorCategoryIds. The provisioner cannot create it.");
        }
    }

    /// <summary>
    /// The live product categories he confirmed, each mapped by the SHIPPED config.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-31, naming the three headings whose source was previously unknown:
    /// <i>"Exhibitor Session Sponsors (renamed from Exhibitor Session)"</i>, the Pre-day master
    /// class products under <i>Track Sponsors</i>, and the Founding Partner product under
    /// <i>Found Partner Sponsor</i>.
    /// </remarks>
    [Theory]
    [InlineData("Exhibitor Session Sponsors, Session (20 min)", "Session sponsors")]
    [InlineData("Exhibitor Session Sponsors, Session (60 min)", "Session sponsors")]
    [InlineData("Exhibitor Tier Package with Booth, Track Sponsors", "Track sponsors")]
    [InlineData("Community & Appreciation Sponsor, Found Partner Sponsor", "Founding partner")]
    [InlineData("Swag Sponsor", "Swag sponsor")]
    [InlineData("Hospitality & Comfort Sponsor", "Hospitality & comfort sponsor")]
    [InlineData("Attendee Experience Sponsor", "Attendee experience sponsors")]
    [InlineData("Content & Program Sponsors", "Content & program sponsors")]
    [InlineData("Exhibitor Tier Package with Booth, Gold Exhibitor (Shared)", "Gold sponsors")]
    [InlineData("Exhibitor Tier Package with Booth, Platinum Exhibitor", "Platinum sponsors")]
    [InlineData("Sponsor Tier Package (Digital Only), Silver Sponsor", "Silver sponsors")]
    public void The_shipped_map_files_a_live_product_under_the_right_heading(
        string categoriesText, string expected)
    {
        var mapped = new SponsorZohoCategoryMapper(SponsorConfig()).MapProduct(categoriesText);
        Assert.Contains(expected, mapped, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 🔒 The rename he asked me to check: the webshop category "Exhibitor Session" is now
    /// "Exhibitor Session Sponsors", and the PRODUCT CLASSIFIER's session rule must still fire.
    /// </summary>
    /// <remarks>
    /// The old needle still substring-matches the new name, so nothing broke — but that is a
    /// coincidence of the rename appending a word rather than replacing one, and a coincidence is
    /// not a guarantee. This asserts the outcome so the next rename fails a test instead of quietly
    /// making every sponsor session purchase invisible (which is exactly what the previous rename,
    /// "Sessions" → "Exhibitor Session", did in 2026-07).
    /// </remarks>
    [Fact]
    public void The_renamed_session_category_still_classifies_as_a_session_purchase()
    {
        var cls = new SponsorProductClassifier(SponsorConfig())
            .Classify("Exhibitor Session Sponsors, Session (20 min)", "Sponsor Expo Intro 20 min Session – ELDK27");

        Assert.Equal(SponsorProductKind.Session, cls.Kind);
        Assert.False(cls.Unmatched);
    }

    /// <summary>
    /// ⚠️ Logistics add-ons are NOT sponsorships and must map to no heading at all.
    /// </summary>
    [Fact]
    public void An_addon_never_earns_a_sponsor_heading()
    {
        var mapper = new SponsorZohoCategoryMapper(SponsorConfig());

        Assert.Empty(mapper.MapProduct("Booth Furniture, Booth Option"));
        Assert.Empty(mapper.MapProduct("Extra Staff Tickets"));
        Assert.Empty(mapper.MapProduct("Ticket Packages"));
    }
}
