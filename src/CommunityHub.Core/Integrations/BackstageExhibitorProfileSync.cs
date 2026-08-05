using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// Pushes a sponsor's company profile (overview + short description) into the
/// Zoho Backstage exhibitor record when the sponsor edits it in the hub, so the
/// organizers no longer copy-paste it manually. The Backstage exhibitor is
/// matched by <c>company_name</c> (the hub's resolved company display name vs the
/// exhibitor's company name).
///
/// Gated by <see cref="ZohoOptions.Enabled"/> + a configured portal/event, and
/// FAIL-SOFT: any Zoho hiccup (auth, scope, no match, HTTP error) is logged and
/// swallowed so it never blocks the in-hub save. Requires the
/// <c>ZohoBackstage.exhibitor.READ</c> + <c>ZohoBackstage.exhibitor.UPDATE</c>
/// scopes on the Zoho refresh token.
/// </summary>
public sealed class BackstageExhibitorProfileSync
{
    private readonly ZohoClient _zoho;
    private readonly ZohoOptions _options;
    private readonly ILogger<BackstageExhibitorProfileSync> _log;

    // RULE (operator 2026-07-23): every CEH-made Zoho write must notify info@expertslive.dk
    // (the operator must publish/delete manually in Backstage). Optional so tests/legacy
    // constructions keep compiling; null ⇒ no notification.
    private readonly Email.ZohoChangeNotifier? _zohoChanges;

    public BackstageExhibitorProfileSync(
        ZohoClient zoho, ZohoOptions options, ILogger<BackstageExhibitorProfileSync> log,
        Email.ZohoChangeNotifier? zohoChanges = null)
    {
        _zoho = zoho;
        _options = options;
        _log = log;
        _zohoChanges = zohoChanges;
    }

    /// <summary>
    /// Sync one company's overview/short-description to its Backstage exhibitor.
    /// Never throws.
    /// </summary>
    /// <returns>True only if the Backstage exhibitor profile was actually updated.</returns>
    public async Task<bool> SyncAsync(
        string companyName, string? companyOverview, string? companyShortDescription,
        CancellationToken ct = default,
        string? contactFirstName = null, string? contactLastName = null)
    {
        try
        {
            if (!_options.Enabled
                || string.IsNullOrWhiteSpace(_options.BackstagePortalId)
                || string.IsNullOrWhiteSpace(_options.BackstageEventId))
            {
                _log.LogInformation(
                    "Backstage exhibitor sync skipped (Zoho disabled/unconfigured) for '{Co}'.", companyName);
                return false;
            }
            if (string.IsNullOrWhiteSpace(companyName)) return false;

            var token = await _zoho.GetAccessTokenAsync(ct);
            if (string.IsNullOrWhiteSpace(token))
            {
                _log.LogWarning("Backstage exhibitor sync: no Zoho access token for '{Co}'.", companyName);
                return false;
            }

            var exhibitors = await _zoho.GetExhibitorsAsync(token!, ct);
            var match = exhibitors.FirstOrDefault(e =>
                string.Equals(e.CompanyName.Trim(), companyName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                _log.LogWarning(
                    "Backstage exhibitor sync: no Backstage exhibitor matches company_name '{Co}' "
                    + "(checked {N}). Profile not pushed — align the company name in Backstage with the hub.",
                    companyName, exhibitors.Count);
                return false;
            }

            var ok = await _zoho.UpdateExhibitorAsync(
                token!, match.Id, companyOverview, companyShortDescription, ct,
                companyName: companyName, contactFirstName: contactFirstName, contactLastName: contactLastName);
            if (ok)
            {
                _log.LogInformation("Backstage exhibitor profile pushed for '{Co}' (id {Id}).", companyName, match.Id);
                // Operator 2026-07-23: a successful Zoho write must notify the ops mailbox
                // (publish/delete is manual in Backstage). One save = one-item batch.
                // §791.2 — "PUSHED", not "Updated": `ok` means the PUT was ACCEPTED, and §791.3
                // measured Zoho accepting a PUT and keeping nothing. The mail must not promise more
                // than the response can support.
                if (_zohoChanges is not null)
                    await _zohoChanges.NotifyAsync("Exhibitor profiles",
                        new[] { $"Pushed exhibitor profile for '{companyName}' (Backstage id {match.Id})" }, ct);
            }
            else
            {
                _log.LogWarning("Backstage exhibitor profile update FAILED for '{Co}' (id {Id}).", companyName, match.Id);
            }
            return ok;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Backstage exhibitor sync error for '{Co}' (fail-soft).", companyName);
            return false;
        }
    }
}
