using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// FX rate provider settings. Secret VALUES live in Key Vault; this binds the
/// secret NAME + non-secret endpoint only. Disabled by default — the order
/// currency check then runs as a known-currency gate only (no conversion).
/// </summary>
public sealed class FxRateOptions
{
    public const string SectionName = "FxRates";

    public bool Enabled { get; set; }

    /// <summary>FX rates API base (non-secret).</summary>
    public string ApiBaseUrl { get; set; } = string.Empty;

    /// <summary>API key (resolved from Key Vault by name). May be empty for keyless rate sources.</summary>
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>
/// Live FX rate provider (today's rates).
/// </summary>
/// <remarks>
/// <para>§786.3 — WIRED against the endpoint the operator's retired invoicing script has been using
/// in production: <c>onesimpleapi.com/api/exchange_rate</c>, which answers a conversion query with
/// the converted VALUE as plain text. Asking it to convert exactly 1.0 therefore yields the rate.
/// That indirection is the API's shape, not a trick — and it is the same call the script made, which
/// is the whole reason this endpoint is trusted rather than chosen.</para>
///
/// <para>🔒 <b>It still never fabricates a rate.</b> Not configured ⇒ <see cref="CanQuote"/> is false
/// and the caller must not ask; asked anyway ⇒ it throws. A lookup that fails or returns a
/// non-positive number returns <c>null</c>, and <see cref="WebshopDraftInvoiceService"/> then leaves
/// the order UNINVOICED with a named reason. An invented rate would reach a customer as a wrong
/// amount, which is far worse than a missing invoice.</para>
///
/// <para>⚠️ <b>The API key is a SECRET and lives only in Key Vault</b> (<c>FxRates:ApiKey</c>), and it
/// must be present on BOTH web slots and the Functions app or the invoicing job converts nothing in
/// exactly one environment. It is never logged: the failure log below prints the currencies, never
/// the request URL, because the key travels in the query string.</para>
/// </remarks>
public sealed class FxRateProvider : IFxRateProvider
{
    private readonly HttpClient _http;
    private readonly FxRateOptions _options;
    private readonly ILogger<FxRateProvider> _log;

    public FxRateProvider(
        HttpClient http,
        FxRateOptions options,
        ILogger<FxRateProvider> log)
    {
        _http = http;
        _options = options;
        _log = log;
    }

    public bool CanQuote =>
        _options.Enabled && !string.IsNullOrWhiteSpace(_options.ApiBaseUrl);

    public async Task<decimal?> GetRateAsync(
        string baseCurrency, string quoteCurrency, CancellationToken ct)
    {
        if (!CanQuote)
        {
            throw new InvalidOperationException("FxRateProvider is not configured (CanQuote is false).");
        }

        var from = (baseCurrency ?? string.Empty).Trim().ToUpperInvariant();
        var to = (quoteCurrency ?? string.Empty).Trim().ToUpperInvariant();

        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return 1m;
        }

        // Convert exactly 1.0 — the answer IS the rate. See the remarks: this endpoint converts an
        // amount rather than quoting a rate.
        var url = $"{_options.ApiBaseUrl.TrimEnd('/')}/exchange_rate"
                  + $"?token={Uri.EscapeDataString(_options.ApiKey)}"
                  + $"&output=text&from_currency={from}&to_currency={to}&from_value=1";

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            var body = (await resp.Content.ReadAsStringAsync(ct)).Trim();

            // InvariantCulture: "1.1354" read under a Danish culture becomes 11354, which would
            // multiply every converted invoice by ten thousand.
            if (!decimal.TryParse(
                    body, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var rate)
                || rate <= 0m)
            {
                _log.LogWarning(
                    "FX lookup {From}->{To} returned an unusable answer; no rate is available.",
                    from, to);
                return null;
            }

            return rate;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 🔒 The currencies, never the URL — the API key is a query parameter.
            _log.LogWarning(ex, "FX lookup {From}->{To} failed; no rate is available.", from, to);
            return null;
        }
    }
}
