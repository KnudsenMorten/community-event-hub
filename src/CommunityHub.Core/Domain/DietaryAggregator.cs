namespace CommunityHub.Core.Domain;

/// <summary>
/// Pure, testable roll-up over a set of <see cref="DietaryRequirement"/> rows
/// (REQUIREMENTS §21 Participant [H] — structured dietary capture must be
/// aggregatable for catering). Counts how many people need each allergen avoided
/// and how many chose each diet, so a caterer gets head-counts instead of
/// free-text. No DB / EF dependency — feed it whatever rows the caller queried
/// (e.g. all Dinner-surface rows for an edition).
/// </summary>
public static class DietaryAggregator
{
    /// <summary>A single counted bucket (allergen token or diet choice) with its head-count.</summary>
    public sealed record Bucket(string Key, int Count);

    /// <summary>The catering roll-up: per-allergen and per-diet counts + totals.</summary>
    public sealed record Summary(
        IReadOnlyList<Bucket> Allergens,
        IReadOnlyList<Bucket> Diets,
        int FreeTextCount,
        int PeopleWithAnyRequirement);

    /// <summary>
    /// Aggregate the supplied rows. Allergen buckets are returned for every
    /// allergen that at least one person flagged, descending by count; diet
    /// buckets likewise for every non-"None" diet chosen.
    /// </summary>
    public static Summary Aggregate(IEnumerable<DietaryRequirement> rows)
    {
        var list = rows as IReadOnlyCollection<DietaryRequirement> ?? rows.ToList();

        var allergenCounts = DietaryRequirement.AllergenTokens
            .Select(token => new Bucket(
                token,
                list.Count(r => r.Allergens().First(a => a.Token == token).IsSet)))
            .Where(b => b.Count > 0)
            .OrderByDescending(b => b.Count)
            .ThenBy(b => b.Key, StringComparer.Ordinal)
            .ToList();

        var dietCounts = list
            .Where(r => !string.IsNullOrWhiteSpace(r.DietChoice) && r.DietChoice != "None")
            .GroupBy(r => r.DietChoice!)
            .Select(g => new Bucket(g.Key, g.Count()))
            .OrderByDescending(b => b.Count)
            .ThenBy(b => b.Key, StringComparer.Ordinal)
            .ToList();

        var freeText = list.Count(r => !string.IsNullOrWhiteSpace(r.OtherAllergens));
        var withAny = list.Count(r => r.HasAny);

        return new Summary(allergenCounts, dietCounts, freeText, withAny);
    }
}
