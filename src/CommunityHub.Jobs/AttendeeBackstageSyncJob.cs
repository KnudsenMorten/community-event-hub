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
/// THE single authoritative one-way Zoho→CEH sync (REQUIREMENTS §125/§126/§128). Pulls
/// the FULL Backstage dataset — orders + every ticket/attendee (v3, enriched), not just
/// 2-day — and reconciles the local mirror to match Zoho's ACTIVE set exactly, keyed on
/// the ticket id (§6):
/// <list type="bullet">
/// <item>UPSERT orders + attendees; Master-Class eligibility (TicketStatus) is set via the
/// single <see cref="MasterClassTicketPolicy"/> only.</item>
/// <item>A reassigned ticket transfers its Master Class to the new holder (who is emailed
/// to validate).</item>
/// <item>SOFT-CANCEL (§128): a ticket/order gone from the pull is marked Cancelled (seat
/// released → waitlist promoted + notified, history kept). A reappearance flips to Active.</item>
/// <item>Folds the former AttendeeReconcileJob's still-needed behaviour — the
/// "2-day ticket but no Master Class selected" chaser — so there is ONE writer.</item>
/// <item>§241: AUTO-SENDS the Master-Class selection invite (the de-facto 2-day welcome,
/// §215) to every eligible-not-invited ACTIVE 2-day attendee — ring-gated as any welcome
/// (welcome-email feature + §217 cap), §219-paced, per-recipient fail-safe; the invite
/// stamp is delivery-gated so ring-dropped sends retry here when rings widen.</item>
/// <item>§242: the 1-day attendee flow (provisioning + welcome + sign-in) is gated by
/// <c>attendee-1day-access</c> (SUSPENDED by default); 1-day tickets keep syncing, and
/// existing 1-day-only logins are reconciled (locked out / restored) every run so the
/// suspension is reversible.</item>
/// <item>§243: 2-day holders whose TICKET was soft-cancelled get a ticket-cancelled
/// notice (once per cancellation via the SentReminder ledger, ring-gated, retried
/// while undelivered).</item>
/// <item>Records a last-successful-sync marker (<see cref="SyncRun"/>) for telemetry (§127).</item>
/// </list>
/// CEH NEVER writes/deletes anything in Zoho. Gated by the <c>attendee-reconcile</c>
/// feature + <c>zoho.enabled</c>. Runs every 10 minutes (§231, operator 2026-07-07 — was hourly).
/// </summary>
public sealed class AttendeeBackstageSyncJob
{
    /// <summary>
    /// §371 — how long after the SELECTION INVITE the "you haven't picked a Master Class" chaser
    /// may first go out. Matches the 14-day cadence the other attendee reminders use
    /// (<c>AttendeeMasterClassReminderBuilder.IntervalDays</c>, the §232 operator decision), so an
    /// attendee never gets the welcome and a chase-up in the same breath.
    /// </summary>
    private const int PendingSelectionChaseAfterDays = 14;

    private readonly CommunityHubDbContext _db;
    private readonly ZohoClient _zoho;
    private readonly ZohoOptions _options;
    private readonly AttendeeTicketSyncService _sync;
    private readonly MasterClassEmailService _mcEmail;
    private readonly MasterClassPromotionEmailService _promo;
    private readonly AttendeeWelcomeProvisioningService _provisioning;
    private readonly WelcomeWithLoginEmailService _welcome;
    private readonly AttendeeOneDayWelcomeEmailService _oneDayWelcome;
    private readonly ReminderEngine _engine;
    private readonly EmailTemplateProvider _templates;
    private readonly IAuditTrail _audit;
    private readonly TimeProvider _clock;
    private readonly FeatureGateService _gate;
    private readonly IConfiguration _config;
    private readonly MasterClassSignupService _signups;
    private readonly IBulkSendPacer? _pacer;
    private readonly ILogger<AttendeeBackstageSyncJob> _log;
    // §545(b) — optional, so a job can be instrumented without touching its wiring and an
    // un-instrumented job simply says nothing (silence = UNKNOWN, never flagged).
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;
    // §447 — the edition's two-day ticket_class_id(s). Resolved once at construction from the
    // same edition config every other per-edition rule reads.
    private readonly IReadOnlyList<string> _twoDayClassIds;

    public AttendeeBackstageSyncJob(
        CommunityHubDbContext db, ZohoClient zoho, ZohoOptions options,
        AttendeeTicketSyncService sync, MasterClassEmailService mcEmail,
        MasterClassPromotionEmailService promo,
        AttendeeWelcomeProvisioningService provisioning,
        WelcomeWithLoginEmailService welcome,
        AttendeeOneDayWelcomeEmailService oneDayWelcome,
        ReminderEngine engine, EmailTemplateProvider templates, IAuditTrail audit,
        TimeProvider clock, FeatureGateService gate,
        IConfiguration config, ILogger<AttendeeBackstageSyncJob> log,
        MasterClassSignupService signups,
        IBulkSendPacer? pacer = null,
        CommunityHub.Core.Config.EventEditionConfigLoader? editionLoader = null,
        CommunityHub.Core.Config.EventConfigOptions? editionOptions = null,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _activity = activity;
        _db = db; _zoho = zoho; _options = options; _sync = sync;
        _mcEmail = mcEmail; _promo = promo; _provisioning = provisioning;
        _welcome = welcome; _oneDayWelcome = oneDayWelcome; _engine = engine;
        _templates = templates; _audit = audit;
        _clock = clock; _gate = gate; _config = config; _signups = signups;
        _pacer = pacer; _log = log;
        // Optional so existing test wiring constructs unchanged; null ⇒ no ids ⇒ the name rule
        // decides, exactly as before §447.
        _twoDayClassIds = editionLoader is null
            ? Array.Empty<string>()
            : editionLoader.Load((editionOptions ?? new CommunityHub.Core.Config.EventConfigOptions())
                .EventConfigPath).MasterClassTwoDayClassIds;
    }

    [Function("AttendeeBackstageSyncJob")]
    // §869.3 — BASE TICK ONLY. The real cadence is the operator's §510 interval on the Jobs page
    // (JobCatalog default 10), enforced in JobsPauseMiddleware.
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("AttendeeBackstageSyncJob: Zoho disabled.");
            _activity?.ReportInactive("Zoho is switched off, so no attendees or orders are pulled.");
            return;
        }

        var activeEvent = await _db.Events.Where(e => e.IsActive)
            .Select(e => new { e.Id, e.DisplayName }).FirstOrDefaultAsync(ct);
        if (activeEvent is null) { _log.LogWarning("AttendeeBackstageSyncJob: no active event."); return; }
        var eventId = activeEvent.Id;
        if (!await _gate.IsFeatureEnabledAsync("attendee-reconcile", eventId, ct))
        {
            _log.LogInformation("AttendeeBackstageSyncJob: feature off.");
            _activity?.ReportInactive(
                "The 'attendee-reconcile' feature is switched off, so ticket purchases in Zoho are "
                + "not reaching CEH.");
            return;
        }

        // 🔒 §784.5 — A DELIBERATELY BLOCKED HOST IS INACTIVE, NOT FAILING.
        //
        // Operator 2026-08-03: *"this service is now turned off in DEV, so the alerting must also
        // stopped"* — he was receiving "AttendeeBackstageSyncJob has now FAILED 2 time(s) in a row"
        // from an environment that is switched off ON PURPOSE.
        //
        // ⚠️ This check exists because the §783.12b fix would otherwise MANUFACTURE that alert:
        // blocking DEV's token means every Zoho job here finds no token, and "no token" was an
        // ERROR. Turning an integration off must never become a source of pages about it being off —
        // that is how the fix for one noise problem becomes the next noise problem.
        if (!_zoho.HostMayReachZoho)
        {
            _activity?.ReportInactive(
                "Zoho is blocked for this host (Integrations:AllowExternalWrites=false), so no "
                + "ticket data is pulled here. Expected on DEV: DEV and PROD share ONE Zoho refresh "
                + "token and Zoho meters token requests at 10 per 10 minutes, so DEV must not spend "
                + "them (§783.12b).");
            return;
        }

        var token = await _zoho.GetAccessTokenAsync(ct);
        if (token is null) { _log.LogError("AttendeeBackstageSyncJob: no Zoho token."); return; }

        // FULL dataset pull: orders + attendees (every ticket class, §125).
        var orders = (await _zoho.GetBackstageOrdersAsync(token, ct))
            .Select(AttendeeTicketSyncService.FromBackstageOrder).ToList();
        var attendees = await _zoho.GetBackstageAttendeesAsync(token, ct);

        // §545 NO-OP STREAK — ZERO is genuinely abnormal here: this is a LIVE, SOLD event, so a
        // pass that finds no attendees at all means the data stopped arriving even though every
        // gate reported healthy. §585 was exactly this (a resource missing from the paging reject
        // list ⇒ every read came back EMPTY, no error, no failure count).
        _activity?.ReportExamined(attendees.Count, "attendees in Zoho");
        // §447: the ticket_class_id decides 1-day vs 2-day when configured; the class NAME is the
        // fallback for historic orders. Explicit lambda (not a method group) because the optional
        // second parameter otherwise makes Select's index overload ambiguous.
        var rows = attendees.Select(a => AttendeeTicketSyncService.FromBackstage(a, _twoDayClassIds)).ToList();

        // §326ao AMBIGUITY GUARD (the §301b / ZohoWebhookDrainJob `pullLooksEmpty` pattern).
        // SyncAsync soft-cancels EVERY local row it does not see in the pull: seats are
        // released (waitlist promotion mail actually sends), party RSVPs are cancelled and
        // §216 revokes the logins. An EMPTY pull against a NON-EMPTY mirror is therefore
        // indistinguishable from "the whole event was cancelled" — and the far likelier
        // cause is a misconfigured portal/event id or a Zoho outage that still answered 200.
        // Truncated (non-empty but partial) reads can no longer reach here: both fetches now
        // use the STRICT pager and throw. Skip the reconcile; the next run (10 min) retries.
        if (rows.Count == 0)
        {
            var mirrored = await _db.Attendees
                .CountAsync(a => a.EventId == eventId && a.MirrorState == MirrorState.Active, ct);
            if (mirrored > 0)
            {
                _log.LogError(
                    "AttendeeBackstageSyncJob: Zoho returned ZERO attendees while the mirror holds {Mirrored} active rows — "
                    + "SKIPPING the reconcile (it would soft-cancel every one of them). Check the Zoho portal/event id and API health.",
                    mirrored);
                return;
            }
        }

        var result = await _sync.SyncAsync(eventId, rows, orders, ct);

        // §169 RE-ENABLED (operator 2026-06-29): attendees ARE participants. Provision an
        // Active, login-capable, Attendee-role Participant for every 2-day-ticket holder
        // that lacks one — BEFORE any attendee email is built below — so the magic-link
        // seam can bind each attendee's {{hubUrl}} CTA to their personal /go/{token}.
        // Idempotent (skips holders who already have a Participant); a provisioning failure
        // must NEVER break the sync, so it is best-effort.
        var provisioned = 0;
        try
        {
            provisioned = (await _provisioning.ProvisionAsync(eventId, ct)).Count;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AttendeeBackstageSyncJob: attendee provisioning failed (continuing).");
        }

        // §208 + §242: provision NEW 1-day-ticket attendees (login-capable Attendee
        // Participants) and send each newly-created one the 1-day welcome (one task:
        // Party signup; carries the §169 magic link) — but ONLY while the 1-day flow is
        // OPEN (attendee-1day-access, default OFF). While SUSPENDED (§242) the mirror
        // sync above still records 1-day tickets, but nothing is provisioned/welcomed.
        // New-only + idempotent (skips already-welcomed), welcome gated by the
        // welcome-email feature. Best-effort: a failure never breaks the sync.
        var oneDayAccess = await _gate.IsFeatureEnabledAsync("attendee-1day-access", eventId, ct);
        var oneDayProvisioned = 0;
        var oneDayWelcomed = 0;
        if (oneDayAccess)
        {
            try
            {
                var newOneDay = await _provisioning.ProvisionOneDayAsync(eventId, ct);
                oneDayProvisioned = newOneDay.Count;
                if (newOneDay.Count > 0
                    && await _gate.IsFeatureEnabledAsync("welcome-email", eventId, ct))
                {
                    var welcomeDispatched = 0;
                    foreach (var pid in newOneDay)
                    {
                        // §219 PACING: space the bulk welcome loop under Brevo's rate limit
                        // (delay before each send AFTER the first; first fires immediately).
                        if (welcomeDispatched > 0 && _pacer is not null) await _pacer.PaceAsync(ct);
                        welcomeDispatched++;
                        // §219 RETRY: a per-recipient hard failure is logged + skipped so the
                        // loop attempts EVERY remaining recipient (no one aborts the batch).
                        try { if (await _oneDayWelcome.SendForProvisioningAsync(pid, ct)) oneDayWelcomed++; }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "AttendeeBackstageSyncJob: 1-day welcome failed for participant {Pid}.", pid);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "AttendeeBackstageSyncJob: 1-day attendee provisioning failed (continuing).");
            }
        }

        // §242: reconcile EXISTING 1-day-only logins to the flag every run — locked out
        // while suspended, restored (for active-ticket holders) once re-enabled. This is
        // what makes the suspension REVERSIBLE and also keeps the party seeder/reminders
        // away from suspended 1-day attendees (both only touch ACTIVE participants).
        // 2-day logins are untouched (§216 owns them). Best-effort.
        try
        {
            await _provisioning.ReconcileOneDayAccessAsync(eventId, oneDayAccess, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AttendeeBackstageSyncJob: 1-day access reconcile failed (continuing).");
        }

        var domain = _config["Hub:CustomDomain"];
        var baseUrl = string.IsNullOrWhiteSpace(domain) ? "https://eldk27.eventhub.expertslive.dk" : $"https://{domain}";

        // §234 3: the reassignment-validation email is no longer fire-and-forget.
        // 1) Persist the intent FIRST (crash-safe marker on the attendee row);
        // 2) drain EVERY pending validation — this run's plus any earlier failures /
        //    ring-drops (the marker is cleared only on an actually-DELIVERED send);
        // 3) a failure is LOGGED and keeps the marker, so the next run retries.
        var reEmails = 0;
        await _mcEmail.MarkReassignmentValidationsPendingAsync(
            eventId, result.Reassignments.Select(r => r.AttendeeId).ToList(), ct);
        foreach (var r in await _mcEmail.GetPendingReassignmentValidationsAsync(eventId, ct))
        {
            try { if (await _mcEmail.SendReassignmentValidationAsync(r.AttendeeId, r.InheritedMcTitle, baseUrl, ct)) reEmails++; }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "AttendeeBackstageSyncJob: reassignment-validation email failed for attendee {AttendeeId} (kept pending; retried next run).",
                    r.AttendeeId);
            }
        }
        // §243: notify 2-DAY holders whose TICKET was soft-cancelled — sweep + SentReminder
        // ledger (once per cancellation; a reappearance clears CancelledAt so a later
        // cancellation mails again). Ring-gated per recipient (welcome-email feature ring
        // on the person's Participant); a failed / ring-dropped send is not ledgered and
        // is retried by the next run within the 7-day window. Best-effort.
        var cancelNotices = 0;
        try
        {
            cancelNotices = await _mcEmail.SendPendingTicketCancellationsAsync(eventId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AttendeeBackstageSyncJob: ticket-cancelled sweep failed (continuing).");
        }

        var promoEmails = 0;
        foreach (var p in result.FreedPromotions)
            if (p.PromotedSignupId is int id)
                try { if (await _promo.SendPromotionAsync(id, baseUrl, ct, p.ReleasedTitle)) promoEmails++; }
                catch (Exception ex)
                {
                    // §234 3: never silent — the promotion itself is committed; only the
                    // notification failed, and that must be visible in the job logs.
                    _log.LogWarning(ex,
                        "AttendeeBackstageSyncJob: promotion email failed for signup {SignupId}.", id);
                }

        // §241: AUTO-SEND the Master-Class selection invite — the de-facto 2-day
        // WELCOME (§215) — to every eligible-not-invited ACTIVE 2-day attendee, so a
        // NEW attendee registered by this pull is welcomed without waiting for the
        // organizer's bulk-send page. Gated by the welcome-email feature; the sender
        // ring-gates each recipient (welcome-email ring + §217 attendee-welcome cap)
        // and MasterClassInviteSentAt stamps only on REAL delivery, so a ring-dropped
        // invite stays eligible and this sweep auto-retries it once rings widen.
        // §219-paced + per-recipient fail-safe; a sweep failure never breaks the sync.
        var selectionInvites = 0;
        try
        {
            if (await _gate.IsFeatureEnabledAsync("welcome-email", eventId, ct))
            {
                var inviteDispatched = 0;
                foreach (var attendeeId in await _signups.EligibleNotInvitedIdsAsync(eventId, ct))
                {
                    // §219 PACING: space the bulk invite loop under Brevo's rate limit.
                    if (inviteDispatched > 0 && _pacer is not null) await _pacer.PaceAsync(ct);
                    inviteDispatched++;
                    try { if (await _mcEmail.SendSelectionInviteAsync(attendeeId, baseUrl, ct: ct)) selectionInvites++; }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex,
                            "AttendeeBackstageSyncJob: selection invite failed for attendee {AttendeeId} (still eligible; retried next run).",
                            attendeeId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AttendeeBackstageSyncJob: selection-invite sweep failed (continuing).");
        }

        // --- Folded chaser (was AttendeeReconcileJob): "you hold a 2-day ticket but
        //     haven't selected your Master Class yet". One writer now (§125). Only the
        //     ACTIVE mirror set is chased — soft-cancelled rows are excluded. ---
        var chasers = await ChaseUnselectedTwoDayAsync(eventId, activeEvent.DisplayName, ct);

        // --- Last-successful-sync marker for the telemetry "Updated <t>" footer (§127) ---
        await RecordSyncMarkerAsync(eventId, result, ct);

        _log.LogInformation(
            "AttendeeBackstageSyncJob: {Orders} orders ({OC} new, {OX} cancelled), {Pulled} attendees — "
            + "created {C}, updated {U}, reassigned {R} ({RE} validated), cancelled {X} ({PE} promoted), "
            + "reactivated {RA}, provisioned {PV} 2-day + {PV1} 1-day login participant(s) "
            + "({W1} 1-day welcomed, {SI} selection invite(s) auto-sent, {CN} ticket-cancelled "
            + "notice(s), 1-day access {ODA}), {CH} chaser(s) sent.",
            orders.Count, result.OrdersCreated, result.OrdersCancelled, rows.Count,
            result.Created, result.Updated, result.Reassigned, reEmails, result.Cancelled, promoEmails,
            result.Reactivated, provisioned, oneDayProvisioned, oneDayWelcomed, selectionInvites,
            cancelNotices, oneDayAccess ? "ON" : "SUSPENDED (§242)", chasers);

        // Named Engine event (REQUIREMENTS §24) — the sync RUN summary.
        await _audit.RecordAsync(new AuditEntry
        {
            EventId = eventId,
            Category = AuditCategory.Engine,
            Action = "attendee-backstage-sync",
            ActorEmail = "system",
            Source = AuditSource.Job,
            Outcome = AuditOutcome.Success,
            Summary = $"Backstage sync: {result.OrdersActive} active orders, {result.AttendeesActive} active "
                + $"attendees (created {result.Created}, updated {result.Updated}, reassigned {result.Reassigned}, "
                + $"cancelled {result.Cancelled}, reactivated {result.Reactivated}); {selectionInvites} selection "
                + $"invite(s) auto-sent; {chasers} chaser(s) sent",
        }, ct);

        // Attendee provisioning was REMOVED 2026-06-23 (no separate attendee welcome) but
        // RE-ENABLED above 2026-06-29 for §169: attendees ARE participants, so every 2-day
        // holder needs a login-capable Participant for their attendee emails (Master Class
        // confirm/chaser) to carry a personal magic-link. Provisioning mints NO welcome of
        // its own — it only creates the login identity; the emails are still the existing
        // Master-Class sends. See the ProvisionAsync call right after the sync above.
    }

    /// <summary>
    /// Reflect each ACTIVE 2-day attendee's in-hub Master-Class selection onto their row
    /// (BookingStatus / MasterClassName / HasReconciliationMismatch) and send the
    /// "pending-master-class-selection" reminder to those who hold a 2-day ticket but
    /// have not selected a Master Class. Folded from the retired AttendeeReconcileJob so
    /// the mirror sync is the single writer (§125). Returns how many chasers were sent.
    /// </summary>
    private async Task<int> ChaseUnselectedTwoDayAsync(int eventId, string eventDisplayName, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        // CONFIRMED in-hub seats per attendee (the only Master-Class source of truth).
        var confirmed = await _db.MasterClassSignups
            .Where(s => s.EventId == eventId && s.Status == MasterClassSignupStatus.Confirmed)
            .Select(s => new { s.AttendeeId, Title = s.Session.Title })
            .ToListAsync(ct);
        var selectionByAttendee = confirmed
            .GroupBy(x => x.AttendeeId)
            .ToDictionary(g => g.Key, g => g.First().Title);

        // Only ACTIVE 2-day holders (soft-cancelled rows are excluded from the chase, §128).
        var twoDay = await _db.Attendees
            .Where(a => a.EventId == eventId
                        && a.TicketStatus == TicketStatus.TwoDay
                        && a.MirrorState == MirrorState.Active)
            .ToListAsync(ct);

        // §169: map each chased attendee's email to their provisioned login Participant
        // id (Attendee-role preferred, any same-email match accepted) so the
        // pending-selection email's {{hubUrl}} CTA is the recipient's personal
        // auto-login magic-link. One query for the edition's participants, looked up in
        // the loop. Fail-safe: no participant (e.g. not provisioned yet) ⇒ plain hub URL.
        var participantIdByEmail = (await _db.Participants
                .Where(p => p.EventId == eventId)
                .Select(p => new { p.Id, p.Email, p.Role })
                .ToListAsync(ct))
            .GroupBy(p => p.Email.Trim().ToLowerInvariant())
            .ToDictionary(
                g => g.Key,
                g => (g.FirstOrDefault(p => p.Role == ParticipantRole.Attendee) ?? g.First()).Id);

        // §346 — who has CANCELLED a seat inside the chase window. Giving up a seat DELETES the
        // signup row, so the attendee table keeps no memory of it; the durable record is the
        // cancellation mail we sent. Matched on the template rather than the subject so a copy
        // edit cannot quietly switch the gate off.
        var graceStart = now.AddDays(-PendingSelectionChaseAfterDays);
        var cancelledEmails = await _db.EmailLogs
            .Where(l => l.EventId == eventId
                        && l.TemplateName == "masterclass-cancelled"
                        && l.SentAt >= graceStart)
            .Select(l => l.ToEmail)
            .ToListAsync(ct);
        var cancelledSet = cancelledEmails
            .Select(e => (e ?? string.Empty).Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var cancelledRecently = twoDay
            .Where(a => cancelledSet.Contains((a.Email ?? string.Empty).Trim().ToLowerInvariant()))
            .Select(a => a.Id)
            .ToHashSet();

        var due = new List<ReminderMessage>();
        foreach (var a in twoDay)
        {
            var hasSelection = selectionByAttendee.TryGetValue(a.Id, out var mcTitle);
            a.BookingStatus = hasSelection ? MasterClassBookingStatus.Booked : MasterClassBookingStatus.NotBooked;
            a.MasterClassName = hasSelection ? mcTitle : null;
            a.HasReconciliationMismatch = !hasSelection;
            a.LastSyncedAt = now;
            if (hasSelection) continue;

            // §371 (operator 2026-07-26, reported twice): "attendees receive 2 emails, both the
            // reminder + welcome email … they should only receive the reminder … never at the
            // initial invite." THIS is the chaser he was seeing — not the §358 builder I fixed
            // first, which sends a different template.
            //
            // It had NO time gate at all: every active 2-day attendee without a selection was
            // chased on EVERY run, suppressed only by a once-ever ledger row. And it runs in the
            // SAME job pass as SendSelectionInviteAsync — so the first pass for a new attendee (or
            // the first pass after a §355 reset clears the ledger) sent welcome + chaser together.
            //
            // Two gates, both anchored on when the invite actually went out:
            //   1. not invited yet  ⇒ nothing to chase (the state right after a reset, and for the
            //      moments before the invite is sent in this very pass);
            //   2. invited less than one full window ago ⇒ still their time to act.
            if (a.MasterClassInviteSentAt is not DateTimeOffset invitedAt) continue;
            if ((now - invitedAt).TotalDays < PendingSelectionChaseAfterDays) continue;

            // §346 — the THIRD gate, and the one §371 did not cover. Both gates above are anchored
            // on the INVITE, so an attendee invited long ago who GIVES UP their seat becomes
            // "unselected" again and is eligible on the very next pass — chased to "select your
            // Master Class" within ~10 minutes of deliberately un-selecting one. That is almost
            // certainly the mail the operator described as arriving 10-30 minutes after he
            // cancelled (its subject is "select your Master Class", not "cancelled").
            //
            // Anchored on the cancellation itself: give the same full window before chasing again.
            if (cancelledRecently.Contains(a.Id)) continue;

            int? pid = participantIdByEmail.TryGetValue(
                (a.Email ?? string.Empty).Trim().ToLowerInvariant(), out var found) ? found : null;
            var tokens = _templates.NewTokenSet(pid);
            tokens["firstName"] = string.IsNullOrWhiteSpace(a.FirstName) ? "there" : a.FirstName;
            tokens["eventDisplayName"] = eventDisplayName;
            var rendered = _templates.Render("pending-master-class-selection", tokens);
            due.Add(new ReminderMessage(
                a.Email, "pending-master-class-selection", $"pendingmc:{a.Email}",
                rendered.Subject, rendered.HtmlBody, MailKey: "pending-master-class-selection"));
        }

        await _db.SaveChangesAsync(ct);
        return await _engine.SendDueAsync(eventId, due, ct);
    }

    /// <summary>Upsert the per-edition last-successful-sync marker (§127) used by the
    /// telemetry "Updated &lt;t&gt;" footer, with the run's active/cancelled tallies.</summary>
    private async Task RecordSyncMarkerAsync(int eventId, AttendeeTicketSyncService.SyncResult r, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var marker = await _db.SyncRuns.FirstOrDefaultAsync(
            s => s.EventId == eventId && s.Key == SyncRun.AttendeeBackstageKey, ct);
        if (marker is null)
        {
            marker = new SyncRun { EventId = eventId, Key = SyncRun.AttendeeBackstageKey, CreatedAt = now };
            _db.SyncRuns.Add(marker);
        }
        marker.LastSuccessAt = now;
        marker.OrdersActive = r.OrdersActive;
        marker.OrdersCancelled = r.OrdersCancelled;
        marker.AttendeesActive = r.AttendeesActive;
        marker.AttendeesCancelled = r.Cancelled;
        marker.Summary = $"{r.OrdersActive} active orders / {r.AttendeesActive} active attendees "
            + $"(created {r.Created}, updated {r.Updated}, cancelled {r.Cancelled})";
        await _db.SaveChangesAsync(ct);
    }
}
