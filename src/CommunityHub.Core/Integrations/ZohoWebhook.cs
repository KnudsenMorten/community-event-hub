using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// Pure (host-free) helpers for the Zoho Backstage order-change webhook (REQUIREMENTS §128).
/// Kept in Core so the secret check and the payload parse are unit-testable without the
/// Azure Functions host. The <c>ZohoOrderWebhook</c> Function is a thin adapter over these.
/// </summary>
public static class ZohoWebhook
{
    /// <summary>
    /// Authorize an inbound webhook by SHARED SECRET (REQUIREMENTS §128). Zoho Backstage
    /// webhooks expose no HMAC/signature, so the secret is carried in the registered
    /// Endpoint URL query string (<paramref name="providedQuery"/>) and/or a request header
    /// (<paramref name="providedHeader"/>). Returns true only when a non-empty
    /// <paramref name="expectedSecret"/> matches one of them. Comparison is constant-time so
    /// a wrong secret can't be timing-probed. An empty expected secret ALWAYS fails (an
    /// unconfigured endpoint must be un-drivable).
    /// </summary>
    public static bool IsAuthorized(string? expectedSecret, string? providedHeader, string? providedQuery)
    {
        if (string.IsNullOrEmpty(expectedSecret)) return false;
        return FixedTimeEquals(expectedSecret, providedHeader)
            || FixedTimeEquals(expectedSecret, providedQuery);
    }

    private static bool FixedTimeEquals(string expected, string? provided)
    {
        if (string.IsNullOrEmpty(provided)) return false;
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(provided);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>The order id + action parsed out of a Backstage webhook payload (§128).</summary>
    /// <param name="OrderId">The changed order's id, or null when none could be located.</param>
    /// <param name="Action">The webhook action/event string (e.g. <c>order.cancel</c>), lower-cased; may be null.</param>
    /// <param name="IsCancellation">True when the action denotes a cancel/delete (a hint —
    /// the fetch-driven reconcile is still authoritative about whether the order is gone).</param>
    public readonly record struct ParsedPayload(string? OrderId, string? Action, bool IsCancellation);

    // Keys that may carry the order id, across the documented Backstage event shapes
    // (Event Order, Attendee, Order Request) and common webhook envelopes. The first
    // non-empty hit wins; order-specific keys are tried before generic "id".
    private static readonly string[] OrderIdKeys =
        { "order_id", "orderId", "order_number", "orderNumber", "event_order_id", "resource_id", "entity_id", "id" };

    private static readonly string[] ActionKeys =
        { "action", "event", "event_type", "eventType", "operation", "trigger", "type" };

    // Envelope objects a payload may nest the real entity under.
    private static readonly string[] EnvelopeKeys =
        { "data", "payload", "order", "event_order", "attendee", "resource", "body" };

    /// <summary>
    /// Best-effort parse of a Backstage webhook JSON body for the changed ORDER id + action
    /// (REQUIREMENTS §128). Defensive by design: the Backstage docs do not pin the payload
    /// shape, so it searches the top level and one level of common envelopes for any of the
    /// known order-id / action keys. Returns <c>OrderId == null</c> when nothing usable is
    /// found (the caller then no-ops and leaves it to the periodic reconcile). Never throws
    /// on malformed JSON.
    /// </summary>
    public static ParsedPayload ParsePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ParsedPayload(null, null, false);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new ParsedPayload(null, null, false);

            var action = FindString(root, ActionKeys);
            var orderId = FindString(root, OrderIdKeys);

            // Drill into one level of common envelopes if the top level had no order id.
            if (orderId is null)
            {
                foreach (var env in EnvelopeKeys)
                {
                    if (root.TryGetProperty(env, out var inner) && inner.ValueKind == JsonValueKind.Object)
                    {
                        orderId ??= FindString(inner, OrderIdKeys);
                        action ??= FindString(inner, ActionKeys);
                        if (orderId is not null) break;
                    }
                }
            }

            var act = action?.ToLowerInvariant();
            var isCancel = act is not null
                && (act.Contains("cancel") || act.Contains("delete") || act.Contains("refund"));
            return new ParsedPayload(orderId, act, isCancel);
        }
        catch (JsonException)
        {
            return new ParsedPayload(null, null, false);
        }
    }

    private static string? FindString(JsonElement obj, string[] keys)
    {
        foreach (var k in keys)
        {
            if (!obj.TryGetProperty(k, out var v)) continue;
            switch (v.ValueKind)
            {
                case JsonValueKind.String:
                    var s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                    break;
                case JsonValueKind.Number:
                    return v.GetRawText();
            }
        }
        return null;
    }
}
