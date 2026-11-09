using System.Reflection;
using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1389 — the 299 old EF migrations were squashed into one baseline (operator 2026-11-08: "Yes, at a quiet time" · "approved
/// to start"). These pin the startup decision that lets DEV/PROD adopt it without a schema change.
/// </summary>
public sealed class MigrationBaselineTests
{
    [Fact]
    public void A_fully_migrated_pre_squash_database_adopts_the_baseline()
    {
        var history = new[] { "20260525114420_InitialCreate", "20261108190000_TravelReviewLog", MigrationBaseline.LastSquashedMigration };
        Assert.Equal(MigrationBaseline.Action.Adopt, MigrationBaseline.Decide(history));
    }

    [Fact]
    public void An_empty_database_is_left_to_the_baseline_and_an_adopted_one_is_left_alone()
    {
        Assert.Equal(MigrationBaseline.Action.Nothing, MigrationBaseline.Decide(null));
        Assert.Equal(MigrationBaseline.Action.Nothing, MigrationBaseline.Decide(Array.Empty<string>()));
        Assert.Equal(MigrationBaseline.Action.Nothing,
            MigrationBaseline.Decide(new[] { MigrationBaseline.LastSquashedMigration, MigrationBaseline.BaselineMigration }));
    }

    [Fact]
    public void A_database_that_is_behind_is_refused_rather_than_guessed()
    {
        Assert.Equal(MigrationBaseline.Action.Behind,
            MigrationBaseline.Decide(new[] { "20260525114420_InitialCreate", "20261108190000_TravelReviewLog" }));
    }

    [Fact]
    public void The_assembly_has_exactly_the_baseline_and_its_id_matches_the_constant()
    {
        var ids = typeof(CommunityHubDbContext).Assembly.GetTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => t.GetCustomAttribute<MigrationAttribute>()?.Id)
            .ToList();
        // One baseline + any migration added after the squash (always later than the baseline).
        Assert.Contains(MigrationBaseline.BaselineMigration, ids);
        Assert.DoesNotContain(MigrationBaseline.LastSquashedMigration, ids);
        Assert.All(ids, id => Assert.True(string.CompareOrdinal(id, MigrationBaseline.BaselineMigration) >= 0,
            $"{id} is older than the baseline — the old migrations must stay squashed"));
    }
}
