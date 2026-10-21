using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>§1305 — naming of a shop product's pictures in the SharePoint Products folder.</summary>
public static class ShopProductPictures
{
    /// <summary>
    /// <c>{product id}-{slug}-{n}.{ext}</c> — stable, so a re-upload overwrites instead of piling up, and readable, so the folder
    /// makes sense to a human browsing SharePoint. The old webshop's product id is kept for products that came from it (their
    /// existing files keep their names); a product created in CEH uses <c>ceh{id}</c>.
    /// </summary>
    public static string FileName(ShopProduct product, ShopProductImage img, string contentType)
    {
        var ext = contentType switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/svg+xml" => ".svg",
            _ => ".jpg",
        };
        var slug = string.IsNullOrWhiteSpace(product.Slug) ? "product" : product.Slug;
        slug = new string(slug.Where(ch => char.IsLetterOrDigit(ch) || ch == '-').ToArray());
        if (slug.Length > 80) slug = slug[..80];
        var id = product.WooProductId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? $"ceh{product.Id}";
        return $"{id}-{slug}-{img.SortOrder + 1}{ext}";
    }
}
