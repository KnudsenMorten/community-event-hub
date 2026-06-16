using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// e-conomic ERP + sponsor-webshop settings. Secret VALUES live in Key Vault;
/// this binds secret NAMES + non-secret endpoints only. With no app/grant token
/// configured the live client reports <see cref="EconomicErpClient.CanWrite"/>
/// = false and performs no writes.
/// </summary>
public sealed class EconomicErpOptions
{
    public const string SectionName = "EconomicErp";

    public bool Enabled { get; set; }

    /// <summary>e-conomic REST API base URL (non-secret; supplied by the operator, blank until wired).</summary>
    public string ApiBaseUrl { get; set; } = string.Empty;

    /// <summary>e-conomic App-Secret-Token header value (resolved from Key Vault by name).</summary>
    public string AppSecretToken { get; set; } = string.Empty;

    /// <summary>e-conomic Agreement-Grant-Token header value (resolved from Key Vault by name).</summary>
    public string AgreementGrantToken { get; set; } = string.Empty;

    /// <summary>Sponsor webshop sync base URL (non-secret).</summary>
    public string WebshopBaseUrl { get; set; } = string.Empty;

    /// <summary>The "home" currency the ERP keeps its books in (ISO 4217). Used by the FX check.</summary>
    public string BaseCurrency { get; set; } = "DKK";
}

/// <summary>
/// TESTMODE implementation of <see cref="IEconomicErpClient"/>. Performs NO real
/// e-conomic / webshop calls. <see cref="CanWrite"/> is false, so the sync
/// services record <see cref="ErpSyncOutcome.WouldCreate"/>. Lets the whole
/// ERP/webshop flow be exercised offline against the interface.
/// </summary>
public sealed class TestModeEconomicErpClient : IEconomicErpClient
{
    private readonly ILogger<TestModeEconomicErpClient> _log;

    public TestModeEconomicErpClient(ILogger<TestModeEconomicErpClient> log) => _log = log;

    public bool CanWrite => false;

    public Task<string?> FindCustomerNumberAsync(ErpCustomer customer, CancellationToken ct)
    {
        _log.LogInformation("[TESTMODE] ERP FindCustomer for '{Company}' -> none.", customer.Name);
        return Task.FromResult<string?>(null);
    }

    public Task<string> CreateCustomerAsync(ErpCustomer customer, CancellationToken ct)
    {
        _log.LogWarning("[TESTMODE] CreateCustomerAsync called but TESTMODE cannot write.");
        throw new InvalidOperationException("TESTMODE cannot create ERP customers (CanWrite is false).");
    }

    public Task UpdateCustomerAsync(string erpCustomerNumber, ErpCustomer customer, CancellationToken ct)
    {
        _log.LogWarning("[TESTMODE] UpdateCustomerAsync called but TESTMODE cannot write.");
        throw new InvalidOperationException("TESTMODE cannot update ERP customers (CanWrite is false).");
    }

    public Task CreateOrUpdateContactAsync(string erpCustomerNumber, ErpContact contact, CancellationToken ct)
    {
        _log.LogWarning("[TESTMODE] CreateOrUpdateContactAsync called but TESTMODE cannot write.");
        throw new InvalidOperationException("TESTMODE cannot write ERP contacts (CanWrite is false).");
    }

    public Task<string> CreateOrderAsync(string erpCustomerNumber, ErpOrder order, CancellationToken ct)
    {
        _log.LogWarning("[TESTMODE] CreateOrderAsync called but TESTMODE cannot write.");
        throw new InvalidOperationException("TESTMODE cannot create ERP orders (CanWrite is false).");
    }
}

/// <summary>
/// Live e-conomic ERP + sponsor-webshop client.
///
/// ◻ LIVE WIRING NOT COMPLETE. The e-conomic REST payload shapes + the sponsor
/// webshop sync endpoint require operator credentials + verified endpoints that
/// are NOT available in this repo (e-conomic App-Secret + Agreement-Grant
/// tokens; the webshop base URL). Until those are configured this client
/// reports <see cref="CanWrite"/> = false and the sync services record
/// <see cref="ErpSyncOutcome.WouldCreate"/>. The write methods therefore throw
/// if called while CanWrite is false — they must never fabricate a call or
/// hard-code an endpoint/secret. Wiring the real HTTP calls is the remaining ◻
/// item (see docs/DESIGN §6 + REQUIREMENTS §7a).
/// </summary>
public sealed class LiveEconomicErpClient : IEconomicErpClient
{
    private readonly HttpClient _http;
    private readonly EconomicErpOptions _options;
    private readonly ILogger<LiveEconomicErpClient> _log;

    public LiveEconomicErpClient(
        HttpClient http,
        EconomicErpOptions options,
        ILogger<LiveEconomicErpClient> log)
    {
        _http = http;
        _options = options;
        _log = log;
    }

    /// <summary>
    /// True once the e-conomic tokens + base URL are configured. (The actual
    /// HTTP payload wiring is still ◻; this gate is the seam that keeps the
    /// services honest until then.)
    /// </summary>
    public bool CanWrite =>
        _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.ApiBaseUrl)
        && !string.IsNullOrWhiteSpace(_options.AppSecretToken)
        && !string.IsNullOrWhiteSpace(_options.AgreementGrantToken);

    public Task<string?> FindCustomerNumberAsync(ErpCustomer customer, CancellationToken ct)
    {
        if (!CanWrite) return Task.FromResult<string?>(null);
        throw new NotImplementedException(
            "Live e-conomic customer lookup is not wired yet (◻ — needs verified REST payloads + credentials).");
    }

    public Task<string> CreateCustomerAsync(ErpCustomer customer, CancellationToken ct) =>
        throw new NotImplementedException(
            "Live e-conomic customer create is not wired yet (◻ — needs verified REST payloads + credentials).");

    public Task UpdateCustomerAsync(string erpCustomerNumber, ErpCustomer customer, CancellationToken ct) =>
        throw new NotImplementedException(
            "Live e-conomic customer update is not wired yet (◻ — needs verified REST payloads + credentials).");

    public Task CreateOrUpdateContactAsync(string erpCustomerNumber, ErpContact contact, CancellationToken ct) =>
        throw new NotImplementedException(
            "Live e-conomic contact + webshop sync is not wired yet (◻ — needs verified REST/webshop payloads + credentials).");

    public Task<string> CreateOrderAsync(string erpCustomerNumber, ErpOrder order, CancellationToken ct) =>
        throw new NotImplementedException(
            "Live e-conomic order create is not wired yet (◻ — needs verified REST payloads + credentials).");
}
