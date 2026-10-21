using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CommunityHub.Core.Integrations.Erp;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>The outcome of checking a foreign VAT number.</summary>
/// <param name="Valid">True = registered and valid; false = the register says it is NOT valid; null = not checked / unknown.</param>
/// <param name="Note">What to store and show the organizer ("VIES: valid", "VIES unavailable", …).</param>
public sealed record VatCheckResult(bool? Valid, string Note, string? RegisteredName = null);

/// <summary>
/// §1305 item 1 — checks an EU (non-Danish) VAT number in the EU's VIES register, as the retired registration webhook
/// did (it used a paid VIES front-end; this calls the Commission's free REST endpoint directly).
/// </summary>
/// <remarks>
/// <para>🔒 Fails OPEN, visibly: VIES is often slow or down for a member state. An unreachable register does not stop a
/// registration — it is recorded on the company and in the organizer notice, so a human checks before invoicing. A
/// register that answers "invalid" DOES stop it, as the webhook did.</para>
/// <para>Countries outside the EU (Norway, Switzerland, the UK, the US …) are not in VIES and are recorded as
/// "not checked". Denmark is checked by <see cref="ICvrValidator"/> instead.</para>
/// <para>Read-only (a GET), so it is not an external WRITE and is not gated.</para>
/// </remarks>
public sealed class ShopVatChecker
{
    private readonly HttpClient _http;

    public ShopVatChecker(HttpClient http)
    {
        _http = http;
        if (_http.Timeout > TimeSpan.FromSeconds(15)) _http.Timeout = TimeSpan.FromSeconds(15);
    }

    private sealed record ViesResponse(
        [property: JsonPropertyName("isValid")] bool IsValid,
        [property: JsonPropertyName("userError")] string? UserError = null,
        [property: JsonPropertyName("name")] string? Name = null);

    /// <summary>
    /// §1312 — an EU country covered by VIES (not Denmark, which is checked by checksum): registration needs a GREEN check
    /// first (operator 2026-09-27: "a verify vat number button in the registration, which is a prereq of a green verification
    /// before registration can complete for european companies covered by the service … not relevant for fx usa companies").
    /// </summary>
    public static bool NeedsVies(string? countryIso2, string? vatNumber = null)
    {
        var iso = (countryIso2 ?? string.Empty).Trim().ToUpperInvariant();
        return (iso != "DK" && VatZoneMapper.Resolve(iso) == VatZone.Eu) || IsNorthernIrish(iso, vatNumber);
    }

    /// <summary>
    /// §1312 — Northern Ireland: the company's country is the United Kingdom (its address, VAT zone and invoicing stay UK),
    /// but a VAT number written with the <c>XI</c> prefix is registered in VIES under XI and is checked there (operator
    /// 2026-09-27: Northern Ireland missing at registration). "XI" is a VIES prefix, never an ISO country, so it is not added
    /// to the country list.
    /// </summary>
    public static bool IsNorthernIrish(string? countryIso2, string? vatNumber) =>
        string.Equals((countryIso2 ?? string.Empty).Trim(), "GB", StringComparison.OrdinalIgnoreCase)
        && new string((vatNumber ?? string.Empty).Where(char.IsLetterOrDigit).ToArray())
            .StartsWith("XI", StringComparison.OrdinalIgnoreCase);

    public async Task<VatCheckResult> CheckAsync(string countryIso2, string? vatNumber, CancellationToken ct = default)
    {
        var iso = (countryIso2 ?? string.Empty).Trim().ToUpperInvariant();
        // 🔒 Denmark: CHECKSUM ONLY, never VIES. Measured 2026-09-27 on the 58 known companies: 7 real Danish sponsors
        // (e.g. Keepit, Fellowmind, Robopack) are "NOT valid" in VIES — typically a joint VAT registration holds the VAT
        // number, not the company's own CVR — and Danish customers are charged Danish VAT, so there is no refund risk.
        if (iso == "DK") return new(null, "Danish CVR — checked by checksum.");
        var northernIrish = IsNorthernIrish(iso, vatNumber);
        if (!northernIrish && VatZoneMapper.Resolve(iso) != VatZone.Eu) return new(null, "Outside the EU — VAT number not checked (no VIES).");

        var number = LiveEconomicErpClient.NormaliseCvr(vatNumber);
        if (string.IsNullOrWhiteSpace(number)) return new(false, "No VAT number given.");
        var viesCountry = northernIrish ? "XI" : iso == "GR" ? "EL" : iso;   // VIES uses EL for Greece, XI for Northern Ireland

        try
        {
            var r = await _http.GetFromJsonAsync<ViesResponse>(
                $"https://ec.europa.eu/taxation_customs/vies/rest-api/ms/{viesCountry}/vat/{Uri.EscapeDataString(number)}", ct);
            if (r is null) return new(null, "VIES gave no answer — check the VAT number by hand.");
            if (r.IsValid)
                return new(true, $"VIES: valid ({viesCountry}{number}).",
                    string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim() == "---" ? null : r.Name.Trim());
            // §1312 — VIES answers isValid=false ALSO when a member state's own service is down (MS_UNAVAILABLE, TIMEOUT,
            // …_MAX_CONCURRENT_REQ): that is "not answering", never "not valid". Measured 2026-09-27 on a Finnish number
            // the operator had verified by hand. Only a plain invalid answer is "NOT valid".
            if (!string.IsNullOrWhiteSpace(r.UserError) && r.UserError is not ("VALID" or "INVALID" or "INVALID_INPUT"))
                return new(null, $"VIES unavailable for {viesCountry} ({r.UserError}) — try again in a little while.");
            return new(false, $"VIES: NOT valid ({viesCountry}{number}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new(null, "VIES was unavailable — check the VAT number by hand before invoicing.");
        }
    }
}
