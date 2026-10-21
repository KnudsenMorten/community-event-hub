using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommunityHub.Core.Config;

/// <summary>
/// §1305 — the CEH sponsor webshop's per-edition configuration (<c>config/shop.&lt;edition&gt;.json</c>):
/// pricing rules, the contract's fixed content, and the reservation window.
/// </summary>
/// <remarks>
/// A file of its own rather than a block in <c>event.&lt;edition&gt;.json</c>: the contract's terms and
/// the organizer's bank details are a document, not event settings, and keeping them apart means the
/// edition loader (read by dozens of services) does not change shape for one feature. Real values live
/// only in the private <c>config/</c> folder, which the publish denylist strips.
/// </remarks>
public sealed class ShopConfig
{
    [JsonPropertyName("currencyBase")] public string CurrencyBase { get; set; } = "EUR";
    [JsonPropertyName("vatRatePercent")] public decimal VatRatePercent { get; set; } = 25m;
    [JsonPropertyName("lateSurcharge")] public ShopLateSurcharge LateSurcharge { get; set; } = new();
    [JsonPropertyName("includedFurniture")] public ShopIncludedFurniture IncludedFurniture { get; set; } = new();
    [JsonPropertyName("swagPricing")] public ShopSwagPricing SwagPricing { get; set; } = new();
    [JsonPropertyName("event")] public ShopEventInfo Event { get; set; } = new();
    [JsonPropertyName("organizer")] public ShopOrganizer Organizer { get; set; } = new();
    [JsonPropertyName("payment")] public ShopPayment Payment { get; set; } = new();
    [JsonPropertyName("reservation")] public ShopReservationWindow Reservation { get; set; } = new();
    [JsonPropertyName("boothMap")] public ShopBoothMapConfig BoothMap { get; set; } = new();
    [JsonPropertyName("catalogDeck")] public ShopCatalogDeckConfig CatalogDeck { get; set; } = new();
    /// <summary>
    /// §1312 — the selling line under a package on the Availability page (match → text; the match syntax of a catalogue page:
    /// words, <c>;</c> alternatives, <c>=Exact name</c>; the first key that matches wins). Operator 2026-09-28: "include the key
    /// things about leads under these sessions … make it more selling as managers see this".
    /// </summary>
    [JsonPropertyName("packageHighlights")] public Dictionary<string, string> PackageHighlights { get; set; } = new();
    [JsonPropertyName("contractTerms")] public List<ShopContractArticle> ContractTerms { get; set; } = new();
    [JsonPropertyName("agreementText")] public string AgreementText { get; set; } = string.Empty;
}

public sealed class ShopLateSurcharge
{
    [JsonPropertyName("percent")] public decimal Percent { get; set; }
    [JsonPropertyName("daysBeforeEvent")] public int DaysBeforeEvent { get; set; }
    /// <summary><see cref="Domain.ShopGroup"/> names the surcharge applies to.</summary>
    [JsonPropertyName("groups")] public List<string> Groups { get; set; } = new();
}

/// <summary>
/// §1305 — what a swag sponsor pays. Operator 2026-09-26: <i>"define a backend price with 33% sponsor contribution … and
/// times it with a backend amount (1250) so the sponsors contribution amounts to 33% of the total price. price reflected
/// must be that instead of unit price of fx 11.50 euro"</i> ⇒ price = price per piece × <see cref="PricingQuantity"/> ×
/// <see cref="SponsorContributionPercent"/> %, shown as ONE amount (quantity 1) — the per-piece price is never shown.
/// The percent here is the DEFAULT; a product can carry its own (<c>ShopProduct.SponsorContributionPercent</c>) — ELDK
/// covers the rest. Unset quantity ⇒ the bag quantity; unset percent ⇒ 100.
/// </summary>
public sealed class ShopSwagPricing
{
    [JsonPropertyName("pricingQuantity")] public int? PricingQuantity { get; set; }
    [JsonPropertyName("sponsorContributionPercent")] public decimal? SponsorContributionPercent { get; set; }
}

public sealed class ShopIncludedFurniture
{
    [JsonPropertyName("group")] public string Group { get; set; } = "Furniture";
    /// <summary>Allowance in EUR per booth tier key (platinum/diamond/gold/feature).</summary>
    [JsonPropertyName("allowanceEurByTier")] public Dictionary<string, decimal> AllowanceEurByTier { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("label")] public string Label { get; set; } = "Included booth furniture";

    /// <summary>
    /// §1312 — the old webshop's coupon code for the allowance, <c>{tier}</c> replaced by the booth tier. The e-conomic invoice
    /// names it exactly as the webshop invoice did ("Coupon eldk-gold-booth-included: list … − … discount"). Operator
    /// 2026-09-29: "the invoice does not match the old invoices. it must be 100% the same".
    /// </summary>
    [JsonPropertyName("couponCode")] public string CouponCode { get; set; } = "eldk-{tier}-booth-included";
}

public sealed class ShopEventInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("website")] public string Website { get; set; } = string.Empty;
    /// <summary>
    /// §1312 — the sponsor presentation with more about the event and the products, linked in the offer mail. Operator
    /// 2026-09-27: "in the offer email link to this powerpoint with more details about the event and products". Empty = no
    /// link (e.g. once the hub's sponsor catalogue has its pages and replaces the deck).
    /// </summary>
    [JsonPropertyName("presentationUrl")] public string? PresentationUrl { get; set; }
    [JsonPropertyName("datesText")] public List<string> DatesText { get; set; } = new();
    [JsonPropertyName("venue")] public List<string> Venue { get; set; } = new();
}

public sealed class ShopContact
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
    [JsonPropertyName("phone")] public string Phone { get; set; } = string.Empty;
    /// <summary>
    /// §1305 — the contract owner's own signature picture, a file in the shop's contracts folder in the document
    /// library (never in the repository: the source is mirrored publicly). Printed on every contract.
    /// </summary>
    [JsonPropertyName("signatureImageFileName")] public string? SignatureImageFileName { get; set; }
}

public sealed class ShopOrganizer
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("address")] public List<string> Address { get; set; } = new();
    [JsonPropertyName("cvr")] public string Cvr { get; set; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
    [JsonPropertyName("contractOwner")] public ShopContact ContractOwner { get; set; } = new();
    /// <summary>Who hears about registrations, signed contracts and cancellations.</summary>
    [JsonPropertyName("notifyEmails")] public List<string> NotifyEmails { get; set; } = new();
    /// <summary>
    /// §1312 — ALSO receive every signed contract (the organizer notice with the signed PDF), and nothing else (operator
    /// 2026-09-27: "contracts signed in webshop must also be sent to info@expertslive.dk").
    /// </summary>
    [JsonPropertyName("contractEmails")] public List<string> ContractEmails { get; set; } = new();
    /// <summary>
    /// §1312 — CC'd on every shop mail to a sponsor / requester (offer, contract to signer, signed, cart and reservation
    /// reminders). Operator 2026-09-27: "add the account … to any webshop emails … just add to CC". Empty = no CC.
    /// </summary>
    [JsonPropertyName("copyEmails")] public List<string> CopyEmails { get; set; } = new();
}

public sealed class ShopPayment
{
    [JsonPropertyName("bankName")] public List<string> BankName { get; set; } = new();
    [JsonPropertyName("accountName")] public string AccountName { get; set; } = string.Empty;
    [JsonPropertyName("accountNumber")] public string AccountNumber { get; set; } = string.Empty;
    [JsonPropertyName("iban")] public string Iban { get; set; } = string.Empty;
    [JsonPropertyName("bic")] public string Bic { get; set; } = string.Empty;
    [JsonPropertyName("currencyNote")] public string CurrencyNote { get; set; } = string.Empty;

    /// <summary>The payment block as lines, for the contract's "{{paymentBlock}}" paragraph.</summary>
    public IReadOnlyList<string> Lines() =>
        new List<string> { "Bank:" }
            .Concat(BankName)
            .Concat(new[]
            {
                string.Empty,
                $"Account name: {AccountName}",
                $"Account number: {AccountNumber}",
                $"IBAN: {Iban}",
                $"BIC/SWIFT: {Bic}",
            })
            .ToList();
}

public sealed class ShopReservationWindow
{
    [JsonPropertyName("opensOn")] public string OpensOn { get; set; } = string.Empty;
    [JsonPropertyName("windowDays")] public int WindowDays { get; set; } = 31;
    [JsonPropertyName("reminderEveryDays")] public int ReminderEveryDays { get; set; } = 7;

    /// <summary>The first day reservations are open, or null when not configured.</summary>
    public DateOnly? OpensOnDate => DateOnly.TryParse(OpensOn, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    /// <summary>The last day reservations are open (inclusive), or null when not configured.</summary>
    public DateOnly? ClosesOnDate => OpensOnDate?.AddDays(Math.Max(1, WindowDays) - 1);

    public bool IsOpen(DateOnly today) =>
        OpensOnDate is DateOnly o && ClosesOnDate is DateOnly c && today >= o && today <= c;
}

/// <summary>
/// §1312 — the sponsor PowerPoint the catalogue is imported from (operator 2026-09-27: "import the powerpoint into the catalog
/// including pictures"): a DRIVE-RELATIVE SharePoint folder and the file name. Slides whose title contains one of
/// <see cref="SkipTitlesContaining"/> are not imported (they name who bought what — never on the public catalogue).
/// </summary>
public sealed class ShopCatalogDeckConfig
{
    [JsonPropertyName("folder")] public string Folder { get; set; } = string.Empty;
    [JsonPropertyName("fileName")] public string FileName { get; set; } = string.Empty;
    [JsonPropertyName("skipTitlesContaining")] public List<string> SkipTitlesContaining { get; set; } = new();
    /// <summary>Table columns left out (default: who bought it, and the deck's static prices).</summary>
    [JsonPropertyName("skipColumnsNamed")] public List<string> SkipColumnsNamed { get; set; } = new();
    /// <summary>§1312 — pages not wanted in the catalogue, by exact title (e.g. the deck's cover slide). Their slides are left out.</summary>
    [JsonPropertyName("skipPagesTitled")] public List<string> SkipPagesTitled { get; set; } = new();
    /// <summary>§1312 — whole chapters left out, by exact title (the deck's old "Logistics" steps — operator: "orphaned").</summary>
    [JsonPropertyName("skipChaptersTitled")] public List<string> SkipChaptersTitled { get; set; } = new();
    /// <summary>
    /// §1312 — a deck page's packages when its title alone does not name them (page title → match, <c>;</c> between
    /// alternatives, <c>=Name</c> for one package by its exact name). Operator 2026-09-28: "some are missing links".
    /// </summary>
    [JsonPropertyName("productMatches")] public Dictionary<string, string> ProductMatches { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ShopBoothMapConfig
{
    [JsonPropertyName("imageFileName")] public string ImageFileName { get; set; } = string.Empty;
}

public sealed class ShopContractArticle
{
    [JsonPropertyName("heading")] public string Heading { get; set; } = string.Empty;
    [JsonPropertyName("paragraphs")] public List<string> Paragraphs { get; set; } = new();
}

/// <summary>Where the shop config file lives. Bound from <c>Shop:ConfigPath</c>.</summary>
public sealed class ShopConfigOptions
{
    public const string SectionName = "Shop";

    public string ConfigPath { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "config", "shop.eldk27.json");

    /// <summary>
    /// Per-ENVIRONMENT override of the reservation window's first day (<c>Shop:ReservationOpensOn</c>, yyyy-MM-dd).
    /// Empty ⇒ the config file's date. Operator 2026-09-26: <i>"yes, move the window to today in dev"</i> — the config
    /// file ships to DEV and PROD alike, so rehearsing the reservation flow on DEV needs an app setting, not a file edit.
    /// </summary>
    public string ReservationOpensOn { get; set; } = string.Empty;

    /// <summary>
    /// Per-HOST override of <c>Invoicing:DryRun</c> for the CEH shop's invoices only (<c>Shop:InvoiceDryRun</c>).
    /// Unset ⇒ <c>Invoicing:DryRun</c>. Operator 2026-09-26: <i>"you can create invoice from dev also so we can verify
    /// it works"</i> — set to false on the DEV WEB host only, so the organizer's button creates a real draft while the
    /// hourly job and every other invoicing path stay dry.
    /// </summary>
    public bool? InvoiceDryRun { get; set; }
}

/// <summary>Reads <see cref="ShopConfig"/>, caching per path + last-write time.</summary>
public sealed class ShopConfigLoader
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _gate = new();
    private (string Path, DateTime Stamp, ShopConfig Config)? _cache;

    /// <summary>
    /// The config, or an EMPTY one when the file is missing — callers show "not configured" rather
    /// than crash. ⚠️ A contract must never be built from an empty config; see <see cref="IsUsable"/>.
    /// </summary>
    public ShopConfig Load(string path)
    {
        if (!File.Exists(path)) return new ShopConfig();
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (_gate)
        {
            if (_cache is { } c && c.Path == path && c.Stamp == stamp) return c.Config;
        }
        var cfg = JsonSerializer.Deserialize<ShopConfig>(File.ReadAllText(path), Json) ?? new ShopConfig();
        lock (_gate) { _cache = (path, stamp, cfg); }
        return cfg;
    }

    /// <summary>True when the file carries the minimum a contract needs (organizer + terms).</summary>
    public static bool IsUsable(ShopConfig c) =>
        !string.IsNullOrWhiteSpace(c.Organizer.Name) && c.ContractTerms.Count > 0;

    /// <summary>Parse from a string — used by tests to prove the real file binds.</summary>
    public static ShopConfig Parse(string json) =>
        JsonSerializer.Deserialize<ShopConfig>(json, Json) ?? new ShopConfig();
}
