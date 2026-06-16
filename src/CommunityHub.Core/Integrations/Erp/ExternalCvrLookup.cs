using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// External CVR registry lookup settings. Secret VALUES live in Key Vault; this
/// binds the secret NAME + non-secret endpoint only. Disabled by default — the
/// CVR validator then applies the offline format + checksum gate only.
/// </summary>
public sealed class ExternalCvrLookupOptions
{
    public const string SectionName = "CvrLookup";

    public bool Enabled { get; set; }

    /// <summary>CVR register API base (non-secret). e.g. a Virk/CVR data endpoint.</summary>
    public string ApiBaseUrl { get; set; } = string.Empty;

    /// <summary>API key (resolved from Key Vault by name).</summary>
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>
/// Live external CVR register lookup.
///
/// ◻ LIVE WIRING NOT COMPLETE. A real register lookup needs an operator
/// endpoint + key that are NOT available in this repo. Until configured,
/// <see cref="CanLookup"/> is false and <see cref="ICvrValidator"/> validates
/// with the offline format + modulus-11 gate only. The lookup method throws if
/// called while disabled — it must never fabricate a registry response.
/// </summary>
public sealed class ExternalCvrLookup : IExternalCvrLookup
{
    private readonly HttpClient _http;
    private readonly ExternalCvrLookupOptions _options;
    private readonly ILogger<ExternalCvrLookup> _log;

    public ExternalCvrLookup(
        HttpClient http,
        ExternalCvrLookupOptions options,
        ILogger<ExternalCvrLookup> log)
    {
        _http = http;
        _options = options;
        _log = log;
    }

    public bool CanLookup =>
        _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.ApiBaseUrl)
        && !string.IsNullOrWhiteSpace(_options.ApiKey);

    public Task<bool> ExistsAndActiveAsync(string normalizedCvr, CancellationToken ct)
    {
        if (!CanLookup)
        {
            throw new InvalidOperationException(
                "ExternalCvrLookup is not configured (CanLookup is false).");
        }
        throw new NotImplementedException(
            "Live CVR register lookup is not wired yet (◻ — needs verified endpoint + key).");
    }
}
