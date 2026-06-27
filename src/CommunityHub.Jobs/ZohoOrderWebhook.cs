using System.Net;
using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// REAL-TIME leg of the authoritative one-way Zoho→CEH mirror (REQUIREMENTS §128): the
/// FIRST HTTP-triggered Function in this worker (every other job is a TimerTrigger). Zoho
/// Backstage POSTs here on Event Order / Attendee changes (Create / Update / Cancel /
/// Delete / Reassign); the handler validates a shared secret, parses the changed ORDER id,
/// and runs an INCREMENTAL single-order reconcile via
/// <see cref="AttendeeTicketSyncService.SyncOrderAsync"/> — the same upsert + soft-cancel +
/// reassignment path the hourly full sync uses, scoped to that one order so it never
/// touches the rest of the mirror. The hourly <see cref="AttendeeBackstageSyncJob"/> stays
/// on as the drift safety-net for missed/duplicate webhooks.
///
/// <para>CEH NEVER writes/deletes anything in Zoho — strictly read-then-mirror.</para>
///
/// <para>Always returns a definite HTTP status so Zoho's retry behaviour is predictable:
/// <c>401</c> bad/absent secret; <c>200</c> processed OR a deliberate no-op (disabled /
/// paused / feature off / no order id — the periodic reconcile will catch drift);
/// <c>503</c> transient (no active event yet / token refresh failed) so Zoho retries.</para>
///
/// EXEMPT from <see cref="JobsPauseMiddleware"/> (which short-circuits with no HTTP
/// response) — the pause is enforced INSIDE the handler so a paused edition still returns a
/// clean 200 instead of a host 500.
/// </summary>
public sealed class ZohoOrderWebhook
{
    private readonly CommunityHubDbContext _db;
    private readonly ZohoClient _zoho;
    private readonly ZohoOptions _options;
    private readonly AttendeeTicketSyncService _sync;
    private readonly MasterClassEmailService _mcEmail;
    private readonly MasterClassPromotionEmailService _promo;
    private readonly IAuditTrail _audit;
    private readonly FeatureGateService _gate;
    private readonly IConfiguration _config;
    private readonly ILogger<ZohoOrderWebhook> _log;

    public ZohoOrderWebhook(
        CommunityHubDbContext db, ZohoClient zoho, ZohoOptions options,
        AttendeeTicketSyncService sync, MasterClassEmailService mcEmail,
        MasterClassPromotionEmailService promo, IAuditTrail audit,
        FeatureGateService gate, IConfiguration config, ILogger<ZohoOrderWebhook> log)
    {
        _db = db; _zoho = zoho; _options = options; _sync = sync;
        _mcEmail = mcEmail; _promo = promo; _audit = audit;
        _gate = gate; _config = config; _log = log;
    }

    [Function("ZohoOrderWebhook")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "zoho/order-webhook")]
        HttpRequestData req,
        CancellationToken ct)
    {
        // ---- 1. AUTHORIZE (shared secret; no Zoho HMAC exists, §128) -------------
        var providedQuery = GetQueryValue(req.Url.Query, _options.WebhookSecretQueryParam);
        string? providedHeader = null;
        if (req.Headers.TryGetValues(_options.WebhookSecretHeader, out var hv))
            providedHeader = hv.FirstOrDefault();

        if (!ZohoWebhook.IsAuthorized(_options.WebhookSecret, providedHeader, providedQuery))
        {
            _log.LogWarning("ZohoOrderWebhook: rejected — missing/invalid shared secret.");
            return await Text(req, HttpStatusCode.Unauthorized, "unauthorized");
        }

        // ---- 2. Read body + parse the changed order id --------------------------
        var body = await new StreamReader(req.Body).ReadToEndAsync(ct);
        var parsed = ZohoWebhook.ParsePayload(body);

        // ---- 3. No-op gates (return 200 so Zoho doesn't retry a deliberate skip) -
        if (!_options.Enabled || !_options.WebhookEnabled)
            return await Text(req, HttpStatusCode.OK, "webhook disabled — no-op (periodic reconcile active)");

        var eventId = await _db.Events.Where(e => e.IsActive)
            .Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null)
            return await Text(req, HttpStatusCode.ServiceUnavailable, "no active event");
        var ev = eventId.Value;

        // Honour the org-admin "pause all jobs" switch (this fn is pause-middleware-exempt).
        if (await _gate.AreJobsPausedAsync(ev, ct))
            return await Text(req, HttpStatusCode.OK, "jobs paused — no-op");
        if (!await _gate.IsFeatureEnabledAsync("attendee-reconcile", ev, ct))
            return await Text(req, HttpStatusCode.OK, "attendee-reconcile feature off — no-op");

        if (string.IsNullOrWhiteSpace(parsed.OrderId))
        {
            _log.LogInformation("ZohoOrderWebhook: no order id in payload (action={Action}); "
                + "leaving it to the periodic reconcile.", parsed.Action);
            return await Text(req, HttpStatusCode.OK, "no order id in payload — periodic reconcile will catch drift");
        }
        var orderId = parsed.OrderId!;

        // ---- 4. Fetch the CURRENT Zoho state, scoped to this one order ----------
        var token = await _zoho.GetAccessTokenAsync(ct);
        if (token is null)
        {
            _log.LogError("ZohoOrderWebhook: no Zoho token; asking Zoho to retry.");
            return await Text(req, HttpStatusCode.ServiceUnavailable, "zoho token unavailable");
        }

        // Reuse the SAME verified v3 parse the timer job uses, then filter to this order.
        // (A single-order fetch endpoint isn't confirmed; the full pull keeps parsing
        //  identical to the periodic sync. Webhooks are infrequent vs. the data size.)
        var allOrders = await _zoho.GetBackstageOrdersAsync(token, ct);
        var order = allOrders.FirstOrDefault(o => string.Equals(o.OrderId, orderId, StringComparison.Ordinal));

        var allAttendees = await _zoho.GetBackstageAttendeesAsync(token, ct);
        var ticketsForOrder = allAttendees
            .Where(a => string.Equals(a.OrderId, orderId, StringComparison.Ordinal))
            .Select(AttendeeTicketSyncService.FromBackstage)
            .ToList();

        // SAFETY: a transient/empty pull must NOT be mistaken for a real cancellation.
        // The v3 pull swallows API errors and yields an empty list, so if the order is
        // absent AND the whole pull came back empty AND the payload didn't signal a
        // cancel/delete, treat it as ambiguous and let Zoho retry (the periodic reconcile,
        // which sees the full set, is the backstop) rather than soft-cancelling wrongly.
        var pullLooksEmpty = allOrders.Count == 0 && allAttendees.Count == 0;
        if (order is null && !parsed.IsCancellation && pullLooksEmpty)
        {
            _log.LogWarning("ZohoOrderWebhook: order {Order} absent from an EMPTY pull and no cancel "
                + "hint — treating as transient, asking Zoho to retry.", orderId);
            return await Text(req, HttpStatusCode.ServiceUnavailable, "ambiguous empty pull — retry");
        }

        var orderRow = order is null ? null : AttendeeTicketSyncService.FromBackstageOrder(order);
        // The order is gone from Zoho's active set ⇒ whole-order cancellation (corroborated by
        // a cancel/delete hint or a clearly-non-empty pull that simply doesn't contain it).
        var orderRemoved = order is null;

        // ---- 5. INCREMENTAL reconcile of just this order (§128) -----------------
        var result = await _sync.SyncOrderAsync(ev, orderId, orderRow, ticketsForOrder, orderRemoved, ct);

        // ---- 6. Side-effect emails (same as the full sync) ----------------------
        var domain = _config["Hub:CustomDomain"];
        var baseUrl = string.IsNullOrWhiteSpace(domain) ? "https://eldk27.eventhub.expertslive.dk" : $"https://{domain}";
        var reEmails = 0;
        foreach (var r in result.Reassignments)
            try { if (await _mcEmail.SendReassignmentValidationAsync(r.AttendeeId, r.InheritedMcTitle, baseUrl, ct)) reEmails++; } catch { }
        var promoEmails = 0;
        foreach (var p in result.FreedPromotions)
            if (p.PromotedSignupId is int id)
                try { if (await _promo.SendPromotionAsync(id, baseUrl, ct)) promoEmails++; } catch { }

        _log.LogInformation(
            "ZohoOrderWebhook: order {Order} (action={Action}) — created {C}, updated {U}, "
            + "reassigned {R} ({RE} validated), cancelled {X} ({PE} promoted), reactivated {RA}; "
            + "order [created={OC} updated={OU} cancelled={OX} reactivated={ORr}].",
            orderId, parsed.Action, result.Created, result.Updated, result.Reassigned, reEmails,
            result.Cancelled, promoEmails, result.Reactivated,
            result.OrderCreated, result.OrderUpdated, result.OrderCancelled, result.OrderReactivated);

        // --- Record the last-webhook-received stamp on the edition's sync marker (§132).
        //     Lets the Sync-Health dashboard show that the real-time push leg is alive,
        //     distinct from the hourly full-sync's LastSuccessAt. ---
        await RecordWebhookStampAsync(ev, ct);

        await _audit.RecordAsync(new AuditEntry
        {
            EventId = ev,
            Category = AuditCategory.Engine,
            Action = "attendee-backstage-webhook",
            ActorEmail = "system",
            Source = AuditSource.Job,
            Outcome = AuditOutcome.Success,
            Summary = $"Webhook incremental reconcile of order {orderId} (action {parsed.Action ?? "n/a"}): "
                + $"created {result.Created}, updated {result.Updated}, reassigned {result.Reassigned}, "
                + $"cancelled {result.Cancelled}, reactivated {result.Reactivated}"
                + (orderRemoved ? "; order soft-cancelled" : ""),
        }, ct);

        return await Text(req, HttpStatusCode.OK,
            $"ok: order {orderId} reconciled (created {result.Created}, updated {result.Updated}, "
            + $"reassigned {result.Reassigned}, cancelled {result.Cancelled}, reactivated {result.Reactivated})");
    }

    /// <summary>Upsert the edition's attendee-backstage <see cref="SyncRun"/> marker with the
    /// time this webhook was processed (§132). LastSuccessAt stays owned by the hourly
    /// full-sync; if no marker exists yet (a push arrived before any full run), one is created
    /// with both stamps set to now — a processed webhook IS a successful single-order pull.</summary>
    private async Task RecordWebhookStampAsync(int eventId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var marker = await _db.SyncRuns.FirstOrDefaultAsync(
            s => s.EventId == eventId && s.Key == SyncRun.AttendeeBackstageKey, ct);
        if (marker is null)
        {
            marker = new SyncRun
            {
                EventId = eventId,
                Key = SyncRun.AttendeeBackstageKey,
                CreatedAt = now,
                LastSuccessAt = now,
            };
            _db.SyncRuns.Add(marker);
        }
        marker.LastWebhookAt = now;
        await _db.SaveChangesAsync(ct);
    }

    private static async Task<HttpResponseData> Text(HttpRequestData req, HttpStatusCode code, string message)
    {
        var resp = req.CreateResponse(code);
        await resp.WriteStringAsync(message);
        return resp;
    }

    /// <summary>Read one query-string parameter (URL-decoded) from a raw "?a=b&amp;c=d" string,
    /// without taking a System.Web dependency. Case-insensitive on the key.</summary>
    private static string? GetQueryValue(string? rawQuery, string key)
    {
        if (string.IsNullOrEmpty(rawQuery)) return null;
        var q = rawQuery.StartsWith('?') ? rawQuery[1..] : rawQuery;
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = eq < 0 ? pair : pair[..eq];
            if (!string.Equals(Uri.UnescapeDataString(name), key, StringComparison.OrdinalIgnoreCase)) continue;
            return eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }
}
