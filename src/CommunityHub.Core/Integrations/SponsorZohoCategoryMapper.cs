using CommunityHub.Core.Config;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1157 — which Zoho sponsor CATEGORIES a company's purchases put it under.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-31: <i>"something is wrong with the category assigned for sponsors in
/// zoho, as some are linked to silver sponsor, even though the product they bought is linked to one
/// of these categories … it happens when they dont buy a exhibitor booth but non-booth sponsor
/// products"</i> · <i>"a sponsor that buys 3 products that fits into 3 categories must be created 3
/// times and linked to each category"</i>.</para>
///
/// <para>🔑 <b>The bug this replaces.</b> The Zoho category used to be derived from
/// <c>SponsorInfo.SponsorPackage</c>, and the package is derived from the BOOTH TIER — so a company
/// that bought no booth was <c>Silver</c> by definition, and every non-booth sponsorship (swag,
/// hospitality, content &amp; program, competition …) was filed under "Silver sponsors" no matter
/// what it was. The heading a sponsor appears under publicly is what they paid for, so it has to
/// come from the PRODUCT, not from a tier they never bought.</para>
///
/// <para>⚠️ <b>Returns a SET, not one answer.</b> One product can carry two sponsor categories (the
/// Founding Partner product is filed under both "Community &amp; Appreciation Sponsor" and "Found
/// Partner Sponsor"), and a company usually buys several products. Collapsing that to a single
/// winner is the shape of the original defect.</para>
/// </remarks>
public sealed class SponsorZohoCategoryMapper
{
    private readonly IReadOnlyList<(string Category, string[] Needles)> _rules;

    public SponsorZohoCategoryMapper(SponsorConfig config)
    {
        _rules = (config.ZohoSponsorCategoryMap?.Rules ?? new List<ZohoSponsorCategoryRule>())
            .Where(r => !string.IsNullOrWhiteSpace(r.ZohoCategory))
            .Select(r => (
                Category: r.ZohoCategory.Trim(),
                Needles: r.MatchCategoryContains
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim().ToLowerInvariant())
                    .ToArray()))
            .Where(r => r.Needles.Length > 0)
            .ToList();
    }

    /// <summary>True when the edition has no map configured — the caller then keeps the
    /// pre-§1157 single-record behaviour rather than provisioning nothing.</summary>
    public bool IsEmpty => _rules.Count == 0;

    /// <summary>
    /// Every Zoho category one product's WooCommerce categories text puts it under.
    /// </summary>
    /// <remarks>
    /// ⚠️ EVERY matching rule contributes — this deliberately does not stop at the first hit, unlike
    /// <see cref="SponsorProductClassifier"/>, whose question ("what kind of thing is this?") has one
    /// answer while this one's ("where does it belong publicly?") can have several.
    /// </remarks>
    public IReadOnlyList<string> MapProduct(string? categoriesText)
    {
        if (string.IsNullOrWhiteSpace(categoriesText)) return Array.Empty<string>();
        var hay = categoriesText.ToLowerInvariant();

        return _rules
            .Where(r => r.Needles.Any(n => hay.Contains(n, StringComparison.Ordinal)))
            .Select(r => r.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Union the categories across every product a company ordered.</summary>
    public IReadOnlyList<string> MapCompany(IEnumerable<string?> productCategoryTexts) =>
        productCategoryTexts
            .SelectMany(MapProduct)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
