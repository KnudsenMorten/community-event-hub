using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §233 — the COALESCED processor for the Zoho order-change webhook queue. Runs every
/// minute; when nothing is queued it exits WITHOUT touching the Zoho API. When one or
/// more <see cref="ZohoOrderSyncRequest"/> rows are pending it does exactly ONE Zoho
/// pull (orders + attendees — the same verified v3 parse the full sync uses) and runs
/// the incremental single-order reconcile (<see cref="AttendeeTicketSyncService.SyncOrderAsync"/>)
/// for every DISTINCT queued order from that one snapshot, then sends the same
/// side-effect emails the old inline webhook path sent.
///
/// <para>So: 5 companies ordering within 1–2 minutes = 5 queued rows = ONE Zoho pull —
/// never one pull per webhook. Timer triggers are host-singleton, so runs never overlap;
/// a run that straddles new webhooks simply leaves them for the next minute.</para>
///
/// <para>AMBIGUITY SAFETY (carried over from the old inline path): a transient/EMPTY pull
/// must not be mistaken for a real cancellation. If a queued order is absent from an
/// empty-looking pull and no cancel hint was recorded, the request is left pending and
/// retried next minute (max 5 attempts, then handed to the 10-minute full sync).
/// Processed rows are pruned after 7 days.</para>
///
/// <para>§241: when a window reconciled ≥1 order it ALSO runs the same provision +
/// welcome sweeps as the 10-minute full sync (2-day/1-day login provisioning, the 1-day
/// welcome, and the Master-Class selection invite — the 2-day welcome — to every
/// eligible-not-invited active), so a webhook-registered NEW attendee is provisioned +
/// welcomed without waiting for the timer pull. Ring-gated as any welcome, §219-paced,
/// per-recipient fail-safe.</para>
///
/// <para>CEH NEVER writes/deletes anything in Zoho — strictly read-then-mirror.</para>
/// </summary>
public sealed class ZohoWebhookDrainJob
{
    /// <summary>Give up on an ambiguous request after this many drain attempts — the
    /// periodic full sync (every 10 minutes) is the authoritative backstop.</summary>
    public const int MaxAttempts = 5;

    private readonly CommunityHubDbContext _db;
    private readonly ZohoClient _zoho;
    private readonly ZohoOptions _options;
    private readonly AttendeeTicketSyncService _sync;
    private readonly MasterClassEmailService _mcEmail;
    private readonly MasterClassPromotionEmailService _promo;
    private readonly IAuditTrail _audit;
    private readonly FeatureGateService _gate;
    private readonly IConfiguration _config;
    private readonly AttendeeWelcomeProvisioningService _provisioning;
    private readonly AttendeeOneDayWelcomeEmailService _oneDayWelcome;
    private readonly MasterClassSignupService _signups;
    private readonly IBulkSendPacer? _pacer;
    private readonly ILogger<ZohoWebhookDrainJob> _log;

    public ZohoWebhookDrainJob(
        CommunityHubDbContext db, ZohoClient zoho, ZohoOptions options,
        AttendeeTicketSyncService sync, MasterClassEmailService mcEmail,
        MasterClassPromotionEmailService promo, IAuditTrail audit,
        FeatureGateService gate, IConfiguration config, ILogger<ZohoWebhookDrainJob> log,
        AttendeeWelcomeProvisioningService provisioning,
        AttendeeOneDayWelcomeEmailService oneDayWelcome,
        MasterClassSignupService signups,
        IBulkSendPacer? pacer = null,
        CommunityHub.Core.Config.EventEditionConfigLoader? editionLoader = null,
        CommunityHub.Core.Config.EventConfigOptions? editionOptions = null)
    {
        _db = db; _zoho = zoho; _options = options; _sync = sync;
        _mcEmail = mcEmail; _promo = promo; _audit = audit;
        _gate = gate; _config = config; _provisioning = provisioning;
        _oneDayWelcome = oneDayWelcome; _signups = signups; _pacer = pacer; _log = log;
        // §447 — same two-day class-id resolution as the full sync, so a webhook-driven
        // reconcile classifies a ticket identically to the timer pull. Optional ⇒ no ids ⇒ the
        // name rule decides, exactly as before.
        _twoDayClassIds = editionLoader is null
            ? Array.Empty<string>()
            : editionLoader.Load((editionOptions ?? new CommunityHub.Core.Config.EventConfigOptions())
                .EventConfigPath).MasterClassTwoDayClassIds;
    }

    private readonly IReadOnlyList<string> _twoDayClassIds;

    [Function("ZohoWebhookDrainJob")]
    public async Task Run([TimerTrigger("0 * * * * *")] TimerInfo timer, CancellationToken ct)
    {
        if (!_options.Enabled) return;

        var eventId = await _db.Events.Where(e => e.IsActive)
            .Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null) return;
        var ev = eventId.Value;

        // Queued rows WAIT while jobs are paused / the feature is off (never lost).
        if (await _gate.AreJobsPausedAsync(ev, ct)) return;
        if (!await _gate.IsFeatureEnabledAsync("attendee-reconcile", ev, ct)) return;
        // §326ay (operator 2026-07-25: "simplify and disable the webhook so we only have 1
        // sync routine"). This incremental leg is DEFAULT OFF: it reconciles through
        // SyncOrderAsync, which carries none of the §326ao/§326aq/§326as guards the full
        // sync has, and it runs every minute — six times more often, unprotected. Queued
        // rows are NOT discarded while it is off (the loop simply doesn't run): they stay
        // pending and replay if it is switched back on, and the 10-minute full sync
        // reconciles the same truth meanwhile.
        if (!await _gate.IsFeatureEnabledAsync(
                CommunityHub.Core.Settings.FeatureCatalog.WebhookDrainKey, ev, ct)) return;

        var pending = await _db.ZohoOrderSyncRequests
            .Where(r => r.EventId == ev && r.ProcessedAt == null)
            .OrderBy(r => r.Id)
            .ToListAsync(ct);
        if (pending.Count == 0)
        {
            await PruneOldProcessedAsync(ev, ct);
            return;   // nothing queued — NO Zoho call this minute
        }

        // ---- ONE Zoho pull for the whole window (the §233 coalescing point) ------
        var token = await _zoho.GetAccessTokenAsync(ct);
        if (token is null)
        {
            _log.LogWarning("ZohoWebhookDrainJob: no Zoho token — {N} request(s) stay queued.", pending.Count);
            return;   // rows stay pending; retried next minute
        }
        var allOrders = await _zoho.GetBackstageOrdersAsync(token, ct);
        var allAttendees = await _zoho.GetBackstageAttendeesAsync(token, ct);
        var pullLooksEmpty = allOrders.Count == 0 && allAttendees.Count == 0;

        var domain = _config["Hub:CustomDomain"];
        var baseUrl = string.IsNullOrWhiteSpace(domain) ? "https://eldk27.eventhub.expertslive.dk" : $"https://{domain}";

        var now = DateTimeOffset.UtcNow;
        int reconciled = 0, deferred = 0, gaveUp = 0, reEmails = 0, promoEmails = 0;

        // Distinct orders; a repeat row for the same order rides along with the first.
        foreach (var group in pending.GroupBy(r => r.OrderId, StringComparer.Ordinal))
        {
            var orderId = group.Key;
            var cancelHint = group.Any(r => r.CancelHint);
            var order = allOrders.FirstOrDefault(o => string.Equals(o.OrderId, orderId, StringComparison.Ordinal));

            // AMBIGUITY SAFETY: absent order + empty-looking pull + no cancel hint ⇒ do
            // NOT soft-cancel from this snapshot. Retry next minute; give up after
            // MaxAttempts (the periodic full sync reconciles the truth).
            if (order is null && !cancelHint && pullLooksEmpty)
            {
                foreach (var r in group)
                {
                    r.Attempts++;
                    if (r.Attempts >= MaxAttempts)
                    {
                        r.ProcessedAt = now;
                        r.Outcome = "gave-up-ambiguous (full sync covers)";
                        gaveUp++;
                    }
                    else deferred++;
                }
                continue;
            }

            try
            {
                var ticketsForOrder = allAttendees
                    .Where(a => string.Equals(a.OrderId, orderId, StringComparison.Ordinal))
                    // §447: class-id first, name as fallback — same rule as the full sync.
                    .Select(a => AttendeeTicketSyncService.FromBackstage(a, _twoDayClassIds))
                    .ToList();
                var orderRow = order is null ? null : AttendeeTicketSyncService.FromBackstageOrder(order);

                var result = await _sync.SyncOrderAsync(ev, orderId, orderRow, ticketsForOrder,
                    orderRemoved: order is null, ct);

                // §234 3: persist the reassignment-validation intent NOW (crash-safe
                // marker); the actual sends happen ONCE after the whole window (below),
                // so an email failure can never affect this order's queue bookkeeping.
                if (result.Reassignments.Count > 0)
                    await _mcEmail.MarkReassignmentValidationsPendingAsync(
                        ev, result.Reassignments.Select(x => x.AttendeeId).ToList(), ct);
                foreach (var p in result.FreedPromotions)
                    if (p.PromotedSignupId is int id)
                        try { if (await _promo.SendPromotionAsync(id, baseUrl, ct, p.ReleasedTitle)) promoEmails++; }
                        catch (Exception ex)
                        {
                            // §234 3: never silent — the promotion is committed; only its
                            // notification failed, which must be visible in the logs.
                            _log.LogWarning(ex,
                                "ZohoWebhookDrainJob: promotion email failed for signup {SignupId}.", id);
                        }

                foreach (var r in group)
                {
                    r.ProcessedAt = now;
                    r.Attempts++;
                    r.Outcome = $"reconciled (c{result.Created} u{result.Updated} r{result.Reassigned} "
                        + $"x{result.Cancelled} ra{result.Reactivated})";
                }
                reconciled++;
            }
            catch (Exception ex)
            {
                // A single bad order never blocks the rest of the window; the row stays
                // pending (bounded by MaxAttempts like the ambiguous case).
                _log.LogWarning(ex, "ZohoWebhookDrainJob: reconcile failed for order {Order}.", orderId);
                foreach (var r in group)
                {
                    r.Attempts++;
                    if (r.Attempts >= MaxAttempts)
                    {
                        r.ProcessedAt = now;
                        r.Outcome = "gave-up-error (full sync covers)";
                        gaveUp++;
                    }
                    else deferred++;
                }
            }
        }

        await _db.SaveChangesAsync(ct);

        // §241: a webhook-registered NEW attendee must not wait for the 10-minute pull
        // ("either happens when the webhook triggers or time pull"). When this window
        // actually reconciled ≥1 order, run the SAME provision + welcome sweeps the full
        // sync runs — the drain job itself does no provisioning during the reconcile, so
        // do it here first (login participants must exist for the §169 magic links):
        //   1) provision 2-day + 1-day login participants,
        //   2) 1-day welcome to newly-provisioned participants (welcome-email gated),
        //   3) selection invite (the 2-day welcome) to eligible-not-invited actives.
        // All §219-paced, per-recipient fail-safe, and best-effort — an email failure
        // never affects the queue bookkeeping committed above.
        int provisioned = 0, oneDayProvisioned = 0, oneDayWelcomed = 0, selectionInvites = 0, cancelNotices = 0;
        if (reconciled > 0)
        {
            try
            {
                provisioned = (await _provisioning.ProvisionAsync(ev, ct)).Count;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "ZohoWebhookDrainJob: attendee provisioning failed (continuing).");
            }

            // §208 + §242: provision NEW 1-day attendees + send each newly-created one
            // the 1-day welcome — identical to AttendeeBackstageSyncJob's loop, and like
            // there ONLY while the 1-day flow is open (attendee-1day-access, default
            // OFF/suspended). The reconcile sweep afterwards keeps existing 1-day-only
            // logins in the flag's state (a webhook reappearance may have just restored
            // one via §216 — the sweep re-locks it while suspended).
            var oneDayAccess = await _gate.IsFeatureEnabledAsync("attendee-1day-access", ev, ct);
            if (oneDayAccess)
            {
                try
                {
                    var newOneDay = await _provisioning.ProvisionOneDayAsync(ev, ct);
                    oneDayProvisioned = newOneDay.Count;
                    if (newOneDay.Count > 0
                        && await _gate.IsFeatureEnabledAsync("welcome-email", ev, ct))
                    {
                        var welcomeDispatched = 0;
                        foreach (var pid in newOneDay)
                        {
                            // §219 PACING: space the loop under Brevo's rate limit.
                            if (welcomeDispatched > 0 && _pacer is not null) await _pacer.PaceAsync(ct);
                            welcomeDispatched++;
                            try { if (await _oneDayWelcome.SendForProvisioningAsync(pid, ct)) oneDayWelcomed++; }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex, "ZohoWebhookDrainJob: 1-day welcome failed for participant {Pid}.", pid);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "ZohoWebhookDrainJob: 1-day attendee provisioning failed (continuing).");
                }
            }

            // §242: reconcile existing 1-day-only logins to the flag (reversible lockout).
            try
            {
                await _provisioning.ReconcileOneDayAccessAsync(ev, oneDayAccess, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "ZohoWebhookDrainJob: 1-day access reconcile failed (continuing).");
            }

            // §243: a webhook-driven soft-cancel notifies the 2-day holder without waiting
            // for the 10-minute pull — same sweep + SentReminder ledger (once per
            // cancellation; undelivered sends retried by the next drain / full sync).
            try
            {
                cancelNotices = await _mcEmail.SendPendingTicketCancellationsAsync(ev, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "ZohoWebhookDrainJob: ticket-cancelled sweep failed (continuing).");
            }

            // §241: the selection-invite sweep — the sender ring-gates each recipient
            // (welcome-email ring + §217 attendee-welcome cap) and the invite stamp is
            // delivery-gated, so a ring-dropped invite stays eligible and is retried by
            // the next drain / full sync once rings widen.
            try
            {
                if (await _gate.IsFeatureEnabledAsync("welcome-email", ev, ct))
                {
                    var inviteDispatched = 0;
                    foreach (var attendeeId in await _signups.EligibleNotInvitedIdsAsync(ev, ct))
                    {
                        if (inviteDispatched > 0 && _pacer is not null) await _pacer.PaceAsync(ct);
                        inviteDispatched++;
                        try { if (await _mcEmail.SendSelectionInviteAsync(attendeeId, baseUrl, ct: ct)) selectionInvites++; }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex,
                                "ZohoWebhookDrainJob: selection invite failed for attendee {AttendeeId} (still eligible; retried next run).",
                                attendeeId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "ZohoWebhookDrainJob: selection-invite sweep failed (continuing).");
            }
        }

        // §234 3: drain EVERY pending reassignment-validation email — the ones queued by
        // this window plus any left over from earlier failed / ring-dropped attempts (the
        // marker clears only on an actually-DELIVERED send). A failure is logged and keeps
        // the marker, so the next drain (or the 10-minute full sync) retries it.
        foreach (var r in await _mcEmail.GetPendingReassignmentValidationsAsync(ev, ct))
        {
            try { if (await _mcEmail.SendReassignmentValidationAsync(r.AttendeeId, r.InheritedMcTitle, baseUrl, ct)) reEmails++; }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "ZohoWebhookDrainJob: reassignment-validation email failed for attendee {AttendeeId} (kept pending; retried next run).",
                    r.AttendeeId);
            }
        }

        await PruneOldProcessedAsync(ev, ct);

        _log.LogInformation(
            "ZohoWebhookDrainJob: {Pending} queued request(s) → ONE Zoho pull — {Rec} order(s) "
            + "reconciled ({RE} reassignment emails, {PE} promotions), {Def} deferred, {Gave} gave up; "
            + "provisioned {PV} 2-day + {PV1} 1-day login participant(s) "
            + "({W1} 1-day welcomed, {SI} selection invite(s) auto-sent, {CN} ticket-cancelled notice(s)).",
            pending.Count, reconciled, reEmails, promoEmails, deferred, gaveUp,
            provisioned, oneDayProvisioned, oneDayWelcomed, selectionInvites, cancelNotices);

        if (reconciled + gaveUp > 0)
            await _audit.RecordAsync(new AuditEntry
            {
                EventId = ev,
                Category = AuditCategory.Engine,
                Action = "attendee-backstage-webhook-drain",
                ActorEmail = "system",
                Source = AuditSource.Job,
                Outcome = AuditOutcome.Success,
                Summary = $"Coalesced webhook drain: {pending.Count} queued request(s), one Zoho pull, "
                    + $"{reconciled} order(s) reconciled, {deferred} deferred, {gaveUp} gave up; "
                    + $"{selectionInvites} selection invite(s) auto-sent",
            }, ct);
    }

    /// <summary>Housekeeping: drop processed queue rows older than 7 days.</summary>
    private async Task PruneOldProcessedAsync(int eventId, CancellationToken ct)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
            await _db.ZohoOrderSyncRequests
                .Where(r => r.EventId == eventId && r.ProcessedAt != null && r.ProcessedAt < cutoff)
                .ExecuteDeleteAsync(ct);
        }
        catch
        {
            // best-effort housekeeping — never fail the drain
        }
    }
}
