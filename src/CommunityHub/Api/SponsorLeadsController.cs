using System.Globalization;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations.Sponsors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Api;

/// <summary>
/// Per-sponsor leads download API.
///
/// Two endpoints, both authenticated by a per-sponsor API key
/// (header <c>X-Sponsor-Api-Key</c> OR query string <c>?key=</c> -- the
/// query-string form is provided so a sponsor can paste the URL into a
/// browser address bar and have the file land in their Downloads folder
/// without configuring headers):
///
///   GET /api/v1/sponsors/{sponsorCompanyId}/leads.json[?key=&lt;k&gt;]
///       Returns all leads for the sponsor as a JSON array. Each row
///       carries the Zoho columns PLUS:
///         EventName        = "Experts Live Denmark 2027"
///         OrganizerName    = "Experts Live Denmark"
///       so a sponsor importing several editions' feeds into their own
///       CRM can disambiguate without rebuilding the source.
///
///   GET /api/v1/sponsors/{sponsorCompanyId}/leads.csv[?key=&lt;k&gt;]
///       Same data as the JSON endpoint, formatted as RFC 4180 CSV with
///       Content-Disposition: attachment so browser-click drops the file
///       into the user's Downloads folder.
///
/// SCAFFOLD STATE: the endpoints validate the API key correctly + emit
/// proper CSV / JSON shape, but return an EMPTY list of leads until the
/// Zoho pipeline lands. That keeps the contract stable for sponsors who
/// wire up their PowerShell scripts now -- when leads start flowing,
/// the same scripts pick them up with zero changes.
/// </summary>
[ApiController]
[Route("api/v1/sponsors/{sponsorCompanyId}")]
public sealed class SponsorLeadsController : ControllerBase
{
    private readonly CommunityHubDbContext _db;
    private readonly ISponsorApiKeyService _keys;

    public SponsorLeadsController(CommunityHubDbContext db, ISponsorApiKeyService keys)
    {
        _db = db;
        _keys = keys;
    }

    // Column set surfaced to sponsors. When the Zoho pull lands, expand
    // this with whatever Zoho fields we sync; the EventName + OrganizerName
    // columns stay at the end so existing sponsor importers don't shift.
    private static readonly string[] CsvColumns =
    {
        "LeadId",
        "LeadKind",
        "FullName",
        "FirstName",
        "LastName",
        "Email",
        "Phone",
        "Company",
        "JobTitle",
        "City",
        "Country",
        "Source",
        "Notes",
        "CapturedAt",
        "EventName",
        "OrganizerName",
    };

    private const string EventName     = "Experts Live Denmark 2027";
    private const string OrganizerName = "Experts Live Denmark";

    [HttpGet("leads.json")]
    public async Task<IActionResult> GetJsonAsync(
        string sponsorCompanyId,
        [FromQuery(Name = "key")] string? queryKey,
        CancellationToken ct)
    {
        if (!await AuthAsync(sponsorCompanyId, queryKey, ct)) return Unauthorized();
        var rows = await GetLeadsAsync(sponsorCompanyId, ct);
        return new JsonResult(new { sponsor = sponsorCompanyId, count = rows.Count, leads = rows });
    }

    /// <summary>
    /// Filename-in-path variant: <c>GET /api/v1/sponsors/{cid}/leads/leads-2026-06-11.csv</c>
    /// (or any *.csv name the caller chooses). The route segment is purely
    /// cosmetic -- the filename in the URL controls what wget / curl /
    /// browser saves the file as. The actual content + auth are identical
    /// to the canonical <c>leads.csv</c> endpoint. Useful for sponsors
    /// whose download tooling derives the saved name from the URL path
    /// rather than the Content-Disposition header (older PowerShell,
    /// some shell wrappers, etc).
    /// </summary>
    [HttpGet("leads/{fileName}.csv")]
    public Task<IActionResult> GetCsvNamedAsync(
        string sponsorCompanyId,
        string fileName,
        [FromQuery(Name = "key")] string? queryKey,
        CancellationToken ct)
    {
        // Delegate to the canonical handler; the {fileName} part is just
        // for the caller's benefit + audit logs.
        return GetCsvAsync(sponsorCompanyId, queryKey, ct);
    }

    [HttpGet("leads.csv")]
    public async Task<IActionResult> GetCsvAsync(
        string sponsorCompanyId,
        [FromQuery(Name = "key")] string? queryKey,
        CancellationToken ct)
    {
        if (!await AuthAsync(sponsorCompanyId, queryKey, ct)) return Unauthorized();
        var rows = await GetLeadsAsync(sponsorCompanyId, ct);

        var sb = new StringBuilder();
        sb.Append(string.Join(",", CsvColumns)).Append("\r\n");
        foreach (var r in rows)
        {
            sb.Append(string.Join(",", new[]
            {
                Csv(r.LeadId), Csv(r.LeadKind), Csv(r.FullName), Csv(r.FirstName), Csv(r.LastName),
                Csv(r.Email), Csv(r.Phone), Csv(r.Company), Csv(r.JobTitle), Csv(r.City), Csv(r.Country),
                Csv(r.Source), Csv(r.Notes),
                Csv(r.CapturedAt.ToString("u", CultureInfo.InvariantCulture)),
                Csv(EventName), Csv(OrganizerName)
            }));
            sb.Append("\r\n");
        }

        var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fileName = $"leads-{sponsorCompanyId}-{stamp}.csv";
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", fileName);
    }

    // ---- internals ---------------------------------------------------

    private async Task<bool> AuthAsync(string sponsorCompanyId, string? queryKey, CancellationToken ct)
    {
        // Three accepted ways to present the key, in precedence order
        // (most-secure first):
        //   1. `Authorization: Bearer <key>` -- the standard. Works with
        //      every HTTP client + tool out of the box; the form a
        //      typical API consumer reaches for first.
        //   2. `X-Sponsor-Api-Key: <key>` -- legacy custom header kept
        //      working so sponsors who've already wired this up don't
        //      have to change anything.
        //   3. `?key=<key>` query string -- browser-friendly form for
        //      the direct-download CSV URL (when a non-technical sponsor
        //      pastes the URL into the address bar). Discouraged for
        //      automated scripts because URLs end up in access logs +
        //      browser history.
        string? raw = null;
        if (Request.Headers.TryGetValue("Authorization", out var authHeader) && authHeader.Count > 0)
        {
            var v = authHeader[0];
            if (!string.IsNullOrWhiteSpace(v) && v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                raw = v.Substring("Bearer ".Length).Trim();
        }
        if (string.IsNullOrWhiteSpace(raw) && Request.Headers.TryGetValue("X-Sponsor-Api-Key", out var customHeader) && customHeader.Count > 0)
        {
            raw = customHeader[0];
        }
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = queryKey;
        }
        if (string.IsNullOrWhiteSpace(raw)) return false;

        // Bind the validate call to the active edition. The hub is
        // single-event-at-a-time today (the active EventId is derivable
        // from configuration); pick the first non-archived Event row.
        // When the multi-edition model lands, swap this for a tenant-
        // aware lookup.
        var eventId = await _db.Events
            .OrderByDescending(e => e.Id)
            .Select(e => e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId == 0) return false;

        return await _keys.ValidateAsync(eventId, sponsorCompanyId, raw, ct);
    }

    private Task<List<LeadRow>> GetLeadsAsync(string sponsorCompanyId, CancellationToken ct)
    {
        // SCAFFOLD: empty list until Zoho pipeline lands + DbSet<SponsorLead> exists.
        // When the persistence + sync are wired, this becomes:
        //
        //     return await _db.SponsorLeads
        //         .Where(l => l.SponsorCompanyId == sponsorCompanyId && l.EventId == eventId)
        //         .OrderByDescending(l => l.CapturedAt)
        //         .Select(l => new LeadRow(...))
        //         .ToListAsync(ct);
        return Task.FromResult(new List<LeadRow>());
    }

    private static string Csv(string? value)
    {
        if (value is null) return string.Empty;
        var needsQuote = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
        if (!needsQuote) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Row shape returned by both endpoints (JSON + CSV).</summary>
    public sealed record LeadRow(
        string LeadId,
        string LeadKind,
        string FullName,
        string FirstName,
        string LastName,
        string Email,
        string Phone,
        string Company,
        string JobTitle,
        string City,
        string Country,
        string Source,
        string Notes,
        DateTimeOffset CapturedAt);
}
