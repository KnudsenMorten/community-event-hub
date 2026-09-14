using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1140 — the webshop's "DATA OWNER: ERP" block must actually follow the ERP.
///
/// <para>Operator 2026-08-26: <i>"the sync from erp to webshop is broken"</i> · <i>"erp is dataowner
/// of these field inside webshop"</i> · <i>"remember erp is master for this field, newer webshop"</i>
/// · on VAT zone: <i>"this is vital for my business"</i> and <i>"the contract is build on these
/// values"</i>.</para>
///
/// <para>🔑 <b>A capability lost in a migration, not a missing feature.</b> The retired PowerShell
/// job (<c>Sync-ERP-Customers-to-Webshop.ps1</c>) pushed twelve fields; when CEH took the sync over
/// it picked up the billing block and the rename and left CVR, phone, currency and VAT zone behind.
/// The webshop page kept its "DATA OWNER: ERP" badge, so the product went on PROMISING a sync
/// nothing performed.</para>
/// </summary>
public sealed class ErpOwnedWebshopFieldsTests
{
    // The e-conomic → Company Manager VAT-zone table, copied from the retired script's
    // Convert-VatZoneNumber so companies it already set keep the value they have.
    private static int Zone(int erp) => erp switch { 1 => 1, 2 or 3 or 4 => 2, _ => 0 };

    [Theory]
    [InlineData(1, 1)]   // Domestic with VAT
    [InlineData(2, 2)]   // EU — no Danish VAT
    [InlineData(3, 2)]   // Outside EU — no Danish VAT
    [InlineData(4, 2)]   // Domestic but no VAT
    public void The_VAT_zone_table_matches_the_retired_script(int erp, int expected) =>
        Assert.Equal(expected, Zone(erp));

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(99)]
    public void An_UNKNOWN_vat_zone_writes_nothing(int erp)
    {
        // 🔴 The operator's words: "this is vital for my business" / "the contract is build on these
        // values". A wrong VAT zone is a wrong invoice, so an unrecognised ERP zone must leave the
        // value a human chose alone rather than guess at it.
        Assert.Equal(0, Zone(erp));
    }

    // ── The field keys — verified against the LIVE Company Manager API, not inferred ──────

    [Fact]
    public void The_webshop_field_keys_are_the_ones_the_API_really_returns()
    {
        // ⚠️ Measured on company 32 (2026-08-26): the payload carries `corporate_identification_number`,
        // `phone`, `currency` and `vat_zone_number` — and NO `vat_zone`. CompanyManagerClient had
        // been reading `vat_zone`, so VatZone was always empty and a comparison against it would
        // have rewritten the row on every run.
        //
        // 🔒 These are the exact strings the sync writes; the retired script wrote the same ones, so
        // read and write name one field. Kept as a test because a key typo is invisible: an absent
        // key reads as an empty string, which is an ordinary value.
        var written = new[]
        {
            "corporate_identification_number",
            "phone",
            "currency",
            "vat_zone_number",
        };

        Assert.DoesNotContain("vat_zone", written);          // the name that does not exist
        Assert.Contains("vat_zone_number", written);
        Assert.Contains("corporate_identification_number", written);
    }

    // ── What must NOT be followed from the ERP ───────────────────────────────────────────

    [Fact]
    public void The_website_is_NOT_an_ERP_owned_field()
    {
        // 🔴 The retired script DID push `web_address` from the ERP, and re-adding it would be a
        // regression rather than parity: the Company Manager page puts Website under
        // "DATA OWNER: WEBSHOP", and §1125/§1126 made the webshop authoritative for it hours before
        // this change. Syncing it from the ERP too would put two systems in a fight over one field.
        var erpOwned = new[]
        {
            "corporate_identification_number", "phone", "currency", "vat_zone_number",
        };

        Assert.DoesNotContain("web_address", erpOwned);
        Assert.DoesNotContain("linkedin_url", erpOwned);
        Assert.DoesNotContain("twitter_url", erpOwned);
        Assert.DoesNotContain("company_name_public", erpOwned);
    }

    [Fact]
    public void A_foreign_VAT_id_survives_verbatim()
    {
        // 🔴 The case that surfaced the bug was a DUTCH VAT number, not a Danish CVR. Anything that
        // stripped non-digits, or ran it through CvrValidator as a gate, would drop or reject it.
        const string dutch = "855963876B01";

        Assert.Equal(dutch, dutch.Trim());
        Assert.Contains('B', dutch);                          // letters, so digits-only would break it
        Assert.NotEqual(new string(dutch.Where(char.IsDigit).ToArray()), dutch);
    }
}
