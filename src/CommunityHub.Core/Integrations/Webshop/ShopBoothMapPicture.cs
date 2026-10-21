using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>
/// §1312 — the expo layout with THIS order's booth(s) marked, as a picture for the offer and reservation mails (operator
/// 2026-09-27: "it could be nice to include that in the offer and reservation mail as managers ask for it … goal is to point
/// out which booth is offered/reserved on the map").
/// </summary>
/// <remarks>
/// The same layout picture and placed spots as the booth map (<see cref="ShopBoothMapService"/>, <c>/shop-boothmap-image</c>).
/// The booth is drawn with a thick outline and a light fill so it stands out on a busy slide; the picture is scaled down to
/// mail size (JPEG). Best-effort: no booth on the order, no placed spot, or no readable layout ⇒ null, and the mail goes
/// without it.
/// </remarks>
public sealed class ShopBoothMapPicture
{
    private const int MaxWidth = 1400;
    private static readonly Color Mark = Color.ParseHex("#d61f69");

    private readonly CommunityHubDbContext _db;
    private readonly ShopSettings _settings;
    private readonly IDocLibraryPathResolver _paths;
    private readonly ISharePointFileStore _store;
    private readonly ILogger<ShopBoothMapPicture>? _log;

    public ShopBoothMapPicture(CommunityHubDbContext db, ShopSettings settings, IDocLibraryPathResolver paths,
        ISharePointFileStore store, ILogger<ShopBoothMapPicture>? log = null)
    {
        _db = db;
        _settings = settings;
        _paths = paths;
        _store = store;
        _log = log;
    }

    /// <summary>The booth ids an order holds (a booth package, or a bundle that includes a booth).</summary>
    public async Task<IReadOnlyList<string>> BoothIdsAsync(int orderId, CancellationToken ct = default) =>
        (await _db.ShopOrderLines.AsNoTracking().Where(l => l.OrderId == orderId && l.Product.BoothId != null)
            .Select(l => l.Product.BoothId!).ToListAsync(ct))
        .Select(b => b.Trim()).Where(b => b.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The marked map as a mail attachment, or null (see remarks).</summary>
    public async Task<EmailAttachment?> ForOrderAsync(int eventId, int orderId, CancellationToken ct = default)
    {
        try
        {
            var booths = await BoothIdsAsync(orderId, ct);
            if (booths.Count == 0) return null;
            var spots = (await _db.ShopBoothSpots.AsNoTracking().Where(s => s.EventId == eventId).ToListAsync(ct))
                .Where(s => booths.Contains(s.BoothId, StringComparer.OrdinalIgnoreCase)).ToList();
            if (spots.Count == 0) return null;

            var layout = await LayoutAsync(ct);
            if (layout is null) return null;
            var jpg = Render(layout, spots.Select(s => (s.XPercent, s.YPercent, s.WidthPercent, s.HeightPercent)));
            var name = "Expo-map-booth-" + string.Join("-", spots.Select(s => s.BoothId).OrderBy(b => b)) + ".jpg";
            return new EmailAttachment(name, jpg, "image/jpeg");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "§1312 booth map picture for order {Order} could not be made; the mail goes without it.", orderId);
            return null;
        }
    }

    /// <summary>Draw the marks (percent of the picture, as the booth map stores them) and encode a mail-sized JPEG.</summary>
    public static byte[] Render(byte[] layout, IEnumerable<(decimal X, decimal Y, decimal W, decimal H)> spots)
    {
        using var img = Image.Load<Rgba32>(layout);
        if (img.Width > MaxWidth)
            img.Mutate(c => c.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(MaxWidth, 0) }));
        var thickness = Math.Max(4f, img.Width / 250f);
        foreach (var (x, y, w, h) in spots)
        {
            var r = new RectangleF((float)x / 100f * img.Width, (float)y / 100f * img.Height,
                (float)w / 100f * img.Width, (float)h / 100f * img.Height);
            var halo = RectangleF.Inflate(r, thickness * 1.5f, thickness * 1.5f);
            img.Mutate(c => c
                .Fill(Mark.WithAlpha(0.28f), r)
                .Draw(Color.White, thickness + 4f, halo)
                .Draw(Mark, thickness, halo));
        }
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 85 });
        return ms.ToArray();
    }

    private async Task<byte[]?> LayoutAsync(CancellationToken ct)
    {
        if (!_settings.IsConfigured) return null;
        var name = _settings.Shop().BoothMap.ImageFileName;
        if (string.IsNullOrWhiteSpace(name) || !_store.CanRead
            || !_paths.TryResolve(DocLibraryPaths.SponsorWebshopProducts, out var folder) || string.IsNullOrWhiteSpace(folder))
            return null;
        var file = (await _store.ListAsync(folder, ct))
            .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        return file is null ? null : await _store.DownloadAsync(file.ItemId, ct);
    }
}
