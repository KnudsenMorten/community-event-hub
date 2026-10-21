using System.Text.RegularExpressions;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Uploads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// 🔴 §1313 — COPIES of sponsor files that a person can find in SharePoint (operator 2026-10-01: <i>"we dont understand just
/// the company id"</i> · <i>"for sponsor walls i need a subfolder structure with platinum, diamond, gold, feature … this will
/// help sabine to structure the files"</i> · <i>"i dont mind they exist 2 times on sharepoint"</i>).
/// </summary>
/// <remarks>
/// <para><b>Copies, never renames.</b> The originals keep their names because services read them by those names: the SoMe
/// engine finds <c>sponsor-{companyId}.png</c>, the upload pages and the artefact check find
/// <c>{sponsor}-exhibitor-wall-{n}.{ext}</c>. A copy goes into a SUBFOLDER, which every folder listing skips, so no reader
/// ever sees one.</para>
/// <list type="bullet">
/// <item>SoMe sponsor graphic <c>sponsor-{id}.png</c> → <c>By name/{public-name}-sponsor-{id}.png</c> beside it.</item>
/// <item>Exhibitor wall → <c>Platinum/</c>, <c>Diamond/</c>, <c>Gold/</c> or <c>Feature/</c> under the wall folder, by the
/// booth the company bought — into EACH level it bought (2Pint has a Platinum and a Feature booth). A company with no
/// booth on record goes to <c>Other/</c>.</item>
/// </list>
/// <para>A copy is made when it is missing or older than the original, server-side (no bytes through this host). Nothing is
/// ever deleted: a copy left behind by a renamed company or a changed level stays until someone removes it.</para>
/// </remarks>
public sealed class SponsorFileMirrorService
{
    /// <summary>The subfolder of the SoMe sponsor-graphics folder that holds the named copies.</summary>
    public const string ByNameFolder = "By name";

    /// <summary>The wall subfolder for a company without a booth on record.</summary>
    public const string OtherFolder = "Other";

    private static readonly Regex SoMeSponsorFile = new(@"^sponsor-(.+)\.png$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly CommunityHubDbContext _db;
    private readonly ISharePointFileStore _store;
    private readonly IDocLibraryPathResolver _paths;
    private readonly IWebshopOrderSource _oldOrders;
    private readonly ILogger<SponsorFileMirrorService>? _log;

    public SponsorFileMirrorService(
        CommunityHubDbContext db, ISharePointFileStore store, IDocLibraryPathResolver paths,
        IWebshopOrderSource oldOrders, ILogger<SponsorFileMirrorService>? log = null)
    {
        _db = db;
        _store = store;
        _paths = paths;
        _oldOrders = oldOrders;
        _log = log;
    }

    public sealed record Result(int GraphicsCopied, int WallsCopied, int UpToDate, IReadOnlyList<string> Problems);

    public async Task<Result> RunAsync(int eventId, CancellationToken ct = default)
    {
        var problems = new List<string>();
        if (!_store.CanRead || !_store.CanStore)
            return new Result(0, 0, 0, new[] { "SharePoint is not configured — nothing copied." });

        var upToDate = 0;
        var graphics = await MirrorSoMeGraphicsAsync(eventId, problems, () => upToDate++, ct);
        var walls = await MirrorWallsAsync(eventId, problems, () => upToDate++, ct);
        return new Result(graphics, walls, upToDate, problems);
    }

    /// <summary>The named copy of a SoMe sponsor graphic, e.g. <c>acme-corp-sponsor-42.png</c>.</summary>
    public static string NamedGraphicFileName(string publicName, string companyId)
    {
        var slug = SponsorUploadNaming.Slug(publicName);
        return string.IsNullOrEmpty(slug) ? $"sponsor-{companyId}.png" : $"{slug}-sponsor-{companyId}.png";
    }

    /// <summary>The wall subfolder names for a set of booth tiers (Other when none is a booth level).</summary>
    public static IReadOnlyList<string> WallFolders(IEnumerable<BoothTier> tiers)
    {
        var order = new[] { BoothTier.Platinum, BoothTier.Diamond, BoothTier.Gold, BoothTier.Feature };
        var set = tiers.ToHashSet();
        var folders = order.Where(set.Contains).Select(t => t.ToString()).ToList();
        return folders.Count > 0 ? folders : new[] { OtherFolder };
    }

    private async Task<int> MirrorSoMeGraphicsAsync(int eventId, List<string> problems, Action upToDate, CancellationToken ct)
    {
        if (!_paths.TryResolve(DocLibraryPaths.SponsorGraphicsSponsors, out var folder)) return 0;
        var target = folder.TrimEnd('/') + "/" + ByNameFolder;

        var sources = await _store.ListAsync(folder, ct);
        var byId = sources
            .Select(f => (File: f, M: SoMeSponsorFile.Match(f.Name)))
            .Where(x => x.M.Success)
            .ToDictionary(x => x.M.Groups[1].Value, x => x.File, StringComparer.OrdinalIgnoreCase);
        if (byId.Count == 0) return 0;

        var names = await PublicNamesAsync(eventId, byId.Keys, ct);
        var existing = (await _store.ListAsync(target, ct)).ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        var copied = 0;
        foreach (var (companyId, file) in byId)
        {
            if (!names.TryGetValue(companyId, out var name) || string.IsNullOrWhiteSpace(name))
            {
                problems.Add($"SoMe graphic '{file.Name}': no company name for {companyId} — not copied.");
                continue;
            }
            if (await CopyIfNewerAsync(file, target, NamedGraphicFileName(name, companyId), existing, problems, ct)) copied++;
            else upToDate();
        }
        return copied;
    }

    /// <summary>
    /// The public name per company: the shop's public name (the shop owns it), else the name CEH holds for the company
    /// (<see cref="SponsorCompanyNameService.LocalNameAsync"/>). A company with neither is left out — never "Company 42".
    /// </summary>
    private async Task<Dictionary<string, string>> PublicNamesAsync(int eventId, IEnumerable<string> companyIds, CancellationToken ct)
    {
        var ids = companyIds.ToList();
        var shop = await _db.ShopCompanies.AsNoTracking()
            .Where(c => c.EventId == eventId && ids.Contains(c.SponsorCompanyId) && c.PublicName != null && c.PublicName != "")
            .Select(c => new { c.SponsorCompanyId, c.PublicName })
            .ToListAsync(ct);
        var names = shop.GroupBy(c => c.SponsorCompanyId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().PublicName!.Trim(), StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids.Where(i => !names.ContainsKey(i)))
            if (await SponsorCompanyNameService.LocalNameAsync(_db, id, ct) is { } local) names[id] = local;
        return names;
    }

    private async Task<int> MirrorWallsAsync(int eventId, List<string> problems, Action upToDate, CancellationToken ct)
    {
        if (!_paths.TryResolve(DocLibraryPaths.SponsorExhibitorWall, out var folder)) return 0;
        var sources = await _store.ListAsync(folder, ct);
        if (sources.Count == 0) return 0;

        // The upload record names the company; the file name only carries a slug of it.
        var owners = await _db.SponsorUploadAudits.AsNoTracking()
            .Where(a => a.EventId == eventId && a.Kind == "wall")
            .Select(a => new { a.FileName, a.SponsorCompanyId })
            .ToListAsync(ct);
        var ownerOf = owners.GroupBy(o => o.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().SponsorCompanyId, StringComparer.OrdinalIgnoreCase);

        var tiers = await BoothTiersAsync(eventId, ct);
        var listed = new Dictionary<string, Dictionary<string, SharePointFileRef>>(StringComparer.OrdinalIgnoreCase);

        var copied = 0;
        foreach (var file in sources)
        {
            if (!ownerOf.TryGetValue(file.Name, out var companyId))
            {
                problems.Add($"Exhibitor wall '{file.Name}': no upload record names its company — not copied.");
                continue;
            }
            var levels = WallFolders(tiers.TryGetValue(companyId, out var t) ? t : Enumerable.Empty<BoothTier>());
            foreach (var level in levels)
            {
                var target = folder.TrimEnd('/') + "/" + level;
                if (!listed.TryGetValue(target, out var existing))
                {
                    existing = (await _store.ListAsync(target, ct)).ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
                    listed[target] = existing;
                }
                if (await CopyIfNewerAsync(file, target, file.Name, existing, problems, ct)) copied++;
                else upToDate();
            }
        }
        return copied;
    }

    /// <summary>
    /// Every booth level each company bought: its sponsor record's tier, plus the tier of every booth on its old-webshop
    /// orders and its live CEH shop orders — so a company with two booths of different levels has both.
    /// </summary>
    internal async Task<Dictionary<string, HashSet<BoothTier>>> BoothTiersAsync(int eventId, CancellationToken ct)
    {
        var map = new Dictionary<string, HashSet<BoothTier>>(StringComparer.OrdinalIgnoreCase);
        void Add(string? companyId, BoothTier tier)
        {
            if (string.IsNullOrWhiteSpace(companyId) || tier == BoothTier.None) return;
            if (!map.TryGetValue(companyId, out var set)) map[companyId] = set = new HashSet<BoothTier>();
            set.Add(tier);
        }

        foreach (var s in await _db.SponsorInfos.IgnoreQueryFilters().AsNoTracking()
                     .Where(s => s.EventId == eventId).Select(s => new { s.SponsorCompanyId, s.Tier }).ToListAsync(ct))
            Add(s.SponsorCompanyId, s.Tier);

        foreach (var order in await _oldOrders.GetOrdersAsync("completed", ct))
            foreach (var line in order.LineItems)
                Add(order.CompanyId, BoothTierOf(line.CategoriesText, line.ProductName));

        var live = await _db.ShopOrderLines.AsNoTracking()
            .Where(l => l.Order.EventId == eventId && l.Product.BoothId != null
                        && (l.Order.Status == ShopOrderStatus.Signed || l.Order.Status == ShopOrderStatus.Reserved
                            || l.Order.Status == ShopOrderStatus.AwaitingSignature))
            .Select(l => new
            {
                l.Order.SponsorCompanyId,
                l.ProductName,
                Categories = l.Product.Categories.Select(c => c.Category.Name).ToList(),
            })
            .ToListAsync(ct);
        foreach (var l in live) Add(l.SponsorCompanyId, BoothTierOf(string.Join(", ", l.Categories), l.ProductName));

        return map;
    }

    /// <summary>
    /// The booth level of one product from its categories (the per-level category the shop files it under), falling back to
    /// its name for a booth sold inside a sponsorship ("… Booth E-27 … (feature size)"). None when it is not a booth.
    /// </summary>
    public static BoothTier BoothTierOf(string? categories, string? productName)
    {
        var c = (categories ?? string.Empty).ToLowerInvariant();
        var n = (productName ?? string.Empty).ToLowerInvariant();
        if (c.Contains("feature exhibitor")) return BoothTier.Feature;
        if (c.Contains("platinum exhibitor")) return BoothTier.Platinum;
        if (c.Contains("diamond exhibitor")) return BoothTier.Diamond;
        if (c.Contains("gold exhibitor")) return BoothTier.Gold;
        if (!Regex.IsMatch(n, @"\bbooth e-\d+", RegexOptions.CultureInvariant)) return BoothTier.None;
        if (n.Contains("feature")) return BoothTier.Feature;
        if (n.Contains("platinum")) return BoothTier.Platinum;
        if (n.Contains("diamond")) return BoothTier.Diamond;
        if (n.Contains("gold")) return BoothTier.Gold;
        return BoothTier.None;
    }

    private async Task<bool> CopyIfNewerAsync(
        SharePointFileRef source, string targetFolder, string targetName,
        IDictionary<string, SharePointFileRef> existing, List<string> problems, CancellationToken ct)
    {
        if (existing.TryGetValue(targetName, out var copy)
            && (source.LastModified is null || (copy.LastModified is { } at && at >= source.LastModified)))
            return false;
        try
        {
            if (!await _store.CopyToFolderAsync(source.ItemId, targetFolder, targetName, ct))
            {
                problems.Add($"'{source.Name}' was gone before it could be copied.");
                return false;
            }
            // A server-side copy is stamped with the copy time, so the next run sees it as current.
            existing[targetName] = source with { Name = targetName, LastModified = DateTimeOffset.UtcNow };
            _log?.LogInformation("Sponsor file copy: '{Source}' → '{Folder}/{Name}'.", source.Name, targetFolder, targetName);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problems.Add($"Could not copy '{source.Name}' to '{targetFolder}/{targetName}': {ex.Message}");
            return false;
        }
    }
}
