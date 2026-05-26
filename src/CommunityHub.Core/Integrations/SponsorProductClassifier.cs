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
    bool GeneratesTasks);

/// <summary>
/// Classifies a WooCommerce product into a sponsor product kind + booth tier,
/// using its category text (falling back to the product name). This replaces
/// the obsolete fixed product-ID lists from the source PowerShell: the ELDK27
/// webshop makes every booth its own product, so classification must be
/// rule-based (CONTEXT.md - sponsor product classification).
///
/// The rules here are the documented defaults; in the full config wiring they
/// come from sponsor.&lt;edition&gt;.json -&gt; productClassification.
/// </summary>
public sealed class SponsorProductClassifier
{
    public SponsorProductClass Classify(string categoriesText, string productName)
    {
        var cats = (categoriesText ?? string.Empty).ToLowerInvariant();
        var name = (productName ?? string.Empty).ToLowerInvariant();
        var hay = cats + " " + name;

        // Booth: a tier package with an exhibitor booth, or a "Booth E-NN".
        if (cats.Contains("tier packages with exhibitor booth")
            || ContainsBoothCode(name))
        {
            return new SponsorProductClass(
                SponsorProductKind.Booth, DetectTier(hay), GeneratesTasks: true);
        }

        // Silver: tier package without a booth -> baseline only, no booth tasks.
        if (cats.Contains("tier packages without booth")
            || cats.Contains("digital only"))
        {
            return new SponsorProductClass(
                SponsorProductKind.Booth, BoothTier.None, GeneratesTasks: false);
        }

        if (hay.Contains("pre-day") || hay.Contains("preday"))
        {
            return new SponsorProductClass(
                SponsorProductKind.PreDay, BoothTier.None, GeneratesTasks: true);
        }

        if (cats.Contains("sessions") || hay.Contains("session"))
        {
            return new SponsorProductClass(
                SponsorProductKind.Session, BoothTier.None, GeneratesTasks: true);
        }

        if (hay.Contains("branded") || hay.Contains("feature"))
        {
            return new SponsorProductClass(
                SponsorProductKind.BrandedFeature, BoothTier.None, GeneratesTasks: true);
        }

        // Booth options / package handling / add-ons - no tasks generated.
        if (cats.Contains("booth options")
            || cats.Contains("package handling")
            || cats.Contains("options")
            || cats.Contains("addons")
            || cats.Contains("uncategorized"))
        {
            return new SponsorProductClass(
                SponsorProductKind.Addon, BoothTier.None, GeneratesTasks: false);
        }

        // Unknown - treat as a non-task addon so it never silently creates work.
        return new SponsorProductClass(
            SponsorProductKind.Addon, BoothTier.None, GeneratesTasks: false);
    }

    /// <summary>A booth product whose category gives no tier defaults to Gold.</summary>
    private static BoothTier DetectTier(string hay)
    {
        if (hay.Contains("platinum")) return BoothTier.Platinum;
        if (hay.Contains("diamond")) return BoothTier.Diamond;
        if (hay.Contains("feature")) return BoothTier.Feature;
        if (hay.Contains("gold")) return BoothTier.Gold;
        return BoothTier.Gold; // documented default
    }

    /// <summary>Matches a booth code like "booth e-1" / "e-21".</summary>
    private static bool ContainsBoothCode(string name)
    {
        // Look for "e-" followed by a digit.
        var idx = name.IndexOf("e-", StringComparison.Ordinal);
        return idx >= 0
               && idx + 2 < name.Length
               && char.IsDigit(name[idx + 2]);
    }
}
