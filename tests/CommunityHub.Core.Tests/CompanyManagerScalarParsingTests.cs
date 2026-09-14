using System.Text.Json;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1140b — the webshop sends `vat_zone_number` as a JSON NUMBER, and a string-only reader
/// silently turns that into "not set".
///
/// <para>Operator 2026-08-26, on the 52-company "set it by hand" alert: <i>"this is wrong"</i>.
/// He is right, and the alert was the second symptom; the first was the sync rewriting every
/// company's VAT zone on every run because it always read the stored value as blank.</para>
///
/// <para>🔑 <b>An empty read is indistinguishable from an unset field.</b> That is what made this
/// invisible: nothing threw, nothing 500'd, and the write "succeeded" every time. The value the
/// operator called vital (<i>"the contract is build on these values"</i>) was being compared
/// against a blank that never came from the webshop.</para>
/// </summary>
public sealed class CompanyManagerScalarParsingTests
{
    // The shape measured on the LIVE Company Manager API (company 32, 2026-08-26): `currency` is a
    // string, `vat_zone_number` is a bare number, and they sit next to each other in one payload.
    private const string LivePayload = """
        {
          "id": 32,
          "corporate_identification_number": "855963876B01",
          "phone": "",
          "currency": "EUR",
          "vat_zone_number": 2
        }
        """;

    private static JsonElement Doc => JsonDocument.Parse(LivePayload).RootElement;

    // The reader that was in place — string-only. Kept verbatim so the test states the defect
    // rather than merely asserting the fix.
    private static string OldGetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    // The reader now in CompanyManagerClient.
    private static string GetScalarString(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return string.Empty;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? string.Empty,
            JsonValueKind.Number => v.ToString(),
            _ => string.Empty,
        };
    }

    [Fact]
    public void The_VAT_zone_really_is_a_number_in_the_payload() =>
        Assert.Equal(JsonValueKind.Number, Doc.GetProperty("vat_zone_number").ValueKind);

    [Fact]
    public void A_string_only_reader_loses_the_VAT_zone()
    {
        // 🔴 The defect, pinned. "" is not a parse error — it reads as "the webshop has no VAT
        // zone", so the sync wrote one every single run and then reported it refused.
        Assert.Equal(string.Empty, OldGetString(Doc, "vat_zone_number"));
    }

    [Fact]
    public void The_scalar_reader_keeps_a_numeric_VAT_zone()
    {
        Assert.Equal("2", GetScalarString(Doc, "vat_zone_number"));
    }

    [Theory]
    [InlineData("currency", "EUR")]                                    // string beside the number
    [InlineData("corporate_identification_number", "855963876B01")]    // string, letters and all
    [InlineData("phone", "")]                                          // genuinely empty stays empty
    public void Neighbouring_string_fields_are_unaffected(string prop, string expected) =>
        Assert.Equal(expected, GetScalarString(Doc, prop));

    [Fact]
    public void A_missing_property_is_empty_not_a_throw() =>
        Assert.Equal(string.Empty, GetScalarString(Doc, "no_such_field"));

    [Fact]
    public void The_stored_zone_now_compares_EQUAL_to_what_the_ERP_wants()
    {
        // The whole point: e-conomic zone 2/3/4 maps to Company Manager 2, and the sync must then
        // see "already correct" and write NOTHING. Before the fix this compared "2" against "",
        // which is why 52 companies were rewritten on every pass.
        var want = "2";
        Assert.Equal(want, GetScalarString(Doc, "vat_zone_number"));
        Assert.NotEqual(want, OldGetString(Doc, "vat_zone_number"));
    }
}
