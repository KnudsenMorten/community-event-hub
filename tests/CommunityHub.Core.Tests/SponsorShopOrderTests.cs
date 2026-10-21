using System.Net;
using System.Text;
using System.Text.Json;
using CommunityHub.Core.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1305 — the CEH sponsor shop past the catalogue: pricing, the cart and its two-way stock, registration, signing,
/// the reminders, reservation rights, the contract PDF, and the safety helpers (HTML, captcha, config).
/// </summary>
/// <remarks>
/// Operator 2026-09-26 — the rules these pin, in his words: <i>"+50% if ordered less than 30 days before the event"</i> ·
/// <i>"TAX (Danish customers only)"</i> · <i>"ceh and woocommerce must have same availability … two-way"</i> ·
/// <i>"once a sponsor signs, then the normal flow to onboard them as sponsor starts"</i> · <i>"a daily reminder …
/// until it is emptied … an empty cart stops the flow"</i> · <i>"stored under the company so both signer and event
/// coordinator can see the things in cart"</i>. NO real customer names.
/// </remarks>
public sealed class SponsorShopOrderTests
{
    // =============================================================================================
    // Test doubles
    // =============================================================================================

    /// <summary>
    /// 🔴 §1312 — CEH's own stock (the ONE stock counter; the old webshop is closed). Indexed by the old webshop id the tests
    /// use as a product handle; setting it is "someone else bought it meanwhile" and is saved at once.
    /// </summary>
    private sealed class CehStock(CommunityHubDbContext db)
    {
        public (bool Manage, int? Qty) this[long wooId]
        {
            get
            {
                var p = db.ShopProducts.Single(x => x.WooProductId == wooId);
                return (p.ManageStock, p.StockQuantity);
            }
            set
            {
                var p = db.ShopProducts.Single(x => x.WooProductId == wooId);
                p.ManageStock = value.Manage;
                p.StockQuantity = value.Manage ? value.Qty : null;
                p.InStock = !value.Manage || (value.Qty ?? 0) > 0;
                db.SaveChanges();
            }
        }
    }

    private sealed class Magic : IEmailMagicLinkService
    {
        public Task<string> GetOrCreateTokenAsync(int participantId, CancellationToken ct = default) => Task.FromResult("tok" + participantId);
        public string BuildUrl(string baseUrl, string token, string? localPath = null) => $"{baseUrl}/go/{token}?r={localPath}";
        public Task<string> BuildUrlForParticipantAsync(int participantId, string baseUrl, string? localPath = null, CancellationToken ct = default) =>
            Task.FromResult($"{baseUrl}/go/tok{participantId}?r={localPath}");
        public Task<EmailMagicLinkResolution> ResolveAsync(string token, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Store : ISharePointFileStore
    {
        public List<string> Uploads { get; } = new();
        public bool CanStore => true;
        public bool CanRead => true;
        public Task<StoredFile> UploadToFolderAsync(string f, string n, byte[] c, string t, CancellationToken ct = default)
        { Uploads.Add($"{f}/{n}"); return Task.FromResult(new StoredFile($"{f}/{n}", "https://sp.test/x", "item-" + n)); }
        public Task<StoredFile> StoreAsync(string p, byte[] c, string t, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task DeleteAsync(string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteFromFolderAsync(string f, string n, CancellationToken ct = default) => Task.CompletedTask;
        /// <summary>Files the fake library holds, by name (any folder).</summary>
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string f, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(Files.Keys.Select(n => new SharePointFileRef("file:" + n, n, "https://sp.test/" + n)).ToList());
        public Task<byte[]?> DownloadAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(id.StartsWith("file:", StringComparison.Ordinal) && Files.TryGetValue(id[5..], out var b) ? b : null);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal const string MinimalConfig = """
    {
      "vatRatePercent": 25,
      "lateSurcharge": { "percent": 50, "daysBeforeEvent": 30, "groups": [ "Furniture", "PackageHandling" ] },
      "includedFurniture": { "group": "Furniture", "allowanceEurByTier": { "gold": 99, "platinum": 239 }, "label": "Included booth furniture" },
      "event": { "name": "TEST27", "website": "https://event.example.test", "datesText": [ "Day 1" ], "venue": [ "A venue" ] },
      "organizer": { "name": "Example Organizer", "address": [ "Street 1" ], "cvr": "DK11111111", "email": "org@example.test",
                     "contractOwner": { "name": "Owner Person", "email": "owner@example.test", "phone": "+45 0000" }, "notifyEmails": [ "ops@example.test" ] },
      "payment": { "bankName": [ "Bank" ], "accountName": "Acc", "accountNumber": "1", "iban": "DK00", "bic": "BIC", "currencyNote": "Converted at acceptance." },
      "reservation": { "opensOn": "2027-02-11", "windowDays": 31 },
      "boothMap": { "imageFileName": "map.png" },
      "contractTerms": [ { "heading": "ARTICLE 1", "paragraphs": [ "Terms for {{sponsorLegalName}}.", "{{paymentBlock}}" ] } ],
      "agreementText": "I agree."
    }
    """;

    private sealed class World
    {
        public required CommunityHubDbContext Db { get; init; }
        public required int EventId { get; init; }
        public required CehStock Stock { get; init; }
        public required Clock Clock { get; init; }
        public required ShopSettings Settings { get; init; }
        public required ShopOrderService Orders { get; init; }
        public required ShopMailer Mailer { get; init; }
        public required CapturingEmailSender Mail { get; init; }
        public required Store Store { get; init; }
        public required ShopSigningService Signing { get; init; }
    }

    private static async Task<World> NewWorldAsync(string? configJson = null)
    {
        var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var clock = new Clock(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        var path = Path.Combine(Path.GetTempPath(), $"shop-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, configJson ?? MinimalConfig);
        var settings = new ShopSettings(new ShopConfigLoader(), new ShopConfigOptions { ConfigPath = path });
        var stock = new ShopStockService(clock);
        var templates = new EmailTemplateProvider(Options.Create(new EmailTemplateOptions
        { TemplateDirectory = RepoPaths.EmailTemplates(), HubUrl = "https://hub.example.test" }));
        var mail = new CapturingEmailSender();
        var mailer = new ShopMailer(db, templates, mail, settings);
        var orders = new ShopOrderService(db, stock, settings, clock, mail: mailer);
        var store = new Store();
        var paths = new DocLibraryPathResolver(new DocLibraryOptions { Enabled = true, SiteUrl = "https://sp.test/sites/x", RootFolderPath = "General/Test/EventHub" });
        var signing = new ShopSigningService(db, orders, settings, new ShopContractPdfService(), mailer, store, paths, clock);
        return new World { Db = db, EventId = seed.EventId, Stock = new CehStock(db), Clock = clock, Settings = settings, Orders = orders, Mailer = mailer, Mail = mail, Store = store, Signing = signing };
    }

    /// <summary>A category + product with its stock in CEH.</summary>
    private static async Task<ShopProduct> ProductAsync(
        World w, string name, ShopGroup group, decimal price, long wooId, bool manage = true, int? qty = 1, string? booth = null, string? categoryName = null)
    {
        var cat = await w.Db.ShopCategories.FirstOrDefaultAsync(c => c.EventId == w.EventId && c.Group == group && c.Name == (categoryName ?? group.ToString()));
        if (cat is null)
        {
            cat = new ShopCategory { EventId = w.EventId, Name = categoryName ?? group.ToString(), Group = group, CreatedAt = w.Clock.Now };
            w.Db.ShopCategories.Add(cat);
        }
        var p = new ShopProduct
        {
            EventId = w.EventId, WooProductId = wooId, Name = name, PriceEur = price, Status = ShopProductStatus.Published,
            ManageStock = manage, StockQuantity = manage ? qty : null, InStock = !manage || (qty ?? 0) > 0,
            BoothId = booth, PrimaryCategory = cat, CreatedAt = w.Clock.Now,
        };
        p.Categories.Add(new ShopProductCategory { Product = p, Category = cat });
        w.Db.ShopProducts.Add(p);
        await w.Db.SaveChangesAsync();
        return p;
    }

    /// <summary>A company with a Danish identity and a signer + coordinator.</summary>
    private static async Task<(ShopCompany Company, Participant Signer, Participant Coordinator)> CompanyAsync(
        World w, string id = "ceh-test0001", string country = "DK", int zone = 1)
    {
        var company = new ShopCompany
        {
            EventId = w.EventId, SponsorCompanyId = id, LegalName = "Example Company ApS", CountryIso2 = country, VatZone = zone,
            Currency = "EUR", State = ShopCompanyState.Candidate, Source = ShopCompanySource.Registered, CreatedAt = w.Clock.Now,
        };
        w.Db.ShopCompanies.Add(company);
        w.Db.SponsorInfos.Add(new SponsorInfo { EventId = w.EventId, SponsorCompanyId = id, CompanyName = "Example Company", IsSponsor = false });
        var signer = new Participant { EventId = w.EventId, Email = $"signer@{id}.test", FullName = "Sam Signer", Role = ParticipantRole.Sponsor, SponsorCompanyId = id, IsActive = true, IsSigner = true, LifecycleState = ParticipantLifecycleState.Active };
        var coord = new Participant { EventId = w.EventId, Email = $"coord@{id}.test", FullName = "Casey Coordinator", Role = ParticipantRole.Sponsor, SponsorCompanyId = id, IsActive = true, IsEventCoordinator = true, LifecycleState = ParticipantLifecycleState.Active };
        w.Db.Participants.AddRange(signer, coord);
        await w.Db.SaveChangesAsync();
        w.Db.SponsorCompanyContacts.AddRange(
            new SponsorCompanyContact { EventId = w.EventId, ParticipantId = signer.Id, SponsorCompanyId = id, IsSigner = true, IsPrimary = true },
            new SponsorCompanyContact { EventId = w.EventId, ParticipantId = coord.Id, SponsorCompanyId = id, IsEventCoordinator = true, IsPrimary = true });
        await w.Db.SaveChangesAsync();
        return (company, signer, coord);
    }

    private static byte[] Png()
    {
        using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(40, 20);
        using var ms = new MemoryStream();
        SixLabors.ImageSharp.ImageExtensions.SaveAsPng(img, ms);
        return ms.ToArray();
    }

    // =============================================================================================
    // Pricing
    // =============================================================================================

    private static readonly ShopConfig Cfg = ShopConfigLoader.Parse(MinimalConfig);

    [Fact]
    public void The_late_surcharge_applies_only_inside_30_days_and_only_to_the_configured_groups()
    {
        var start = new DateTimeOffset(2027, 2, 10, 8, 0, 0, TimeSpan.Zero);
        Assert.Equal(0m, ShopPricing.SurchargePercentFor(ShopGroup.Furniture, start.AddDays(-31), start, Cfg.LateSurcharge));
        Assert.Equal(50m, ShopPricing.SurchargePercentFor(ShopGroup.Furniture, start.AddDays(-29), start, Cfg.LateSurcharge));
        Assert.Equal(50m, ShopPricing.SurchargePercentFor(ShopGroup.PackageHandling, start.AddDays(-1), start, Cfg.LateSurcharge));
        // A booth ordered late is NOT surcharged — the rule is per group.
        Assert.Equal(0m, ShopPricing.SurchargePercentFor(ShopGroup.Booths, start.AddDays(-1), start, Cfg.LateSurcharge));
        // No known event start ⇒ no surcharge, never a guess.
        Assert.Equal(0m, ShopPricing.SurchargePercentFor(ShopGroup.Furniture, start, null, Cfg.LateSurcharge));
    }

    [Theory]
    [InlineData("DK", 0, true)]
    [InlineData("DK", 1, true)]
    [InlineData("DK", 4, false)] // 'Indland uden moms' — a deliberate exemption survives
    [InlineData("SE", 0, false)]
    [InlineData("SE", 2, false)]
    [InlineData("GB", 3, false)]
    public void VAT_is_charged_to_Danish_customers_only(string country, int zone, bool vat) =>
        Assert.Equal(vat, ShopPricing.VatApplies(country, zone));

    [Fact]
    public void Included_furniture_is_capped_at_the_allowance_and_at_the_furniture_actually_bought()
    {
        var cat = new ShopCategory { Group = ShopGroup.Furniture };
        var chair = new ShopProduct { Id = 1, Name = "Chair", PriceEur = 35m, PrimaryCategory = cat };
        var table = new ShopProduct { Id = 2, Name = "Table", PriceEur = 29m, PrimaryCategory = cat };

        var exact = ShopPricing.Price(new[] { (1, chair, 2, (decimal?)null, (decimal?)null), (2, table, 1, null, null) },
            Cfg, DateTimeOffset.UtcNow, null, "gold", "DK", 1);
        Assert.Equal(99m, exact.SubtotalEur);
        Assert.Equal(99m, exact.IncludedFurnitureEur);   // the gold package's chairs + table cost nothing
        Assert.Equal(0m, exact.TotalEur);

        var more = ShopPricing.Price(new[] { (1, chair, 4, (decimal?)null, (decimal?)null) }, Cfg, DateTimeOffset.UtcNow, null, "gold", "DK", 1);
        Assert.Equal(99m, more.IncludedFurnitureEur);
        Assert.Equal(140m - 99m, more.NetEur);
        Assert.Equal(Math.Round((140m - 99m) * 0.25m, 2), more.VatEur);

        var noBooth = ShopPricing.Price(new[] { (1, chair, 2, (decimal?)null, (decimal?)null) }, Cfg, DateTimeOffset.UtcNow, null, null, "SE", 2);
        Assert.Equal(0m, noBooth.IncludedFurnitureEur);
        Assert.Equal(0m, noBooth.VatEur);
        Assert.Equal(70m, noBooth.TotalEur);
    }

    [Theory]
    [InlineData("Platinum Exhibitor", "platinum")]
    [InlineData("Gold Exhibitor (Shared)", "gold")]
    [InlineData("Feature Exhibitor", "feature")]
    [InlineData("Silver Sponsor", null)]
    public void The_booth_tier_is_read_from_a_category_name(string name, string? tier) =>
        Assert.Equal(tier, ShopPricing.TierKeyFromName(name));

    // =============================================================================================
    // Cart, reservation, cancellation — the two-way stock
    // =============================================================================================

    [Fact]
    public async Task A_cart_holds_nothing_and_is_the_companys_not_the_persons()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12", categoryName: "Gold Exhibitor");

        var added = await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, booth.Id, 1, coord.Id);

        Assert.True(added.Ok, added.Message);
        Assert.Equal(1, w.Stock[10602].Qty);                          // nothing taken at "add to cart"
        var cart = await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId);
        Assert.NotNull(cart);
        Assert.Equal(signer.Id, cart!.SignerParticipantId);          // defaults: the company's signer …
        Assert.Equal(coord.Id, cart.CoordinatorParticipantId);       // … and coordinator, whoever added it
    }

    [Fact]
    public async Task Adding_something_sold_out_is_refused()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-13", ShopGroup.Booths, 7500m, 10603, qty: 0, booth: "E-13");

        var r = await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, booth.Id, 1, coord.Id);

        Assert.False(r.Ok);
        Assert.Contains("sold out", r.Message);
    }

    [Fact]
    public async Task Reserving_takes_CEHs_stock_and_freezes_the_price()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12");
        var chair = await ProductAsync(w, "Chair", ShopGroup.Furniture, 35m, 10654, manage: false);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, booth.Id, 1, coord.Id);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, chair.Id, 2, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;

        var r = await w.Orders.ReserveAsync(cart);

        Assert.True(r.Ok, r.Message);
        Assert.Equal(ShopOrderStatus.Reserved, cart.Status);
        Assert.Equal("TEST27-" + cart.Id.ToString("0000"), cart.Number);
        Assert.Equal(0, w.Stock[10602].Qty);                          // the booth is taken …
        Assert.Equal((false, (int?)null), w.Stock[10654]);            // … the unlimited chair takes nothing
        Assert.True(cart.Lines.Single(l => l.ProductId == booth.Id).StockTaken);
        Assert.False(cart.Lines.Single(l => l.ProductId == chair.Id).StockTaken);
        Assert.Equal(25m, cart.VatRatePercent);
    }

    /// <summary>🔴 Half a reservation is worse than none: one line failing gives back every line already taken.</summary>
    [Fact]
    public async Task A_reservation_is_all_or_nothing()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        var a = await ProductAsync(w, "Gold - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12");
        var b = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, a.Id, 1, coord.Id);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, b.Id, 1, coord.Id);
        w.Stock[10627] = (true, 0);   // sold to someone else a second after it went into the cart
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;

        var r = await w.Orders.ReserveAsync(cart);

        Assert.False(r.Ok);
        Assert.Contains("Nothing was reserved", r.Message);
        Assert.Equal(ShopOrderStatus.Cart, cart.Status);
        Assert.Equal(1, w.Stock[10602].Qty);                     // the booth taken first was given back
        Assert.False(cart.Lines.Single(l => l.Product.WooProductId == 10602).StockTaken);
    }

    [Fact]
    public async Task Cancelling_gives_back_exactly_what_was_taken()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12");
        var chair = await ProductAsync(w, "Chair", ShopGroup.Furniture, 35m, 10654, manage: false);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, booth.Id, 1, coord.Id);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, chair.Id, 3, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        await w.Orders.ReserveAsync(cart);

        var r = await w.Orders.CancelAsync(cart, "coord@test", "changed our mind");

        Assert.True(r.Ok, r.Message);
        Assert.Equal(ShopOrderStatus.Cancelled, cart.Status);
        Assert.Equal(1, w.Stock[10602].Qty);
        Assert.Equal((false, (int?)null), w.Stock[10654]);           // nothing was taken for the chair, nothing given back
    }

    /// <summary>
    /// 🔴 §1312 — operator 2026-10-01: "we dont want to use woocommerce anymore. ceh ows the webshop functionality 100%". CEH is
    /// the ONE stock counter: a reservation takes CEH's saved count (and marks a product at 0 out of stock), a cancel gives it
    /// back — and no shop service can reach the closed webshop, because none of them is given a way to.
    /// </summary>
    [Fact]
    public async Task Reserve_takes_CEHs_saved_stock_and_cancel_gives_it_back_without_any_webshop()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        var tv = await ProductAsync(w, "TV rental", ShopGroup.BoothOptions, 450m, 10700, qty: 2);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, tv.Id, 2, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;

        Assert.True((await w.Orders.ReserveAsync(cart)).Ok);
        w.Db.ChangeTracker.Clear();                                    // what was SAVED, not what EF holds
        var saved = await w.Db.ShopProducts.AsNoTracking().SingleAsync(p => p.Id == tv.Id);
        Assert.Equal(0, saved.StockQuantity);
        Assert.False(saved.InStock);
        Assert.False((await new ShopStockService(w.Clock).GetAsync(new[] { saved }))[tv.Id].Available);

        var order = (await w.Orders.GetAnyAsync(w.EventId, cart.Id))!;
        Assert.True((await w.Orders.CancelAsync(order, "coord@test", "changed our mind")).Ok);
        w.Db.ChangeTracker.Clear();
        saved = await w.Db.ShopProducts.AsNoTracking().SingleAsync(p => p.Id == tv.Id);
        Assert.Equal(2, saved.StockQuantity);
        Assert.True(saved.InStock);

        // Structural: the stock, order, signing and reservation services take no HTTP client (the webshop client no longer exists).
        foreach (var type in new[] { typeof(ShopStockService), typeof(ShopOrderService), typeof(ShopSigningService), typeof(ShopReservationService) })
            Assert.All(type.GetConstructors().SelectMany(c => c.GetParameters()), prm =>
                Assert.True(prm.ParameterType != typeof(HttpClient),
                    $"{type.Name} takes a {prm.ParameterType.Name} — the closed webshop must not be reachable from the shop."));
    }

    [Fact]
    public async Task Only_the_companys_own_people_can_be_named_signer_or_coordinator()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var (_, stranger, _) = await CompanyAsync(w, "ceh-other001");
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;

        var bad = await w.Orders.UpdateDetailsAsync(cart, "PO-1", stranger.Id, coord.Id);
        var good = await w.Orders.UpdateDetailsAsync(cart, "PO-1", signer.Id, coord.Id);

        Assert.False(bad.Ok);
        Assert.True(good.Ok);
        Assert.Equal("PO-1", cart.PurchaseOrderRef);
    }

    // =============================================================================================
    // Signing
    // =============================================================================================

    [Fact]
    public async Task Signing_makes_the_company_a_sponsor_archives_the_PDF_and_mails_both_people()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;

        var r = await w.Signing.SignAsync(cart, signer.Id, ShopSignatureKind.Typed, Png(), "10.0.0.1");

        Assert.True(r.Ok, r.Message);
        Assert.Equal(ShopOrderStatus.Signed, cart.Status);
        Assert.Equal(ShopCompanyState.Sponsor, (await w.Db.ShopCompanies.SingleAsync(c => c.SponsorCompanyId == company.SponsorCompanyId)).State);
        Assert.True((await w.Db.SponsorInfos.IgnoreQueryFilters().SingleAsync(s => s.SponsorCompanyId == company.SponsorCompanyId)).IsSponsor);
        Assert.Contains(w.Store.Uploads, u => u.StartsWith("General/Test/EventHub/Sponsors/Webshop/Contracts/", StringComparison.Ordinal));
        Assert.Contains(w.Mail.Sent, m => m.To == signer.Email);
        Assert.Contains(w.Mail.Sent, m => m.To == coord.Email);
        Assert.Contains(w.Mail.Sent, m => m.To == "ops@example.test");
        Assert.Equal(0, w.Stock[10627].Qty);                     // signing a cart reserves it first
    }

    [Fact]
    public async Task Only_the_signer_named_on_the_order_can_sign()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;

        var r = await w.Signing.SignAsync(cart, coord.Id, ShopSignatureKind.Drawn, Png(), null);

        Assert.False(r.Ok);
        Assert.Equal(ShopOrderStatus.Cart, cart.Status);
        Assert.Equal(1, w.Stock[10627].Qty);                          // nothing taken
    }

    [Fact]
    public async Task A_signature_that_is_not_a_picture_is_refused_and_nothing_is_signed()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;

        var r = await w.Signing.SignAsync(cart, signer.Id, ShopSignatureKind.Uploaded, Encoding.UTF8.GetBytes("<script>alert(1)</script>"), null);

        Assert.False(r.Ok);
        Assert.Equal(ShopOrderStatus.Cart, cart.Status);
    }

    [Fact]
    public async Task The_contract_is_a_real_PDF_with_the_special_terms()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        await w.Orders.SetSpecialTermsAsync(cart, "Invoice in January.", new DateOnly(2027, 1, 1), 45);

        var data = await w.Signing.BuildAsync(cart);
        var pdf = await w.Signing.RenderAsync(cart);

        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Equal("Invoice in January.", data.SpecialTerms);
        Assert.Equal(12500m, data.Totals.TotalEur);                    // 10 000 + 25% Danish VAT
        Assert.Equal(new DateOnly(2027, 1, 1), cart.InvoiceNotBefore);
        Assert.Equal(45, cart.PaymentDays);
        // The sponsor sees the special billing terms before signing — they are part of the Agreed Terms.
        Assert.Contains("Invoice in January.", data.AgreedTerms);
        Assert.Contains("the invoice is dated 01/01/2027", data.AgreedTerms);
        Assert.Contains("45 days net", data.AgreedTerms);
    }

    /// <summary>Operator 2026-09-26: the Agreed Terms box is shown ONLY when something special was agreed.</summary>
    [Fact]
    public async Task Agreed_terms_are_left_out_when_nothing_special_was_agreed()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;

        Assert.Null((await w.Signing.BuildAsync(cart)).AgreedTerms);

        await w.Orders.SetSpecialTermsAsync(cart, null, null, 60);    // billing terms alone count
        Assert.Equal("Payment terms: 60 days net.", (await w.Signing.BuildAsync(cart)).AgreedTerms);
    }

    /// <summary>The contract carries each product's WHOLE description — paragraphs and bullets kept, nothing cut.</summary>
    [Fact]
    public async Task The_contract_carries_the_full_product_description()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12");
        var longTail = string.Concat(Enumerable.Repeat("More detail about the booth. ", 40));
        p.ShortDescriptionHtml = "<p>Short.</p>";
        p.DescriptionHtml = $"<p>A 3x2 m booth.</p><ul><li>Power</li><li>Wi-Fi&nbsp;access</li></ul><p>{longTail}THE END</p>";
        await w.Db.SaveChangesAsync();
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;

        var d = Assert.Single((await w.Signing.BuildAsync(cart)).Lines).Description!;

        Assert.StartsWith("A 3x2 m booth.\n• Power\n• Wi-Fi access\n", d);
        Assert.EndsWith("THE END", d);
        Assert.DoesNotContain("Short.", d);
    }

    /// <summary>
    /// 📄 The PDF layout gate (operator 2026-09-26: "the pages must separate nicely … build gates into the pdf
    /// build"). A deliberately awkward contract — many products, long descriptions, long agreed terms — must
    /// lay out with nothing past a margin, no heading stranded at a page foot, and no near-empty last page.
    /// </summary>
    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(6, 3, true)]
    [InlineData(14, 12, true)]
    [InlineData(30, 70, true)]
    public void The_contract_PDF_passes_the_layout_gate(int products, int descriptionSentences, bool agreedTerms)
    {
        var sentence = "Includes a branded counter, two stools, power and a daily cleaning of the booth area. ";
        var lines = Enumerable.Range(1, products).Select(i => (
            Name: $"Product {i} with a reasonably long name that might wrap on a narrow column",
            Description: descriptionSentences == 0 ? (string?)null
                : string.Join("\n", Enumerable.Range(0, descriptionSentences).Select(s => (s % 4 == 0 ? "• " : "") + sentence)),
            Quantity: i % 3 == 0 ? 2 : 1, LineEur: 1000m * i, SurchargePercent: i % 5 == 0 ? 50m : 0m)).ToList();
        var subtotal = lines.Sum(l => l.LineEur);
        var totals = new ShopTotals(Array.Empty<ShopPricedLine>(), subtotal, 99m, "Included furniture", subtotal - 99m, 25m,
            (subtotal - 99m) * 0.25m, (subtotal - 99m) * 1.25m, false, 40m);
        var terms = agreedTerms ? string.Join("\n\n", Enumerable.Repeat("Special condition agreed with the organizer, in full. " + sentence, 25)) : null;
        var cfg = ShopConfigLoader.Parse(MinimalConfig);
        cfg.ContractTerms = Enumerable.Range(1, 19).Select(n => new ShopContractArticle
        {
            Heading = $"ARTICLE {n} - HEADING",
            Paragraphs = Enumerable.Range(0, 1 + n % 4).Select(_ => string.Concat(Enumerable.Repeat(sentence, 1 + n % 6))).ToList(),
        }).ToList();
        var data = new ShopContractData(cfg, "TEST27-0001", new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero),
            "Example Company ApS", "Example", "DK12345678", new[] { "Street 1", "1000 City", "Denmark" },
            ("Signer Person", "signer@example.test", "+45 1111"), ("Coordinator Person", "coord@example.test", "+45 2222"),
            "invoice@example.test", "PO-1", "EUR", "https://example.test", "example", "@example",
            lines, totals, terms, null, null, null, null, null, agreedTerms ? 45 : null);

        var (pdf, pages, issues) = new ShopContractPdfService().RenderChecked(data);

        // Set CEH_CONTRACT_DUMP to a folder to look at the rendered contracts by eye.
        if (Environment.GetEnvironmentVariable("CEH_CONTRACT_DUMP") is { Length: > 0 } dump)
            File.WriteAllBytes(Path.Combine(dump, $"contract-{products}-{descriptionSentences}-{agreedTerms}.pdf"), pdf);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.True(pages >= 5);
        Assert.Empty(issues);
    }

    /// <summary>
    /// Operator 2026-09-26: "add a handwritten organizer signature on the pdf". The handwriting face is embedded (a
    /// Linux container has no fonts), and a signed contract with both signatures still passes the layout gate.
    /// </summary>
    [Fact]
    public async Task A_signed_contract_carries_a_handwritten_organizer_signature()
    {
        var face = new CommunityHub.Core.Evaluation.EmbeddedFontResolver()
            .ResolveTypeface(CommunityHub.Core.Evaluation.EmbeddedFontResolver.SignatureFamilyName, false, false)!;
        var bytes = new CommunityHub.Core.Evaluation.EmbeddedFontResolver().GetFont(face.FaceName)!;
        Assert.Equal(new byte[] { 0, 1, 0, 0 }, bytes[..4]);                // a real TrueType font, not Open Sans' fallback
        Assert.NotEqual(new CommunityHub.Core.Evaluation.EmbeddedFontResolver().GetFont("OpenSans#Regular")!.Length, bytes.Length);

        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        Assert.True((await w.Signing.SignAsync(cart, signer.Id, ShopSignatureKind.Typed, Png(), null)).Ok);

        var (pdf, _, issues) = new ShopContractPdfService().RenderChecked(await w.Signing.BuildAsync(cart));
        if (Environment.GetEnvironmentVariable("CEH_CONTRACT_DUMP") is { Length: > 0 } dump)
            File.WriteAllBytes(Path.Combine(dump, "contract-signed.pdf"), pdf);
        Assert.Empty(issues);
        Assert.Contains("Allura", Encoding.Latin1.GetString(pdf));            // the handwriting face is embedded in the file
    }

    /// <summary>
    /// Operator 2026-09-26: "here is my signature … add it into all contracts". The contract owner's own signature
    /// picture (named in the private shop config, kept in the document library — never in the repository) is on
    /// every contract, a draft included, in place of the handwriting fallback.
    /// </summary>
    [Fact]
    public async Task The_contract_owners_own_signature_picture_is_on_every_contract()
    {
        var config = MinimalConfig.Replace("\"phone\": \"+45 0000\" }",
            "\"phone\": \"+45 0000\", \"signatureImageFileName\": \"owner-signature-test.png\" }", StringComparison.Ordinal);
        Assert.NotEqual(MinimalConfig, config);
        var w = await NewWorldAsync(config);
        w.Store.Files["owner-signature-test.png"] = Png();
        var (company, _, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var draft = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;

        var data = await w.Signing.BuildAsync(draft);
        var (pdf, _, issues) = new ShopContractPdfService().RenderChecked(data);

        Assert.NotNull(data.OrganizerSignature);                              // a draft carries it already
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Empty(issues);
    }

    [Fact]
    public async Task Reserving_the_last_unit_says_reserved_not_sold_out_and_signing_it_says_sold()
    {
        // Operator 2026-09-28: "i made a reservation, but it send me an email like this" (Sold out … by order ELDK27-0007) —
        // "this is just a reservation, but it should block so noone can reserve it".
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Sponsor Long 60 min Session - timeslot: 4", ShopGroup.Sessions, 8000m, 10640, qty: 1);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;

        Assert.True((await w.Orders.ReserveAsync(cart)).Ok);

        var notice = Assert.Single(w.Mail.Messages, m => m.To == "ops@example.test");
        Assert.Contains("Reserved — no longer available", notice.Subject);
        Assert.DoesNotContain("Sold out", notice.Subject);
        Assert.Contains("not signed yet", notice.Html);
        Assert.Contains("Example Company ApS", notice.Html);                              // for whom
        Assert.False((await w.Db.ShopProducts.SingleAsync(x => x.Id == p.Id)).InStock);  // …and it IS blocked for everyone else
    }

    [Fact]
    public async Task A_contract_waiting_for_its_signer_is_reminded_daily_with_the_signing_mail_itself()
    {
        // Operator 2026-09-28: "if a webshop order is in awaiting signer, then the daily reminder must be the mail that
        // sends the signing mail to the signer".
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627, qty: 5);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        cart.SignerParticipantId = signer.Id;
        await w.Db.SaveChangesAsync();
        Assert.True((await w.Signing.SendForSignatureAsync(cart)).Ok);
        var day0 = DateOnly.FromDateTime(w.Clock.Now.UtcDateTime);
        w.Mail.AttachmentMessages.Clear();

        Assert.Equal(0, await w.Signing.SendSignRemindersAsync(w.EventId, day0));          // the signer got it today already

        var day1 = day0.AddDays(1);
        // 🔴 §1312 — the nightly job starts from NOTHING loaded (ELDK27-0003: "values are missing … also in contract"): the order
        // was read without its lines, so the mail said 0 € and the PDF had no items. Clearing the tracker reproduces the job.
        w.Db.ChangeTracker.Clear();
        Assert.Equal(1, await w.Signing.SendSignRemindersAsync(w.EventId, day1));
        var reminder = Assert.Single(w.Mail.AttachmentMessages);
        Assert.Equal(signer.Email, reminder.To);
        Assert.Contains("Coffee Sponsor", reminder.Html);                                     // the items…
        Assert.Contains(ShopPricing.Amount(12500m) + " €", reminder.Html);                       // …and the real total (10 000 + 25 % VAT)
        Assert.Contains("attached as a PDF", reminder.Html);                                 // the signing mail, not the cart mail
        Assert.Equal("%PDF", Encoding.ASCII.GetString(Assert.Single(reminder.Attachments).Content, 0, 4));

        Assert.Equal(0, await w.Signing.SendSignRemindersAsync(w.EventId, day1));          // once a day
        Assert.Equal(1, await w.Signing.SendSignRemindersAsync(w.EventId, day1.AddDays(1)));

        // …and the generic cart reminder no longer chases it.
        Assert.Empty(await w.Mailer.BuildCartRemindersAsync(w.EventId, day1, CancellationToken.None));
    }

    [Fact]
    public async Task The_signing_mail_carries_the_contract_PDF_and_the_organizer_notice_is_real_HTML()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627, qty: 5);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        cart.SignerParticipantId = signer.Id;
        await w.Db.SaveChangesAsync();

        Assert.True((await w.Signing.SendForSignatureAsync(cart)).Ok);
        var request = Assert.Single(w.Mail.AttachmentMessages, m => m.To == signer.Email);
        var pdf = Assert.Single(request.Attachments);
        Assert.Equal("application/pdf", pdf.ContentType);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf.Content, 0, 4));
        // Operator 2026-09-27: "confusing text, do they click the button … or below. and where is the pdf file" — one
        // way in (the button), and the PDF sentence because the PDF is attached.
        Assert.Contains("attached as a PDF", request.Html);
        Assert.DoesNotContain("Go straight to signing", request.Html);
        Assert.Contains("width:360px", request.Html);                  // Outlook's button fits "Read and sign the contract"

        Assert.True((await w.Signing.SignAsync(cart, signer.Id, ShopSignatureKind.Typed, Png(), null)).Ok);
        var notice = Assert.Single(w.Mail.AttachmentMessages, m => m.To == "ops@example.test");
        Assert.EndsWith("-signed.pdf", Assert.Single(notice.Attachments).FileName);
        Assert.DoesNotContain("&lt;p&gt;", notice.Html);               // the detail is markup, not escaped text
        Assert.Contains("<b>", notice.Html);
    }

    /// <summary>
    /// Operator 2026-09-26: an OFFER to a potential sponsor — products and prices in the mail, the draft contract
    /// attached, nothing reserved.
    /// </summary>
    [Fact]
    public async Task An_offer_mails_products_prices_and_the_draft_contract_without_reserving()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee <Sponsor>", ShopGroup.Branding, 10000m, 10627, qty: 1);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        var totals = await w.Orders.PriceAsync(cart, company);

        await w.Mailer.SendOfferAsync(cart, totals, "prospect@example.test", "Hi & welcome", await w.Signing.RenderAsync(cart), default);

        var m = Assert.Single(w.Mail.AttachmentMessages);
        Assert.Equal("prospect@example.test", m.To);
        Assert.Contains("Coffee &lt;Sponsor&gt;", m.Html);             // product names are encoded
        Assert.Contains("12500 €", m.Html);                              // total incl. 25% VAT — no thousands separator (§1312)
        Assert.Contains("Hi &amp; welcome", m.Html);
        Assert.Equal("application/pdf", Assert.Single(m.Attachments).ContentType);
        Assert.Equal(ShopOrderStatus.Cart, cart.Status);                  // nothing reserved
        Assert.Equal(1, w.Stock[10627].Qty);                              // nothing taken
        // Operator 2026-09-27: "rephrase this to show this is just an offer with no reservations involved … OFFER,
        // Reservation and CONFIRMATION, 3 stages".
        Assert.Contains("(not reserved)", m.Subject);
        Assert.Contains("nothing is reserved for you yet", m.Html);
        Assert.Contains("first come, first served", m.Html);
        Assert.Contains("<strong>Reservation</strong>", m.Html);
        Assert.Contains("<strong>Confirmation</strong>", m.Html);
        Assert.Contains("attached as a PDF", m.Html);
    }

    /// <summary>
    /// §1312 — operator 2026-09-27: "any deadlines … marked clearly so they dont forget to sign before the date and
    /// understand that products will be released. they can contact me … if they need extension. only mention if deadline
    /// is defined on the reservation".
    /// </summary>
    [Fact]
    public async Task A_reservation_deadline_is_marked_clearly_and_only_when_one_is_set()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627, qty: 5);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var order = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        order.SignerParticipantId = signer.Id;
        order.ReservationRequesterEmail = "requester@example.test";
        order.ReservationRequesterName = "Rita Requester";

        // No deadline: nothing about one, in either mail.
        await w.Mailer.SendSignRequestAsync(order, null, null, default);
        await w.Mailer.SendOrganizerReservationReminderAsync(order, company.LegalName, null, default);
        Assert.All(w.Mail.Messages, m =>
        {
            Assert.DoesNotContain("Deadline", m.Html);
            Assert.DoesNotContain("sign by", m.Subject);
            Assert.DoesNotContain("to be agreed", m.Subject);
        });

        // A deadline: in the subject, in a marked box, with the release and the extension contact.
        w.Mail.Messages.Clear();
        order.ReservationDeadline = new DateOnly(2026, 10, 15);
        await w.Mailer.SendSignRequestAsync(order, null, null, default);
        await w.Mailer.SendOrganizerReservationReminderAsync(order, company.LegalName, null, default);
        Assert.Equal(2, w.Mail.Messages.Count);
        Assert.All(w.Mail.Messages, m =>
        {
            Assert.Contains("sign by 15 October 2026", m.Subject);
            Assert.Contains("Deadline: please sign by 15 October 2026.", m.Html);
            Assert.Contains("released to other sponsors", m.Html);
            Assert.Contains("Ask for more time", m.Html);                   // more time is asked for in the portal, not by mail
        });
    }

    /// <summary>
    /// §1312 — operator 2026-09-27: "critical bug in all webshop emails + contract - it is using usa formatting, remove the
    /// comma and make it without (generic)". One format for mails, contract and pages: no thousands separator.
    /// </summary>
    [Theory]
    [InlineData("8150", 8150)]
    [InlineData("10188", 10188)]
    [InlineData("11.50", 11.5)]
    [InlineData("1234567.89", 1234567.89)]
    [InlineData("0", 0)]
    public void Webshop_amounts_have_no_thousands_separator(string expected, double value)
    {
        Assert.Equal(expected, ShopPricing.Amount((decimal)value));
        Assert.Equal("1250", ShopPricing.Count(1250));
    }

    /// <summary>§1312 — no webshop mail, contract or page formats money with a grouping pattern again.</summary>
    [Fact]
    public void No_webshop_code_formats_amounts_with_a_thousands_separator()
    {
        var root = RepoPaths.RepoRoot();
        var files = Directory.GetFiles(Path.Combine(root, "src", "CommunityHub.Core", "Integrations", "Webshop"), "*.cs")
            .Concat(Directory.GetFiles(Path.Combine(root, "src", "CommunityHub", "Pages", "Sponsor", "Shop"), "*.cs*"))
            .Concat(new[]
            {
                Path.Combine(root, "src", "CommunityHub", "Pages", "Sponsor", "SwagCatalog.cshtml"),
                Path.Combine(root, "src", "CommunityHub", "Pages", "Organizer", "SwagCatalog.cshtml"),
                Path.Combine(root, "src", "CommunityHub", "Pages", "Organizer", "WebshopCatalog.cshtml"),
            });
        var offenders = files.Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"#,0|""N0""|""N2""|:N0\}|:#,"))
            .Select(Path.GetFileName).ToList();
        Assert.True(offenders.Count == 0, "Grouped number format in: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// §1312 — the offer has two ways forward (operator: "reservation request (which i can approve) and sign/order") and
    /// links the sponsor presentation when the edition has one ("link to this powerpoint").
    /// </summary>
    [Fact]
    public async Task The_offer_has_order_and_request_buttons_and_the_presentation_only_when_configured()
    {
        var withDeck = MinimalConfig.Replace("\"website\": \"https://event.example.test\",",
            "\"website\": \"https://event.example.test\", \"presentationUrl\": \"https://deck.example.test\",");
        foreach (var (config, deck) in new[] { (withDeck, true), (MinimalConfig, false) })
        {
            var w = await NewWorldAsync(config);
            var (company, _, coord) = await CompanyAsync(w);
            var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627, qty: 1);
            await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
            var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;

            await w.Mailer.SendOfferAsync(cart, await w.Orders.PriceAsync(cart, company), "prospect@example.test", null, null, default);

            var m = Assert.Single(w.Mail.Messages, x => x.To == "prospect@example.test");
            Assert.Contains("/sponsor-registration?next=order", m.Html);
            Assert.Contains("Order and sign", m.Html);
            Assert.Contains("/sponsor-registration?next=request", m.Html);
            Assert.Contains("Request a reservation", m.Html);
            Assert.Equal(deck, m.Html.Contains("https://deck.example.test", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// §1312 — operator 2026-09-27: "why is name gone" · "verify the webshop mails have a hi with name". The offer greets by
    /// the name typed with it, else the person the address belongs to; "Hello," only for an unknown address. A name that is
    /// really an e-mail address is never used as a first name.
    /// </summary>
    [Fact]
    public async Task Shop_mails_greet_by_first_name()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627, qty: 1);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        var totals = await w.Orders.PriceAsync(cart, company);

        await w.Mailer.SendOfferAsync(cart, totals, "typed@example.test", null, null, default, "Jimmy Wind");
        await w.Mailer.SendOfferAsync(cart, totals, coord.Email, null, null, default);            // known in the hub
        await w.Mailer.SendOfferAsync(cart, totals, "nobody@example.test", null, null, default);  // unknown
        await w.Mailer.SendRegistrationCodeAsync(w.EventId, signer.Email, "123456", 15, default);

        Assert.Contains(">Hi Jimmy,<", Assert.Single(w.Mail.Messages, m => m.To == "typed@example.test").Html);
        Assert.Contains(">Hi Casey,<", Assert.Single(w.Mail.Messages, m => m.To == coord.Email).Html);
        Assert.Contains(">Hello,<", Assert.Single(w.Mail.Messages, m => m.To == "nobody@example.test").Html);
        Assert.Contains(">Hi Sam,<", Assert.Single(w.Mail.Messages, m => m.To == signer.Email).Html);

        // A contact whose stored "name" is their address gets "Hi there," — never "Hi rita@example.test,".
        coord.FullName = coord.Email;
        await w.Db.SaveChangesAsync();
        cart.ReservationRequesterEmail = coord.Email;
        cart.ReservationRequesterName = coord.Email;
        w.Mail.Messages.Clear();
        await w.Mailer.SendOrganizerReservationReminderAsync(cart, company.LegalName, null, default);
        Assert.Contains(">Hi there,<", Assert.Single(w.Mail.Messages).Html);
    }

    /// <summary>§1312 — the signed mail says the contract is attached only when it is; otherwise it points to the portal.</summary>
    [Fact]
    public async Task The_signed_mail_says_attached_only_when_the_signed_PDF_is()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627, qty: 1);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        cart.SignerParticipantId = signer.Id;
        cart.SignedByName = "Sam Signer";

        await w.Mailer.SendSignedAsync(cart, null, null, default);
        var without = Assert.Single(w.Mail.Messages, m => m.To == signer.Email);
        Assert.DoesNotContain("attached", without.Html);
        Assert.Contains("open and download the signed contract", without.Html);

        w.Mail.Messages.Clear();
        await w.Mailer.SendSignedAsync(cart, null, new byte[] { 1, 2, 3 }, default);
        Assert.Contains("The signed contract is attached", Assert.Single(w.Mail.AttachmentMessages, m => m.To == signer.Email).Html);
    }

    /// <summary>
    /// §1312 — operator 2026-09-27: "contracts signed in webshop must also be sent to info@expertslive.dk". The signed contract
    /// (with its PDF) goes to <c>organizer.contractEmails</c> too — and only the signed contract, not every organizer notice.
    /// </summary>
    [Fact]
    public async Task A_signed_contract_also_goes_to_the_contract_mailbox_and_nothing_else_does()
    {
        const string info = "info@example.test";
        var w = await NewWorldAsync(MinimalConfig.Replace(
            "\"notifyEmails\": [ \"ops@example.test\" ] }", $"\"notifyEmails\": [ \"ops@example.test\" ], \"contractEmails\": [ \"{info}\" ] }}"));
        var (company, signer, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627, qty: 1);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        cart.SignerParticipantId = signer.Id;

        await w.Mailer.SendSignedAsync(cart, null, new byte[] { 1, 2, 3 }, default);
        Assert.Single(w.Mail.AttachmentMessages, m => m.To == info);
        Assert.Single(w.Mail.AttachmentMessages, m => m.To == "ops@example.test");

        w.Mail.Messages.Clear();
        w.Mail.AttachmentMessages.Clear();
        await w.Mailer.NotifyOrganizersAsync(w.EventId, "Something else", "<p>x</p>", default);
        Assert.DoesNotContain(w.Mail.Messages, m => m.To == info);
    }

    /// <summary>Operator 2026-09-27: "there is NO pdf file attached" — without a PDF the mail must not claim one.</summary>
    [Fact]
    public async Task An_offer_without_a_contract_PDF_does_not_say_one_is_attached()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627, qty: 1);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;

        await w.Mailer.SendOfferAsync(cart, await w.Orders.PriceAsync(cart, company), "prospect@example.test", null, null, default);

        var m = Assert.Single(w.Mail.Messages, x => x.To == "prospect@example.test");
        Assert.DoesNotContain("attached as a PDF", m.Html);
        Assert.Contains("nothing is reserved for you yet", m.Html);
    }

    /// <summary>
    /// §1312 — operator 2026-09-27: "add the account … to any webshop emails like signer emails, offer emails, reminders
    /// reservations, cart reminder emails" · "just add to CC". Every sponsor-facing shop mail carries
    /// <c>organizer.copyEmails</c> as CC; the registration code (a sign-in secret) and the organizer notices do not.
    /// </summary>
    [Fact]
    public async Task Every_sponsor_facing_shop_mail_copies_the_organizer_in_CC()
    {
        const string copy = "copy@example.test";
        var w = await NewWorldAsync(MinimalConfig.Replace(
            "\"notifyEmails\": [ \"ops@example.test\" ] }", $"\"notifyEmails\": [ \"ops@example.test\" ], \"copyEmails\": [ \"{copy}\" ] }}"));
        var (company, signer, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627, qty: 5);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;

        // Cart reminder (reminder engine carries the CC).
        var due = await w.Mailer.BuildCartRemindersAsync(w.EventId, new DateOnly(2026, 10, 1), default);
        Assert.NotEmpty(due);
        Assert.All(due, d => Assert.Equal(new[] { copy }, d.Cc));

        // Offer (with the draft contract attached).
        await w.Mailer.SendOfferAsync(cart, await w.Orders.PriceAsync(cart, company), "prospect@example.test", null, await w.Signing.RenderAsync(cart), default);
        Assert.Equal(new[] { copy }, Assert.Single(w.Mail.AttachmentCc, m => m.To == "prospect@example.test").Cc);

        // Contract to the signer, then the signed mail to signer + coordinator.
        cart.SignerParticipantId = signer.Id;
        cart.CoordinatorParticipantId = coord.Id;
        await w.Db.SaveChangesAsync();
        Assert.True((await w.Signing.SendForSignatureAsync(cart)).Ok);
        Assert.Equal(new[] { copy }, Assert.Single(w.Mail.AttachmentCc, m => m.To == signer.Email).Cc);
        Assert.True((await w.Signing.SignAsync(cart, signer.Id, ShopSignatureKind.Typed, Png(), null)).Ok);
        Assert.Equal(new[] { copy }, Assert.Single(w.Mail.AttachmentCc, m => m.To == coord.Email).Cc);

        // The organizer notice is not copied (it already goes to the organizers).
        Assert.All(w.Mail.AttachmentCc.Where(m => m.To == "ops@example.test"), m => Assert.Empty(m.Cc));

        // Organizer reservation reminder to a requester.
        cart.ReservationRequesterEmail = "requester@example.test";
        cart.ReservationRequesterName = "Rita Requester";
        Assert.True(await w.Mailer.SendOrganizerReservationReminderAsync(cart, company.LegalName, null, default));
        Assert.Equal(new[] { copy }, Assert.Single(w.Mail.CcSent, m => m.To == "requester@example.test").Cc);

        // The registration code is a sign-in secret: never copied.
        await w.Mailer.SendRegistrationCodeAsync(w.EventId, "new@example.test", "123456", 15, default);
        Assert.Empty(Assert.Single(w.Mail.CcSent, m => m.To == "new@example.test").Cc);
    }

    /// <summary>§1312 — with no <c>copyEmails</c> configured nothing is CC'd (the default for every other edition).</summary>
    [Fact]
    public async Task Without_copyEmails_no_shop_mail_is_copied()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627, qty: 5);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        cart.SignerParticipantId = signer.Id;
        await w.Db.SaveChangesAsync();

        Assert.All(await w.Mailer.BuildCartRemindersAsync(w.EventId, new DateOnly(2026, 10, 1), default), d => Assert.Null(d.Cc));
        Assert.True((await w.Signing.SendForSignatureAsync(cart)).Ok);
        Assert.Empty(Assert.Single(w.Mail.AttachmentCc, m => m.To == signer.Email).Cc);
    }

    /// <summary>
    /// Operator 2026-09-26: "people are confused when i select 1 item of powerbank … for 11.50 euro". Swag enters a cart
    /// only through the configurator (all bags, one unit of stock), is ordered once, and "Swag Sponsor" is organizer-only.
    /// </summary>
    [Fact]
    public async Task Swag_goes_only_through_the_configurator_and_the_swag_sponsor_product_is_organizer_only()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        var bank = await ProductAsync(w, "Power bank with your logo", ShopGroup.Swag, 11.50m, 10853, qty: 1);
        var free = await ProductAsync(w, "Swag Sponsor", ShopGroup.OrganizerOnly, 0m, 10900, manage: false);

        var plain = await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, bank.Id, 1, coord.Id);
        Assert.False(plain.Ok);
        Assert.Contains("swag configurator", plain.Message);

        Assert.True((await w.Orders.AddSwagAsync(w.EventId, company.SponsorCompanyId, 10853, 1500, coord.Id)).Ok);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        var line = Assert.Single(cart.Lines);
        Assert.Equal(1500, line.PiecesPerUnit);
        Assert.Equal(17250m, (await w.Orders.PriceAsync(cart, company)).SubtotalEur);
        Assert.False((await w.Orders.SetQuantityAsync(w.EventId, company.SponsorCompanyId, line.Id, 2)).Ok);   // ordered once

        Assert.False((await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, free.Id, 1, coord.Id)).Ok);
        Assert.True((await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, free.Id, 1, null, default, ShopAddMode.Organizer)).Ok);
        Assert.Equal(ShopGroup.OrganizerOnly, ShopGroupMapper.FromName("Swag Sponsor"));
        Assert.Equal(ShopGroup.Swag, ShopGroupMapper.FromName("Swags"));
    }

    /// <summary>
    /// Operator 2026-09-26: "you can create invoice from dev also so we can verify it works". A CEH shop invoice asks its
    /// OWN ceiling (never the §1119 one that keeps webshop/coupon invoicing off DEV), and <c>Shop:InvoiceDryRun</c>
    /// overrides <c>Invoicing:DryRun</c> for the shop only.
    /// </summary>
    [Fact]
    public async Task A_shop_invoice_asks_its_own_ceiling_and_its_own_dry_run_switch()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        company.ErpCustomerNumber = "5001";
        await w.Db.SaveChangesAsync();
        var order = await SignedOrderAsync(w, company, signer, coord, await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627));
        var inv = new Invoices();
        var shopWet = new ShopDraftInvoiceService(w.Db, w.Orders, inv, new Contacts(), new Fx(null), new EconomicErpOptions(),
            new InvoicingOptions { DryRun = true }, w.Clock, shopOptions: new ShopConfigOptions { InvoiceDryRun = false });

        Assert.False(shopWet.DryRun);
        var (ok, message) = await shopWet.InvoiceNowAsync(order);

        Assert.True(ok, message);
        Assert.True(Assert.Single(inv.Created).CehShopOrder);
        Assert.Contains("Draft invoice", message);
        Assert.False((await shopWet.InvoiceNowAsync(order)).Ok);          // never twice
        Assert.True(Invoicer(w, new Invoices(), dryRun: true).DryRun);      // unset ⇒ Invoicing:DryRun
    }

    /// <summary>
    /// Operator 2026-09-26: "a backend price with 33% sponsor contribution … times it with a backend amount (1250) … price
    /// reflected must be that instead of unit price" · "the % for swags are individual per product, default 33% … then other
    /// part is covered by ELDK". A swag line is ONE amount = per piece × 1250 × the sponsor's share, quantity 1.
    /// </summary>
    [Fact]
    public async Task A_swag_item_costs_the_sponsors_share_of_every_bag_as_one_amount()
    {
        var config = MinimalConfig.Replace("\"includedFurniture\":",
            "\"swagPricing\": { \"pricingQuantity\": 1250, \"sponsorContributionPercent\": 33 },\n      \"includedFurniture\":", StringComparison.Ordinal);
        Assert.NotEqual(MinimalConfig, config);
        var w = await NewWorldAsync(config);
        var (company, _, coord) = await CompanyAsync(w);
        var bank = await ProductAsync(w, "Power bank with your logo", ShopGroup.Swag, 11.50m, 10853, qty: 1);
        var pad = await ProductAsync(w, "Charging pad with your logo", ShopGroup.Swag, 8.90m, 10854, qty: 3);
        pad.SponsorContributionPercent = 50m;                       // this product's own share
        await w.Db.SaveChangesAsync();

        Assert.True((await w.Orders.AddSwagAsync(w.EventId, company.SponsorCompanyId, 10853, 1500, coord.Id)).Ok);
        Assert.True((await w.Orders.AddSwagAsync(w.EventId, company.SponsorCompanyId, 10854, 1500, coord.Id)).Ok);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        var lines = (await w.Orders.PriceAsync(cart, company)).Lines.OrderBy(l => l.ProductId).ToList();

        Assert.Equal(4743.75m, lines.Single(l => l.ProductId == bank.Id).LineEur);   // 11.50 × 1250 × 33 %
        Assert.Equal(5562.50m, lines.Single(l => l.ProductId == pad.Id).LineEur);    // 8.90 × 1250 × 50 %
        Assert.All(lines, l => Assert.Equal(1, l.Quantity));
        Assert.All(lines, l => Assert.DoesNotContain("per piece", l.Name));

        Assert.True((await w.Orders.ReserveAsync(cart)).Ok);                         // the amount is frozen as it was shown
        Assert.Equal(4743.75m, cart.Lines.Single(l => l.ProductId == bank.Id).UnitPriceEur);
    }

    /// <summary>
    /// Operator 2026-09-26: "option to add discount and mark it with discount marking, so people can see normal price and new
    /// price". The discounted price is what is paid (cart, reservation, contract); the normal price travels with the line
    /// and is frozen at reservation, so a discount removed later does not change a reserved order.
    /// </summary>
    [Fact]
    public async Task A_discount_is_paid_and_the_normal_price_stays_visible_through_the_contract()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Party Sponsor", ShopGroup.Branding, 10000m, 10630, qty: 1);
        p.SalePriceEur = 7500m;
        await w.Db.SaveChangesAsync();
        Assert.True(p.IsDiscounted);
        Assert.Equal(25, p.DiscountPercent);

        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        var line = Assert.Single((await w.Orders.PriceAsync(cart, company)).Lines);
        Assert.Equal(7500m, line.LineEur);
        Assert.Equal(10000m, line.NormalUnitEur);

        Assert.True((await w.Orders.ReserveAsync(cart)).Ok);
        p.SalePriceEur = null;                                   // the discount ends after the reservation…
        await w.Db.SaveChangesAsync();
        var frozen = Assert.Single((await w.Orders.PriceAsync(cart, company)).Lines);
        Assert.Equal(7500m, frozen.LineEur);                     // …the reserved order keeps its price and its marking
        Assert.Equal(10000m, frozen.NormalUnitEur);

        var contract = Assert.Single((await w.Signing.BuildAsync(cart)).Lines);
        Assert.StartsWith("Discounted: normal price €10000, your price €7500 (−25%).", contract.Description);
    }

    /// <summary>
    /// §1312 — operator 2026-09-27: "give red hat a discount of 3000 euro for a 60 min session. it is only for them … normal
    /// price 8000 euro and then a new price of 5000 euro and show the discount". A price agreed on ONE company's line: paid
    /// by that company only, normal price beside it, re-frozen on a reserved order, refused once signed.
    /// </summary>
    [Fact]
    public async Task A_price_agreed_with_one_company_shows_the_normal_price_and_leaves_the_product_alone()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Sponsor Long 60 min Session", ShopGroup.Branding, 8000m, 10640, qty: 3);

        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        var lineId = cart.Lines.Single().Id;
        Assert.True((await w.Orders.SetAgreedPriceAsync(w.EventId, cart.Id, lineId, 5000m)).Ok);

        var line = Assert.Single((await w.Orders.PriceAsync(cart, company)).Lines);
        Assert.Equal(5000m, line.LineEur);
        Assert.Equal(8000m, line.NormalUnitEur);
        Assert.Equal(8000m, p.EffectivePriceEur);                                   // the product itself is unchanged

        Assert.True((await w.Orders.ReserveAsync(cart)).Ok);
        Assert.Equal(5000m, cart.Lines.Single().UnitPriceEur);                      // frozen at the agreed price
        Assert.True((await w.Orders.SetAgreedPriceAsync(w.EventId, cart.Id, lineId, 5500m)).Ok);
        Assert.Equal(5500m, Assert.Single((await w.Orders.PriceAsync(cart, company)).Lines).LineEur);   // re-frozen
        Assert.True((await w.Orders.SetAgreedPriceAsync(w.EventId, cart.Id, lineId, null)).Ok);
        var back = Assert.Single((await w.Orders.PriceAsync(cart, company)).Lines);
        Assert.Equal(8000m, back.LineEur);
        Assert.Null(back.NormalUnitEur);

        cart.Status = ShopOrderStatus.Signed;
        await w.Db.SaveChangesAsync();
        Assert.False((await w.Orders.SetAgreedPriceAsync(w.EventId, cart.Id, lineId, 1m)).Ok);
    }

    [Fact]
    public void A_sale_price_that_is_not_lower_is_no_discount()
    {
        var p = new ShopProduct { PriceEur = 100m, SalePriceEur = 100m };
        Assert.False(p.IsDiscounted);
        Assert.Equal(100m, p.EffectivePriceEur);
        p.SalePriceEur = 120m;
        Assert.False(p.IsDiscounted);
        p.SalePriceEur = 80m;
        Assert.Equal(80m, p.EffectivePriceEur);
    }

    /// <summary>Operator 2026-09-26: "info when one is sold so i can add a new one" — the sold-out mail names the corner.</summary>
    [Fact]
    public async Task A_promoted_product_that_sells_out_says_so_and_links_to_the_corner()
    {
        var w = await NewWorldAsync();
        var p = await ProductAsync(w, "Keynote Video Sponsor", ShopGroup.Branding, 5000m, 10631, qty: 1);
        p.PromotedSlot = 1;
        await w.Mailer.NotifySoldOutAsync(w.EventId, new[] { p }, "by order X", default);

        var m = Assert.Single(w.Mail.Messages);
        Assert.Contains("commercial corner (place 1)", m.Html);
        Assert.Contains("/Organizer/ShopPromotions", m.Html);
    }

    /// <summary>Operator 2026-09-26: "i must also get email when a product stock has run out".</summary>
    [Fact]
    public async Task Taking_the_last_unit_tells_the_organizers_it_is_sold_out()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        var last = await ProductAsync(w, "Attendee Bag Sponsor", ShopGroup.Branding, 3000m, 10624, qty: 1);
        var plenty = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627, qty: 5);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, last.Id, 1, coord.Id);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, plenty.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;

        Assert.True((await w.Orders.ReserveAsync(cart)).Ok);

        var sold = Assert.Single(w.Mail.Messages, m => m.To == "ops@example.test");
        // §1312 (2026-09-28) — a reservation took it, so it reads "Reserved — no longer available", not "Sold out".
        Assert.Contains("Reserved — no longer available", sold.Subject + sold.Html);
        Assert.Contains("Attendee Bag Sponsor", sold.Html);
        Assert.DoesNotContain("Coffee Sponsor", sold.Html);
    }

    /// <summary>
    /// §1312 — operator 2026-09-27: "i clicked digitally sign … i had NOT signed yet, and then i already received this [sold-out]
    /// email. order has not made until ticked off and signed … it must happen when you tick and sign". Sending the contract
    /// freezes prices and numbers the order but takes NO stock; signing takes it (and only then says sold out).
    /// </summary>
    [Fact]
    public async Task Nothing_is_ordered_until_the_contract_is_signed()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var last = await ProductAsync(w, "Attendee Bag Sponsor", ShopGroup.Branding, 3000m, 10624, qty: 1);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, last.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        cart.SignerParticipantId = signer.Id;
        await w.Db.SaveChangesAsync();

        Assert.True((await w.Signing.SendForSignatureAsync(cart)).Ok);
        Assert.Equal(ShopOrderStatus.AwaitingSignature, cart.Status);
        Assert.NotNull(cart.Number);
        Assert.False(cart.Lines.Single().StockTaken);
        Assert.Equal(1, last.StockQuantity);
        Assert.DoesNotContain(w.Mail.Messages, m => (m.Subject + m.Html).Contains("Sold out"));

        Assert.True((await w.Signing.SignAsync(cart, signer.Id, ShopSignatureKind.Typed, Png(), null)).Ok);
        Assert.True(cart.Lines.Single().StockTaken);
        Assert.Equal(0, last.StockQuantity);
        Assert.Contains(w.Mail.Messages, m => m.To == "ops@example.test" && (m.Subject + m.Html).Contains("Sold out"));
    }

    /// <summary>
    /// §1312 — operator 2026-09-29: the sponsor signed a RESERVED order (ELDK27-0008) and he got only "reserved — no longer
    /// available", never "sold out". Signing a reservation that holds the last unit must send the sold-out mail.
    /// </summary>
    [Fact]
    public async Task Signing_a_reservation_that_holds_the_last_unit_says_sold_out()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var last = await ProductAsync(w, "Attendee Bag Sponsor", ShopGroup.Branding, 3000m, 10624, qty: 1);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, last.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        cart.SignerParticipantId = signer.Id;
        await w.Db.SaveChangesAsync();
        Assert.True((await w.Orders.ReserveAsync(cart)).Ok);
        Assert.True(cart.Lines.Single().StockTaken);
        Assert.DoesNotContain(w.Mail.Messages, m => (m.Subject + m.Html).Contains("Sold out"));

        var r = await w.Signing.SignAsync(cart, signer.Id, ShopSignatureKind.Typed, Png(), null);
        Assert.True(r.Ok, r.Message);
        var sold = Assert.Single(w.Mail.Messages, m => m.To == "ops@example.test" && (m.Subject + m.Html).Contains("Sold out"));
        Assert.Contains("Attendee Bag Sponsor", sold.Html);
        Assert.Contains("(signed)", sold.Html);
    }

    /// <summary>§1312 — the last unit went elsewhere while the contract was out: signing is refused and nothing is ordered.</summary>
    [Fact]
    public async Task Signing_is_refused_when_an_item_sold_out_while_the_contract_was_out()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627, qty: 1);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        cart.SignerParticipantId = signer.Id;
        await w.Db.SaveChangesAsync();
        Assert.True((await w.Signing.SendForSignatureAsync(cart)).Ok);

        w.Stock[10627] = (true, 0);                                          // someone else signed first
        Assert.False((await w.Signing.SignAsync(cart, signer.Id, ShopSignatureKind.Typed, Png(), null)).Ok);
        Assert.NotEqual(ShopOrderStatus.Signed, cart.Status);
    }

    /// <summary>
    /// §1312 — operator 2026-09-27: "organizer manually complete the order if the sponsor make a manual pdf signing … i need a
    /// Manuel Confirm Order button". Confirming does what a signature does: stock taken, Signed (kind Offline), signed mails.
    /// </summary>
    [Fact]
    public async Task An_organizer_confirms_an_order_the_sponsor_signed_offline()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627, qty: 2);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        cart.SignerParticipantId = signer.Id;
        await w.Db.SaveChangesAsync();

        var r = await w.Signing.ConfirmOfflineAsync(cart, "Olga Organizer", Encoding.ASCII.GetBytes("%PDF-1.4 signed"), "signed.pdf");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(ShopOrderStatus.Signed, cart.Status);
        Assert.Equal(ShopSignatureKind.Offline, cart.SignatureKind);
        Assert.Equal(signer.FullName, cart.SignedByName);
        Assert.True(cart.Lines.Single().StockTaken);
        Assert.Equal(1, p.StockQuantity);
        Assert.Contains(w.Mail.AttachmentMessages, m => m.To == signer.Email);
        Assert.False((await w.Signing.ConfirmOfflineAsync(cart, "Olga Organizer", null, null)).Ok);   // once only
    }

    /// <summary>§1305 item 7b — a signed CEH order is fed to the existing onboarding pipeline, shaped like a webshop order.</summary>
    [Fact]
    public async Task A_signed_order_is_presented_to_the_onboarding_pipeline_as_a_completed_order()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12", categoryName: "Gold Exhibitor");
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, booth.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        await w.Signing.SignAsync(cart, signer.Id, ShopSignatureKind.Typed, Png(), null);

        var orders = await new ShopOrderSource(w.Db).SignedOrdersAsync(w.EventId);

        var o = Assert.Single(orders);
        Assert.True(o.OrderId < 0);                                    // never collides with a webshop id
        Assert.Equal(company.SponsorCompanyId, o.CompanyId);
        Assert.Equal("completed", o.Status);
        var line = Assert.Single(o.LineItems);
        Assert.Equal(10602, line.ProductId);
        Assert.Contains("Gold Exhibitor", line.CategoriesText);         // the classifier matches on category names
    }

    // =============================================================================================
    // Reminders
    // =============================================================================================

    [Fact]
    public async Task The_daily_cart_reminder_goes_to_signer_and_coordinator_and_stops_when_the_cart_is_empty()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var p = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var today = new DateOnly(2026, 10, 1);

        var due = await w.Mailer.BuildCartRemindersAsync(w.EventId, today, default);

        Assert.Equal(new[] { coord.Email, signer.Email }.OrderBy(x => x), due.Select(d => d.RecipientEmail).OrderBy(x => x));
        Assert.All(due, d => Assert.EndsWith(":2026-10-01", d.OccasionKey));   // once per DAY per person
        Assert.All(due, d => Assert.Contains("/Sponsor/Shop/Cart", d.HtmlBody));

        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        await w.Orders.SetQuantityAsync(w.EventId, company.SponsorCompanyId, cart.Lines[0].Id, 0);
        Assert.Empty(await w.Mailer.BuildCartRemindersAsync(w.EventId, today, default));
    }

    [Fact]
    public async Task The_reservation_reminder_runs_only_while_the_window_is_open_and_something_is_unreserved()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        w.Db.ShopEntitlements.Add(new ShopEntitlement
        {
            EventId = w.EventId, SponsorCompanyId = company.SponsorCompanyId, CompanyName = "Example Company",
            SourceEdition = "TEST26", ProductName = "Gold Exhibitor - Booth E-12", BoothId = "E-12", ImportedAt = w.Clock.Now,
        });
        await w.Db.SaveChangesAsync();

        Assert.Empty(await w.Mailer.BuildReservationRemindersAsync(w.EventId, new DateOnly(2027, 2, 10), default)); // before
        var open = await w.Mailer.BuildReservationRemindersAsync(w.EventId, new DateOnly(2027, 2, 15), default);
        Assert.Equal(2, open.Count);
        Assert.All(open, m => Assert.Contains("w7", m.OccasionKey));                                                  // once per ISO week
        Assert.Empty(await w.Mailer.BuildReservationRemindersAsync(w.EventId, new DateOnly(2027, 3, 14), default)); // after
    }

    // =============================================================================================
    // Registration
    // =============================================================================================

    private static ShopRegistrationService Registration(World w, IEconomicErpClient? erp = null) =>
        new(w.Db, new PinService(), w.Mailer, new CvrValidator(), new SponsorCompanyMembershipService(w.Db, w.Clock), w.Clock, erp);

    /// <summary>A fake e-conomic: CVR lookups miss, creates hand out customer numbers from 900001.</summary>
    private sealed class FakeErp : IEconomicErpClient
    {
        public List<ErpCustomer> Created { get; } = new();
        public bool CanWrite => true;
        public Task<string?> FindCustomerNumberAsync(ErpCustomer customer, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<string> CreateCustomerAsync(ErpCustomer customer, CancellationToken ct)
        { Created.Add(customer); return Task.FromResult((900000 + Created.Count).ToString()); }
        public Task UpdateCustomerAsync(string n, ErpCustomer c, CancellationToken ct) => Task.CompletedTask;
        public Task CreateOrUpdateContactAsync(string n, ErpContact c, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateOrderAsync(string n, ErpOrder o, CancellationToken ct) => Task.FromResult("1");
    }

    /// <summary>
    /// 🔴 §1312 — operator 2026-09-27: "the registraion of balanceme newer hit erp, it doesnt exist. critical bug, as erp is
    /// master for every registration". The web host had no e-conomic client, and registration skipped e-conomic SILENTLY.
    /// Now a missing client is recorded on the company (and shown to the organizers), and "Create in e-conomic now" creates
    /// the customer from what CEH holds and stamps its number.
    /// </summary>
    [Fact]
    public async Task A_registration_that_could_not_reach_economic_says_so_and_can_be_created_afterwards()
    {
        var w = await NewWorldAsync();
        var noErp = Registration(w);                       // as the web host was: no e-conomic client
        Assert.True((await noErp.StartAsync(w.EventId, "reg@example.test", "1.2.3.4")).Ok);
        var (ok, token) = await noErp.VerifyAsync(w.EventId, "reg@example.test", CodeFrom(w.Mail));
        Assert.True(ok.Ok, ok.Message);
        var outcome = await noErp.CompleteAsync(w.EventId, token!, Input("reg@example.test"));
        Assert.True(outcome.Ok, outcome.Message);

        var company = await w.Db.ShopCompanies.AsNoTracking().SingleAsync(c => c.SponsorCompanyId == outcome.SponsorCompanyId);
        Assert.Null(company.ErpCustomerNumber);
        Assert.Contains("NOT created", company.ErpError);                                   // never silent
        Assert.Contains(w.Mail.Messages, m => m.To == "ops@example.test" && m.Html.Contains("NOT created"));

        var erp = new FakeErp();
        var r = await Registration(w, erp).EnsureInErpAsync(w.EventId, outcome.SponsorCompanyId!);

        Assert.True(r.Ok, r.Message);
        company = await w.Db.ShopCompanies.AsNoTracking().SingleAsync(c => c.SponsorCompanyId == outcome.SponsorCompanyId);
        Assert.Equal("900001", company.ErpCustomerNumber);
        Assert.Equal("Example Registrant ApS", Assert.Single(erp.Created).Name);
        Assert.True((await Registration(w, erp).EnsureInErpAsync(w.EventId, outcome.SponsorCompanyId!)).Ok);
        Assert.Single(erp.Created);                                                         // never twice
    }

    /// <summary>
    /// 🔴 §1312 — operator 2026-09-29: "it is using the billing company name in the creation on master packager, that is wrong.
    /// it must be the public name" … "but it must NOT impact the invoice, sponsor registration, contract". A registration's
    /// PUBLIC name (sponsor record → event site, app, Zoho) drops the legal form; the LEGAL name — and the e-conomic customer
    /// the invoice is built on — keeps it.
    /// </summary>
    [Fact]
    public async Task A_registration_derives_the_public_name_and_keeps_the_legal_name_for_billing()
    {
        var w = await NewWorldAsync();
        var erp = new FakeErp();
        var reg = Registration(w, erp);
        Assert.True((await reg.StartAsync(w.EventId, "reg@example.test", "1.2.3.4")).Ok);
        var (ok, token) = await reg.VerifyAsync(w.EventId, "reg@example.test", CodeFrom(w.Mail));
        Assert.True(ok.Ok, ok.Message);
        var outcome = await reg.CompleteAsync(w.EventId, token!, Input("reg@example.test"));
        Assert.True(outcome.Ok, outcome.Message);

        var company = await w.Db.ShopCompanies.AsNoTracking().SingleAsync(c => c.SponsorCompanyId == outcome.SponsorCompanyId);
        Assert.Equal("Example Registrant ApS", company.LegalName);                  // contract + invoice
        Assert.Equal("Example Registrant", company.PublicName);                     // announcements
        var info = await w.Db.SponsorInfos.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.SponsorCompanyId == outcome.SponsorCompanyId);
        Assert.Equal("Example Registrant", info.CompanyName);                       // event site, app, Zoho, uploads
        Assert.Equal("Example Registrant ApS", Assert.Single(erp.Created).Name);   // the e-conomic customer the invoice uses
    }

    private static ShopRegistrationInput Input(string email, string cvr = "10150817") => new(
        "Example Registrant ApS", cvr, "finance@example.test", "+45 1", "https://example.test",
        "www.linkedin.com/company/example-registrant", null,   // §1312: website + LinkedIn are required
        "Street 2", "7400", "Herning", "DK", "DKK",
        new ShopRegistrationPerson("Reg", "Istrant", email, null), null);

    private static string CodeFrom(CapturingEmailSender mail) =>
        System.Text.RegularExpressions.Regex.Match(mail.Messages.Last().Html, @"\b\d{6}\b").Value;

    [Fact]
    public async Task Registration_creates_a_hidden_candidate_with_its_signer_and_signs_them_in()
    {
        var w = await NewWorldAsync();
        var reg = Registration(w);

        Assert.True((await reg.StartAsync(w.EventId, "reg@example.test", "1.2.3.4")).Ok);
        var (verified, token) = await reg.VerifyAsync(w.EventId, "reg@example.test", CodeFrom(w.Mail));
        Assert.True(verified.Ok, verified.Message);

        var outcome = await reg.CompleteAsync(w.EventId, token!, Input("reg@example.test"));

        Assert.True(outcome.Ok, outcome.Message);
        Assert.StartsWith("ceh-", outcome.SponsorCompanyId);
        var company = await w.Db.ShopCompanies.SingleAsync(c => c.SponsorCompanyId == outcome.SponsorCompanyId);
        Assert.Equal(ShopCompanyState.Candidate, company.State);
        Assert.Equal(1, company.VatZone);                                         // derived from the COUNTRY
        Assert.Equal("10150817", company.Cvr);
        Assert.False((await w.Db.SponsorInfos.IgnoreQueryFilters().SingleAsync(s => s.SponsorCompanyId == outcome.SponsorCompanyId)).IsSponsor);
        Assert.Empty(await w.Db.SponsorInfos.Where(s => s.SponsorCompanyId == outcome.SponsorCompanyId).ToListAsync()); // hidden from every sponsor list
        var person = await w.Db.Participants.SingleAsync(p => p.Id == outcome.SignInParticipantId);
        Assert.Equal(ParticipantRole.Sponsor, person.Role);
        var link = await w.Db.SponsorCompanyContacts.SingleAsync(l => l.ParticipantId == person.Id);
        Assert.True(link.IsSigner);
        Assert.True(link.IsEventCoordinator);                                     // no separate coordinator ⇒ both roles
        Assert.Contains(w.Mail.Sent, m => m.To == "ops@example.test");
    }

    [Fact]
    public async Task A_company_registers_once_its_CVR_is_the_key_not_its_name()
    {
        var w = await NewWorldAsync();
        var reg = Registration(w);
        async Task<ShopRegistrationOutcome> Register(string email)
        {
            await reg.StartAsync(w.EventId, email, null);
            var (_, token) = await reg.VerifyAsync(w.EventId, email, CodeFrom(w.Mail));
            return await reg.CompleteAsync(w.EventId, token!, Input(email, "10150817"));
        }

        Assert.True((await Register("first@example.test")).Ok);
        var second = await Register("second@example.test");

        Assert.False(second.Ok);
        Assert.Contains("already registered", second.Message);
        Assert.Single(await w.Db.ShopCompanies.ToListAsync());
    }

    [Fact]
    public async Task The_code_locks_after_five_wrong_attempts()
    {
        var w = await NewWorldAsync();
        var reg = Registration(w);
        await reg.StartAsync(w.EventId, "reg@example.test", null);
        var real = CodeFrom(w.Mail);
        for (var i = 0; i < 5; i++) Assert.False((await reg.VerifyAsync(w.EventId, "reg@example.test", "000000" == real ? "111111" : "000000")).Result.Ok);

        var (r, token) = await reg.VerifyAsync(w.EventId, "reg@example.test", real);

        Assert.False(r.Ok);
        Assert.Null(token);
    }

    [Fact]
    public async Task The_verified_address_must_be_one_of_the_people_being_registered()
    {
        var w = await NewWorldAsync();
        var reg = Registration(w);

        var problems = await reg.ValidateAsync(Input("someone.else@example.test"), "reg@example.test");

        Assert.Contains(problems, p => p.Contains("verified address"));
    }

    [Fact]
    public async Task A_Danish_CVR_with_a_wrong_checksum_is_refused()
    {
        var w = await NewWorldAsync();
        var problems = await Registration(w).ValidateAsync(Input("reg@example.test", "12345678"), "reg@example.test");
        Assert.Contains(problems, p => p.Contains("CVR"));
    }

    // =============================================================================================
    // Reservation rights
    // =============================================================================================

    [Fact]
    public void A_right_matches_by_booth_then_timeslot_then_name_and_never_guesses()
    {
        var products = new List<ShopProduct>
        {
            new() { Id = 1, Name = "Gold Exhibitor TEST28 - Booth E-12", BoothId = "E-12" },
            new() { Id = 2, Name = "Sponsor Long 60 min Session - TEST28 - timeslot: 2", Timeslot = "2" },
            new() { Id = 3, Name = "Sponsor Expo Intro 20 min Session - TEST28 - timeslot: 2", Timeslot = "2" },
            new() { Id = 4, Name = "Coffee Sponsor ELDK28" },
        };

        Assert.Equal(1, ShopEntitlementService.Match(new ShopEntitlementItem { ProductName = "Gold Exhibitor ELDK27 - Booth E-12", BoothId = "E-12" }, products)?.Id);
        Assert.Equal(3, ShopEntitlementService.Match(new ShopEntitlementItem { ProductName = "Sponsor Expo Intro 20 min Session - ELDK27 - timeslot: 2", Timeslot = "2" }, products)?.Id);
        Assert.Equal(4, ShopEntitlementService.Match(new ShopEntitlementItem { ProductName = "Coffee Sponsor ELDK27" }, products)?.Id);
        Assert.Null(ShopEntitlementService.Match(new ShopEntitlementItem { ProductName = "Popcorn Sponsor ELDK27" }, products));
    }

    // =============================================================================================
    // Safety helpers
    // =============================================================================================

    [Fact]
    public void Product_HTML_keeps_text_tags_and_drops_every_attribute_and_script()
    {
        var html = "<p class=\"x\" onclick=\"evil()\">Hello <strong>world</strong></p><script>alert(1)</script>"
                   + "<a href=\"javascript:evil()\">link</a><img src=x onerror=evil()><ul><li>One</li></ul>";

        var clean = ShopHtml.Sanitize(html);

        Assert.Equal("<p>Hello <strong>world</strong></p>link<ul><li>One</li></ul>", clean);
    }

    [Fact]
    public void The_built_in_captcha_needs_the_right_answer_a_human_pause_and_an_untampered_token()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var captcha = new ShopCaptcha(new ShopCaptchaOptions(), new EphemeralDataProtectionProvider(), clock);
        var c = captcha.NewChallenge();
        var parts = System.Text.RegularExpressions.Regex.Matches(c.Question, @"\d+").Select(m => int.Parse(m.Value)).ToList();
        var answer = (parts[0] + parts[1]).ToString();

        Assert.False(captcha.VerifyAsync(c.Token, answer, null).Result);                  // too fast: a bot
        clock.Now = clock.Now.AddSeconds(5);
        Assert.False(captcha.VerifyAsync(c.Token, "999", null).Result);                   // wrong answer
        Assert.False(captcha.VerifyAsync(c.Token + "x", answer, null).Result);            // tampered token
        Assert.True(captcha.VerifyAsync(c.Token, answer, null).Result);
        clock.Now = clock.Now.AddMinutes(30);
        Assert.False(captcha.VerifyAsync(c.Token, answer, null).Result);                  // expired
    }

    // =============================================================================================
    // Batch 3 — swag in the cart, invoicing, roles from e-conomic, foreign VAT
    // =============================================================================================

    private sealed class Invoices : IEconomicInvoiceClient
    {
        public List<EconomicDraftInvoice> Created { get; } = new();
        public HashSet<string> Existing { get; } = new();
        public string CustomerCurrency { get; set; } = "EUR";
        public int CustomerVatZone { get; set; } = 1;
        public bool CanWrite => true;
        public Task<IReadOnlyCollection<string>> ListInvoicedOrderReferencesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(Existing);
        public Task<IReadOnlyCollection<EconomicInvoiceReference>> ListInvoiceReferencesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<EconomicInvoiceReference>>(Array.Empty<EconomicInvoiceReference>());
        public Task<EconomicCustomerDetail?> GetCustomerAsync(int customerNumber, CancellationToken ct = default) =>
            Task.FromResult<EconomicCustomerDetail?>(new EconomicCustomerDetail(customerNumber, "Example Company ApS", null, null, null, "Denmark",
                CustomerCurrency, CustomerVatZone, null, 1, null, null, null, null));
        public Task<(int LayoutNumber, string? Self)?> FindLayoutAsync(string nameLike, CancellationToken ct = default) =>
            Task.FromResult<(int, string?)?>((7, null));
        public Task<int> CreateDraftInvoiceAsync(EconomicDraftInvoice invoice, CancellationToken ct = default)
        {
            Created.Add(invoice);
            if (invoice.OtherReference is { } r) Existing.Add(r);   // e-conomic lists what it holds, drafts included
            return Task.FromResult(900 + Created.Count);
        }
        /// <summary>The live agreement's terms (read 2026-09-30).</summary>
        public static readonly IReadOnlyList<EconomicPaymentTerm> Terms = new[]
        {
            new EconomicPaymentTerm(1, "Netto 8 dage", "net", 8, null), new EconomicPaymentTerm(3, "Netto 14 dage", "net", 14, null),
            new EconomicPaymentTerm(6, "Løbende måned + 7", "invoiceMonth", 7, null), new EconomicPaymentTerm(7, "Forfaldsdato", "dueDate", null, null),
            new EconomicPaymentTerm(8, "Netto 30 dage", "net", 30, null),
        };
        public Task<IReadOnlyList<EconomicPaymentTerm>> ListPaymentTermsAsync(CancellationToken ct = default) => Task.FromResult(Terms);
    }

    private sealed class Contacts : IEconomicContactAdminClient
    {
        public List<EconomicContactRow> Rows { get; } = new();
        public bool CanWrite => true;
        public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(string? search, int? customerGroup = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicCustomerRow>>(Array.Empty<EconomicCustomerRow>());
        public Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(int customerNumber, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicContactRow>>(Rows);
        public Task<int> CreateContactAsync(int customerNumber, EconomicContactInput input, CancellationToken ct = default) => Task.FromResult(1);
        public Task UpdateContactAsync(int customerNumber, int contactNumber, EconomicContactInput input, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteContactAsync(int customerNumber, int contactNumber, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Fx(decimal? rate) : IFxRateProvider
    {
        public bool CanQuote => rate is not null;
        public Task<decimal?> GetRateAsync(string b, string q, CancellationToken ct) => Task.FromResult(rate);
    }

    private static ShopDraftInvoiceService Invoicer(World w, Invoices inv, bool dryRun = false, decimal? rate = null) =>
        new(w.Db, w.Orders, inv, new Contacts(), new Fx(rate), new EconomicErpOptions(), new InvoicingOptions { DryRun = dryRun }, w.Clock,
            mail: w.Mailer);

    private static async Task<ShopOrder> SignedOrderAsync(World w, ShopCompany company, Participant signer, Participant coord, params ShopProduct[] products)
    {
        foreach (var p in products) await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, p.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        var r = await w.Signing.SignAsync(cart, signer.Id, ShopSignatureKind.Typed, Png(), null);
        Assert.True(r.Ok, r.Message);
        return cart;
    }

    /// <summary>🔑 Swag: ONE unit of stock (one sponsor takes the item), priced per piece × every bag.</summary>
    [Fact]
    public async Task A_swag_item_is_one_unit_of_stock_priced_per_piece_times_the_bags()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        await ProductAsync(w, "Power bank with your logo", ShopGroup.Swag, 11.50m, 10853);

        var r = await w.Orders.AddSwagAsync(w.EventId, company.SponsorCompanyId, 10853, 1500, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        var totals = await w.Orders.PriceAsync(cart, company);
        var reserved = await w.Orders.ReserveAsync(cart);

        Assert.True(r.Ok, r.Message);
        var line = Assert.Single(totals.Lines);
        Assert.Equal(17250m, line.LineEur);                        // no swagPricing in this config: 11.50 × every bag, 100 %
        Assert.Contains("in every attendee bag", line.Name);      // one amount — no piece count, no per-piece price
        Assert.DoesNotContain("per piece", line.Name);
        Assert.True(reserved.Ok, reserved.Message);
        Assert.Equal(0, w.Stock[10853].Qty);                        // one unit taken, not 1 500
    }

    [Fact]
    public async Task A_swag_credit_pays_only_for_swag_is_spent_once_and_comes_back_on_cancel()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        await ProductAsync(w, "Power bank", ShopGroup.Swag, 1m, 10853);
        var coffee = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627);
        w.Db.SwagCatalogCredits.Add(new SwagCatalogCredit { EventId = w.EventId, SponsorCompanyId = company.SponsorCompanyId,
            CompanyName = "Example", Code = "CREDIT-1", Amount = 2000m, CreatedByEmail = "org@test" });
        await w.Db.SaveChangesAsync();
        await w.Orders.AddSwagAsync(w.EventId, company.SponsorCompanyId, 10853, 1500, coord.Id);   // 1 500 € of swag
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, coffee.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;

        var totals = await w.Orders.PriceAsync(cart, company);
        Assert.Equal(1500m, totals.SwagCreditEur);                  // capped at the swag, never touches the coffee
        Assert.Equal(10000m, totals.NetEur);

        await w.Orders.ReserveAsync(cart);
        var credit = await w.Db.SwagCatalogCredits.SingleAsync();
        Assert.Equal(cart.Id, credit.UsedOnShopOrderId);
        Assert.Equal(1500m, cart.SwagCreditEur);

        await w.Orders.CancelAsync(cart, "coord@test", null);
        Assert.Null(credit.UsedOnShopOrderId);                       // live again
    }

    /// <summary>🔴 The invoice bills lines NET of their discounts, so it totals what the contract totals.</summary>
    [Fact]
    public async Task A_signed_order_becomes_one_draft_invoice_that_matches_the_contract()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        company.ErpCustomerNumber = "4711";
        w.Db.SponsorInfos.IgnoreQueryFilters().Single(s => s.SponsorCompanyId == company.SponsorCompanyId).Tier = BoothTier.Gold;
        await w.Db.SaveChangesAsync();
        var chair = await ProductAsync(w, "Chair", ShopGroup.Furniture, 35m, 10654, manage: false);
        var coffee = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627);
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, chair.Id, 4, coord.Id);
        var order = await SignedOrderAsync(w, company, signer, coord, coffee);
        var contract = await w.Orders.PriceAsync(order, company);
        var inv = new Invoices();

        var r = await Invoicer(w, inv).RunAsync(w.EventId);
        var again = await Invoicer(w, inv).RunAsync(w.EventId);

        Assert.Equal(1, r.Created);
        Assert.Equal(0, again.Due);                                   // never twice
        var draft = Assert.Single(inv.Created);
        Assert.Equal("CehShopOrder-" + order.Number, draft.OtherReference);
        var billed = draft.Lines.Where(l => l.UnitNetPrice is not null).Sum(l => (l.Quantity ?? 0m) * (l.UnitNetPrice ?? 0m));
        Assert.Equal(contract.NetEur, billed);                        // 140 − 99 included furniture + 10 000
        Assert.Equal(10041m, billed);
        Assert.NotNull(order.InvoicedAt);
        // 🔴 §1312 — every line matches the contract (operator 2026-09-29: "a separate line with the coupon event discount …
        // so each amount matches" · "just make a line"): the chairs at their contract price, never reduced, and the included
        // furniture as ONE negative line named by the webshop coupon code.
        var chairs = Assert.Single(draft.Lines, l => l.Description.StartsWith("Chair", StringComparison.Ordinal));
        Assert.Equal(4m, chairs.Quantity);
        Assert.Equal(35m, chairs.UnitNetPrice);
        var coupon = Assert.Single(draft.Lines, l => l.UnitNetPrice < 0);
        Assert.Equal(-99m, coupon.UnitNetPrice);
        Assert.Equal(1m, coupon.Quantity);
        Assert.Contains("coupon eldk-gold-booth-included", coupon.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(draft.Lines, l => l.Description.Contains("discount", StringComparison.OrdinalIgnoreCase));

        // 🔴 §1312 — the organizers are TOLD (operator 2026-09-30: "i dont think i received email when invoice was created").
        var notice = Assert.Single(w.Mail.Messages, m => m.To == "ops@example.test" && m.Subject.Contains("Draft invoice"));
        Assert.Contains("CehShopOrder-" + order.Number, notice.Html);
    }

    /// <summary>§1312 — an order that cannot be invoiced is told to the organizers with the reason, ONCE — not every run.</summary>
    [Fact]
    public async Task An_invoice_problem_is_told_once_not_every_run()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);   // no e-conomic customer number ⇒ cannot be invoiced
        await SignedOrderAsync(w, company, signer, coord, await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627));
        var inv = new Invoices();

        await Invoicer(w, inv).RunAsync(w.EventId);
        await Invoicer(w, inv).RunAsync(w.EventId);

        var notice = Assert.Single(w.Mail.Messages, m => m.To == "ops@example.test" && m.Subject.Contains("Invoice NOT created"));
        Assert.Contains("no e-conomic customer number", notice.Html);
        Assert.Empty(inv.Created);
    }

    /// <summary>
    /// 🔴 §1312 — operator 2026-09-29: "earlier it was much better, as i just looked in all invoices booked for the code and if
    /// none found, it would just add it again" · "this happens weekly" · "i loose money with this". e-conomic decides: a
    /// draft DELETED there is invoiced again on the next run, with no stamp to clear. An empty e-conomic answer is not trusted.
    /// </summary>
    [Fact]
    public async Task A_draft_deleted_in_economic_is_invoiced_again_without_touching_the_database()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        company.ErpCustomerNumber = "4711";
        await w.Db.SaveChangesAsync();
        var order = await SignedOrderAsync(w, company, signer, coord, await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627));
        var inv = new Invoices();
        inv.Existing.Add("WebshopOrderId-10871");                       // e-conomic holds other invoices too

        Assert.Equal(1, (await Invoicer(w, inv).RunAsync(w.EventId)).Created);
        Assert.Equal(0, (await Invoicer(w, inv).RunAsync(w.EventId)).Due);   // present in e-conomic ⇒ never twice

        inv.Existing.Remove("CehShopOrder-" + order.Number);          // the organizer deletes the draft in e-conomic
        var again = await Invoicer(w, inv).RunAsync(w.EventId);
        Assert.Equal(1, again.Created);                                // …and it comes back by itself
        Assert.Equal(2, inv.Created.Count);
        Assert.Equal(902, order.ErpDraftInvoiceNumber);

        // 🔒 An EMPTY answer from e-conomic is a failed read, not "everything was deleted": nothing is invoiced twice.
        inv.Existing.Clear();
        Assert.Equal(0, (await Invoicer(w, inv).RunAsync(w.EventId)).Created);
        Assert.Equal(2, inv.Created.Count);
    }

    /// <summary>
    /// 🔴 §1312 — operator 2026-09-30: "erp invoicing must match this, but i prefer to send the invoice now but just change the
    /// invoice date to what is agreed upon and terms". Invoiced at once, DATED the agreed day, with the agreed days as the
    /// e-conomic payment term — a matching "net" term, else the due-date term carrying the date.
    /// </summary>
    [Fact]
    public async Task An_order_is_invoiced_now_dated_the_agreed_day_with_the_agreed_payment_term()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        company.ErpCustomerNumber = "4711";
        await w.Db.SaveChangesAsync();
        var coffee = await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627);
        var order = await SignedOrderAsync(w, company, signer, coord, coffee);
        var agreedDate = DateOnly.FromDateTime(w.Clock.Now.UtcDateTime).AddDays(10);
        await w.Orders.SetSpecialTermsAsync(order, "Invoice in the new year", agreedDate, 30);
        var inv = new Invoices();

        Assert.Equal(1, (await Invoicer(w, inv).RunAsync(w.EventId)).Created);   // now — not in 10 days

        var draft = Assert.Single(inv.Created);
        Assert.Equal(agreedDate, draft.Date);
        Assert.Contains("30 days net", draft.TextLine1);
        Assert.Equal(8, draft.PaymentTerms!.Number);                             // Netto 30, not the customer's own (1)
        Assert.Null(draft.PaymentTerms.DueDate);
        var payload = LiveEconomicInvoiceClient.BuildPayload(draft);
        Assert.Equal(agreedDate.ToString("yyyy-MM-dd"), payload["date"]);
        Assert.Equal(8, ((Dictionary<string, object?>)payload["paymentTerms"]!)["paymentTermsNumber"]);
        Assert.False(payload.ContainsKey("dueDate"));
    }

    [Fact]
    public void Agreed_days_without_a_net_term_use_the_due_date_term_and_no_term_at_all_is_a_problem()
    {
        var day = new DateOnly(2027, 1, 1);
        var due = ShopDraftInvoiceService.PickPaymentTerm(Invoices.Terms, 45, day)!;
        Assert.Equal(7, due.Number);
        Assert.Equal(new DateOnly(2027, 2, 15), due.DueDate);
        Assert.Equal("2027-02-15", LiveEconomicInvoiceClient.BuildPayload(new EconomicDraftInvoice(
            new EconomicCustomerDetail(1, "X", null, null, null, "DK", "EUR", 1, null, 1, null, null, null, null), day, 7, null, "EUR",
            "r", 3, null, null, "h", "t", Array.Empty<EconomicInvoiceLine>(), PaymentTerms: due))["dueDate"]);
        Assert.Equal(3, ShopDraftInvoiceService.PickPaymentTerm(Invoices.Terms, 14, day)!.Number);
        Assert.Null(ShopDraftInvoiceService.PickPaymentTerm(Invoices.Terms.Where(t => t.Type == "net").ToList(), 45, day));
    }

    /// <summary>No agreed days ⇒ the customer's own e-conomic terms, and no date override ⇒ today (§811a unchanged).</summary>
    [Fact]
    public async Task Without_agreed_terms_the_customers_own_terms_and_today_are_used()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        company.ErpCustomerNumber = "4711";
        await w.Db.SaveChangesAsync();
        await SignedOrderAsync(w, company, signer, coord, await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627));
        var inv = new Invoices();

        await Invoicer(w, inv).RunAsync(w.EventId);

        var draft = Assert.Single(inv.Created);
        Assert.Null(draft.PaymentTerms);
        Assert.Equal(DateOnly.FromDateTime(w.Clock.Now.UtcDateTime), draft.Date);
        Assert.Equal(1, ((Dictionary<string, object?>)LiveEconomicInvoiceClient.BuildPayload(draft)["paymentTerms"]!)["paymentTermsNumber"]);
    }

    [Fact]
    public async Task A_dry_run_reports_and_creates_nothing()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        company.ErpCustomerNumber = "4711";
        await w.Db.SaveChangesAsync();
        var order = await SignedOrderAsync(w, company, signer, coord, await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627));
        var inv = new Invoices();

        var r = await Invoicer(w, inv, dryRun: true).RunAsync(w.EventId);

        Assert.Equal(1, r.WouldCreate);
        Assert.Empty(inv.Created);
        Assert.Null(order.InvoicedAt);
        Assert.StartsWith("DRY RUN", order.InvoiceProblem);
    }

    [Fact]
    public async Task A_company_without_an_e_conomic_customer_is_reported_not_invoiced()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var order = await SignedOrderAsync(w, company, signer, coord, await ProductAsync(w, "Coffee Sponsor", ShopGroup.Branding, 10000m, 10627));
        var inv = new Invoices();

        var r = await Invoicer(w, inv).RunAsync(w.EventId);

        Assert.Empty(inv.Created);
        Assert.Contains("no e-conomic customer number", Assert.Single(r.Problems));
        Assert.Contains("no e-conomic customer number", order.InvoiceProblem);
    }

    [Fact]
    public async Task A_foreign_currency_customer_is_converted_and_rounded_up_like_webshop_invoices()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        company.ErpCustomerNumber = "4711";
        await w.Db.SaveChangesAsync();
        await SignedOrderAsync(w, company, signer, coord, await ProductAsync(w, "Chair", ShopGroup.Furniture, 35.10m, 10654, manage: false));
        var inv = new Invoices { CustomerCurrency = "DKK" };

        await Invoicer(w, inv, rate: 7.46m).RunAsync(w.EventId);

        var line = Assert.Single(Assert.Single(inv.Created).Lines, l => l.UnitNetPrice is not null);
        Assert.Equal(Math.Ceiling(35.10m * 7.46m), line.UnitNetPrice);
        Assert.Equal("DKK", inv.Created[0].Currency);
    }

    /// <summary>§1305 item 8 — a registered company's signer / coordinator follow e-conomic, the master of people.</summary>
    [Fact]
    public async Task Roles_follow_e_conomic_for_a_registered_company()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        company.ErpCustomerNumber = "4711";
        await w.Db.SaveChangesAsync();
        var contacts = new Contacts();
        contacts.Rows.Add(new EconomicContactRow(1, "Sam Signer", signer.Email, null, "Role:2"));         // signer became coordinator only
        contacts.Rows.Add(new EconomicContactRow(2, "New Person", "new.person@example.test", null, "Role:1")); // new signer

        var r = await RoleSync(w, contacts).RunAsync(w.EventId);

        Assert.Equal(1, r.PeopleAdded);
        var links = await w.Db.SponsorCompanyContacts.Where(l => l.SponsorCompanyId == company.SponsorCompanyId).ToListAsync();
        var sam = links.Single(l => l.ParticipantId == signer.Id);
        Assert.False(sam.IsSigner);                                         // an e-conomic contact carries e-conomic's roles
        Assert.True(sam.IsEventCoordinator);
        Assert.Equal(1, sam.ErpContactNumber);
        // 🔴 §1312 — no longer an e-conomic contact: the role is KEPT and WARNED, never removed silently (operator
        // 2026-09-30: "then you must warn me of missing roles").
        var casey = links.Single(l => l.ParticipantId == coord.Id);
        Assert.True(casey.IsEventCoordinator);
        Assert.Contains("Casey Coordinator", company.ErpRoleWarning);
        var newbie = await w.Db.Participants.SingleAsync(p => p.Email == "new.person@example.test");
        Assert.True(links.Single(l => l.ParticipantId == newbie.Id).IsSigner);
        // The known people ARE e-conomic's contacts.
        Assert.Equal(2, await w.Db.ShopKnownContacts.CountAsync(k => k.SponsorCompanyId == company.SponsorCompanyId && k.Source == "Economic"));
    }

    private static ShopErpRoleSyncService RoleSync(World w, Contacts contacts) =>
        new(w.Db, contacts, new SponsorCompanyMembershipService(w.Db, w.Clock), w.Clock, mail: w.Mailer);

    /// <summary>An existing sponsor from the old webshop, with its e-conomic number (Company Manager is retired).</summary>
    private static async Task<ShopCompany> OldWebshopCompanyAsync(World w, string id, ShopCompanyState state, string erp)
    {
        var c = new ShopCompany
        {
            EventId = w.EventId, SponsorCompanyId = id, LegalName = $"Company {id} ApS", ErpCustomerNumber = erp, State = state,
            Source = ShopCompanySource.CompanyManager, CreatedAt = w.Clock.Now,
        };
        w.Db.ShopCompanies.Add(c);
        w.Db.SponsorInfos.Add(new SponsorInfo { EventId = w.EventId, SponsorCompanyId = id, CompanyName = $"Company {id}", IsSponsor = state == ShopCompanyState.Sponsor });
        await w.Db.SaveChangesAsync();
        return c;
    }

    /// <summary>
    /// 🔴 §1312 — a POTENTIAL sponsor's e-conomic contacts are offered, never created. Operator 2026-09-30: "these contact must be
    /// for old sponsors in erp, potential sponsor contact. but they are not a sponsor today so they should not get welcome".
    /// SquaredUp was the case: Lee Benney (Role:1,2) and Kirstie Walker (Role:2) in e-conomic, only Richard in the hub.
    /// </summary>
    [Fact]
    public async Task A_potential_sponsors_economic_contacts_become_known_people_only_and_a_linked_one_follows_its_roles()
    {
        var w = await NewWorldAsync();
        var company = await OldWebshopCompanyAsync(w, "20", ShopCompanyState.Candidate, "341409313");
        var richard = new Participant { EventId = w.EventId, Email = "richard@squaredup.test", FullName = "Richard Benwell", Role = ParticipantRole.Sponsor, SponsorCompanyId = "20", IsActive = true };
        w.Db.Participants.Add(richard);
        await w.Db.SaveChangesAsync();
        w.Db.SponsorCompanyContacts.Add(new SponsorCompanyContact { EventId = w.EventId, ParticipantId = richard.Id, SponsorCompanyId = "20", IsPrimary = true });
        await w.Db.SaveChangesAsync();
        var contacts = new Contacts();
        contacts.Rows.Add(new EconomicContactRow(181, "Richard Benwell", "Richard@squaredup.test", null, "Role:1"));
        contacts.Rows.Add(new EconomicContactRow(120, "Lee Benney", "lee@squaredup.test", null, "Type:1; Role:1,2"));
        contacts.Rows.Add(new EconomicContactRow(11, "Kirstie Walker", "kirstie@squaredup.test", null, "Role:2"));

        var r = await RoleSync(w, contacts).RunAsync(w.EventId);

        Assert.Equal(0, r.PeopleAdded);                                                  // nobody created…
        Assert.False(await w.Db.Participants.AnyAsync(p => p.Email == "lee@squaredup.test"));
        var known = await w.Db.ShopKnownContacts.Where(k => k.SponsorCompanyId == "20").ToListAsync();
        Assert.Equal(3, known.Count);                                                    // …but all three are offered
        Assert.True(known.Single(k => k.Email == "lee@squaredup.test") is { IsSigner: true, IsEventCoordinator: true });
        Assert.True((await w.Db.SponsorCompanyContacts.SingleAsync(l => l.ParticipantId == richard.Id)).IsSigner);   // linked ⇒ follows e-conomic
        Assert.Null(company.ErpRoleWarning);                                            // potential sponsors are not warned about
        Assert.Empty(w.Mail.Messages);                                                   // and nobody is mailed

        // Picked on an order, a known person becomes a contact of the company — with e-conomic's roles, and no mail.
        var directory = new ShopCompanyDirectoryService(w.Db, new SponsorCompanyMembershipService(w.Db, w.Clock), w.Clock);
        var pid = await directory.KnownToContactAsync(w.EventId, "20", known.Single(k => k.Email == "kirstie@squaredup.test").Id);
        Assert.NotNull(pid);
        Assert.True((await w.Db.SponsorCompanyContacts.SingleAsync(l => l.ParticipantId == pid)).IsEventCoordinator);
        Assert.Empty(w.Mail.Messages);
    }

    /// <summary>
    /// 🔴 §1312 — a SIGNED sponsor's e-conomic contacts become linked contacts; a role CEH holds that e-conomic does not back is
    /// kept and warned — once, not every run; the same name under another e-mail is never created twice (Robopack).
    /// </summary>
    [Fact]
    public async Task A_signed_sponsor_is_synced_and_every_mismatch_is_warned_once_and_nothing_is_removed()
    {
        var w = await NewWorldAsync();
        var company = await OldWebshopCompanyAsync(w, "44", ShopCompanyState.Sponsor, "32559735");
        var laura = new Participant { EventId = w.EventId, Email = "laura@softwarecentral.test", FullName = "Laura Gulbe", Role = ParticipantRole.Sponsor, SponsorCompanyId = "44", IsActive = true, IsEventCoordinator = true };
        w.Db.Participants.Add(laura);
        await w.Db.SaveChangesAsync();
        w.Db.SponsorCompanyContacts.Add(new SponsorCompanyContact { EventId = w.EventId, ParticipantId = laura.Id, SponsorCompanyId = "44", IsEventCoordinator = true, IsPrimary = true });
        // Deactivated in CEH (left the company) but still flagged: never warned about (operator 2026-09-30: "not working at
        // arrow anymore so disable him").
        var kim = new Participant { EventId = w.EventId, Email = "kim@old.test", FullName = "Kim Left", Role = ParticipantRole.Sponsor, SponsorCompanyId = "44", IsActive = false, IsSigner = true };
        w.Db.Participants.Add(kim);
        await w.Db.SaveChangesAsync();
        w.Db.SponsorCompanyContacts.Add(new SponsorCompanyContact { EventId = w.EventId, ParticipantId = kim.Id, SponsorCompanyId = "44", IsSigner = true });
        await w.Db.SaveChangesAsync();
        var contacts = new Contacts();
        contacts.Rows.Add(new EconomicContactRow(1, "Laura  Gulbe", "laura@robopack.test", null, "Role:1,2"));   // same person, new domain
        contacts.Rows.Add(new EconomicContactRow(2, "Gunita Zarina", "gunita@robopack.test", null, "Role:2"));

        var r = await RoleSync(w, contacts).RunAsync(w.EventId);
        await RoleSync(w, contacts).RunAsync(w.EventId);                                // a second run tells nobody again

        Assert.Equal(1, r.PeopleAdded);                                                  // Gunita — not Laura a second time
        Assert.False(await w.Db.Participants.AnyAsync(p => p.Email == "laura@robopack.test"));
        Assert.True((await w.Db.SponsorCompanyContacts.SingleAsync(l => l.ParticipantId == laura.Id)).IsEventCoordinator);   // kept
        Assert.Contains("laura@robopack.test", company.ErpRoleWarning);
        Assert.DoesNotContain("not a contact in e-conomic", company.ErpRoleWarning);    // the same-name warning covers Laura
        Assert.DoesNotContain("Kim Left", company.ErpRoleWarning);                     // an inactive person is never warned about
        var notice = Assert.Single(w.Mail.Messages, m => m.To == "ops@example.test" && m.Subject.Contains("Roles to check"));
        Assert.Contains("Company 44 ApS", notice.Html);
    }

    /// <summary>
    /// 🔴 §1312 — every field of a contact follows e-conomic (operator 2026-10-01: "sync any changes from erp to ceh, including
    /// billing fields, contacts etc - it must compare the fields and update if different" · "we prefer phone number pe
    /// contact"): an e-mail changed in e-conomic RENAMES the same person (matched by the e-conomic contact number — never a
    /// second person), and the name and phone follow, a blank phone included.
    /// </summary>
    [Fact]
    public async Task A_contacts_email_name_and_phone_follow_economic_by_contact_number()
    {
        var w = await NewWorldAsync();
        await OldWebshopCompanyAsync(w, "45", ShopCompanyState.Sponsor, "4545");
        var andreas = new Participant { EventId = w.EventId, Email = "ats@softwarecentral.test", FullName = "Andreas S", Phone = "+45 1",
            Role = ParticipantRole.Sponsor, SponsorCompanyId = "45", IsActive = true, IsSigner = true };
        var gunita = new Participant { EventId = w.EventId, Email = "gunita@robopack.test", FullName = "Gunita", Phone = "+371 2",
            Role = ParticipantRole.Sponsor, SponsorCompanyId = "45", IsActive = true };
        w.Db.Participants.AddRange(andreas, gunita);
        await w.Db.SaveChangesAsync();
        w.Db.SponsorCompanyContacts.AddRange(
            new SponsorCompanyContact { EventId = w.EventId, ParticipantId = andreas.Id, SponsorCompanyId = "45", IsSigner = true, ErpContactNumber = 7 },
            new SponsorCompanyContact { EventId = w.EventId, ParticipantId = gunita.Id, SponsorCompanyId = "45", ErpContactNumber = 8 });
        await w.Db.SaveChangesAsync();
        var contacts = new Contacts();
        contacts.Rows.Add(new EconomicContactRow(7, "Andreas Spiegelhauer", "ats@robopack.test", "+45 9999", "Role:1"));
        contacts.Rows.Add(new EconomicContactRow(8, "Gunita Zarina", "gunita@robopack.test", null, "Role:2"));

        var r = await RoleSync(w, contacts).RunAsync(w.EventId);

        Assert.Equal(0, r.PeopleAdded);                                                   // renamed, not created again
        Assert.Equal("ats@robopack.test", andreas.Email);
        Assert.Equal("Andreas Spiegelhauer", andreas.FullName);
        Assert.Equal("+45 9999", andreas.Phone);
        Assert.Equal("Gunita Zarina", gunita.FullName);
        Assert.Null(gunita.Phone);                                                        // blank in e-conomic ⇒ blank in CEH
        Assert.True((await w.Db.SponsorCompanyContacts.SingleAsync(l => l.ParticipantId == gunita.Id)).IsEventCoordinator);
    }

    /// <summary>§1312 — a withdrawn company (the duplicate Master Packager #110) is skipped; the page's save syncs one company only.</summary>
    [Fact]
    public async Task A_withdrawn_company_is_skipped_and_a_single_customer_sync_touches_only_that_company()
    {
        var w = await NewWorldAsync();
        await OldWebshopCompanyAsync(w, "110", ShopCompanyState.Sponsor, "111");
        (await w.Db.SponsorInfos.IgnoreQueryFilters().SingleAsync(s => s.SponsorCompanyId == "110")).Status = SponsorStatus.Withdrawn;
        await OldWebshopCompanyAsync(w, "21", ShopCompanyState.Sponsor, "222");
        await OldWebshopCompanyAsync(w, "22", ShopCompanyState.Sponsor, "333");
        await w.Db.SaveChangesAsync();
        var contacts = new Contacts();
        contacts.Rows.Add(new EconomicContactRow(1, "Edijs Perkums", "edijs@example.test", null, "Role:1,2"));

        var all = await RoleSync(w, contacts).RunAsync(w.EventId);
        Assert.Equal(2, all.Companies);                                                  // not the withdrawn one
        Assert.False(await w.Db.ShopKnownContacts.AnyAsync(k => k.SponsorCompanyId == "110"));

        var one = await RoleSync(w, contacts).RunAsync(w.EventId, onlyErpCustomerNumber: 333);
        Assert.Equal(1, one.Companies);
    }

    /// <summary>🔒 An empty answer from e-conomic is not evidence that everybody left.</summary>
    [Fact]
    public async Task An_empty_contact_list_from_e_conomic_strips_nobody()
    {
        var w = await NewWorldAsync();
        var (company, signer, _) = await CompanyAsync(w);
        company.ErpCustomerNumber = "4711";
        await w.Db.SaveChangesAsync();

        await new ShopErpRoleSyncService(w.Db, new Contacts(), new SponsorCompanyMembershipService(w.Db, w.Clock), w.Clock).RunAsync(w.EventId);

        Assert.True((await w.Db.SponsorCompanyContacts.SingleAsync(l => l.ParticipantId == signer.Id)).IsSigner);
    }

    private sealed class Vies(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            LastUrl = r.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Foreign_VAT_is_checked_in_VIES_and_a_down_register_fails_open_visibly()
    {
        var valid = new Vies(HttpStatusCode.OK, "{\"isValid\":true}");
        var ok = await new ShopVatChecker(new HttpClient(valid)).CheckAsync("GR", "EL 123456789");
        Assert.True(ok.Valid);
        Assert.Contains("/ms/EL/vat/123456789", valid.LastUrl);           // Greece is EL in VIES; prefix stripped

        Assert.False((await new ShopVatChecker(new HttpClient(new Vies(HttpStatusCode.OK, "{\"isValid\":false}"))).CheckAsync("SE", "5566908074")).Valid);

        var down = await new ShopVatChecker(new HttpClient(new Vies(HttpStatusCode.ServiceUnavailable, ""))).CheckAsync("DE", "123456789");
        Assert.Null(down.Valid);
        Assert.Contains("unavailable", down.Note);

        var norway = await new ShopVatChecker(new HttpClient(valid)).CheckAsync("NO", "123456789");
        Assert.Null(norway.Valid);
        Assert.Contains("Outside the EU", norway.Note);
    }

    /// <summary>
    /// §1312 — VIES answers isValid=false ALSO when a member state's service is down. Operator 2026-09-27: "arrow finland is
    /// FI15408025 so it should work, i verified it manually" — VIES had answered userError=MS_UNAVAILABLE, which the checker
    /// read as "NOT valid". Only a plain invalid answer is invalid; the country code a sponsor types is stripped.
    /// </summary>
    [Theory]
    [InlineData("{\"isValid\":false,\"userError\":\"MS_UNAVAILABLE\"}", null)]
    [InlineData("{\"isValid\":false,\"userError\":\"TIMEOUT\"}", null)]
    [InlineData("{\"isValid\":false,\"userError\":\"MS_MAX_CONCURRENT_REQ\"}", null)]
    [InlineData("{\"isValid\":false,\"userError\":\"INVALID\"}", false)]
    [InlineData("{\"isValid\":false,\"userError\":\"INVALID_INPUT\"}", false)]
    [InlineData("{\"isValid\":false}", false)]
    [InlineData("{\"isValid\":true,\"userError\":\"VALID\",\"name\":\"ARROW ECS OY\"}", true)]
    public async Task A_member_state_that_is_down_is_never_read_as_an_invalid_VAT_number(string answer, bool? expected)
    {
        var vies = new Vies(HttpStatusCode.OK, answer);

        var r = await new ShopVatChecker(new HttpClient(vies)).CheckAsync("FI", "FI 1540 8025");

        Assert.Equal(expected, r.Valid);
        Assert.Contains("/ms/FI/vat/15408025", vies.LastUrl);               // the typed country code and spaces stripped
        if (expected == true) Assert.Equal("ARROW ECS OY", r.RegisteredName);
    }

    /// <summary>
    /// §1312 — operator 2026-09-27: "goal is to point out which booth is offered/reserved on the map". The offer carries the
    /// expo layout with the order's booth marked (and says so); an order without a booth carries no map.
    /// </summary>
    [Fact]
    public async Task The_offer_carries_the_expo_map_with_the_booth_marked()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12", categoryName: "Gold Exhibitor");
        w.Db.ShopBoothSpots.Add(new ShopBoothSpot { EventId = w.EventId, BoothId = "E-12", XPercent = 40, YPercent = 40, WidthPercent = 20, HeightPercent = 20 });
        await w.Db.SaveChangesAsync();
        using (var layout = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(200, 100, SixLabors.ImageSharp.Color.White))
        using (var ms = new MemoryStream()) { SixLabors.ImageSharp.ImageExtensions.SaveAsPng(layout, ms); w.Store.Files["map.png"] = ms.ToArray(); }

        var paths = new DocLibraryPathResolver(new DocLibraryOptions { Enabled = true, SiteUrl = "https://sp.test/sites/x", RootFolderPath = "General/Test/EventHub" });
        var mailer = new ShopMailer(w.Db, new EmailTemplateProvider(Options.Create(new EmailTemplateOptions
            { TemplateDirectory = RepoPaths.EmailTemplates(), HubUrl = "https://hub.example.test" })), w.Mail, w.Settings,
            boothMap: new ShopBoothMapPicture(w.Db, w.Settings, paths, w.Store));

        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, booth.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        await mailer.SendOfferAsync(cart, await w.Orders.PriceAsync(cart, company), "prospect@example.test", null, null, default);

        var sent = Assert.Single(w.Mail.AttachmentMessages, m => m.To == "prospect@example.test");
        Assert.Contains("Booth E-12 is marked on the expo map", sent.Html);
        var map = Assert.Single(sent.Attachments, a => a.ContentType == "image/jpeg");
        Assert.Equal("Expo-map-booth-E-12.jpg", map.FileName);
        using var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(map.Content);
        var inside = img[100, 50];                                  // centre of the booth: tinted, no longer white
        Assert.True(inside.G < 240, $"booth not marked: {inside}");
        Assert.True(img[5, 5].G > 240);                             // far from the booth: untouched
    }

    /// <summary>
    /// §1312 — Northern Ireland was missing at registration. The company stays United Kingdom; a VAT number written with the
    /// XI prefix is checked in VIES under XI (and must be green, like an EU number). A plain UK number is not checked.
    /// </summary>
    [Fact]
    public async Task A_Northern_Irish_VAT_number_is_checked_in_VIES_under_XI()
    {
        Assert.True(ShopVatChecker.IsNorthernIrish("GB", "XI 123 4567 89"));
        Assert.True(ShopVatChecker.NeedsVies("GB", "xi123456789"));
        Assert.False(ShopVatChecker.NeedsVies("GB", "GB123456789"));
        Assert.False(ShopVatChecker.IsNorthernIrish("IE", "XI123456789"));

        var vies = new Vies(HttpStatusCode.OK, "{\"isValid\":true}");
        var ni = await new ShopVatChecker(new HttpClient(vies)).CheckAsync("GB", "XI 123456789");
        Assert.True(ni.Valid);
        Assert.Contains("/ms/XI/vat/123456789", vies.LastUrl);

        var uk = await new ShopVatChecker(new HttpClient(vies)).CheckAsync("GB", "GB123456789");
        Assert.Null(uk.Valid);
        Assert.Contains("Outside the EU", uk.Note);
    }

    /// <summary>
    /// §1312 — operator 2026-09-27: "it must be clear if people must add https:// or not in urls. it should block if http:// is
    /// used, as zoho does not support non-secure url". https:// is added when left out; http:// is refused.
    /// </summary>
    [Theory]
    [InlineData("www.company.com", "https://www.company.com/", false)]
    [InlineData("  https://company.com/about  ", "https://company.com/about", false)]
    [InlineData("HTTPS://Company.com", "https://company.com/", false)]
    [InlineData("http://company.com", "https://company.com/", false)]   // §1312 — upgraded, not refused
    [InlineData("HTTP://company.com", "https://company.com/", false)]
    [InlineData("not a url", null, true)]
    [InlineData("", null, false)]
    public void Web_addresses_are_made_secure_and_http_is_refused(string raw, string? stored, bool refused)
    {
        var (url, error) = ShopRegistrationService.SecureUrl(raw);
        Assert.Equal(stored, url);
        Assert.Equal(refused, error is not null);
    }

    /// <summary>
    /// §1312 — operator 2026-09-27: "i did not provide https as you told me not to do" — a LinkedIn address typed WITHOUT
    /// https:// (any LinkedIn host, or the lnkd.in short link) passes; something that is not LinkedIn is named in the error.
    /// </summary>
    [Theory]
    [InlineData("linkedin.com/company/balanceme", true)]
    [InlineData("www.linkedin.com/company/balanceme", true)]
    [InlineData("dk.linkedin.com/in/someone", true)]
    [InlineData("https://lnkd.in/abc", true)]
    [InlineData("www.facebook.com/balanceme", false)]
    public async Task A_LinkedIn_address_without_https_is_accepted(string linkedIn, bool ok)
    {
        var w = await NewWorldAsync();
        var input = Input("reg@example.test") with { LinkedIn = linkedIn };

        var problems = await Registration(w).ValidateAsync(input, "reg@example.test");

        Assert.Equal(ok, !problems.Any(p => p.Contains("LinkedIn")));
        if (!ok) Assert.Contains(problems, p => p.Contains(linkedIn));      // the error names what was typed
    }

    /// <summary>§1312 — the company / invoice e-mail is mandatory.</summary>
    [Fact]
    public async Task The_company_invoice_email_is_required()
    {
        var w = await NewWorldAsync();
        var problems = await Registration(w).ValidateAsync(Input("reg@example.test") with { CompanyEmail = " " }, "reg@example.test");
        Assert.Contains(problems, p => p.Contains("invoice e-mail"));
    }

    /// <summary>
    /// §1312 — operator 2026-09-27: "sponsor + me must get an email when an reservation is cancelled. sponsor + me must get
    /// an email when order is cancelled". The order's people get one mail each, CC the organizers; a plain cart sends none.
    /// </summary>
    [Fact]
    public async Task Cancelling_a_reservation_or_order_tells_its_people_and_the_organizers_but_not_for_a_plain_cart()
    {
        const string copy = "copy@example.test";
        var w = await NewWorldAsync(MinimalConfig.Replace(
            "\"notifyEmails\": [ \"ops@example.test\" ] }", $"\"notifyEmails\": [ \"ops@example.test\" ], \"copyEmails\": [ \"{copy}\" ] }}"));
        var (company, signer, coord) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12");

        // A plain cart: no mail.
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, booth.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        await w.Orders.CancelAsync(cart, coord.Email, "Cancelled by the sponsor");
        Assert.DoesNotContain(w.Mail.Messages, m => m.Subject.StartsWith("Cancelled"));

        // An organizer reservation released by the organizers: the requester is told, with the reason, CC the organizers.
        await Reservations(w).CreateAsync(w.EventId, Resv(company.SponsorCompanyId, null, booth.Id), "org@example.test");
        var resv = await w.Db.ShopOrders.Include(o => o.Lines).SingleAsync(o => o.ReservationRequesterEmail != null && o.Status == ShopOrderStatus.Reserved);
        w.Mail.Messages.Clear(); w.Mail.CcSent.Clear();
        await w.Orders.CancelAsync(resv, "org@example.test", "The booth moved to another sponsor.", byOrganizers: true);

        var mail = Assert.Single(w.Mail.Messages, m => m.To == "rita@example.test");
        Assert.StartsWith("Cancelled: reservation", mail.Subject);
        Assert.Contains("by the organizers", mail.Html);
        Assert.Contains("The booth moved to another sponsor.", mail.Html);
        Assert.Contains(w.Mail.CcSent, c => c.To == "rita@example.test" && c.Cc.Contains(copy));
        Assert.Equal(1, w.Stock[10602].Qty);                                   // the booth is back on sale
    }

    /// <summary>§1312 — which countries need a green VIES check before registration can complete.</summary>
    [Theory]
    [InlineData("FI", true)]
    [InlineData("DE", true)]
    [InlineData("SE", true)]
    [InlineData("DK", false)]   // checksum instead
    [InlineData("NO", false)]   // not in the EU
    [InlineData("GB", false)]
    [InlineData("US", false)]   // "not relevant for fx usa companies"
    public void Only_EU_countries_covered_by_VIES_need_a_green_check(string country, bool needed) =>
        Assert.Equal(needed, ShopVatChecker.NeedsVies(country));

    /// <summary>
    /// The per-environment override moves only the window's START (operator: "move the window to today in dev");
    /// its length comes from the file, and without the setting the file's date stands.
    /// </summary>
    [Fact]
    public void An_environment_can_move_the_reservation_window_without_editing_the_shared_config()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shop-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, MinimalConfig);

        var plain = new ShopSettings(new ShopConfigLoader(), new ShopConfigOptions { ConfigPath = path }).Shop().Reservation;
        var moved = new ShopSettings(new ShopConfigLoader(), new ShopConfigOptions { ConfigPath = path, ReservationOpensOn = "2026-09-26" })
            .Shop().Reservation;

        Assert.Equal(new DateOnly(2027, 2, 11), plain.OpensOnDate);
        Assert.Equal(new DateOnly(2026, 9, 26), moved.OpensOnDate);
        Assert.Equal(new DateOnly(2026, 10, 26), moved.ClosesOnDate);   // same 31-day length as the file
        Assert.True(moved.IsOpen(new DateOnly(2026, 9, 26)));
    }

    /// <summary>🔑 The REAL private config file binds — a mis-spelled property would silently give an empty contract.</summary>
    [Fact]
    public void The_real_shop_config_binds_everything_a_contract_needs()
    {
        var path = Path.Combine(RepoPaths.RepoRoot(), "config", "shop.eldk27.json");
        if (!File.Exists(path)) return;   // the public mirror strips config/

        var cfg = new ShopConfigLoader().Load(path);

        Assert.True(ShopConfigLoader.IsUsable(cfg));
        Assert.Equal(19, cfg.ContractTerms.Count);
        Assert.Contains(cfg.ContractTerms.SelectMany(a => a.Paragraphs), p => p == "{{paymentBlock}}");
        Assert.Equal(50m, cfg.LateSurcharge.Percent);
        Assert.Equal(99m, cfg.IncludedFurniture.AllowanceEurByTier["gold"]);
        Assert.Equal(new DateOnly(2027, 2, 11), cfg.Reservation.OpensOnDate);
        Assert.False(string.IsNullOrWhiteSpace(cfg.Payment.Iban));
    }

    // =============================================================================================
    // §1308 — organizer reservations for THIS edition (operator 2026-09-27: "I have multiple companies like Red Hat, that
    // have reserved a booth … marked as RESERVED and not available to select … weekly email … deadline … requester …
    // cadence … byweekly or disabled until after a specific date").
    // =============================================================================================

    private static ShopReservationService Reservations(World w) => new(w.Db, w.Orders, w.Mailer, w.Clock);

    private static ShopReservationInput Resv(string? companyId, string? newName, int productId,
        int weeks = 1, DateOnly? deadline = null, DateOnly? from = null) =>
        new(companyId, newName, new[] { productId }, "Rita Requester", "rita@example.test", deadline, weeks, from, null);

    // ---- §1312 — the sponsor asks, the organizer decides (operator 2026-09-27: "Offer: make reservation request, flip to
    // order (if available) + sign · reservation: extend, cancel, flip to order + sign" · "only thing i have to do is to
    // accept or deny reservation request and i can add deadline, reminder, etc") ----------------------------------------

    [Fact]
    public async Task A_reservation_request_reserves_nothing_mails_the_organizers_a_direct_link_and_stops_the_cart_reminder()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12");
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, booth.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;

        var r = await Reservations(w).RequestReservationAsync(w.EventId, company.SponsorCompanyId, cart.Id, coord.Id);

        Assert.True(r.Ok, r.Message);
        var order = await w.Db.ShopOrders.SingleAsync(o => o.Id == cart.Id);
        Assert.Equal(ShopOrderStatus.Cart, order.Status);                               // nothing reserved (first come)
        Assert.True(order.HasOpenReservationRequest);
        Assert.Equal(1, w.Stock[10602].Qty);                                        // no stock taken
        var notice = Assert.Single(w.Mail.Messages, m => m.To == "ops@example.test");
        Assert.Contains("/Organizer/ShopEventReservations#request-" + cart.Id, notice.Html);  // the direct link
        Assert.DoesNotContain(w.Mail.Messages, m => m.To == coord.Email);                // the sponsor gets nothing yet
        Assert.Empty(await w.Mailer.BuildCartRemindersAsync(w.EventId, new DateOnly(2026, 10, 1), default));  // waiting on us
        Assert.Single(await Reservations(w).OpenRequestsAsync(w.EventId));
    }

    [Fact]
    public async Task Accepting_a_request_reserves_it_with_the_organizers_deadline_and_cadence_and_mails_the_reservation()
    {
        var w = await NewWorldAsync();
        var (company, signer, coord) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12");
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, booth.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        await Reservations(w).RequestReservationAsync(w.EventId, company.SponsorCompanyId, cart.Id, coord.Id);
        w.Mail.Messages.Clear();

        var r = await Reservations(w).AcceptRequestAsync(w.EventId, cart.Id, new DateOnly(2026, 10, 15), 2, null, "OK for two weeks");

        Assert.True(r.Ok, r.Message);
        var order = await w.Db.ShopOrders.SingleAsync(o => o.Id == cart.Id);
        Assert.Equal(ShopOrderStatus.Reserved, order.Status);
        Assert.Equal(0, w.Stock[10602].Qty);                                        // held now
        Assert.Equal(coord.Email, order.ReservationRequesterEmail);                      // the person who asked
        Assert.Equal(new DateOnly(2026, 10, 15), order.ReservationDeadline);
        Assert.Equal(2, order.ReservationReminderWeeks);
        Assert.Equal(signer.Id, order.SignerParticipantId);                              // the company's signer signs
        var mail = Assert.Single(w.Mail.Messages, m => m.To == coord.Email);
        Assert.Contains("sign by 15 October 2026", mail.Subject);
        Assert.Contains("Review and sign", mail.Html);
        Assert.Contains("Ask for more time", mail.Html);
        Assert.Contains("Cancel reservation", mail.Html);
        Assert.Contains("/Sponsor/Shop/Reservation/" + cart.Id + "#cancel", mail.Html);  // opens the page — never cancels by itself
        Assert.DoesNotContain("reply to this e-mail or write to", mail.Html.Replace("Questions or problems? Reply to this e-mail or write to", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Denying_a_request_leaves_the_cart_theirs_and_tells_them()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12");
        await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, booth.Id, 1, coord.Id);
        var cart = (await w.Orders.CartAsync(w.EventId, company.SponsorCompanyId))!;
        await Reservations(w).RequestReservationAsync(w.EventId, company.SponsorCompanyId, cart.Id, coord.Id);

        var r = await Reservations(w).DenyRequestAsync(w.EventId, cart.Id, "The booth is promised to a returning sponsor.");

        Assert.True(r.Ok, r.Message);
        var order = await w.Db.ShopOrders.SingleAsync(o => o.Id == cart.Id);
        Assert.Equal(ShopOrderStatus.Cart, order.Status);
        Assert.False(order.HasOpenReservationRequest);
        var answer = Assert.Single(w.Mail.Messages, m => m.To == coord.Email);
        Assert.Contains("promised to a returning sponsor", answer.Html);
        Assert.Contains("Open your order", answer.Html);
        Assert.Empty(await Reservations(w).OpenRequestsAsync(w.EventId));
    }

    [Fact]
    public async Task More_time_is_asked_with_a_date_and_reason_and_the_deadline_moves_only_when_approved()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12");
        var res = Reservations(w);
        await res.CreateAsync(w.EventId, Resv(company.SponsorCompanyId, null, booth.Id, deadline: new DateOnly(2026, 10, 15)), "org@example.test");
        var order = await w.Db.ShopOrders.SingleAsync(o => o.ReservationRequesterEmail != null);

        Assert.False((await res.RequestExtensionAsync(w.EventId, company.SponsorCompanyId, order.Id, coord.Id, new DateOnly(2026, 10, 1), "x")).Ok);  // earlier
        Assert.False((await res.RequestExtensionAsync(w.EventId, company.SponsorCompanyId, order.Id, coord.Id, new DateOnly(2026, 11, 1), " ")).Ok);  // no reason
        Assert.False((await res.RequestExtensionAsync(w.EventId, "another-company", order.Id, coord.Id, new DateOnly(2026, 11, 1), "x")).Ok);         // not theirs
        w.Mail.Messages.Clear();

        var asked = await res.RequestExtensionAsync(w.EventId, company.SponsorCompanyId, order.Id, coord.Id, new DateOnly(2026, 11, 1), "Budget approval on 28 Oct");

        Assert.True(asked.Ok, asked.Message);
        order = await w.Db.ShopOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(new DateOnly(2026, 10, 15), order.ReservationDeadline);             // unchanged until approved
        Assert.True(order.HasOpenExtensionRequest);
        var notice = Assert.Single(w.Mail.Messages, m => m.To == "ops@example.test");
        Assert.Contains("Budget approval on 28 Oct", notice.Html);
        Assert.Contains("/Organizer/ShopEventReservations#extension-" + order.Id, notice.Html);

        w.Mail.Messages.Clear();
        var approved = await res.DecideExtensionAsync(w.EventId, order.Id, approve: true, deadline: null, note: null);

        Assert.True(approved.Ok, approved.Message);
        order = await w.Db.ShopOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(new DateOnly(2026, 11, 1), order.ReservationDeadline);              // the date asked for
        Assert.False(order.HasOpenExtensionRequest);
        Assert.Contains(w.Mail.Messages, m => m.To == "rita@example.test" && m.Subject.Contains("sign by 1 November 2026"));
    }

    [Fact]
    public async Task A_denied_request_for_more_time_keeps_the_deadline_and_tells_them()
    {
        var w = await NewWorldAsync();
        var (company, _, coord) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12");
        var res = Reservations(w);
        await res.CreateAsync(w.EventId, Resv(company.SponsorCompanyId, null, booth.Id, deadline: new DateOnly(2026, 10, 15)), "org@example.test");
        var order = await w.Db.ShopOrders.SingleAsync(o => o.ReservationRequesterEmail != null);
        await res.RequestExtensionAsync(w.EventId, company.SponsorCompanyId, order.Id, coord.Id, new DateOnly(2026, 11, 1), "Budget");
        w.Mail.Messages.Clear();

        var r = await res.DecideExtensionAsync(w.EventId, order.Id, approve: false, deadline: null, note: "Other sponsors are waiting for this booth.");

        Assert.True(r.Ok, r.Message);
        order = await w.Db.ShopOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(new DateOnly(2026, 10, 15), order.ReservationDeadline);
        var answer = Assert.Single(w.Mail.Messages, m => m.To == coord.Email);
        Assert.Contains("15 October 2026", answer.Html);
        Assert.Contains("Other sponsors are waiting", answer.Html);
    }

    [Fact]
    public async Task Without_a_deadline_the_reservation_mail_has_no_more_time_button_but_can_still_be_cancelled()
    {
        var w = await NewWorldAsync();
        var (company, _, _) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12");
        var res = Reservations(w);
        await res.CreateAsync(w.EventId, Resv(company.SponsorCompanyId, null, booth.Id), "org@example.test");
        var order = await w.Db.ShopOrders.SingleAsync(o => o.ReservationRequesterEmail != null);
        w.Mail.Messages.Clear();

        await res.SendNowAsync(w.EventId, order.Id);

        var mail = Assert.Single(w.Mail.Messages, m => m.To == "rita@example.test");
        Assert.DoesNotContain("Ask for more time", mail.Html);
        Assert.Contains("Cancel reservation", mail.Html);
        Assert.Contains("Review and sign", mail.Html);
    }

    [Fact]
    public async Task A_reservation_holds_the_booth_in_both_shops_and_shows_as_reserved()
    {
        var w = await NewWorldAsync();
        var (company, _, _) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12");

        var r = await Reservations(w).CreateAsync(w.EventId, Resv(company.SponsorCompanyId, null, booth.Id), "org@example.test");

        Assert.True(r.Ok, r.Message);
        Assert.Equal(0, w.Stock[10602].Qty);                                  // taken from CEH's stock too
        Assert.Contains(booth.Id, await ShopReservationService.HeldProductIdsAsync(w.Db, w.EventId, new[] { booth.Id }));
        var order = await w.Db.ShopOrders.SingleAsync(o => o.ReservationRequesterEmail != null);
        Assert.Equal(ShopOrderStatus.Reserved, order.Status);
        Assert.True(order.IsOrganizerReservation);
    }

    /// <summary>
    /// 🔴 §1312 — operator 2026-09-30: "i have two reservations for red hat, how i can merge them together into one contract?"
    /// Merging moves the lines into the target with the stock STILL HELD (never back on sale), keeps the earlier deadline,
    /// closes the source as merged without a cancellation mail, and refuses across companies.
    /// </summary>
    [Fact]
    public async Task Two_reservations_of_one_company_merge_into_one_with_the_stock_still_held()
    {
        var w = await NewWorldAsync();
        var (company, _, _) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-17", ShopGroup.Booths, 7500m, 10607, booth: "E-17");
        var session = await ProductAsync(w, "Sponsor Long 60 min Session - timeslot: 4", ShopGroup.Sessions, 8000m, 10640, qty: 1);
        Assert.True((await Reservations(w).CreateAsync(w.EventId,
            Resv(company.SponsorCompanyId, null, booth.Id, deadline: new DateOnly(2026, 10, 15)), "org@example.test")).Ok);
        Assert.True((await Reservations(w).CreateAsync(w.EventId,
            Resv(company.SponsorCompanyId, null, session.Id, deadline: new DateOnly(2026, 10, 10)), "org@example.test")).Ok);
        var (first, second) = (await w.Db.ShopOrders.Where(o => o.ReservationRequesterEmail != null).OrderBy(o => o.Id).ToListAsync()) switch
        { var l => (l[0], l[1]) };
        var mailsBefore = w.Mail.Messages.Count;

        var r = await Reservations(w).MergeAsync(w.EventId, second.Id, first.Id, "org@example.test");

        Assert.True(r.Ok, r.Message);
        var target = await w.Db.ShopOrders.Include(o => o.Lines).SingleAsync(o => o.Id == first.Id);
        Assert.Equal(2, target.Lines.Count);                                          // booth + session on ONE order
        Assert.All(target.Lines, l => Assert.True(l.StockTaken));                      // still held …
        Assert.Equal(0, w.Stock[10607].Qty);                                     // … and not back on sale
        Assert.Equal(0, w.Stock[10640].Qty);
        Assert.Equal(new DateOnly(2026, 10, 10), target.ReservationDeadline);          // the earlier deadline
        var source = await w.Db.ShopOrders.Include(o => o.Lines).SingleAsync(o => o.Id == second.Id);
        Assert.Equal(ShopOrderStatus.Cancelled, source.Status);
        Assert.Empty(source.Lines);
        Assert.Contains("Merged into " + first.Number, source.CancelReason);
        Assert.Equal(mailsBefore, w.Mail.Messages.Count);                             // no cancellation mail

        // Refused: the source is closed now, and across companies it never works.
        Assert.False((await Reservations(w).MergeAsync(w.EventId, second.Id, first.Id, "org@example.test")).Ok);
    }

    [Fact]
    public async Task A_new_company_is_created_once_as_a_candidate()
    {
        var w = await NewWorldAsync();
        var a = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12");
        var b = await ProductAsync(w, "Gold Exhibitor - Booth E-13", ShopGroup.Booths, 7500m, 10603, booth: "E-13");

        Assert.True((await Reservations(w).CreateAsync(w.EventId, Resv(null, "Red Hat", a.Id), "org@example.test")).Ok);
        Assert.True((await Reservations(w).CreateAsync(w.EventId, Resv(null, "Red Hat", b.Id), "org@example.test")).Ok);

        var redHat = await w.Db.ShopCompanies.Where(c => c.LegalName == "Red Hat").ToListAsync();
        Assert.Single(redHat);
        Assert.Equal(ShopCompanyState.Candidate, redHat[0].State);
    }

    [Fact]
    public async Task A_booth_that_is_already_gone_is_not_reserved_and_the_attempt_is_kept()
    {
        var w = await NewWorldAsync();
        var (company, _, _) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, qty: 0, booth: "E-12");

        var r = await Reservations(w).CreateAsync(w.EventId, Resv(company.SponsorCompanyId, null, booth.Id), "org@example.test");

        Assert.False(r.Ok);
        var kept = await w.Db.ShopOrders.SingleAsync(o => o.ReservationRequesterEmail != null);   // never deleted
        Assert.Equal(ShopOrderStatus.Cancelled, kept.Status);
        Assert.Contains("could not be made", kept.CancelReason);
    }

    [Theory]
    [InlineData(1, new[] { 0, 7, 14 })]      // weekly
    [InlineData(2, new[] { 0, 14 })]         // every other week
    [InlineData(0, new int[0])]              // off
    public async Task The_requester_is_reminded_on_the_reservations_own_cadence(int weeks, int[] expectedDays)
    {
        var w = await NewWorldAsync();
        var (company, _, _) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12");
        var svc = Reservations(w);
        Assert.True((await svc.CreateAsync(w.EventId, Resv(company.SponsorCompanyId, null, booth.Id, weeks), "org@example.test")).Ok);
        var start = new DateOnly(2026, 10, 1);

        var sentOn = new List<int>();
        for (var d = 0; d <= 20; d++)
        {
            var before = w.Mail.Sent.Count(m => m.To == "rita@example.test");
            await svc.SendDueAsync(w.EventId, start.AddDays(d));
            await svc.SendDueAsync(w.EventId, start.AddDays(d));   // a second run the same day sends nothing more
            if (w.Mail.Sent.Count(m => m.To == "rita@example.test") > before) sentOn.Add(d);
        }

        Assert.Equal(expectedDays, sentOn.ToArray());
    }

    [Fact]
    public async Task No_reminder_before_the_chosen_date_and_the_mail_shows_what_is_reserved_and_the_deadline()
    {
        var w = await NewWorldAsync();
        var (company, _, _) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12");
        var svc = Reservations(w);
        Assert.True((await svc.CreateAsync(w.EventId,
            Resv(company.SponsorCompanyId, null, booth.Id, from: new DateOnly(2026, 10, 10), deadline: new DateOnly(2026, 11, 30)),
            "org@example.test")).Ok);

        await svc.SendDueAsync(w.EventId, new DateOnly(2026, 10, 9));
        Assert.DoesNotContain(w.Mail.Messages, m => m.To == "rita@example.test");

        await svc.SendDueAsync(w.EventId, new DateOnly(2026, 10, 10));
        var mail = Assert.Single(w.Mail.Messages, m => m.To == "rita@example.test");
        Assert.Contains("Gold Exhibitor - Booth E-12", mail.Html);
        Assert.Contains("30 November 2026", mail.Html);
    }

    [Fact]
    public async Task After_the_deadline_the_requester_is_left_alone_and_the_organizers_are_told_once()
    {
        var w = await NewWorldAsync();
        var (company, _, _) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12");
        var svc = Reservations(w);
        Assert.True((await svc.CreateAsync(w.EventId,
            Resv(company.SponsorCompanyId, null, booth.Id, deadline: new DateOnly(2026, 10, 5)), "org@example.test")).Ok);

        await svc.SendDueAsync(w.EventId, new DateOnly(2026, 10, 6));
        await svc.SendDueAsync(w.EventId, new DateOnly(2026, 10, 13));

        Assert.DoesNotContain(w.Mail.Sent, m => m.To == "rita@example.test");
        Assert.Single(w.Mail.Sent, m => m.To == "ops@example.test" && m.Subject.Contains("deadline passed"));
        Assert.Equal(0, w.Stock[10602].Qty);   // still held — the organizers decide
    }

    [Fact]
    public async Task A_reservation_is_not_also_chased_by_the_daily_cart_reminder_and_releasing_gives_the_booth_back()
    {
        var w = await NewWorldAsync();
        var (company, _, _) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12");
        Assert.True((await Reservations(w).CreateAsync(w.EventId, Resv(company.SponsorCompanyId, null, booth.Id), "org@example.test")).Ok);

        Assert.Empty(await w.Mailer.BuildCartRemindersAsync(w.EventId, new DateOnly(2026, 10, 1), default));

        var order = (await w.Orders.GetAnyAsync(w.EventId, (await w.Db.ShopOrders.SingleAsync()).Id))!;
        Assert.True((await w.Orders.CancelAsync(order, "org@example.test", "released")).Ok);
        Assert.Equal(1, w.Stock[10602].Qty);
        Assert.Empty(await ShopReservationService.HeldProductIdsAsync(w.Db, w.EventId, new[] { booth.Id }));
    }

    [Fact]
    public async Task A_sponsor_adds_their_cart_to_a_reservation_so_one_order_is_signed_and_nothing_is_lost()
    {
        var w = await NewWorldAsync();
        var (company, signer, _) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12");
        var tv = await ProductAsync(w, "TV rental", ShopGroup.BoothOptions, 450m, 10700, qty: 5);
        Assert.True((await Reservations(w).CreateAsync(w.EventId, Resv(company.SponsorCompanyId, null, booth.Id), "org@example.test")).Ok);
        Assert.True((await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, tv.Id, 2, signer.Id)).Ok);

        var reservation = (await w.Orders.GetAnyAsync(w.EventId, (await w.Db.ShopOrders.SingleAsync(o => o.ReservationRequesterEmail != null)).Id))!;
        var r = await w.Orders.MergeCartIntoAsync(reservation);

        Assert.True(r.Ok, r.Message);
        w.Db.ChangeTracker.Clear();   // read back what was SAVED, not what EF holds in memory
        var lines = await w.Db.ShopOrderLines.AsNoTracking().Where(l => l.OrderId == reservation.Id).ToListAsync();
        Assert.Equal(2, lines.Count);                               // the booth + the TV line — none orphaned/deleted
        Assert.All(lines, l => Assert.True(l.StockTaken));
        Assert.Equal(3, w.Stock[10700].Qty);                   // the TV stock is taken now
        Assert.False(await w.Db.ShopOrderLines.AnyAsync(l => l.Order.Status == ShopOrderStatus.Cart));
    }

    [Fact]
    public async Task Adding_a_cart_whose_item_is_gone_moves_nothing_and_gives_back_what_it_took()
    {
        var w = await NewWorldAsync();
        var (company, signer, _) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, booth: "E-12");
        var tv = await ProductAsync(w, "TV rental", ShopGroup.BoothOptions, 450m, 10700, qty: 5);
        var chair = await ProductAsync(w, "Chair", ShopGroup.Furniture, 50m, 10701, qty: 1);
        Assert.True((await Reservations(w).CreateAsync(w.EventId, Resv(company.SponsorCompanyId, null, booth.Id), "org@example.test")).Ok);
        Assert.True((await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, tv.Id, 1, signer.Id)).Ok);
        Assert.True((await w.Orders.AddAsync(w.EventId, company.SponsorCompanyId, chair.Id, 1, signer.Id)).Ok);
        w.Stock[10701] = (true, 0);   // someone else bought the last chair meanwhile

        var reservation = (await w.Orders.GetAnyAsync(w.EventId, (await w.Db.ShopOrders.SingleAsync(o => o.ReservationRequesterEmail != null)).Id))!;
        Assert.False((await w.Orders.MergeCartIntoAsync(reservation)).Ok);

        w.Db.ChangeTracker.Clear();
        Assert.Single(await w.Db.ShopOrderLines.Where(l => l.OrderId == reservation.Id).ToListAsync());   // only the booth
        Assert.Equal(2, await w.Db.ShopOrderLines.CountAsync(l => l.Order.Status == ShopOrderStatus.Cart)); // cart untouched
        Assert.Equal(5, w.Stock[10700].Qty);                                                          // TV given back
    }

    [Fact]
    public async Task The_requester_gets_signer_access_without_becoming_a_coordinator_across_companies_but_never_for_another_role()
    {
        var w = await NewWorldAsync();
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-17", ShopGroup.Booths, 7500m, 10607, booth: "E-17");
        var b2 = await ProductAsync(w, "Gold Exhibitor - Booth E-18", ShopGroup.Booths, 7500m, 10608, booth: "E-18");
        var svc = Reservations(w);
        Assert.True((await svc.CreateAsync(w.EventId, Resv(null, "Red Hat", booth.Id), "org@example.test")).Ok);
        var redHat = await w.Db.ShopOrders.SingleAsync(o => o.ReservationRequesterEmail != null);

        var r = await svc.GrantRequesterAccessAsync(w.EventId, redHat.Id);

        Assert.True(r.Ok, r.Message);
        var person = await w.Db.Participants.SingleAsync(p => p.Email == "rita@example.test");
        Assert.Equal(ParticipantRole.Sponsor, person.Role);
        Assert.Equal(redHat.SponsorCompanyId, person.SponsorCompanyId);
        Assert.False(person.IsEventCoordinator);                     // the sponsor welcome goes to coordinators only
        var link = await w.Db.SponsorCompanyContacts.SingleAsync(c => c.ParticipantId == person.Id);
        Assert.True(link.IsSigner);
        Assert.False(link.IsEventCoordinator);
        Assert.Equal(person.Id, (await w.Db.ShopOrders.FindAsync(redHat.Id))!.SignerParticipantId);

        // The same person as requester for ANOTHER company is linked to that one too (one CFO, several companies) —
        // one participant, two company links, signer in both.
        Assert.True((await svc.CreateAsync(w.EventId, Resv(null, "Egiss", b2.Id), "org@example.test")).Ok);
        var egiss = await w.Db.ShopOrders.SingleAsync(o => o.ReservationRequesterEmail != null && o.Id != redHat.Id);
        Assert.True((await svc.GrantRequesterAccessAsync(w.EventId, egiss.Id)).Ok);
        Assert.Single(await w.Db.Participants.Where(p => p.Email == "rita@example.test").ToListAsync());
        Assert.Equal(2, await w.Db.SponsorCompanyContacts.CountAsync(c => c.ParticipantId == person.Id && c.IsSigner));

        // An address that belongs to someone in another ROLE is refused.
        w.Db.Participants.Add(new Participant { EventId = w.EventId, Email = "org@example.test", FullName = "Org", Role = ParticipantRole.Organizer, IsActive = true });
        var b3 = await ProductAsync(w, "Gold Exhibitor - Booth E-19", ShopGroup.Booths, 7500m, 10609, booth: "E-19");
        Assert.True((await svc.CreateAsync(w.EventId, Resv(null, "Other", b3.Id) with { RequesterEmail = "org@example.test" }, "org@example.test")).Ok);
        var other = await w.Db.ShopOrders.SingleAsync(o => o.ReservationRequesterEmail == "org@example.test");
        Assert.False((await svc.GrantRequesterAccessAsync(w.EventId, other.Id)).Ok);
    }

    [Fact]
    public async Task A_reservation_for_stock_already_held_takes_nothing_but_gives_it_back_on_release()
    {
        // The booth's unit is already held elsewhere (stock is already 0) — the reservation records that it holds it.
        var w = await NewWorldAsync();
        var (company, _, _) = await CompanyAsync(w);
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-12", ShopGroup.Booths, 7500m, 10602, qty: 0, booth: "E-12");

        var r = await Reservations(w).CreateAsync(w.EventId,
            Resv(company.SponsorCompanyId, null, booth.Id) with { StockAlreadyHeld = true }, "org@example.test");

        Assert.True(r.Ok, r.Message);
        Assert.Equal(0, w.Stock[10602].Qty);                                       // nothing taken twice
        var order = (await w.Orders.GetAnyAsync(w.EventId, (await w.Db.ShopOrders.SingleAsync()).Id))!;
        Assert.Equal(ShopOrderStatus.Reserved, order.Status);
        Assert.True(order.Lines.Single().StockTaken);                                   // it holds it …
        Assert.True((await w.Orders.CancelAsync(order, "org@example.test", "released")).Ok);
        Assert.Equal(1, w.Stock[10602].Qty);                                       // … so a release gives it back
    }

    [Fact]
    public async Task A_reservation_gives_the_requester_sign_in_by_default_and_sends_no_mail()
    {
        var w = await NewWorldAsync();
        var booth = await ProductAsync(w, "Gold Exhibitor - Booth E-17", ShopGroup.Booths, 7500m, 10607, booth: "E-17");

        var r = await Reservations(w).CreateAsync(w.EventId, Resv(null, "Red Hat", booth.Id), "org@example.test");

        Assert.True(r.Ok, r.Message);
        Assert.Contains("can sign in", r.Message);
        var person = await w.Db.Participants.SingleAsync(p => p.Email == "rita@example.test");
        Assert.True((await w.Db.SponsorCompanyContacts.SingleAsync(c => c.ParticipantId == person.Id)).IsSigner);
        Assert.False((await w.Db.SponsorInfos.IgnoreQueryFilters().SingleAsync(s => s.SponsorCompanyId == person.SponsorCompanyId)).IsSponsor);
        // Nothing to the requester or any sponsor (reminders off, no welcome). The ONLY mail is the organizers' own
        // sold-out notice, because this test booth had one unit left (§1305 sold-out mail to organizer.notifyEmails).
        Assert.DoesNotContain(w.Mail.Sent, m => m.To != "ops@example.test");
        Assert.All(w.Mail.Sent, m => Assert.Contains("Reserved — no longer available", m.Subject));   // §1312: a reservation, not a sale
    }
}