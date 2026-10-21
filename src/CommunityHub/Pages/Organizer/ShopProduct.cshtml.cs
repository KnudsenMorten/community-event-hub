using CommunityHub.Audit;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Integrations.Webshop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1305 — the organizer's product editor: status, price, stock, descriptions, category, sort order, booth
/// and timeslot of one shop product.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-26: <i>"i need an organizer page where i can manage each products including all
/// relevant fields like status (published, private), price, stock, description, etc"</i>.</para>
///
/// <para>🔒 <b>Only STOCK goes back to the webshop</b> (operator 2026-09-26: <i>"no writeback to woocommerce of
/// anything except stock"</i>). Stock is the shared counter, written at once under the narrow <c>WebshopStock</c>
/// ceiling (the same one a sale uses). Everything else — name, status, price, texts, category, pictures — stays in
/// CEH (§1312 — the old webshop is closed, so CEH owns every product; <see cref="ShopProduct.ContentManagedInCeh"/> is kept
/// only as history).</para>
/// </remarks>
[Authorize]
public class ShopProductModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ShopStockService _stock;
    private readonly TimeProvider _clock;
    private readonly ISharePointFileStore _store;
    private readonly IDocLibraryPathResolver _paths;

    public ShopProductModel(
        CommunityHubDbContext db, ICurrentParticipantAccessor participant,
        ShopStockService stock, TimeProvider clock, ISharePointFileStore store, IDocLibraryPathResolver paths)
    {
        _store = store;
        _paths = paths;
        _db = db;
        _participant = participant;
        _stock = stock;
        _clock = clock;
    }

    public bool AccessDenied { get; private set; }
    public ShopProduct? Product { get; private set; }
    public ShopAvailability? Live { get; private set; }
    public IReadOnlyList<ShopCategory> Categories { get; private set; } = Array.Empty<ShopCategory>();
    [TempData] public string? Message { get; set; }
    [TempData] public bool MessageIsError { get; set; }

    /// <summary>The statuses an organizer can choose (Removed is the import's own verdict).</summary>
    public static readonly ShopProductStatus[] EditableStatuses =
        { ShopProductStatus.Published, ShopProductStatus.Private, ShopProductStatus.Draft };

    public sealed class ProductInput
    {
        public string Name { get; set; } = string.Empty;
        public ShopProductStatus Status { get; set; }
        public decimal PriceEur { get; set; }
        /// <summary>Discounted price; blank = no discount.</summary>
        public decimal? SalePriceEur { get; set; }
        /// <summary>Swag only: the sponsor's share in %; blank = the edition default.</summary>
        public decimal? SponsorContributionPercent { get; set; }
        public bool ManageStock { get; set; }
        public int? StockQuantity { get; set; }
        public string? ShortDescriptionHtml { get; set; }
        public string? DescriptionHtml { get; set; }
        public int? PrimaryCategoryId { get; set; }
        public int SortOrder { get; set; }
        public string? BoothId { get; set; }
        public string? Timeslot { get; set; }
        public string? TimeslotTime { get; set; }
    }

    [BindProperty] public ProductInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return Challenge();
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }
        if (!await LoadAsync(me.EventId, id, ct)) return NotFound();
        var p = Product!;
        Input = new ProductInput
        {
            Name = p.Name, Status = p.Status, PriceEur = p.PriceEur, SalePriceEur = p.SalePriceEur, SponsorContributionPercent = p.SponsorContributionPercent, ManageStock = p.ManageStock,
            StockQuantity = Live?.Remaining ?? p.StockQuantity, ShortDescriptionHtml = p.ShortDescriptionHtml,
            DescriptionHtml = p.DescriptionHtml, PrimaryCategoryId = p.PrimaryCategoryId, SortOrder = p.SortOrder,
            BoothId = p.BoothId, Timeslot = p.Timeslot, TimeslotTime = p.TimeslotTime,
        };
        return Page();
    }

    [Audit("Edited a sponsor shop product", Category = AuditCategory.Admin, Action = "webshop.product-edit", TargetType = "ShopProduct")]
    public async Task<IActionResult> OnPostSaveAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var p = await _db.ShopProducts.Include(x => x.Categories)
            .FirstOrDefaultAsync(x => x.Id == id && x.EventId == me.EventId, ct);
        if (p is null) return NotFound();

        var i = Input;
        if (string.IsNullOrWhiteSpace(i.Name)) return Fail(id, "The product needs a name.");
        if (i.PriceEur < 0) return Fail(id, "The price cannot be negative.");
        if (i.SalePriceEur is decimal sale && (sale < 0 || sale >= i.PriceEur))
            return Fail(id, "The discounted price must be lower than the normal price (leave it blank for no discount).");
        if (i.ManageStock && i.StockQuantity is null or < 0) return Fail(id, "Enter a stock of 0 or more, or untick 'Count stock'.");
        if (!EditableStatuses.Contains(i.Status)) return Fail(id, "Choose Published, Private or Draft.");
        ShopCategory? category = null;
        if (i.PrimaryCategoryId is int cid)
        {
            category = await _db.ShopCategories.FirstOrDefaultAsync(c => c.Id == cid && c.EventId == me.EventId, ct);
            if (category is null) return Fail(id, "That category no longer exists.");
        }

        var notes = new List<string>();

        // ---- Stock: CEH's own counter --------------------------------------------------------------------
        var stockChanged = i.ManageStock != p.ManageStock || (i.ManageStock && i.StockQuantity != p.StockQuantity);
        if (stockChanged)
        {
            notes.Add("stock updated");   // §1312 — CEH is the one stock counter (the old webshop is closed)
            p.ManageStock = i.ManageStock;
            p.StockQuantity = i.ManageStock ? i.StockQuantity : null;
            p.InStock = !i.ManageStock || i.StockQuantity > 0;
        }

        // ---- Content ---------------------------------------------------------------------------------
        var name = i.Name.Trim();
        // Unchanged text is kept byte for byte (a stock-only save must not count as a content edit);
        // anything typed here is sanitized before it is stored or sent.
        var shortHtml = Html(i.ShortDescriptionHtml, p.ShortDescriptionHtml);
        var longHtml = Html(i.DescriptionHtml, p.DescriptionHtml);
        var contentChanged = name != p.Name || i.Status != p.Status || i.PriceEur != p.PriceEur
            || shortHtml != p.ShortDescriptionHtml || longHtml != p.DescriptionHtml || i.SortOrder != p.SortOrder;
        var localChanged = category?.Id != p.PrimaryCategoryId || Blank(i.BoothId) != p.BoothId
            || Blank(i.Timeslot) != p.Timeslot || Blank(i.TimeslotTime) != p.TimeslotTime;

        if (contentChanged)
        {
            p.Name = name;
            p.Status = i.Status;
            p.PriceEur = i.PriceEur;
            p.ShortDescriptionHtml = shortHtml;
            p.DescriptionHtml = longHtml;
            p.SortOrder = i.SortOrder;
            // 🔒 Never written back to the webshop — only stock is (operator 2026-09-26).
            notes.Add("text, price and status saved in CEH (the webshop is not changed)");
        }
        if (localChanged)
        {
            p.PrimaryCategory = category;
            p.PrimaryCategoryId = category?.Id;
            if (category is not null && p.Categories.All(c => c.CategoryId != category.Id))
                p.Categories.Add(new ShopProductCategory { ProductId = p.Id, CategoryId = category.Id });
            p.BoothId = Blank(i.BoothId);
            p.Timeslot = Blank(i.Timeslot);
            p.TimeslotTime = Blank(i.TimeslotTime);
            notes.Add("category/booth/timeslot saved in CEH");
        }

        // The discount is CEH's own — it never touches the webshop and does not stop the sync from updating the
        // normal price, so it does not take the product over.
        if (i.SponsorContributionPercent is decimal share && (share <= 0 || share > 100))
            return Fail(id, "The sponsor's share must be between 1 and 100 % (leave it blank for the default).");
        if (i.SponsorContributionPercent != p.SponsorContributionPercent)
        {
            p.SponsorContributionPercent = i.SponsorContributionPercent;
            notes.Add(i.SponsorContributionPercent is decimal sh
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"sponsor share set to {sh:0.##} %")
                : "sponsor share back to the default");
        }

        if (i.SalePriceEur != p.SalePriceEur)
        {
            p.SalePriceEur = i.SalePriceEur;
            notes.Add(i.SalePriceEur is decimal s
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"discount set: €{s:0.##} instead of €{i.PriceEur:0.##}")
                : "discount removed");
        }

        if (contentChanged || localChanged) p.ContentManagedInCeh = true;
        if (notes.Count == 0) { Message = "Nothing changed."; return RedirectToPage(new { id }); }
        p.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        Message = "Saved: " + string.Join("; ", notes) + ".";
        return RedirectToPage(new { id });
    }

    /// <summary>A picture larger than this is refused before it is decoded.</summary>
    public const long MaxPictureBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Add a picture from the organizer's computer. It is decoded and re-encoded (so only a real image is ever
    /// stored), saved in the shop's products folder in SharePoint and served by CEH — never sent to the webshop.
    /// </summary>
    [Audit("Added a picture to a sponsor shop product", Category = AuditCategory.Admin, Action = "webshop.product-picture-add", TargetType = "ShopProduct")]
    public async Task<IActionResult> OnPostAddPictureAsync(int id, IFormFile? picture, string? altText, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var p = await _db.ShopProducts.Include(x => x.Images).FirstOrDefaultAsync(x => x.Id == id && x.EventId == me.EventId, ct);
        if (p is null) return NotFound();
        if (picture is null || picture.Length == 0) return Fail(id, "Choose a picture first.");
        if (picture.Length > MaxPictureBytes) return Fail(id, "The picture is larger than 8 MB.");
        if (!_store.CanStore || !_paths.TryResolve(DocLibraryPaths.SponsorWebshopProducts, out var folder) || string.IsNullOrWhiteSpace(folder))
            return Fail(id, "Pictures cannot be stored right now: no SharePoint folder is configured for the shop's products.");

        byte[] bytes;
        string contentType;
        try
        {
            await using var s = picture.OpenReadStream();
            using var img = await SixLabors.ImageSharp.Image.LoadAsync(s, ct);
            img.Mutate(x => x.Resize(new SixLabors.ImageSharp.Processing.ResizeOptions
            {
                Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max,
                Size = new SixLabors.ImageSharp.Size(2000, 2000),
            }));
            using var ms = new MemoryStream();
            var png = picture.ContentType.Contains("png", StringComparison.OrdinalIgnoreCase);
            if (png) await img.SaveAsPngAsync(ms, ct); else await img.SaveAsJpegAsync(ms, ct);
            bytes = ms.ToArray();
            contentType = png ? "image/png" : "image/jpeg";
        }
        catch (Exception ex) when (ex is SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException)
        {
            return Fail(id, "That file is not a picture CEH can read (use PNG, JPG or WebP).");
        }

        // §1312 — ONE upload (operator 2026-09-27: "upload if none exist. replace if one exist already"): with a picture
        // already there, the new file replaces the MAIN picture in place; the old file stays in SharePoint.
        var main = p.Images.OrderBy(x => x.SortOrder).FirstOrDefault();
        if (main is not null)
        {
            main.SourceUrl = ShopProductImage.CehUploadPrefix + Guid.NewGuid().ToString("N");
            main.ContentType = contentType;
            if (!string.IsNullOrWhiteSpace(altText)) main.AltText = altText.Trim();
            var replaced = await _store.UploadToFolderAsync(folder, ShopProductPictures.FileName(p, main, contentType), bytes, contentType, ct);
            main.SharePointPath = replaced.Path;
            main.SharePointItemId = replaced.ItemId;
            main.CopiedAt = _clock.GetUtcNow();
            main.CopyError = null;
            p.ContentManagedInCeh = true;   // the sync must not put the webshop's picture back
            p.UpdatedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            Message = "Main picture replaced (in CEH only — the webshop is not changed).";
            return RedirectToPage(new { id });
        }

        var row = new ShopProductImage
        {
            SourceUrl = ShopProductImage.CehUploadPrefix + Guid.NewGuid().ToString("N"),
            AltText = string.IsNullOrWhiteSpace(altText) ? null : altText.Trim(),
            SortOrder = p.Images.Count == 0 ? 0 : p.Images.Max(x => x.SortOrder) + 1,
            ContentType = contentType,
        };
        var stored = await _store.UploadToFolderAsync(folder, ShopProductPictures.FileName(p, row, contentType), bytes, contentType, ct);
        row.SharePointPath = stored.Path;
        row.SharePointItemId = stored.ItemId;
        row.CopiedAt = _clock.GetUtcNow();
        p.Images.Add(row);
        p.ContentManagedInCeh = true;       // the sync must not drop the pictures chosen here
        p.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        Message = "Picture added (in CEH only — the webshop is not changed).";
        return RedirectToPage(new { id });
    }

    [Audit("Removed a picture from a sponsor shop product", Category = AuditCategory.Admin, Action = "webshop.product-picture-remove", TargetType = "ShopProduct")]
    public async Task<IActionResult> OnPostRemovePictureAsync(int id, int imageId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var p = await _db.ShopProducts.Include(x => x.Images).FirstOrDefaultAsync(x => x.Id == id && x.EventId == me.EventId, ct);
        var img = p?.Images.FirstOrDefault(x => x.Id == imageId);
        if (p is null || img is null) return NotFound();
        // The SharePoint file is left in place: a signed contract or a cached page may still point at it.
        p.Images.Remove(img);
        _db.ShopProductImages.Remove(img);
        p.ContentManagedInCeh = true;
        p.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        Message = "Picture removed from the CEH shop (the webshop is not changed).";
        return RedirectToPage(new { id });
    }

    /// <summary>Make a picture the first one — the one the shop and the catalogue show.</summary>
    [Audit("Chose the main picture of a sponsor shop product", Category = AuditCategory.Admin, Action = "webshop.product-picture-first", TargetType = "ShopProduct")]
    public async Task<IActionResult> OnPostFirstPictureAsync(int id, int imageId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var p = await _db.ShopProducts.Include(x => x.Images).FirstOrDefaultAsync(x => x.Id == id && x.EventId == me.EventId, ct);
        var img = p?.Images.FirstOrDefault(x => x.Id == imageId);
        if (p is null || img is null) return NotFound();
        var n = 1;
        foreach (var other in p.Images.Where(x => x.Id != imageId).OrderBy(x => x.SortOrder)) other.SortOrder = n++;
        img.SortOrder = 0;
        p.ContentManagedInCeh = true;
        p.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        Message = "Main picture changed (in CEH only).";
        return RedirectToPage(new { id });
    }

    private async Task<bool> LoadAsync(int eventId, int id, CancellationToken ct)
    {
        Product = await _db.ShopProducts.AsNoTracking()
            .Include(p => p.PrimaryCategory).Include(p => p.Images)
            .FirstOrDefaultAsync(p => p.Id == id && p.EventId == eventId, ct);
        if (Product is null) return false;
        Categories = await _db.ShopCategories.AsNoTracking().Where(c => c.EventId == eventId)
            .OrderBy(c => c.Group).ThenBy(c => c.Name).ToListAsync(ct);
        Live = (await _stock.GetAsync(new[] { Product }, ct)).GetValueOrDefault(Product.Id);
        return true;
    }

    private IActionResult Fail(int id, string message)
    {
        Message = message;
        MessageIsError = true;
        return RedirectToPage(new { id });
    }

    private static string? Html(string? posted, string? stored)
    {
        static string N(string? s) => (s ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (N(posted) == N(stored)) return stored;
        return string.IsNullOrWhiteSpace(posted) ? null : ShopHtml.Sanitize(posted);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
