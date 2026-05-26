using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// TESTMODE implementation of the Backstage exhibitor API. Performs NO real
/// Zoho calls. It treats the configured test sponsor as "already exists" and
/// everyone else as "missing" so the sync's create-and-notify path can be
/// exercised end to end without touching Zoho. <see cref="CanCreate"/> is
/// false, so the sync records WouldCreate rather than Created.
/// </summary>
public sealed class TestModeBackstageExhibitorApi : IBackstageExhibitorApi
{
    private readonly TestModeOptions _testMode;
    private readonly ILogger<TestModeBackstageExhibitorApi> _log;

    public TestModeBackstageExhibitorApi(
        TestModeOptions testMode,
        ILogger<TestModeBackstageExhibitorApi> log)
    {
        _testMode = testMode;
        _log = log;
    }

    public bool CanCreate => false;

    public Task<bool> ExistsAsync(
        ExhibitorRecord exhibitor, CancellationToken ct)
    {
        var exists = string.Equals(
            exhibitor.CompanyName,
            _testMode.TestSponsorName,
            StringComparison.OrdinalIgnoreCase);

        _log.LogInformation(
            "[TESTMODE] Backstage exists-check for '{Company}' -> {Exists}",
            exhibitor.CompanyName, exists);
        return Task.FromResult(exists);
    }

    public Task CreateAsync(ExhibitorRecord exhibitor, CancellationToken ct)
    {
        _log.LogWarning(
            "[TESTMODE] CreateAsync called but TESTMODE cannot create.");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Settings for the live Zoho Backstage exhibitor API. The portal and event
/// ids are reused from <see cref="ZohoOptions"/>; only the booth category is
/// specific to exhibitor creation.
/// </summary>
public sealed class BackstageExhibitorOptions
{
    public const string SectionName = "BackstageExhibitor";

    /// <summary>
    /// The Backstage booth category id a synced exhibitor is filed under
    /// (required by the Add-exhibitor-request API). Get this id from the
    /// event's booth categories in Backstage.
    /// </summary>
    public string DefaultBoothCategoryId { get; set; } = string.Empty;

    /// <summary>Language code sent with the request.</summary>
    public string Language { get; set; } = "en";
}

/// <summary>
/// Live Zoho Backstage exhibitor API (verified against the Backstage v3 REST
/// docs). It uses the Exhibitor Requests endpoint:
///   POST /backstage/v3/portals/{portal_id}/events/{event_id}/exhibitor_requests
/// which submits an exhibitor application (it is created with a pending
/// status; an organizer approves it in Backstage). OAuth scope required:
/// zohobackstage.exhibitor.CREATE - it must be included in the Zoho refresh
/// token's granted scopes.
///
/// HONEST NOTE on <see cref="ExistsAsync"/>: the Backstage "get exhibitor
/// request" endpoint looks a request up by its system id, not by company
/// name, so there is no documented "find by company" call. Until a list
/// endpoint is wired, ExistsAsync returns false (treat as missing) - the sync
/// then records WouldCreate or creates, and the coordinator email is the
/// backstop against duplicates. This is a known limitation, not a guess.
/// </summary>
public sealed class LiveBackstageExhibitorApi : IBackstageExhibitorApi
{
    private readonly HttpClient _http;
    private readonly ZohoClient _zoho;
    private readonly ZohoOptions _zohoOptions;
    private readonly BackstageExhibitorOptions _options;
    private readonly ILogger<LiveBackstageExhibitorApi> _log;

    public LiveBackstageExhibitorApi(
        HttpClient http,
        ZohoClient zoho,
        ZohoOptions zohoOptions,
        BackstageExhibitorOptions options,
        ILogger<LiveBackstageExhibitorApi> log)
    {
        _http = http;
        _zoho = zoho;
        _zohoOptions = zohoOptions;
        _options = options;
        _log = log;
    }

    /// <summary>
    /// True once a booth category id is configured - creation needs it.
    /// </summary>
    public bool CanCreate =>
        !string.IsNullOrWhiteSpace(_options.DefaultBoothCategoryId)
        && !string.IsNullOrWhiteSpace(_zohoOptions.BackstagePortalId)
        && !string.IsNullOrWhiteSpace(_zohoOptions.BackstageEventId);

    public Task<bool> ExistsAsync(
        ExhibitorRecord exhibitor, CancellationToken ct)
    {
        // The documented "get exhibitor request" endpoint keys on the request
        // id, not company name - there is no documented find-by-company call.
        // Treating the exhibitor as missing is the safe answer: the sync then
        // notifies the coordinator, who is the human check against duplicates.
        _log.LogInformation(
            "Backstage ExistsAsync: no find-by-company endpoint; treating "
            + "'{Company}' as missing (coordinator will be notified).",
            exhibitor.CompanyName);
        return Task.FromResult(false);
    }

    public async Task CreateAsync(
        ExhibitorRecord exhibitor, CancellationToken ct)
    {
        if (!CanCreate)
        {
            throw new InvalidOperationException(
                "Backstage exhibitor creation needs DefaultBoothCategoryId, "
                + "BackstagePortalId and BackstageEventId to be configured.");
        }

        var token = await _zoho.GetAccessTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "Could not obtain a Zoho OAuth access token.");
        }

        // Split the contact name into first / last for the API's data object.
        var (firstName, lastName) = SplitName(exhibitor.ContactEmail, exhibitor);

        var url =
            $"{_zohoOptions.ApiDomain}/backstage/v3/portals/"
            + $"{_zohoOptions.BackstagePortalId}/events/"
            + $"{_zohoOptions.BackstageEventId}/exhibitor_requests";

        // Payload shape per the Backstage v3 Add-exhibitor-request docs.
        var payload = new
        {
            booth_category_id = _options.DefaultBoothCategoryId,
            language = _options.Language,
            data = new
            {
                first_name = firstName,
                last_name = lastName,
                email = exhibitor.ContactEmail ?? string.Empty,
                company_name = exhibitor.CompanyName,
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {token}");

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Backstage exhibitor create failed ({(int)resp.StatusCode}): "
                + body);
        }

        _log.LogInformation(
            "Backstage exhibitor request created for '{Company}'.",
            exhibitor.CompanyName);
    }

    /// <summary>
    /// The API's data object wants first/last name; the hub only carries a
    /// company and a contact email. Derive a best-effort name from the email
    /// local part - the company_name field carries the real identity.
    /// </summary>
    private static (string First, string Last) SplitName(
        string? email, ExhibitorRecord exhibitor)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            return (exhibitor.CompanyName, "Contact");
        }
        var local = email[..email.IndexOf('@')];
        var parts = local.Split('.', '_', '-');
        var first = Capitalize(parts[0]);
        var last = parts.Length > 1 ? Capitalize(parts[^1]) : "Contact";
        return (first, last);
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s)
            ? s
            : char.ToUpperInvariant(s[0]) + s[1..];
}
