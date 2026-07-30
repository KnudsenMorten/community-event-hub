using System.Text.Json;
using CommunityHub.Core.Config;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §690 — the contracted booth-member allowance per tier.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-29: <i>"this is optional outside of the agreed booth members count per
/// contract. feature/gold = 2, diamond = 4, platinum = 6"</i>. These are COMMERCIAL TERMS — what a
/// sponsor is owed under their contract — so a silent change is a customer-facing error, not a
/// styling one. They are pinned here against the SHIPPED config.</para>
///
/// <para>🔒 The allowance drives whether the "Extra booth members" purchase block reads as an
/// exception or as something everyone must do. Get the number wrong and a Platinum sponsor is told
/// to buy staff tickets they already have.</para>
/// </remarks>
public class BoothMemberAllowanceTests
{
    private static SponsorConfig LoadShippedConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CommunityHub.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var path = Path.Combine(dir!.FullName, "config", "sponsor.eldk27.json");
        Assert.True(File.Exists(path), $"Expected the shipped sponsor config at {path}");

        var cfg = JsonSerializer.Deserialize<SponsorConfig>(
            File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(cfg);
        return cfg!;
    }

    [Theory]
    [InlineData("feature", 2)]
    [InlineData("gold", 2)]
    [InlineData("diamond", 4)]
    [InlineData("platinum", 6)]
    public void The_shipped_config_states_the_agreed_allowance_per_tier(string tier, int expected)
    {
        var tiers = LoadShippedConfig().BoothWallSpecs?.Tiers;

        Assert.NotNull(tiers);
        Assert.True(tiers!.TryGetValue(tier, out var spec), $"No '{tier}' tier in boothWallSpecs.");
        Assert.Equal(expected, spec!.BoothMembersIncluded);
    }

    [Fact]
    public void Every_tier_states_an_allowance_so_none_silently_reads_as_zero()
    {
        // 🔒 A tier with no allowance omits the line entirely rather than claiming "0 included" —
        // but for THIS edition every booth tier has a contracted number, so a missing one means the
        // config was edited and a tier was forgotten. Absent is not zero; here it is a gap.
        var tiers = LoadShippedConfig().BoothWallSpecs?.Tiers;
        Assert.NotNull(tiers);

        var missing = tiers!
            .Where(t => t.Value.BoothMembersIncluded <= 0)
            .Select(t => t.Key)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "These booth tiers state no booth-member allowance, so their sponsors will see no "
            + "'your package includes N' line and the extra-ticket block will read as mandatory: "
            + string.Join(", ", missing));
    }
}
