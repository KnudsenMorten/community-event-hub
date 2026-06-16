using CommunityHub.Core.Domain;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// REQUIREMENTS §21 Participant [H] — the structured dietary capture must be
/// AGGREGATABLE for catering (free text is not). These pin the pure
/// <see cref="DietaryAggregator"/> roll-up: allergen head-counts, diet
/// head-counts, free-text count, and "people with any requirement". No DB.
/// </summary>
public sealed class DietaryAggregatorTests
{
    private static DietaryRequirement Row(
        string? diet = null, bool gluten = false, bool milk = false,
        bool peanuts = false, string? other = null) =>
        new()
        {
            DietChoice = diet, Gluten = gluten, Milk = milk, Peanuts = peanuts,
            OtherAllergens = other,
        };

    [Fact]
    public void Counts_each_allergen_across_people()
    {
        var rows = new[]
        {
            Row(gluten: true),
            Row(gluten: true, milk: true),
            Row(milk: true),
            Row(),                       // no requirement
        };

        var summary = DietaryAggregator.Aggregate(rows);

        // Gluten 2, Milk 2 — both present; descending then alpha-stable.
        Assert.Equal(2, summary.Allergens.Single(b => b.Key == nameof(DietaryRequirement.Gluten)).Count);
        Assert.Equal(2, summary.Allergens.Single(b => b.Key == nameof(DietaryRequirement.Milk)).Count);
        // Allergens nobody flagged are not in the result.
        Assert.DoesNotContain(summary.Allergens, b => b.Key == nameof(DietaryRequirement.Fish));
    }

    [Fact]
    public void Counts_each_diet_choice_ignoring_none()
    {
        var rows = new[]
        {
            Row(diet: "Vegan"),
            Row(diet: "Vegan"),
            Row(diet: "Vegetarian"),
            Row(diet: "None"),           // "None" is not a special diet — excluded
            Row(diet: null),             // not stated — excluded
        };

        var summary = DietaryAggregator.Aggregate(rows);

        Assert.Equal(2, summary.Diets.Single(b => b.Key == "Vegan").Count);
        Assert.Equal(1, summary.Diets.Single(b => b.Key == "Vegetarian").Count);
        Assert.DoesNotContain(summary.Diets, b => b.Key == "None");
        // Vegan (2) ranks before Vegetarian (1).
        Assert.Equal("Vegan", summary.Diets[0].Key);
    }

    [Fact]
    public void Counts_free_text_and_people_with_any_requirement()
    {
        var rows = new[]
        {
            Row(other: "kiwi"),
            Row(gluten: true),
            Row(diet: "Vegan"),
            Row(),                       // nothing
            Row(diet: "None"),           // "None" + no flags + no text = no requirement
        };

        var summary = DietaryAggregator.Aggregate(rows);

        Assert.Equal(1, summary.FreeTextCount);
        Assert.Equal(3, summary.PeopleWithAnyRequirement);   // kiwi, gluten, vegan
    }

    [Fact]
    public void Empty_input_yields_empty_buckets()
    {
        var summary = DietaryAggregator.Aggregate(Array.Empty<DietaryRequirement>());

        Assert.Empty(summary.Allergens);
        Assert.Empty(summary.Diets);
        Assert.Equal(0, summary.FreeTextCount);
        Assert.Equal(0, summary.PeopleWithAnyRequirement);
    }
}
