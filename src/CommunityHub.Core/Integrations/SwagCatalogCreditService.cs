using System.Security.Cryptography;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>What a grant attempt produced.</summary>
/// <param name="Credit">The stored credit, when it worked.</param>
/// <param name="Error">Why it did not, in words an organizer can act on.</param>
public sealed record GrantCreditResult(SwagCatalogCredit? Credit, string? Error)
{
    public bool Ok => Credit is not null;
}

/// <summary>
/// §1165k — grant a sponsor a euro credit for the swag catalogue.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-01: <i>"add option to provide a coupon/discount code i provide them with a
/// euro value"</i>, and — asked who creates it — <b>CEH, through the Woo API</b>.</para>
///
/// <para>🔴 §1312 — <b>the credit is CEH's alone</b> (the old webshop is closed; operator 2026-10-01: "ceh ows the webshop
/// functionality 100%"). It used to be created as a webshop coupon first; now the CEH shop spends it on swag items when the
/// sponsor orders (<see cref="Webshop.ShopOrderService"/> pricing) and gives it back if the order is cancelled.</para>
/// </remarks>
public sealed class SwagCatalogCreditService
{
    private readonly CommunityHubDbContext _db;
    private readonly SponsorConfigLoader _configLoader;
    private readonly SponsorConfigOptions _configOptions;
    private readonly ILogger<SwagCatalogCreditService> _log;

    public SwagCatalogCreditService(
        CommunityHubDbContext db,
        SponsorConfigLoader configLoader,
        SponsorConfigOptions configOptions,
        ILogger<SwagCatalogCreditService> log)
    {
        _db = db;
        _configLoader = configLoader;
        _configOptions = configOptions;
        _log = log;
    }

    /// <summary>
    /// Build a coupon code that is readable aloud and not guessable.
    /// </summary>
    /// <remarks>
    /// <para>🔑 Both properties matter. It gets read out in meetings and typed off a screen, so it
    /// avoids the characters people confuse (0/O, 1/I/L) — and it carries randomness, because a
    /// predictable code (company name + round number) is one somebody else can simply try.</para>
    /// </remarks>
    public static string NewCode(string editionCode, string companyName)
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);

        var suffix = string.Concat(bytes.ToArray().Select(b => alphabet[b % alphabet.Length]));

        var slug = new string((companyName ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Take(10)
            .ToArray())
            .ToLowerInvariant();

        var prefix = string.IsNullOrWhiteSpace(editionCode) ? "swag" : editionCode.ToLowerInvariant();
        return string.IsNullOrWhiteSpace(slug)
            ? $"{prefix}-swag-{suffix}"
            : $"{prefix}-swag-{slug}-{suffix}";
    }

    /// <summary>
    /// Record the credit in CEH (§1312 — no webshop coupon any more; the CEH shop spends it on swag items).
    ///
    /// </summary>
    /// <param name="restrictToEmails">
    /// The sponsor's own contact addresses. ⚠️ Strongly recommended: without them a forwarded code
    /// is spendable by anyone. Passing none is allowed, and is a decision the caller is making.
    /// </param>
    public async Task<GrantCreditResult> GrantAsync(
        int eventId,
        string sponsorCompanyId,
        string companyName,
        decimal amount,
        DateOnly? expiresOn,
        string grantedByEmail,
        string? note,
        IReadOnlyCollection<string>? restrictToEmails,
        string editionCode,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sponsorCompanyId))
            return new(null, "No sponsor company was given.");
        if (amount <= 0)
            return new(null, "A credit must be worth more than zero.");

        // 🔒 The category restriction is NOT optional. A credit that is not tied to the catalogue is
        // spendable on a booth, a session slot or anything else in the shop — which is not what was
        // agreed with the sponsor, and is discovered only when the invoice is short.
        string? category;
        try
        {
            category = _configLoader.Load(_configOptions.SponsorConfigPath).SwagCatalog?.CategoryName?.Trim();
        }
        catch (FileNotFoundException)
        {
            return new(null, "The sponsor config could not be read, so the catalogue category is unknown.");
        }

        if (string.IsNullOrWhiteSpace(category))
        {
            return new(null,
                "No swag-catalogue category is configured (sponsor.<edition>.json → "
                + "swagCatalog.categoryName). A credit cannot be limited to catalogue items until it "
                + "is, and an unrestricted credit is spendable on anything in the shop.");
        }

        // 🔴 §1312 — the credit lives in CEH only (the old webshop is closed, so no webshop coupon is created): the CEH shop
        // spends it on swag items alone when the sponsor orders (ShopOrderService pricing), and gives it back on a cancel.
        var code = NewCode(editionCode, companyName);

        var credit = new SwagCatalogCredit
        {
            EventId = eventId,
            SponsorCompanyId = sponsorCompanyId,
            CompanyName = companyName,
            Code = code,
            Amount = amount,
            WooCouponId = null,   // §1312 — CEH-only credit
            ExpiresOn = expiresOn,
            CreatedByEmail = grantedByEmail,
            Note = note,
        };

        _db.SwagCatalogCredits.Add(credit);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "§1165k: credit {Code} ({Amount}) granted to {Company} by {By}.",
            code, amount, companyName, grantedByEmail);

        return new(credit, null);
    }

    /// <summary>Every live credit for a company — what its catalogue page shows.</summary>
    public async Task<IReadOnlyList<SwagCatalogCredit>> LiveForCompanyAsync(
        int eventId, string sponsorCompanyId, DateOnly today, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sponsorCompanyId)) return Array.Empty<SwagCatalogCredit>();

        var all = await _db.SwagCatalogCredits
            .Where(c => c.EventId == eventId && c.SponsorCompanyId == sponsorCompanyId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

        // Filtered in memory: IsLiveOn carries the rule, and a second copy of it in a LINQ predicate
        // is how the page and the organizer view would come to disagree about who has a credit.
        return all.Where(c => c.IsLiveOn(today)).ToList();
    }
}
