using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// Read-only e-conomic ROLE source settings (REQUIREMENTS §7c). The actual
/// e-conomic credentials + endpoint live on <see cref="EconomicErpOptions"/>
/// (secret VALUES from Key Vault, base URL from operator config); this section
/// only carries the opt-in flag. ADDITIVE + OFF by default: with
/// <see cref="Enabled"/> = false (or the e-conomic tokens blank) the role client
/// is disabled and returns NO data, so the sponsor-email audience falls back to
/// the Company Manager single default coordinator — existing behaviour unchanged.
/// </summary>
public sealed class EconomicRolesOptions
{
    public const string SectionName = "EconomicRoles";

    /// <summary>
    /// Opt-in switch for resolving the sponsor-email coordinator audience from
    /// e-conomic contact role data. Default false. Even when true the client is
    /// inert unless the e-conomic app/grant tokens (on
    /// <see cref="EconomicErpOptions"/>) are configured.
    /// </summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// The set of e-conomic ERP roles held by one contact, keyed by contact email.
/// Roles follow the operator convention encoded in the contact's <c>notes</c>
/// field (<c>Type:N;Role:1,2</c>): Role 1 = Signer, Role 2 = Event Coordinator
/// (a contact can hold both; multiple Role-2 contacts per company are valid).
/// </summary>
public sealed record EconomicContactRoles(string Email, IReadOnlyCollection<int> RoleIds)
{
    public bool IsSigner => RoleIds.Contains(EconomicRoleClient.SignerRoleId);
    public bool IsEventCoordinator => RoleIds.Contains(EconomicRoleClient.EventCoordinatorRoleId);
}

/// <summary>
/// Read-only source of e-conomic contact role data for one ERP customer
/// (REQUIREMENTS §7c). Given a company's <c>erp_customer_number</c> it returns
/// the role-id set per contact email, parsed from each contact's <c>notes</c>
/// field. STRICTLY READ-ONLY — it only ever GETs from e-conomic.
///
/// Two implementations: a <see cref="NullEconomicRoleClient"/> (disabled — the
/// default, returns nothing) and a live <see cref="EconomicRoleClient"/>. The
/// resolver treats a null/empty result as "ERP roles unavailable" and falls back
/// to the Company Manager single default coordinator, so the sponsor-email
/// feature works whether or not e-conomic roles are wired.
/// </summary>
public interface IEconomicRoleClient
{
    /// <summary>
    /// Whether this client can actually read e-conomic role data (false for the
    /// Null client, and false for the live client until it is enabled + the
    /// e-conomic tokens are configured). When false the resolver skips it.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Read every contact of the given e-conomic customer and return their parsed
    /// roles, keyed by contact email. <b>Fail-soft:</b> returns an empty list on
    /// any error, when disabled, or when the customer number is blank — it NEVER
    /// throws and NEVER writes.
    /// </summary>
    Task<IReadOnlyList<EconomicContactRoles>> GetContactRolesAsync(
        string erpCustomerNumber, CancellationToken ct = default);
}

/// <summary>Disabled no-op role source — returns nothing, makes no call. The default.</summary>
public sealed class NullEconomicRoleClient : IEconomicRoleClient
{
    public bool IsEnabled => false;

    public Task<IReadOnlyList<EconomicContactRoles>> GetContactRolesAsync(
        string erpCustomerNumber, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<EconomicContactRoles>>(Array.Empty<EconomicContactRoles>());
}

/// <summary>
/// Live, READ-ONLY e-conomic role client. GETs the customer's contacts
/// (following <c>pagination.nextPage</c>) and parses each contact's <c>notes</c>
/// field for <c>Role:</c> values. Auth is the e-conomic
/// <c>X-AppSecretToken</c> + <c>X-AgreementGrantToken</c> headers (resolved from
/// Key Vault by name onto <see cref="EconomicErpOptions"/>). It performs ONLY
/// GETs — never a POST/PUT/DELETE — and is fail-soft: any error returns an empty
/// result rather than throwing. Mirrors the operator's external
/// <c>Sync-ERP-Contacts-to-Webshop.ps1</c> role logic (READ side only).
/// </summary>
public sealed class EconomicRoleClient : IEconomicRoleClient
{
    /// <summary>Company Manager / e-conomic role id for a contract signer.</summary>
    public const int SignerRoleId = 1;

    /// <summary>Company Manager / e-conomic role id for an event coordinator.</summary>
    public const int EventCoordinatorRoleId = 2;

    /// <summary>Default e-conomic REST base used when the operator leaves ApiBaseUrl blank.</summary>
    private const string DefaultEconomicApiBaseUrl = "https://restapi.e-conomic.com";

    // Captures the numbers after "Role:" in a contact notes field, e.g.
    // "Type:1;Role:1,2" -> "1,2". Mirrors the operator script's regex.
    private static readonly Regex RoleNotesRegex =
        new(@"Role:([0-9,\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly EconomicRolesOptions _roleOptions;
    private readonly EconomicErpOptions _erpOptions;
    private readonly ILogger<EconomicRoleClient> _log;

    public EconomicRoleClient(
        HttpClient http,
        EconomicRolesOptions roleOptions,
        EconomicErpOptions erpOptions,
        ILogger<EconomicRoleClient> log)
    {
        _http = http;
        _roleOptions = roleOptions;
        _erpOptions = erpOptions;
        _log = log;

        // e-conomic auth headers (READ-ONLY use). Only set when present; an
        // unconfigured client is simply disabled (IsEnabled == false) and never
        // makes a call.
        if (!string.IsNullOrWhiteSpace(_erpOptions.AppSecretToken))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-AppSecretToken", _erpOptions.AppSecretToken);
        }
        if (!string.IsNullOrWhiteSpace(_erpOptions.AgreementGrantToken))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-AgreementGrantToken", _erpOptions.AgreementGrantToken);
        }
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>
    /// Enabled only when opted in AND both e-conomic tokens are configured. With
    /// either missing the client makes no call and the resolver falls back to the
    /// Company Manager default coordinator.
    /// </summary>
    public bool IsEnabled =>
        _roleOptions.Enabled
        && !string.IsNullOrWhiteSpace(_erpOptions.AppSecretToken)
        && !string.IsNullOrWhiteSpace(_erpOptions.AgreementGrantToken);

    public async Task<IReadOnlyList<EconomicContactRoles>> GetContactRolesAsync(
        string erpCustomerNumber, CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(erpCustomerNumber))
        {
            return Array.Empty<EconomicContactRoles>();
        }

        try
        {
            var baseUrl = string.IsNullOrWhiteSpace(_erpOptions.ApiBaseUrl)
                ? DefaultEconomicApiBaseUrl
                : _erpOptions.ApiBaseUrl.TrimEnd('/');

            // e-conomic: GET /customers/{number}/contacts returns a paged
            // { collection: [...], pagination: { nextPage } } envelope.
            var url = $"{baseUrl}/customers/{Uri.EscapeDataString(erpCustomerNumber.Trim())}/contacts?pagesize=1000";

            var byEmail = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            var pagesFetched = 0;
            const int maxPages = 1000; // safety stop against a misbehaving nextPage loop

            while (!string.IsNullOrWhiteSpace(url) && pagesFetched < maxPages)
            {
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogWarning(
                        "e-conomic role read for customer {Customer} returned {Status}; falling back (no ERP roles).",
                        erpCustomerNumber, (int)resp.StatusCode);
                    return Array.Empty<EconomicContactRoles>();
                }

                var page = await resp.Content.ReadFromJsonAsync<EconomicContactsPage>(cancellationToken: ct);
                pagesFetched++;

                if (page?.Collection is { Count: > 0 })
                {
                    foreach (var contact in page.Collection)
                    {
                        var email = contact.Email?.Trim();
                        if (string.IsNullOrWhiteSpace(email)) continue; // no email -> cannot match a participant

                        var roles = ParseRoles(contact.Notes);
                        if (!byEmail.TryGetValue(email, out var set))
                        {
                            set = new HashSet<int>();
                            byEmail[email] = set;
                        }
                        foreach (var r in roles) set.Add(r);
                    }
                }

                url = page?.Pagination?.NextPage;
            }

            return byEmail
                .Select(kv => new EconomicContactRoles(kv.Key, kv.Value.ToArray()))
                .ToList();
        }
        catch (Exception ex)
        {
            // Fail-soft: any error (network, auth, parse) -> no ERP roles, never throw.
            _log.LogWarning(ex,
                "e-conomic role read for customer {Customer} failed; falling back (no ERP roles).",
                erpCustomerNumber);
            return Array.Empty<EconomicContactRoles>();
        }
    }

    /// <summary>
    /// Parse the role ids out of a contact's <c>notes</c> field
    /// (<c>Type:N;Role:1,2</c> → {1,2}). Tolerates spaces and any separator
    /// style; returns an empty set when no <c>Role:</c> segment is present.
    /// Public + static so it is unit-testable without an HTTP client.
    /// </summary>
    public static IReadOnlyCollection<int> ParseRoles(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return Array.Empty<int>();

        var match = RoleNotesRegex.Match(notes);
        if (!match.Success) return Array.Empty<int>();

        var roles = new HashSet<int>();
        foreach (var token in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token.Trim(), out var id)) roles.Add(id);
        }
        return roles;
    }

    // --- e-conomic contacts page envelope (only the fields we read) ----------

    private sealed record EconomicContactsPage(
        [property: System.Text.Json.Serialization.JsonPropertyName("collection")]
        IReadOnlyList<EconomicContact>? Collection,
        [property: System.Text.Json.Serialization.JsonPropertyName("pagination")]
        EconomicPagination? Pagination);

    private sealed record EconomicContact(
        [property: System.Text.Json.Serialization.JsonPropertyName("email")] string? Email,
        [property: System.Text.Json.Serialization.JsonPropertyName("notes")] string? Notes);

    private sealed record EconomicPagination(
        [property: System.Text.Json.Serialization.JsonPropertyName("nextPage")] string? NextPage);
}
