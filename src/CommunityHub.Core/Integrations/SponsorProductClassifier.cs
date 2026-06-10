using System.Text.RegularExpressions;
using CommunityHub.Core.Config;

namespace CommunityHub.Core.Integrations;

/// <summary>The kind of thing a sponsor ordered.</summary>
public enum SponsorProductKind
{
    Unknown = 0,
    Booth = 1,
    Session = 2,
    BrandedFeature = 3,
    PreDay = 4,
    Addon = 5,
}

/// <summary>The booth tier, where the product is a booth.</summary>
public enum BoothTier
{
    None = 0,
    Gold = 1,
    Diamond = 2,
    Platinum = 3,
    Feature = 4,
}

/// <summary>The classification of one ordered product.</summary>
public sealed record SponsorProductClass(
    SponsorProductKind Kind,
    BoothTier Tier,
    bool GeneratesTasks,
    string? BoothNumber = null);

/// <summary>
/// Classifies a WooCommerce product into a sponsor product kind + booth tier
/// using rules from <c>sponsor.&lt;edition&gt;.json -&gt; productClassification</c>.
/// Walks the rules in declaration order; the first rule whose
/// <c>matchCategoryContains</c> OR <c>matchNameRegex</c> hits wins. This
/// matches the JSON's own semantics ("a product can match multiple types;
/// each matched type's task set is added" - in practice the order in the
/// file puts the more-specific rule first, so first-match-wins behaves the
/// same for our line items). The hardcoded fallbacks the previous version
/// used (substring "branded" / "feature" / "session" / "pre-day") are gone -
/// editing the JSON is now the only knob.
/// </summary>
public sealed class SponsorProductClassifier
{
    private readonly IReadOnlyList<CompiledRule> _rules;

    public SponsorProductClassifier(SponsorConfig config)
    {
        var raw = config.ProductClassification?.Rules ?? new List<ProductClassificationRule>();
        var compiled = new List<CompiledRule>();
        foreach (var r in raw)
        {
            Regex? regex = null;
            if (!string.IsNullOrWhiteSpace(r.MatchNameRegex))
            {
                regex = new Regex(r.MatchNameRegex,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
            }

            compiled.Add(new CompiledRule(
                Kind: MapKind(r.Type),
                CategoryNeedles: r.MatchCategoryContains
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.ToLowerInvariant())
                    .ToArray(),
                NameRegex: regex,
                TierFromSuffix: r.TierFromCategorySuffix
                    .ToDictionary(
                        kv => kv.Key.ToLowerInvariant(),
                        kv => MapTier(kv.Value),
                        StringComparer.OrdinalIgnoreCase),
                DefaultTier: r.DefaultTier is null ? BoothTier.None : MapTier(r.DefaultTier),
                GeneratesTasks: r.GeneratesTasks));
        }
        _rules = compiled;
    }

    // Booth-number extractor: matches "E-1" through "E-99" anywhere in
    // the product name (case-insensitive). Drives the {{boothNumber}}
    // placeholder substituted into the shipping task so the sponsor's
    // actual booth code shows up next to the DSV address.
    private static readonly Regex BoothNumberRegex = new(
        @"\bE-(\d{1,3})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public SponsorProductClass Classify(string categoriesText, string productName)
    {
        var cats = (categoriesText ?? string.Empty).ToLowerInvariant();
        var name = (productName ?? string.Empty).ToLowerInvariant();

        foreach (var rule in _rules)
        {
            var matchByCat = rule.CategoryNeedles.Any(n => cats.Contains(n));
            var matchByName = rule.NameRegex is not null && rule.NameRegex.IsMatch(name);
            if (!matchByCat && !matchByName) continue;

            var tier = BoothTier.None;
            string? boothNumber = null;
            if (rule.Kind == SponsorProductKind.Booth)
            {
                tier = DetectBoothTier(rule, cats, name, matchedByCategory: matchByCat);
                var bnm = BoothNumberRegex.Match(productName ?? string.Empty);
                if (bnm.Success) boothNumber = "E-" + bnm.Groups[1].Value;
            }
            return new SponsorProductClass(rule.Kind, tier, rule.GeneratesTasks, boothNumber);
        }

        // Nothing matched: treat as a non-task addon so it never silently
        // creates work for an unknown SKU.
        return new SponsorProductClass(
            SponsorProductKind.Addon, BoothTier.None, GeneratesTasks: false);
    }

    /// <summary>
    /// Booth tier resolution: first try category-suffix overrides
    /// ("...Exhibitor Booth, Platinum" -&gt; platinum), else fall back to the
    /// rule's default. A product matching booth ONLY via the name regex
    /// (e.g. "Booth E-NN" inside a Branded Feature Package category) is
    /// classified as the Feature tier per the config's own _featureTierNote.
    /// </summary>
    private static BoothTier DetectBoothTier(
        CompiledRule rule, string cats, string name, bool matchedByCategory)
    {
        foreach (var (suffix, tier) in rule.TierFromSuffix)
        {
            if (cats.Contains(suffix)) return tier;
        }
        if (!matchedByCategory && rule.NameRegex is not null && rule.NameRegex.IsMatch(name))
        {
            // Matched booth purely by the "Booth E-NN" name regex - this is
            // a Lounge / Appreciation feature pack that bundles a smaller wall.
            return BoothTier.Feature;
        }
        return rule.DefaultTier;
    }

    private static SponsorProductKind MapKind(string type) => type?.ToLowerInvariant() switch
    {
        "booth"          => SponsorProductKind.Booth,
        "session"        => SponsorProductKind.Session,
        "brandedfeature" => SponsorProductKind.BrandedFeature,
        "preday"         => SponsorProductKind.PreDay,
        "addon"          => SponsorProductKind.Addon,
        _                => SponsorProductKind.Unknown,
    };

    private static BoothTier MapTier(string tier) => tier?.ToLowerInvariant() switch
    {
        "gold"     => BoothTier.Gold,
        "diamond"  => BoothTier.Diamond,
        "platinum" => BoothTier.Platinum,
        "feature"  => BoothTier.Feature,
        _          => BoothTier.None,
    };

    private sealed record CompiledRule(
        SponsorProductKind Kind,
        string[] CategoryNeedles,
        Regex? NameRegex,
        Dictionary<string, BoothTier> TierFromSuffix,
        BoothTier DefaultTier,
        bool GeneratesTasks);
}
