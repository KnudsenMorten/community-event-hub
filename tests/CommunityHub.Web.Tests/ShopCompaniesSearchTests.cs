using CommunityHub.Core.Domain;
using CommunityHub.Pages.Organizer;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>§1312 — the organizer company list is searchable (operator 2026-09-30: "where is the search for sponsor").</summary>
public sealed class ShopCompaniesSearchTests
{
    private static readonly ShopCompany Company = new()
    {
        SponsorCompanyId = "20", LegalName = "SquaredUp Ltd", PublicName = "SquaredUp", ErpCustomerNumber = "341409313", Cvr = "GB123456789",
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("squared")]          // any part of the name, any case
    [InlineData("  SquaredUp ")]
    [InlineData("#20")]              // the company # as shown
    [InlineData("341409313")]        // e-conomic #
    [InlineData("gb1234")]           // CVR / VAT
    public void A_company_is_found_by_name_number_or_vat(string? q) => Assert.True(ShopCompaniesModel.Matches(Company, q));

    [Fact]
    public void Another_company_is_not_listed() => Assert.False(ShopCompaniesModel.Matches(Company, "Keepit"));
}
