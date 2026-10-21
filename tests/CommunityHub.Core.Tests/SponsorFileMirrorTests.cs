using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1312 — named copies of the SoMe sponsor graphics and the exhibitor walls filed by booth level (operator 2026-10-01:
/// "we dont understand just the company id" · "a subfolder structure with platinum, diamond, gold, feature"). Copies only:
/// the originals keep their names, and nothing is deleted.
/// </summary>
public sealed class SponsorFileMirrorTests
{
    private const int EventId = 1;

    private sealed class FakeStore : ISharePointFileStore
    {
        public readonly Dictionary<string, Dictionary<string, SharePointFileRef>> Folders = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Copies { get; } = new();
        public List<string> Deletes { get; } = new();

        public void Put(string folder, string name, DateTimeOffset modified)
        {
            if (!Folders.TryGetValue(folder, out var f)) Folders[folder] = f = new(StringComparer.OrdinalIgnoreCase);
            f[name] = new SharePointFileRef($"{folder}|{name}", name, $"https://sp.test/{folder}/{name}", 1, modified);
        }

        public bool CanStore => true;
        public bool CanRead => true;

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string folder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(Folders.TryGetValue(folder, out var f) ? f.Values.ToList() : []);

        public Task<bool> CopyToFolderAsync(string itemId, string folder, string fileName, CancellationToken ct = default)
        {
            Copies.Add($"{folder}/{fileName}");
            Put(folder, fileName, DateTimeOffset.UtcNow);
            return Task.FromResult(true);
        }

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task<StoredFile> UploadToFolderAsync(string f, string n, byte[] c, string t, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<StoredFile> StoreAsync(string p, byte[] c, string t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string p, CancellationToken ct = default) { Deletes.Add(p); return Task.CompletedTask; }
        public Task DeleteFromFolderAsync(string f, string n, CancellationToken ct = default) { Deletes.Add($"{f}/{n}"); return Task.CompletedTask; }
    }

    private static readonly IDocLibraryPathResolver Paths = new DocLibraryPathResolver(new DocLibraryOptions
    {
        Enabled = true,
        SiteUrl = "https://sp.test/sites/x",
        RootFolderPath = "General/Test/EventHub",
    });

    private static string GraphicsFolder => Paths.Resolve(DocLibraryPaths.SponsorGraphicsSponsors);
    private static string WallFolder => Paths.Resolve(DocLibraryPaths.SponsorExhibitorWall);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<CommunityHubDbContext> SeedAsync()
    {
        var db = NewDb();
        db.Events.Add(new Event { Id = EventId, Code = "ELDK27", CommunityName = "C", DisplayName = "E", IsActive = true });
        // 109: a Platinum sponsor record that ALSO bought a Feature booth in the old webshop (2Pint's case).
        db.SponsorInfos.Add(new SponsorInfo { EventId = EventId, SponsorCompanyId = "109", CompanyName = "2Pint Software", Tier = BoothTier.Platinum });
        db.SponsorInfos.Add(new SponsorInfo { EventId = EventId, SponsorCompanyId = "41", CompanyName = "Arrow record", Tier = BoothTier.Gold });
        db.SponsorInfos.Add(new SponsorInfo { EventId = EventId, SponsorCompanyId = "77", CompanyName = "No Booth Co" });
        db.ShopCompanies.Add(new ShopCompany { EventId = EventId, SponsorCompanyId = "41", PublicName = "ARROW", LegalName = "ARROW ECS DENMARK A/S" });
        db.SponsorUploadAudits.Add(new SponsorUploadAudit { EventId = EventId, SponsorCompanyId = "109", Kind = "wall", FileName = "2pint-software-exhibitor-wall-1.pdf" });
        db.SponsorUploadAudits.Add(new SponsorUploadAudit { EventId = EventId, SponsorCompanyId = "77", Kind = "wall", FileName = "no-booth-co-exhibitor-wall-1.pdf" });
        await db.SaveChangesAsync();
        return db;
    }

    private static FakeWebshopOrderSource OldOrders() => new(new WooOrder(10871, "completed", "", "", "109", null, new[]
    {
        new WooLineItem(45, "Exhibitor ELDK27 - Booth E-24 - no backdrop wall",
            "Community & Appreciation Sponsor, Exhibitor Tier Package with Booth, Feature Exhibitor"),
    }));

    [Fact]
    public async Task SoMe_graphic_gets_a_copy_named_with_the_public_name_and_the_original_is_untouched()
    {
        await using var db = await SeedAsync();
        var store = new FakeStore();
        var t = DateTimeOffset.UtcNow.AddHours(-1);
        store.Put(GraphicsFolder, "sponsor-41.png", t);
        store.Put(GraphicsFolder, "sponsor-109.png", t);
        store.Put(GraphicsFolder, "sponsor-tier-gold.gif", t);   // not a sponsor graphic

        var r = await new SponsorFileMirrorService(db, store, Paths, OldOrders()).RunAsync(EventId);

        Assert.Equal(2, r.GraphicsCopied);
        // The shop's public name wins over the sponsor record's name.
        Assert.Contains($"{GraphicsFolder}/By name/arrow-sponsor-41.png", store.Copies);
        Assert.Contains($"{GraphicsFolder}/By name/2pint-software-sponsor-109.png", store.Copies);
        Assert.Contains("sponsor-41.png", store.Folders[GraphicsFolder].Keys);
        Assert.Empty(store.Deletes);
    }

    [Fact]
    public async Task Walls_are_copied_into_every_level_the_company_bought_and_Other_without_a_booth()
    {
        await using var db = await SeedAsync();
        var store = new FakeStore();
        store.Put(WallFolder, "2pint-software-exhibitor-wall-1.pdf", DateTimeOffset.UtcNow.AddHours(-1));
        store.Put(WallFolder, "no-booth-co-exhibitor-wall-1.pdf", DateTimeOffset.UtcNow.AddHours(-1));
        store.Put(WallFolder, "unknown-exhibitor-wall-1.pdf", DateTimeOffset.UtcNow.AddHours(-1));

        var r = await new SponsorFileMirrorService(db, store, Paths, OldOrders()).RunAsync(EventId);

        Assert.Equal(3, r.WallsCopied);
        Assert.Contains($"{WallFolder}/Platinum/2pint-software-exhibitor-wall-1.pdf", store.Copies);
        Assert.Contains($"{WallFolder}/Feature/2pint-software-exhibitor-wall-1.pdf", store.Copies);
        Assert.Contains($"{WallFolder}/Other/no-booth-co-exhibitor-wall-1.pdf", store.Copies);
        Assert.Contains(r.Problems, p => p.Contains("unknown-exhibitor-wall-1.pdf"));
        Assert.Empty(store.Deletes);
    }

    [Fact]
    public async Task A_second_run_copies_nothing_until_the_original_changes()
    {
        await using var db = await SeedAsync();
        var store = new FakeStore();
        store.Put(GraphicsFolder, "sponsor-41.png", DateTimeOffset.UtcNow.AddHours(-1));
        var svc = new SponsorFileMirrorService(db, store, Paths, OldOrders());

        await svc.RunAsync(EventId);
        var again = await svc.RunAsync(EventId);
        Assert.Equal(0, again.GraphicsCopied);
        Assert.Equal(1, again.UpToDate);

        store.Put(GraphicsFolder, "sponsor-41.png", DateTimeOffset.UtcNow.AddMinutes(5));   // re-rendered
        var changed = await svc.RunAsync(EventId);
        Assert.Equal(1, changed.GraphicsCopied);
    }

    [Theory]
    [InlineData("Community & Appreciation Sponsor, Exhibitor Tier Package with Booth, Feature Exhibitor", "Exhibitor ELDK27 - Booth E-24", BoothTier.Feature)]
    [InlineData("Branded Feature Package Sponsor, Hospitality & Comfort Sponsor", "Lounge sponsor ELDK27 with Exhibitor Booth E-27 in lounge (feature size)", BoothTier.Feature)]
    [InlineData("Exhibitor Tier Package with Booth, Platinum Exhibitor", "Platinum Exhibitor ELDK27 - Booth E-5", BoothTier.Platinum)]
    [InlineData("Exhibitor Tier Package with Booth, Gold Exhibitor (Shared)", "Gold Exhibitor ELDK27 - Booth E-20A (Shared)", BoothTier.Gold)]
    [InlineData("Branded Feature Package Sponsor", "Coffee Sponsor ELDK27", BoothTier.None)]
    public void Booth_level_comes_from_the_category_then_the_booth_name(string categories, string name, BoothTier expected) =>
        Assert.Equal(expected, SponsorFileMirrorService.BoothTierOf(categories, name));
}
