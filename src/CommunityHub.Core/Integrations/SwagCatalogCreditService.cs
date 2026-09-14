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
/// <para>🔴 <b>THE ORDER OF THE TWO WRITES IS THE DESIGN.</b> The coupon is created in the webshop
/// FIRST, and only a confirmed coupon is recorded in CEH. The other order is far worse than it
/// looks: a stored credit whose coupon does not exist tells a sponsor they have €500 and then fails
/// in front of them at checkout, which costs a conversation and their confidence. The failure this
/// way round is a coupon in the shop that CEH does not list — invisible, harmless, and it cannot
/// spend anything, because it is restricted to that sponsor's own e-mail addresses.</para>
///
/// <para>🔒 <b>CEH never decides that a discount is valid.</b> The shop applies it at checkout. A bug
/// here can misreport a credit; it can never give money away.</para>
/// </remarks>
public sealed class SwagCatalogCreditService
{
    private readonly CommunityHubDbContext _db;
    private readonly WooCommerceClient _woo;
    private readonly SponsorConfigLoader _configLoader;
    private readonly SponsorConfigOptions _configOptions;
    private readonly ILogger<SwagCatalogCreditService> _log;

    public SwagCatalogCreditService(
        CommunityHubDbContext db,
        WooCommerceClient woo,
        SponsorConfigLoader configLoader,
        SponsorConfigOptions configOptions,
        ILogger<SwagCatalogCreditService> log)
    {
        _db = db;
        _woo = woo;
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
    /// Create the coupon in the webshop and record the credit. Nothing is stored unless the shop
    /// confirmed the coupon.
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

        long? categoryId;
        try
        {
            categoryId = await _woo.FindProductCategoryIdAsync(category!, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "§1165k: could not resolve the catalogue category id.");
            return new(null, "The webshop could not be reached to resolve the catalogue category.");
        }

        if (categoryId is null)
        {
            return new(null,
                $"The webshop has no product category called '{category}', so the credit cannot be "
                + "limited to catalogue items. Nothing was created.");
        }

        var code = NewCode(editionCode, companyName);

        // ⚠️ The shop first. See the class remarks: a stored credit whose coupon does not exist
        // fails in front of the sponsor at checkout.
        WooCommerceClient.WooCouponResult created;
        try
        {
            created = await _woo.CreateCouponAsync(
                code, amount, categoryId, expiresOn, restrictToEmails,
                description: $"Swag catalogue credit for {companyName} (granted in CEH by {grantedByEmail}).",
                ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "§1165k: coupon create threw for {Company}.", sponsorCompanyId);
            return new(null, $"The webshop could not be reached to create the coupon: {ex.Message}");
        }

        if (!created.Ok)
        {
            return new(null, $"The webshop refused the coupon: {created.Error}");
        }

        var credit = new SwagCatalogCredit
        {
            EventId = eventId,
            SponsorCompanyId = sponsorCompanyId,
            CompanyName = companyName,
            Code = code,
            Amount = amount,
            WooCouponId = created.Id,
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
