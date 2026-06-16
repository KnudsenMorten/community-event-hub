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
///
/// ◻ LIVE WIRING NOT COMPLETE. A real rate lookup needs an operator endpoint
/// that is NOT available in this repo. Until configured, <see cref="CanQuote"/>
/// is false and the order currency check runs as a known-currency gate only.
/// The quote method throws if called while disabled — it must never fabricate a
/// rate.
/// </summary>
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

    public Task<decimal?> GetRateAsync(string baseCurrency, string quoteCurrency, CancellationToken ct)
    {
        if (!CanQuote)
        {
            throw new InvalidOperationException("FxRateProvider is not configured (CanQuote is false).");
        }
        if (string.Equals(baseCurrency, quoteCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<decimal?>(1m);
        }
        throw new NotImplementedException(
            "Live FX rate lookup is not wired yet (◻ — needs verified endpoint).");
    }
}
