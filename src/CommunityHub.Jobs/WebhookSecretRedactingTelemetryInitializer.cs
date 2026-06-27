using CommunityHub.Core.Integrations;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace CommunityHub.Jobs;

/// <summary>
/// App Insights telemetry initializer that REDACTS the Zoho webhook shared secret from
/// telemetry URLs before they are sent. The Backstage order webhook (REQUIREMENTS §128)
/// carries its secret in the request query string (<c>?token=SECRET</c>) because Backstage
/// offers no HMAC/header signing — so the raw request URL would otherwise be persisted in
/// App Insights <c>RequestTelemetry.Url</c> and the requests table. This replaces the value
/// of the configured secret query parameter (<see cref="ZohoOptions.WebhookSecretQueryParam"/>,
/// default <c>token</c>) with <c>***</c> on Request and Dependency telemetry, case-insensitively
/// on the key (matching the handler's own lookup).
///
/// <para>ISOLATED-WORKER CAVEAT: this initializer runs on the WORKER process's
/// <c>TelemetryConfiguration</c>, so it scrubs worker-emitted telemetry (traces, dependencies,
/// and — on SDK paths that emit it — request telemetry). The Functions HOST process emits its
/// OWN <c>RequestTelemetry</c> for the HTTP trigger; that telemetry does not pass through the
/// worker's DI pipeline and cannot be intercepted here. The durable host-side mitigation is to
/// register the Zoho webhook to send the secret via the <c>X-Webhook-Secret</c> HEADER (already
/// accepted by <see cref="ZohoOrderWebhook"/>) so it never appears in any URL. See Program.cs.</para>
/// </summary>
public sealed class WebhookSecretRedactingTelemetryInitializer : ITelemetryInitializer
{
    private const string Redacted = "***";
    private readonly string _paramName;

    public WebhookSecretRedactingTelemetryInitializer(ZohoOptions options)
        => _paramName = string.IsNullOrWhiteSpace(options.WebhookSecretQueryParam)
            ? "token"
            : options.WebhookSecretQueryParam;

    public void Initialize(ITelemetry telemetry)
    {
        switch (telemetry)
        {
            case RequestTelemetry r:
                if (r.Url is not null)
                {
                    var redacted = RedactQuery(r.Url.OriginalString);
                    if (!ReferenceEquals(redacted, r.Url.OriginalString)
                        && Uri.TryCreate(redacted, UriKind.Absolute, out var u))
                    {
                        r.Url = u;
                    }
                }
                r.Name = RedactQuery(r.Name) ?? r.Name;
                break;
            case DependencyTelemetry d:
                d.Data = RedactQuery(d.Data);
                break;
        }
    }

    /// <summary>
    /// Replace the secret query-parameter VALUE with <c>***</c> in a raw URL/string,
    /// case-insensitive on the (URL-decoded) key. Preserves everything before the
    /// <c>?</c> and any trailing <c>#</c> fragment. Returns the input unchanged (same
    /// reference) when there is no query or the parameter is absent.
    /// </summary>
    public string? RedactQuery(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        int q = value.IndexOf('?');
        if (q < 0) return value;

        string head = value[..(q + 1)];
        string rest = value[(q + 1)..];

        // Keep a trailing #fragment out of the query rewrite.
        string fragment = "";
        int hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            fragment = rest[hash..];
            rest = rest[..hash];
        }

        var pairs = rest.Split('&');
        bool changed = false;
        for (int i = 0; i < pairs.Length; i++)
        {
            var pair = pairs[i];
            if (pair.Length == 0) continue;
            int eq = pair.IndexOf('=');
            var name = eq < 0 ? pair : pair[..eq];
            if (string.Equals(Uri.UnescapeDataString(name), _paramName, StringComparison.OrdinalIgnoreCase))
            {
                pairs[i] = name + "=" + Redacted;
                changed = true;
            }
        }

        return changed ? head + string.Join('&', pairs) + fragment : value;
    }
}
