using System.Text.Json;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations;

/// <summary>One Zoho sponsor record: the category it sits under, and its id.</summary>
public sealed record SponsorZohoLink(string CategoryName, string CategoryId, string ZohoSponsorId);

/// <summary>
/// §1157 — read/write the set of Zoho sponsor records a company has, one per CATEGORY.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-31: <i>"it is important, that ceh stored multiple ids in the sponsor
/// field (array), so any updates happens to all entries … like company name, company description,
/// company website"</i>.</para>
///
/// <para>🔒 <b>ONE place that knows the storage shape.</b> The links live in a JSON column, and a
/// second hand-rolled parse somewhere else is how the two would come to disagree about what a
/// company is linked to — with the visible symptom being a sponsor updated in one Zoho category and
/// stale in another, which is precisely the bug this exists to prevent.</para>
///
/// <para>⚠️ <b>Never throws.</b> Unreadable JSON returns EMPTY, and empty means "no links recorded",
/// which the provisioner treats as work to do — not as "this company has no categories". A parse
/// failure must not silently un-link a company from records that exist in Zoho.</para>
/// </remarks>
public static class SponsorZohoLinks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Every recorded link for this company, newest storage shape first.</summary>
    public static IReadOnlyList<SponsorZohoLink> Read(SponsorInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.ZohoSponsorLinksJson))
        {
            // 🔑 A company provisioned before §1157 has only the primary id and no category
            // recorded. It is surfaced as a link with an EMPTY category rather than dropped: the
            // id is real and must still receive name/description/website updates, even though we
            // cannot yet say which heading it sits under.
            return string.IsNullOrWhiteSpace(info.ZohoSponsorId)
                ? Array.Empty<SponsorZohoLink>()
                : new[] { new SponsorZohoLink(string.Empty, string.Empty, info.ZohoSponsorId!) };
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<SponsorZohoLink>>(info.ZohoSponsorLinksJson!, Json);
            if (parsed is null) return Array.Empty<SponsorZohoLink>();

            return parsed
                .Where(l => !string.IsNullOrWhiteSpace(l.ZohoSponsorId))
                .GroupBy(l => l.ZohoSponsorId, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<SponsorZohoLink>();
        }
    }

    /// <summary>
    /// Store the links, keeping <see cref="SponsorInfo.ZohoSponsorId"/> pointing at the first.
    /// </summary>
    /// <remarks>
    /// 🔒 The primary id is maintained so every pre-§1157 caller keeps working unchanged. It is only
    /// ever set from a link that exists — never cleared while links remain, because a null primary
    /// would make the company look unprovisioned and invite a duplicate create.
    /// </remarks>
    public static void Write(SponsorInfo info, IEnumerable<SponsorZohoLink> links)
    {
        var list = links
            .Where(l => !string.IsNullOrWhiteSpace(l.ZohoSponsorId))
            .GroupBy(l => l.ZohoSponsorId, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(l => l.CategoryName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        info.ZohoSponsorLinksJson = list.Count == 0 ? null : JsonSerializer.Serialize(list, Json);

        // 🔒 The primary is STABLE: it only moves when the record it points at is gone. Re-pointing
        // it at whichever link happens to sort first would silently hand the whole pre-§1157 world
        // — the profile-pushed hash, the stale-id self-heal, every page that shows "the" Zoho id —
        // a different record each time a category was added.
        if (list.Count == 0) return;
        if (string.IsNullOrWhiteSpace(info.ZohoSponsorId)
            || !list.Any(l => string.Equals(l.ZohoSponsorId, info.ZohoSponsorId, StringComparison.Ordinal)))
        {
            info.ZohoSponsorId = list[0].ZohoSponsorId;
        }
    }

    /// <summary>Does this company already have a record under that category?</summary>
    public static bool HasCategory(SponsorInfo info, string categoryName) =>
        Read(info).Any(l => string.Equals(
            l.CategoryName?.Trim(), categoryName?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every Zoho sponsor id to keep in step — what a company-wide field update must fan out to.
    /// </summary>
    /// <remarks>
    /// 🔑 This is the answer to <i>"any updates happens to all entries"</i>: name, description and
    /// website belong to the COMPANY, so a change to one record and not the others leaves the same
    /// company reading differently under two headings on the public site.
    /// </remarks>
    public static IReadOnlyList<string> AllSponsorIds(SponsorInfo info) =>
        Read(info).Select(l => l.ZohoSponsorId).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The Zoho categories this company's purchases entitle it to.</summary>
    public static IReadOnlyList<string> ReadCategories(SponsorInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.ZohoSponsorCategoriesJson)) return Array.Empty<string>();
        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(info.ZohoSponsorCategoriesJson!, Json);
            return parsed?
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Add categories to the company's entitlement set. Returns true when something was added.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>RAISE-ONLY.</b> Nothing is ever removed here — see
    /// <see cref="SponsorInfo.ZohoSponsorCategoriesJson"/>. A company that stops buying a product
    /// keeps the record it already has, and the orphan is reported rather than silently un-listed.
    /// </remarks>
    public static bool MergeCategories(SponsorInfo info, IEnumerable<string> categories)
    {
        var existing = ReadCategories(info).ToList();
        var merged = existing
            .Concat(categories.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (merged.Count == existing.Count) return false;

        info.ZohoSponsorCategoriesJson = JsonSerializer.Serialize(merged, Json);
        return true;
    }
}
