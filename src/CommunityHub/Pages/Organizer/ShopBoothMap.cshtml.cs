using System.Globalization;
using CommunityHub.Audit;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Integrations.Webshop;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1305 item 11 — set up the booth map: the expo layout picture and every booth drawn on it; see live status and
/// who holds each booth.
/// </summary>
/// <remarks>
/// Operator: <i>"Page 97 in the powerpoint shows the expo booth layout. It will be the same for eldk28. Page 96 shows how
/// i today mark availability."</i> The ELDK27 layout's booth positions are built in (measured on the deck picture, 1413 ×
/// 638 px) so the map works the moment the picture is uploaded; any booth can be re-drawn by dragging on the picture.
/// </remarks>
[Authorize]
public class ShopBoothMapModel : PageModel
{
    /// <summary>ELDK27 expo layout (deck slide 97): booth → pixel box on the 1413 × 638 picture.</summary>
    public static readonly IReadOnlyList<(string Booth, int X1, int Y1, int X2, int Y2)> Eldk27Layout = new[]
    {
        ("E-10", 776, 40, 880, 92), ("E-11", 890, 40, 993, 92), ("E-26", 668, 44, 770, 122), ("E-25", 583, 72, 688, 152),
        ("E-4", 549, 267, 674, 318), ("E-3", 549, 319, 674, 372), ("E-9", 860, 267, 963, 318), ("E-8", 860, 319, 963, 372),
        ("E-13", 972, 267, 1056, 318), ("E-12", 972, 319, 1056, 372), ("E-15", 1066, 267, 1150, 318), ("E-14", 1066, 319, 1150, 372),
        ("E-17", 1210, 267, 1294, 318), ("E-16", 1210, 319, 1294, 372), ("E-19", 1302, 267, 1386, 318), ("E-18", 1302, 319, 1386, 372),
        ("E-29", 35, 278, 97, 330), ("E-22", 106, 278, 189, 330), ("E-23", 198, 278, 282, 330), ("E-24", 344, 278, 407, 330),
        ("E-20A", 137, 443, 178, 495), ("E-20B", 179, 443, 220, 495), ("E-21", 230, 443, 313, 495), ("E-5", 322, 443, 448, 495),
        ("E-2", 458, 443, 582, 495), ("E-1", 632, 443, 757, 495), ("E-6", 766, 443, 870, 495), ("E-7", 879, 443, 983, 495),
        ("E-28", 993, 443, 1077, 495), ("E-27", 183, 566, 247, 618), ("E-30", 283, 566, 367, 618),
    };
    private const decimal LayoutWidth = 1413m, LayoutHeight = 638m;

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ShopBoothMapService _map;
    private readonly ShopSettings _settings;
    private readonly ISharePointFileStore _store;
    private readonly IDocLibraryPathResolver _paths;
    private readonly IMemoryCache _cache;
    private readonly ShopBoothDetector? _detector;

    /// <summary>AI booth detection is not offered until it places booths reliably (see <see cref="OnPostDetectAsync"/>).</summary>
    public const bool DetectionOffered = false;

    public ShopBoothMapModel(
        CommunityHubDbContext db, ICurrentParticipantAccessor participant, ShopBoothMapService map, ShopSettings settings,
        ISharePointFileStore store, IDocLibraryPathResolver paths, IMemoryCache cache, ShopBoothDetector? detector = null)
    {
        _detector = detector;
        _db = db;
        _participant = participant;
        _map = map;
        _settings = settings;
        _store = store;
        _paths = paths;
        _cache = cache;
    }

    public bool AccessDenied { get; private set; }
    [TempData] public string? Message { get; set; }
    public IReadOnlyList<ShopBoothView> Booths { get; private set; } = Array.Empty<ShopBoothView>();
    public Shared.ShopBoothMapViewModel? MapView { get; private set; }
    public IReadOnlyList<ShopBoothSpot> Spots { get; private set; } = Array.Empty<ShopBoothSpot>();
    public IReadOnlyList<string> UnplacedBooths { get; private set; } = Array.Empty<string>();
    public string ImageFileName => _settings.Shop().BoothMap.ImageFileName;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return;
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return; }
        Booths = await _map.MapAsync(me.EventId, withCompanyNames: true, ct);
        MapView = new Shared.ShopBoothMapViewModel(Booths, null, ShowList: true);
        Spots = await _db.ShopBoothSpots.AsNoTracking().Where(s => s.EventId == me.EventId).OrderBy(s => s.BoothId).ToListAsync(ct);
        var productBooths = await _db.ShopProducts.AsNoTracking()
            .Where(p => p.EventId == me.EventId && p.BoothId != null && p.Status == ShopProductStatus.Published)
            .Select(p => p.BoothId!).Distinct().ToListAsync(ct);
        UnplacedBooths = productBooths.Where(b => !Spots.Any(s => string.Equals(s.BoothId, b, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(b => b).ToList();
    }

    [Audit("Uploaded the expo layout picture", Category = AuditCategory.Admin, Action = "webshop.boothmap-image", TargetType = "ShopBoothMap")]
    public async Task<IActionResult> OnPostImageAsync(IFormFile? picture, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        if (picture is null || picture.Length == 0 || picture.Length > 15 * 1024 * 1024) { Message = "Choose a PNG or JPG up to 15 MB."; return RedirectToPage(); }
        var type = picture.ContentType is "image/png" or "image/jpeg" ? picture.ContentType : null;
        if (type is null) { Message = "Only PNG or JPG."; return RedirectToPage(); }
        if (string.IsNullOrWhiteSpace(ImageFileName) || !_store.CanStore
            || !_paths.TryResolve(DocLibraryPaths.SponsorWebshopProducts, out var folder))
        { Message = "SharePoint is not writable here, or shop.<edition>.json has no boothMap.imageFileName."; return RedirectToPage(); }

        using var ms = new MemoryStream();
        await picture.CopyToAsync(ms, ct);
        await _store.UploadToFolderAsync(folder, ImageFileName, ms.ToArray(), type, ct);
        _cache.Remove("shop-boothmap:" + ImageFileName);
        Message = "Layout picture saved.";
        return RedirectToPage();
    }

    [Audit("Placed the ELDK27 booth layout on the map", Category = AuditCategory.Admin, Action = "webshop.boothmap-seed", TargetType = "ShopBoothMap")]
    public async Task<IActionResult> OnPostSeedAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var existing = await _db.ShopBoothSpots.Where(s => s.EventId == me.EventId).Select(s => s.BoothId).ToListAsync(ct);
        var added = 0;
        foreach (var (booth, x1, y1, x2, y2) in Eldk27Layout)
        {
            if (existing.Contains(booth, StringComparer.OrdinalIgnoreCase)) continue;
            _db.ShopBoothSpots.Add(new ShopBoothSpot
            {
                EventId = me.EventId, BoothId = booth,
                XPercent = Math.Round(x1 / LayoutWidth * 100m, 3), YPercent = Math.Round(y1 / LayoutHeight * 100m, 3),
                WidthPercent = Math.Round((x2 - x1) / LayoutWidth * 100m, 3), HeightPercent = Math.Round((y2 - y1) / LayoutHeight * 100m, 3),
            });
            added++;
        }
        await _db.SaveChangesAsync(ct);
        Message = $"{added} booth(s) placed from the ELDK27 layout.";
        return RedirectToPage();
    }

    /// <summary>
    /// §1305 — find the booths on the layout picture with AI (operator 2026-09-26: "consider to use ai to detect the
    /// booths on the expo map and mark them. you have their number fx E-17 from the product name"). The booth numbers
    /// come from the published products; each found rectangle is snapped to the drawn outline and saved as a spot.
    /// </summary>
    [Audit("Detected the booths on the expo layout with AI", Category = AuditCategory.Admin, Action = "webshop.boothmap-detect", TargetType = "ShopBoothMap")]
    public async Task<IActionResult> OnPostDetectAsync(bool replace, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        // 🔒 Off until the two-step detector exists: the one-step model placed booths nowhere near the drawn ones
        // (measured 2026-09-26, mean overlap 0.01 on the ELDK27 picture), and saving that would draw the map wrong.
        if (!DetectionOffered) { Message = "AI booth detection is not available yet — use the built-in layout or draw booths by hand."; return RedirectToPage(); }
        if (_detector is null || !_detector.IsConfigured) { Message = "The AI service is not configured on this host."; return RedirectToPage(); }
        if (string.IsNullOrWhiteSpace(ImageFileName) || !_store.CanRead
            || !_paths.TryResolve(DocLibraryPaths.SponsorWebshopProducts, out var folder))
        { Message = "No layout picture to read (shop.<edition>.json boothMap.imageFileName, SharePoint)."; return RedirectToPage(); }

        var file = (await _store.ListAsync(folder, ct))
            .FirstOrDefault(f => string.Equals(f.Name, ImageFileName, StringComparison.OrdinalIgnoreCase));
        var picture = file is null ? null : await _store.DownloadAsync(file.ItemId, ct);
        if (picture is null) { Message = $"Upload the layout picture first ({ImageFileName} was not found)."; return RedirectToPage(); }

        var boothIds = await _db.ShopProducts.AsNoTracking()
            .Where(p => p.EventId == me.EventId && p.BoothId != null && p.Status == ShopProductStatus.Published)
            .Select(p => p.BoothId!).Distinct().ToListAsync(ct);
        var result = await _detector.DetectAsync(picture, boothIds, ct);
        if (result.Error is not null) { Message = result.Error; return RedirectToPage(); }

        var spots = await _db.ShopBoothSpots.Where(s => s.EventId == me.EventId).ToListAsync(ct);
        int added = 0, updated = 0, kept = 0;
        foreach (var b in result.Booths)
        {
            var spot = spots.FirstOrDefault(s => string.Equals(s.BoothId, b.BoothId, StringComparison.OrdinalIgnoreCase));
            if (spot is not null && !replace) { kept++; continue; }
            if (spot is null) { spot = new ShopBoothSpot { EventId = me.EventId, BoothId = b.BoothId }; _db.ShopBoothSpots.Add(spot); added++; }
            else updated++;
            spot.XPercent = Math.Round(b.X1 * 100m / result.Width, 3);
            spot.YPercent = Math.Round(b.Y1 * 100m / result.Height, 3);
            spot.WidthPercent = Math.Round((b.X2 - b.X1) * 100m / result.Width, 3);
            spot.HeightPercent = Math.Round((b.Y2 - b.Y1) * 100m / result.Height, 3);
        }
        await _db.SaveChangesAsync(ct);
        var loose = result.Booths.Where(b => !b.Snapped).Select(b => b.BoothId).ToList();
        Message = $"AI found {result.Booths.Count} of {boothIds.Count} booth(s): {added} added, {updated} replaced, {kept} kept as they were."
            + (loose.Count > 0 ? $" Check these — no drawn outline to snap to: {string.Join(", ", loose)}." : "")
            + (result.NotFound.Count > 0 ? $" Not found: {string.Join(", ", result.NotFound)} — draw them by hand." : "");
        return RedirectToPage();
    }

    [Audit("Placed a booth on the map", Category = AuditCategory.Admin, Action = "webshop.boothmap-spot", TargetType = "ShopBoothSpot")]
    public async Task<IActionResult> OnPostSpotAsync(string boothId, string x, string y, string w, string h, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        if (string.IsNullOrWhiteSpace(boothId) || !TryPct(x, out var px) || !TryPct(y, out var py) || !TryPct(w, out var pw) || !TryPct(h, out var ph))
        { Message = "Give a booth id and four percentages (0–100)."; return RedirectToPage(); }
        var id = boothId.Trim().ToUpperInvariant();
        var spot = await _db.ShopBoothSpots.FirstOrDefaultAsync(s => s.EventId == me.EventId && s.BoothId == id, ct);
        if (spot is null) { spot = new ShopBoothSpot { EventId = me.EventId, BoothId = id }; _db.ShopBoothSpots.Add(spot); }
        spot.XPercent = px; spot.YPercent = py; spot.WidthPercent = pw; spot.HeightPercent = ph;
        await _db.SaveChangesAsync(ct);
        Message = $"{id} saved.";
        return RedirectToPage();
    }

    [Audit("Removed a booth from the map", Category = AuditCategory.Admin, Action = "webshop.boothmap-spot-delete", TargetType = "ShopBoothSpot")]
    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var spot = await _db.ShopBoothSpots.FirstOrDefaultAsync(s => s.Id == id && s.EventId == me.EventId, ct);
        if (spot is not null) { _db.ShopBoothSpots.Remove(spot); await _db.SaveChangesAsync(ct); Message = $"{spot.BoothId} removed."; }
        return RedirectToPage();
    }

    private static bool TryPct(string s, out decimal v) =>
        decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out v) && v >= 0 && v <= 100;
}
