using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1165k — a euro credit granted to a sponsor for the swag catalogue.
///
/// <para>Operator 2026-09-01: <i>"add option to provide a coupon/discount code i provide them with a
/// euro value"</i> — and, asked who should create the coupon, <b>CEH through the Woo API</b>.</para>
///
/// <para>🔴 §1312 (2026-10-01): the old webshop is closed, so the credit lives in CEH only — the CEH shop
/// spends it on swag lines when the sponsor orders. No webshop coupon is created any more.</para>
///
/// <para>NO real customer names.</para>
/// </summary>
public sealed class SwagCatalogCreditTests
{
    private static SwagCatalogCredit Credit(
        DateOnly? expires = null, DateTimeOffset? revoked = null) =>
        new()
        {
            EventId = 1,
            SponsorCompanyId = "42",
            CompanyName = "Contoso",
            Code = "eldk27-swag-contoso-ABC234",
            Amount = 500m,
            WooCouponId = 9001,
            ExpiresOn = expires,
            RevokedAt = revoked,
        };

    private static readonly DateOnly Today = new(2026, 9, 2);

    [Fact]
    public void A_granted_credit_with_no_expiry_is_live()
    {
        Assert.True(Credit().IsLiveOn(Today));
    }

    [Fact]
    public void A_credit_is_live_up_to_and_including_its_expiry_day()
    {
        // 🔑 Inclusive on purpose: a sponsor told "valid until the 30th" who is refused ON the 30th
        // has been misled, and it is us who chose the wording.
        Assert.True(Credit(expires: Today).IsLiveOn(Today));
        Assert.False(Credit(expires: Today.AddDays(-1)).IsLiveOn(Today));
    }

    [Fact]
    public void A_revoked_credit_is_not_live_even_before_its_expiry()
    {
        var c = Credit(expires: Today.AddYears(1), revoked: DateTimeOffset.UtcNow);
        Assert.False(c.IsLiveOn(Today));
    }

    // --- the code -------------------------------------------------------------------------

    /// <summary>
    /// 🔑 The code is read aloud in meetings and typed off a screen.
    /// </summary>
    /// <remarks>
    /// So it must avoid the characters people confuse — 0/O and 1/I/L — and it must still carry
    /// randomness, because a predictable code (company name plus a round number) is one somebody
    /// else can simply try.
    /// </remarks>
    [Fact]
    public void The_code_avoids_characters_people_confuse()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = SwagCatalogCreditService.NewCode("ELDK27", "Contoso");
            var suffix = code[(code.LastIndexOf('-') + 1)..];

            Assert.DoesNotContain('0', suffix);
            Assert.DoesNotContain('O', suffix);
            Assert.DoesNotContain('1', suffix);
            Assert.DoesNotContain('I', suffix);
            Assert.DoesNotContain('L', suffix);
        }
    }

    [Fact]
    public void The_code_carries_the_edition_and_the_company_so_it_reads_as_itself()
    {
        var code = SwagCatalogCreditService.NewCode("ELDK27", "Contoso Widgets");

        Assert.StartsWith("eldk27-swag-", code);
        Assert.Contains("contosowid", code);
    }

    /// <summary>
    /// 🔒 Two grants never collide — the code is not derived from the company alone.
    /// </summary>
    /// <remarks>
    /// The credit table has a unique index on the code, so a collision would surface as a failed
    /// grant rather than a silent overwrite. This keeps that from being a routine event.
    /// </remarks>
    [Fact]
    public void Two_codes_for_the_same_company_differ()
    {
        var codes = Enumerable.Range(0, 200)
            .Select(_ => SwagCatalogCreditService.NewCode("ELDK27", "Contoso"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(codes.Count > 190, $"expected near-unique codes, got {codes.Count} distinct of 200");
    }

    [Fact]
    public void A_company_whose_name_has_no_letters_still_gets_a_usable_code()
    {
        // A blank or symbol-only name must not produce a trailing dash or an empty segment.
        var code = SwagCatalogCreditService.NewCode("ELDK27", "***");

        Assert.Equal("eldk27-swag-", code[..12]);
        Assert.True(code.Length > 12);
        Assert.DoesNotContain("--", code);
    }

    [Fact]
    public void No_edition_code_still_produces_a_prefixed_code()
    {
        Assert.StartsWith("swag-swag-", SwagCatalogCreditService.NewCode("", "Contoso"));
    }

    // --- granting -------------------------------------------------------------------------

    private static SwagCatalogCreditService Service(CommunityHub.Core.Data.CommunityHubDbContext db, string? categoryName)
    {
        var json = categoryName is null
            ? "{ \"_schema\": \"test\" }"
            : $"{{ \"swagCatalog\": {{ \"categoryName\": \"{categoryName}\" }} }}";
        var path = Path.Combine(Path.GetTempPath(), $"swag-credit-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return new SwagCatalogCreditService(db, new CommunityHub.Core.Config.SponsorConfigLoader(),
            new CommunityHub.Core.Config.SponsorConfigOptions { SponsorConfigPath = path },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SwagCatalogCreditService>.Instance);
    }

    /// <summary>
    /// 🔴 §1312 — operator 2026-10-01: "ceh ows the webshop functionality 100%". A credit is created in CEH only: the row is
    /// stored with its code and amount and NO webshop coupon, and the service has no way to reach the closed webshop at all
    /// (the CEH shop spends the credit on swag lines and gives it back on a cancel — SponsorShopOrderTests).
    /// </summary>
    [Fact]
    public async Task A_credit_is_created_in_CEH_without_any_webshop_call()
    {
        using var db = CommunityHub.Core.Tests.Scenario.ScenarioFixture.NewDb();

        var r = await Service(db, "Attendee Bag Catalog").GrantAsync(
            1, "ceh-contoso01", "Contoso", 500m, Today.AddDays(30), "org@example.test", "agreed at the call",
            new[] { "buyer@contoso.test" }, "ELDK27");

        Assert.True(r.Ok, r.Error);
        var row = Assert.Single(db.SwagCatalogCredits);
        Assert.Equal(500m, row.Amount);
        Assert.Equal("ceh-contoso01", row.SponsorCompanyId);
        Assert.StartsWith("eldk27-swag-contoso-", row.Code);
        Assert.Null(row.WooCouponId);                                   // no webshop coupon behind it
        Assert.True(row.IsLiveOn(Today));
        Assert.All(typeof(SwagCatalogCreditService).GetConstructors().SelectMany(c => c.GetParameters()), p =>
            Assert.True(p.ParameterType != typeof(HttpClient),
                $"SwagCatalogCreditService takes a {p.ParameterType.Name} — the closed webshop must not be reachable."));
    }

    /// <summary>🔒 Still refused without a configured catalogue category, so a credit is never spendable on anything.</summary>
    [Fact]
    public async Task A_credit_is_refused_and_nothing_stored_when_no_catalog_category_is_configured()
    {
        using var db = CommunityHub.Core.Tests.Scenario.ScenarioFixture.NewDb();

        var r = await Service(db, null).GrantAsync(1, "ceh-contoso01", "Contoso", 500m, null, "org@example.test", null, null, "ELDK27");

        Assert.False(r.Ok);
        Assert.Contains("swagCatalog.categoryName", r.Error);
        Assert.Empty(db.SwagCatalogCredits);
    }
}
