using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// THE single, authoritative one-way Zoho→CEH sync for the FULL Backstage dataset —
/// orders + every ticket/attendee, not just 2-day (REQUIREMENTS §125). Keyed on the
/// STABLE ticket id (§6): a reassigned ticket (same id, new name/email) <b>updates the
/// same attendee row</b>, so their Master Class selection (linked to the AttendeeId)
/// <b>transfers to the new holder</b> instead of orphaning. Detects: NEW tickets, plain
/// UPDATES, REASSIGNMENTS (email changed → email the new holder to validate their
/// inherited MC), CANCELLATIONS (ticket/order gone from the pull → SOFT-cancel: flip
/// <see cref="MirrorState"/> to Cancelled, release the MC seat so the waitlist promotes,
/// keep the row + history), and REAPPEARANCES (a ticket/order that returns flips back to
/// Active). CEH NEVER writes/deletes anything in Zoho — the local mirror is reconciled to
/// match Zoho's ACTIVE set exactly (§128). Returns the events the caller emails.
///
/// <para>§216 (login lifecycle): cancellation/re-purchase also drives HUB LOGIN. When a
/// cancellation leaves an email with no active ticket, its Attendee-role login Participant is
/// DEACTIVATED (locked out); a re-purchase that makes a ticket active again RESTORES login.
/// §230: a REASSIGNMENT reconciles BOTH holders — the previous holder is locked out (their
/// §169 magic link stops resolving) and the new holder's login is restored/provisioned, so a
/// renamed ticket's new holder gets their OWN welcome + magic link and the old link dies.
/// Reconciled idempotently against the committed mirror by
/// <see cref="ReconcileParticipantLoginsAsync"/> at the end of each reconcile.</para>
/// </summary>
public sealed class AttendeeTicketSyncService
{
    private readonly CommunityHubDbContext _db;
    private readonly MasterClassSignupService _mc;

    public AttendeeTicketSyncService(CommunityHubDbContext db, MasterClassSignupService mc)
    {
        _db = db;
        _mc = mc;
    }

    public sealed record TicketRow(
        string TicketId, string FirstName, string LastName, string Email,
        TicketStatus Status, string? TicketClassName,
        string? OrderId = null, string? CompanyName = null, string? JobTitle = null,
        string? Phone = null, string? Country = null, string? CountryCode = null,
        string? City = null, string? Postcode = null, string? TaxId = null,
        string? CustomFieldsJson = null,
        // §326as — Zoho says so ITSELF: this ticket's status_string is "not_attending".
        // The ONLY thing that may cancel a mirrored ticket (see CancelledUpstream policy).
        bool CancelledUpstream = false,
        // §707.35b — Zoho's STABLE ticket_class_id, carried through so it can be PERSISTED.
        // It was always available here and always thrown away, which left every downstream
        // consumer with only the editable display NAME to go on.
        string? TicketClassId = null);

    /// <summary>
    /// §326as — is this Zoho status an explicit CANCELLATION?
    ///
    /// <para>Verified against the real ELDK26 event (1204 tickets): Zoho KEEPS cancelled
    /// attendees in the feed and marks them <c>status_string = "not_attending"</c>
    /// (1158 attending / 46 not_attending). Orders likewise carry "placed" / "cancelled".
    /// So cancellation is a POSITIVE signal we can read — it never needed to be inferred
    /// from a row's absence, which is what made a bad API read so destructive.</para>
    ///
    /// <para>Deliberately a WHITELIST: only the exact documented values cancel. An unknown
    /// or new status leaves the person alone, because the cost of ignoring a real
    /// cancellation (a seat held too long) is trivial next to the cost of acting on a
    /// misread one (a released seat, waitlist mail we cannot recall, a revoked login).</para>
    /// </summary>
    public static bool IsCancelledTicketStatus(string? zohoStatusString) =>
        string.Equals(zohoStatusString?.Trim(), "not_attending", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc cref="IsCancelledTicketStatus"/>
    public static bool IsCancelledOrderStatus(string? zohoStatusString)
    {
        var s = zohoStatusString?.Trim();
        return string.Equals(s, "cancelled", StringComparison.OrdinalIgnoreCase)
               || string.Equals(s, "canceled", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An order-level mirror row pulled from Backstage (REQUIREMENTS §125).</summary>
    public sealed record OrderRow(
        string OrderId, string? BuyerName, string? BuyerEmail, string? CompanyName,
        string? Country, string? CountryCode, string? City, string? Postcode,
        string? TaxId, string? OrderStatus, DateTimeOffset? SourceCreatedAt, string? RawJson);

    /// <summary>Map an enriched Backstage attendee to a sync row (Master Class eligibility
    /// detected via the shared <see cref="MasterClassTicketPolicy"/> — the ONE 2-day
    /// definition, §125).</summary>
    /// <param name="twoDayClassIds">§447 — the edition's Zoho <c>ticket_class_id</c>(s) that grant
    /// Master Class access. When supplied AND the ticket carries a class id, the ID decides, so a
    /// class RENAME cannot change the verdict. Null/empty ⇒ the name markers decide exactly as
    /// before, which is what keeps historic orders — and any payload without a class id —
    /// resolving unchanged.</param>
    public static TicketRow FromBackstage(
        CommunityHub.Core.Integrations.BackstageAttendee a,
        IEnumerable<string>? twoDayClassIds = null)
    {
        var isTwoDay = MasterClassTicketPolicy.IncludesMasterClass(
            a.TicketClassId, a.TicketClassName, twoDayClassIds);
        var status = !a.Attending ? TicketStatus.None
            : isTwoDay ? TicketStatus.TwoDay : TicketStatus.Other;
        return new TicketRow(a.TicketId, a.FirstName, a.LastName, a.Email, status, a.TicketClassName,
            a.OrderId, a.CompanyName, a.JobTitle, a.Phone, a.Country, a.CountryCode, a.City, a.Postcode,
            a.TaxId, a.CustomFieldsJson,
            CancelledUpstream: IsCancelledTicketStatus(a.StatusString),   // §326as
            TicketClassId: a.TicketClassId);                              // §707.35b
    }

    /// <summary>Map a Backstage order to an order-mirror row (REQUIREMENTS §125).</summary>
    public static OrderRow FromBackstageOrder(CommunityHub.Core.Integrations.BackstageOrder o) =>
        new(o.OrderId, o.BuyerName, o.BuyerEmail, o.CompanyName, o.Country, o.CountryCode,
            o.City, o.Postcode, o.TaxId, o.OrderStatus, o.SourceCreatedAt, o.RawJson);

    private static void Apply(Attendee a, TicketRow t)
    {
        a.FirstName = t.FirstName; a.LastName = t.LastName;
        a.FullName = $"{t.FirstName} {t.LastName}".Trim();
        a.TicketStatus = t.Status; a.TicketClassName = t.TicketClassName;
        // §707.35b — PERSIST the stable class id. Only overwrite when the feed actually carries one,
        // so a payload without an id never ERASES an id we already hold (that would silently demote
        // a row back to name-matching).
        if (!string.IsNullOrWhiteSpace(t.TicketClassId)) a.TicketClassId = t.TicketClassId;
        a.OrderId = t.OrderId; a.CompanyName = t.CompanyName; a.JobTitle = t.JobTitle;
        a.Phone = t.Phone; a.Country = t.Country; a.CountryCode = t.CountryCode;
        a.City = t.City; a.Postcode = t.Postcode; a.TaxId = t.TaxId;
        a.CustomFieldsJson = t.CustomFieldsJson;
    }

    private static void ApplyOrder(Order o, OrderRow r)
    {
        o.BuyerName = r.BuyerName; o.BuyerEmail = r.BuyerEmail; o.CompanyName = r.CompanyName;
        o.Country = r.Country; o.CountryCode = r.CountryCode; o.City = r.City;
        o.Postcode = r.Postcode; o.TaxId = r.TaxId; o.OrderStatus = r.OrderStatus;
        o.SourceCreatedAt = r.SourceCreatedAt; o.RawJson = r.RawJson;
    }

    /// <summary>A ticket reassigned to a new person who inherited a Master Class — email them.</summary>
    public sealed record Reassignment(int AttendeeId, string NewEmail, string NewName, string? InheritedMcTitle);

    public sealed record SyncResult(
        int Created, int Updated, int Reassigned, int Cancelled, int Reactivated,
        int OrdersCreated, int OrdersUpdated, int OrdersCancelled, int OrdersReactivated,
        int OrdersActive, int AttendeesActive,
        IReadOnlyList<Reassignment> Reassignments,
        IReadOnlyList<MasterClassSignupService.PromotionResult> FreedPromotions,
        // §326as — rows we hold that the (complete) pull did not mention at all. NOT
        // cancelled, only reported: with a proven-complete read this means a hard delete
        // upstream, which an operator should look at rather than a job guess about.
        int AbsentNotCancelled = 0,
        // §326aq CIRCUIT BREAKER — how many cancellations this run REFUSED to apply because
        // the batch was implausibly large. Non-zero means the mirror was deliberately left
        // alone: the caller must alert, and nothing was cancelled (not even the plausible
        // ones — a partial apply would be the same guess with extra steps).
        int CancelSuppressed = 0, int OrdersCancelSuppressed = 0);

    /// <summary>
    /// §326aq (operator 2026-07-25: "that must NOT happen — never!!!! — then we better redesign
    /// things so this can not happen due to some integration issue"). A CAUSE-INDEPENDENT
    /// circuit breaker on the reconcile's only destructive act.
    ///
    /// <para>The §326ao guards close the two failure modes we KNOW about (a truncated read, an
    /// empty read). This closes the class: whatever the cause — a Zoho bug, a wrong portal id,
    /// an auth blip that still answers 200, a schema change that makes every ticket id parse as
    /// empty — a single run may never cancel more than <see cref="CancelFraction"/> of the
    /// active mirror, with a floor of <see cref="CancelFloor"/> rows so ordinary refund traffic
    /// (and small test events) is untouched. Real bulk cancellations DO happen, so this is not
    /// a hard stop: the operator sees the alert and can act, and every later run re-offers the
    /// same cancellations. Deferring a real cancellation costs a delayed seat release;
    /// applying a false one sends waitlist-promotion mail we cannot unsend.</para>
    /// </summary>
    public const int CancelFloor = 10;
    /// <inheritdoc cref="CancelFloor"/>
    public const double CancelFraction = 0.25;

    /// <summary>§326aq — true when cancelling <paramref name="wouldCancel"/> of
    /// <paramref name="active"/> rows is too big a blast to apply unsupervised.</summary>
    public static bool CancelBatchIsImplausible(int wouldCancel, int active) =>
        wouldCancel > Math.Max(CancelFloor, (int)Math.Ceiling(active * CancelFraction));

    /// <summary>
    /// Reconcile the local mirror to the pulled Zoho dataset. <paramref name="orders"/> is
    /// the FULL order set — pass it (even empty) to mirror + reconcile orders; pass
    /// <c>null</c> to skip the order half entirely (the legacy ticket-only path). The
    /// ticket-only reconcile (soft-cancel + reassignment + reappear) always runs.
    /// </summary>
    public async Task<SyncResult> SyncAsync(
        int eventId,
        IReadOnlyList<TicketRow> tickets,
        IReadOnlyList<OrderRow>? orders = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // ---- 1. ORDERS: upsert + reconcile (only when an order set was pulled) ----
        int ordersCreated = 0, ordersUpdated = 0, ordersCancelled = 0, ordersReactivated = 0;
        int ordersCancelSuppressed = 0, cancelSuppressed = 0;   // §326aq
        // §326as: ticket ids Zoho itself reports as "not_attending" in THIS pull.
        var upstreamCancelled = new HashSet<string>(StringComparer.Ordinal);
        var knownOrderIds = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, Order>? existingOrders = null;
        if (orders is not null)
        {
            existingOrders = await _db.Orders
                .Where(o => o.EventId == eventId)
                .ToDictionaryAsync(o => o.BackstageOrderId, o => o, ct);
            var seenOrders = new HashSet<string>(StringComparer.Ordinal);
            var cancelledOrderIds = new HashSet<string>(StringComparer.Ordinal);   // §326as
            foreach (var o in orders)
            {
                if (string.IsNullOrWhiteSpace(o.OrderId)) continue;
                seenOrders.Add(o.OrderId);
                knownOrderIds.Add(o.OrderId);
                // §326as: Zoho's own verdict for this order.
                if (IsCancelledOrderStatus(o.OrderStatus)) cancelledOrderIds.Add(o.OrderId);
                if (existingOrders.TryGetValue(o.OrderId, out var row))
                {
                    // §326as: a cancelled order is PRESENT in every pull, so presence alone
                    // can no longer reactivate it — only Zoho calling it live again can.
                    if (row.MirrorState == MirrorState.Cancelled && !IsCancelledOrderStatus(o.OrderStatus))
                    { row.MirrorState = MirrorState.Active; row.CancelledAt = null; ordersReactivated++; }
                    ApplyOrder(row, o);
                    row.LastSyncedAt = now;
                    ordersUpdated++;
                }
                else
                {
                    var nr = new Order
                    {
                        EventId = eventId, BackstageOrderId = o.OrderId,
                        MirrorState = MirrorState.Active, CreatedAt = now, LastSyncedAt = now,
                    };
                    ApplyOrder(nr, o);
                    _db.Orders.Add(nr);
                    existingOrders[o.OrderId] = nr;
                    ordersCreated++;
                }
            }
            // §326as: cancel the orders ZOHO marks cancelled (status_string "cancelled" —
            // confirmed on ELDK26: "placed" vs "cancelled", and cancelled orders STAY in
            // the feed). Absence is no longer a cancellation trigger; it is reported only.
            // §326aq: decide the whole batch FIRST, then apply it only if it is plausible.
            var ordersToCancel = existingOrders
                .Where(kv => kv.Value.MirrorState == MirrorState.Active
                             && cancelledOrderIds.Contains(kv.Key))
                .Select(kv => kv.Value)
                .ToList();
            var ordersActiveBefore = existingOrders.Values.Count(o => o.MirrorState == MirrorState.Active);
            if (CancelBatchIsImplausible(ordersToCancel.Count, ordersActiveBefore))
            {
                ordersCancelSuppressed = ordersToCancel.Count;
            }
            else
            {
                foreach (var row in ordersToCancel)
                {
                    row.MirrorState = MirrorState.Cancelled;
                    row.CancelledAt = now;
                    ordersCancelled++;
                }
            }
        }

        // ---- 2. ATTENDEES: upsert (keyed on stable ticket id) --------------------
        var existing = await _db.Attendees
            .Where(a => a.EventId == eventId && a.BackstageTicketId != null)
            .ToDictionaryAsync(a => a.BackstageTicketId!, a => a, ct);

        // §253 G16: PRE-SYNC per-email mirror state — used to detect a RETURNING
        // attendee (an email whose every prior ticket was cancelled) buying a NEW
        // ticket (new BackstageTicketId), which the created-branch below would
        // otherwise treat as a plain first-time provision while the stale
        // cancel-time party "No" + the once-ever invite/chaser ledger stamps block
        // every re-engagement email.
        var priorByEmail = existing.Values
            .Where(a => !string.IsNullOrWhiteSpace(a.Email))
            .GroupBy(a => a.Email.Trim().ToLowerInvariant())
            .ToDictionary(
                g => g.Key,
                g => (HasActive: g.Any(x => x.MirrorState == MirrorState.Active),
                      MaxCancelledAt: g.Max(x => x.CancelledAt)));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int created = 0, updated = 0, reassigned = 0, cancelled = 0, reactivated = 0;
        var reassignments = new List<Reassignment>();
        var freed = new List<MasterClassSignupService.PromotionResult>();
        // §216: emails whose login Participant must be reconciled (deactivate on
        // cancellation, restore on re-purchase). Reconciled once at the end (below).
        var loginAffected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // §253 G16: returning emails to RE-ENGAGE (welcome/invite + party re-ask),
        // each with the cancellation instant its stale state was stamped at.
        var reEngage = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in tickets)
        {
            if (string.IsNullOrWhiteSpace(t.TicketId)) continue;     // can't key without an id
            seen.Add(t.TicketId);
            var email = (t.Email ?? "").Trim().ToLowerInvariant();

            // FK safety: a ticket may reference an order missing from the orders pull —
            // create a minimal Active order stub so the (EventId, OrderId) link resolves.
            if (orders is not null && existingOrders is not null
                && !string.IsNullOrWhiteSpace(t.OrderId) && !knownOrderIds.Contains(t.OrderId!))
            {
                _db.Orders.Add(new Order
                {
                    EventId = eventId, BackstageOrderId = t.OrderId!,
                    CompanyName = t.CompanyName, Country = t.Country, CountryCode = t.CountryCode,
                    City = t.City, Postcode = t.Postcode, TaxId = t.TaxId,
                    MirrorState = MirrorState.Active, CreatedAt = now, LastSyncedAt = now,
                });
                knownOrderIds.Add(t.OrderId!);
            }

            // §326as: Zoho's own verdict on this ticket. A "not_attending" row is an
            // EXPLICIT cancellation that stays in the feed — it must neither be revived
            // by the reappear arm below nor counted as a live ticket.
            if (t.CancelledUpstream) upstreamCancelled.Add(t.TicketId);

            if (existing.TryGetValue(t.TicketId, out var a))
            {
                // REAPPEAR: a previously soft-cancelled ticket is back in the pull → Active.
                // §326as: only when Zoho still calls it attending — a cancelled ticket is
                // PRESENT in every pull, so presence alone can no longer mean "they're back".
                var wasReactivated = false;
                var priorCancelledAt = a.CancelledAt;   // §253 G16: the stale-state stamp instant
                if (a.MirrorState == MirrorState.Cancelled && !t.CancelledUpstream)
                { a.MirrorState = MirrorState.Active; a.CancelledAt = null; reactivated++; wasReactivated = true; }

                var isReassign = !string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)
                                 && !string.IsNullOrWhiteSpace(email);
                Apply(a, t);
                a.LastSyncedAt = now;
                if (isReassign)
                {
                    var oldEmail = a.Email;     // the PREVIOUS holder (Apply leaves Email untouched)
                    // 🔒 §707.23 — KEEP THE OLD REFERENCE. The row is keyed on the ticket id, so
                    // without this the previous holder is overwritten without trace. Matters most
                    // for a bulk order assigned from placeholder plus-addresses to real people.
                    a.PreviousEmail = oldEmail;
                    a.ReassignedAt = now;
                    a.Email = email;
                    reassigned++;
                    // The inherited Master Class is KEPT (never cancelled on reassign) —
                    // the MasterClassSignup stays linked to this same attendee row, so the
                    // confirmed seat transfers to the new holder automatically (seat stays
                    // HELD, never released — no double-count).
                    var mcTitle = await _db.MasterClassSignups.AsNoTracking()
                        .Where(s => s.EventId == eventId && s.AttendeeId == a.Id
                                    && s.Status == MasterClassSignupStatus.Confirmed)
                        .Select(s => s.Session.Title).FirstOrDefaultAsync(ct);
                    // If they inherited an MC, don't re-prompt selection (the
                    // reassignment-VALIDATION email covers them); only re-open the
                    // selection invite when there's nothing inherited to validate.
                    a.MasterClassInviteSentAt = string.IsNullOrEmpty(mcTitle) ? null : now;
                    reassignments.Add(new Reassignment(a.Id, email, $"{t.FirstName} {t.LastName}".Trim(), mcTitle));
                    // §209: the PREVIOUS holder is gone — cancel THEIR party reservation so
                    // it no longer counts. The new holder (keyed by the new email) has no
                    // party row yet, so they start UNANSWERED and must choose their own (the
                    // old party answer is NOT inherited).
                    await CancelPartyRsvpAsync(eventId, oldEmail, now, ct);
                    // §230: a REASSIGNMENT changes who holds the ticket — reconcile BOTH
                    // login Participants at the end. The OLD holder (no active ticket left)
                    // is LOCKED OUT so their magic link stops working; the NEW holder's
                    // login (if a previously-deactivated Participant exists) is RESTORED.
                    loginAffected.Add(oldEmail);
                    loginAffected.Add(email);
                }
                else updated++;
                // §216: a RE-PURCHASE (the ticket became active again) restores login —
                // reconcile the (final, post-reassign) email's Participant at the end.
                // §253 G16: it also RE-ENGAGES the holder — clear the stale cancel-time
                // state so the welcome/invite + party re-ask fire again (below).
                if (wasReactivated)
                {
                    loginAffected.Add(a.Email);
                    AddReEngage(reEngage, a.Email, priorCancelledAt);
                }
            }
            else
            {
                var na = new Attendee
                {
                    EventId = eventId, BackstageTicketId = t.TicketId, Email = email,
                    // §326as: a ticket we have never seen that Zoho ALREADY calls
                    // "not_attending" is mirrored as cancelled from birth — never created
                    // Active and then cancelled, which would release a seat it never held
                    // and mail a waitlist that was never blocked.
                    MirrorState = t.CancelledUpstream ? MirrorState.Cancelled : MirrorState.Active,
                    CancelledAt = t.CancelledUpstream ? now : null,
                    CreatedAt = now, LastSyncedAt = now,
                };
                Apply(na, t);
                _db.Attendees.Add(na);
                created++;
                // §253 G16: a NEW ticket for an email whose every PRIOR ticket was
                // cancelled = a RETURNING attendee — re-engage them (below).
                if (!t.CancelledUpstream
                    && priorByEmail.TryGetValue(email, out var prior) && !prior.HasActive)
                {
                    loginAffected.Add(email);
                    AddReEngage(reEngage, email, prior.MaxCancelledAt);
                }
            }
        }
        await _db.SaveChangesAsync(ct);

        // ---- 3. CANCEL the tickets ZOHO SAYS are cancelled (§128, redesigned §326as) ----
        // A cancelled ticket → release its MC seat (waitlist promotes per §93/§94), flip
        // MirrorState to Cancelled and stamp CancelledAt. The row + history are KEPT;
        // TicketStatus is left intact (cancellation rides MirrorState — §126/§128).
        //
        // §326as: the trigger is now Zoho's OWN "not_attending" status, not the ticket's
        // ABSENCE from the pull. Verified on real data: Zoho keeps cancelled tickets in the
        // feed, so absence never meant cancellation — it only ever meant the read was
        // incomplete, which is exactly how a broken pager or a bad API response turned into
        // mass cancellation. Absence is now reported (AbsentNotCancelled) and never acted on.
        //
        // §326aq CIRCUIT BREAKER retained as defence in depth: decide the whole batch FIRST.
        // This is the only destructive act in the reconcile and the only one with
        // IRREVERSIBLE side effects — releasing a seat runs the waitlist promotion, which
        // SENDS mail to other people. An implausibly large batch is left entirely unapplied.
        var toCancel = existing
            .Where(kv => upstreamCancelled.Contains(kv.Key) && kv.Value.MirrorState == MirrorState.Active)
            .Select(kv => kv.Value)
            .ToList();

        // Rows we hold that Zoho did not mention AT ALL. With a complete read (the pager
        // now proves completeness against Zoho's own total_count) this means the ticket was
        // hard-deleted upstream — rare, and not something to guess about. Report it; the
        // job raises it for the operator. Never mutate.
        var absentNotCancelled = existing
            .Count(kv => !seen.Contains(kv.Key) && kv.Value.MirrorState == MirrorState.Active);
        var attendeesActiveBefore = existing.Values.Count(a => a.MirrorState == MirrorState.Active);
        if (CancelBatchIsImplausible(toCancel.Count, attendeesActiveBefore))
        {
            cancelSuppressed = toCancel.Count;
        }
        else
        {
            foreach (var a in toCancel)
            {
                await SoftCancelAttendeeAsync(eventId, a, now, freed, loginAffected, ct);
                cancelled++;
            }
            if (cancelled > 0) await _db.SaveChangesAsync(ct);
        }

        // §216: lock out / restore the login Participant for every attendee whose ticket
        // was cancelled or re-purchased in this reconcile (runs AFTER the mirror saves so
        // it reads the committed MirrorState). No-op when nothing changed.
        await ReconcileParticipantLoginsAsync(eventId, loginAffected, ct);

        // §253 G16: RE-ENGAGE returning attendees (re-purchase / new ticket after a
        // full cancellation) — clear the cancel-time party "No", re-arm the §241
        // selection invite (the 2-day welcome) and the pendingmc chaser ledger.
        await ReEngageReturningAttendeesAsync(eventId, reEngage, ct);

        // ---- 4. Active-set tallies for the last-successful-sync marker (§127) -----
        var ordersActive = orders is null
            ? 0
            : await _db.Orders.CountAsync(o => o.EventId == eventId && o.MirrorState == MirrorState.Active, ct);
        var attendeesActive = await _db.Attendees
            .CountAsync(a => a.EventId == eventId && a.MirrorState == MirrorState.Active, ct);

        return new SyncResult(
            created, updated, reassigned, cancelled, reactivated,
            ordersCreated, ordersUpdated, ordersCancelled, ordersReactivated,
            ordersActive, attendeesActive,
            reassignments, freed,
            AbsentNotCancelled: absentNotCancelled,          // §326as
            CancelSuppressed: cancelSuppressed,              // §326aq
            OrdersCancelSuppressed: ordersCancelSuppressed);
    }

    /// <summary>The outcome of an INCREMENTAL single-order reconcile (REQUIREMENTS §128,
    /// the webhook path). Same shape as <see cref="SyncResult"/> but scoped to one order.</summary>
    public sealed record OrderSyncResult(
        string OrderId,
        bool OrderCreated, bool OrderUpdated, bool OrderCancelled, bool OrderReactivated,
        int Created, int Updated, int Reassigned, int Cancelled, int Reactivated,
        IReadOnlyList<Reassignment> Reassignments,
        IReadOnlyList<MasterClassSignupService.PromotionResult> FreedPromotions);

    /// <summary>
    /// INCREMENTAL reconcile of a SINGLE order (REQUIREMENTS §128 — the real-time Zoho
    /// Backstage order-change webhook path). Applies exactly the same upsert + soft-cancel +
    /// reassignment + reappear semantics as <see cref="SyncAsync"/>, but the reconcile is
    /// SCOPED to this one order: only attendees linked to <paramref name="orderId"/> are
    /// considered, so it never touches (let alone cancels) the rest of the mirror. The hourly
    /// <c>SyncAsync</c> remains the full drift safety-net.
    /// <list type="bullet">
    /// <item><paramref name="order"/> = the order as Zoho currently returns it (null when it
    /// is gone from Zoho's active set).</item>
    /// <item><paramref name="ticketsForOrder"/> = the tickets/attendees Zoho currently returns
    /// FOR this order (any rows with a different <c>OrderId</c> are ignored).</item>
    /// <item><paramref name="orderRemoved"/> = force whole-order cancellation (e.g. an Event
    /// Order <c>Cancel</c>/<c>Delete</c> webhook). Also inferred when <paramref name="order"/>
    /// is null or its status string says "cancel".</item>
    /// </list>
    /// CEH NEVER writes/deletes anything in Zoho.
    /// </summary>
    public async Task<OrderSyncResult> SyncOrderAsync(
        int eventId,
        string orderId,
        OrderRow? order,
        IReadOnlyList<TicketRow> ticketsForOrder,
        bool orderRemoved = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderId))
            throw new ArgumentException("orderId is required.", nameof(orderId));

        var now = DateTimeOffset.UtcNow;
        var reassignments = new List<Reassignment>();
        var freed = new List<MasterClassSignupService.PromotionResult>();
        // §216: emails whose login Participant must be reconciled (see SyncAsync).
        var loginAffected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // §253 G16: returning emails to RE-ENGAGE (see SyncAsync).
        var reEngage = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        int created = 0, updated = 0, reassigned = 0, cancelled = 0, reactivated = 0;
        bool orderCreated = false, orderUpdated = false, orderCancelled = false, orderReactivated = false;

        // A whole-order cancellation when forced, or the order is gone, or Zoho's status says so.
        var orderIsCancelled = orderRemoved || order is null
            || (order.OrderStatus is { } st && st.Contains("cancel", StringComparison.OrdinalIgnoreCase));

        // ---- 1. ORDER row (scoped to this single order) -------------------------
        var existingOrder = await _db.Orders
            .FirstOrDefaultAsync(o => o.EventId == eventId && o.BackstageOrderId == orderId, ct);

        if (orderIsCancelled)
        {
            if (existingOrder is { MirrorState: MirrorState.Active })
            {
                existingOrder.MirrorState = MirrorState.Cancelled;
                existingOrder.CancelledAt = now;
                existingOrder.LastSyncedAt = now;
                orderCancelled = true;
            }
        }
        else if (order is not null)
        {
            if (existingOrder is null)
            {
                existingOrder = new Order
                {
                    EventId = eventId, BackstageOrderId = orderId,
                    MirrorState = MirrorState.Active, CreatedAt = now, LastSyncedAt = now,
                };
                ApplyOrder(existingOrder, order);
                _db.Orders.Add(existingOrder);
                orderCreated = true;
            }
            else
            {
                if (existingOrder.MirrorState == MirrorState.Cancelled)
                { existingOrder.MirrorState = MirrorState.Active; existingOrder.CancelledAt = null; orderReactivated = true; }
                ApplyOrder(existingOrder, order);
                existingOrder.LastSyncedAt = now;
                orderUpdated = true;
            }
        }

        // ---- 2. ATTENDEES of THIS order: upsert (keyed on stable ticket id) -----
        // Candidates for soft-cancel = the rows currently linked to this order locally.
        var localForOrder = await _db.Attendees
            .Where(a => a.EventId == eventId && a.OrderId == orderId && a.BackstageTicketId != null)
            .ToListAsync(ct);
        var toCancel = localForOrder.ToDictionary(a => a.BackstageTicketId!, a => a, StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var incoming = orderIsCancelled ? Array.Empty<TicketRow>() : ticketsForOrder;
        foreach (var t in incoming)
        {
            if (string.IsNullOrWhiteSpace(t.TicketId)) continue;
            if (!string.Equals(t.OrderId, orderId, StringComparison.Ordinal)) continue;  // only this order
            seen.Add(t.TicketId);
            var email = (t.Email ?? "").Trim().ToLowerInvariant();

            // Look up event-wide by ticket id (handles reappear + a ticket moved here from
            // another order). Same key as the full sync (§6).
            var a = await _db.Attendees
                .FirstOrDefaultAsync(x => x.EventId == eventId && x.BackstageTicketId == t.TicketId, ct);
            if (a is not null)
            {
                var wasReactivated = false;
                var priorCancelledAt = a.CancelledAt;   // §253 G16
                if (a.MirrorState == MirrorState.Cancelled)
                { a.MirrorState = MirrorState.Active; a.CancelledAt = null; reactivated++; wasReactivated = true; }

                var isReassign = !string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)
                                 && !string.IsNullOrWhiteSpace(email);
                Apply(a, t);
                a.LastSyncedAt = now;
                if (isReassign)
                {
                    var oldEmail = a.Email;     // the PREVIOUS holder (Apply leaves Email untouched)
                    // 🔒 §707.23 — KEEP THE OLD REFERENCE. The row is keyed on the ticket id, so
                    // without this the previous holder is overwritten without trace. Matters most
                    // for a bulk order assigned from placeholder plus-addresses to real people.
                    a.PreviousEmail = oldEmail;
                    a.ReassignedAt = now;
                    a.Email = email;
                    reassigned++;
                    var mcTitle = await _db.MasterClassSignups.AsNoTracking()
                        .Where(s => s.EventId == eventId && s.AttendeeId == a.Id
                                    && s.Status == MasterClassSignupStatus.Confirmed)
                        .Select(s => s.Session.Title).FirstOrDefaultAsync(ct);
                    a.MasterClassInviteSentAt = string.IsNullOrEmpty(mcTitle) ? null : now;
                    reassignments.Add(new Reassignment(a.Id, email, $"{t.FirstName} {t.LastName}".Trim(), mcTitle));
                    // §209: cancel the PREVIOUS holder's party reservation; the new holder
                    // starts UNANSWERED (same as the full sync above).
                    await CancelPartyRsvpAsync(eventId, oldEmail, now, ct);
                    // §230: reconcile BOTH holders' logins (lock out the old, restore the
                    // new) — same as the full sync above.
                    loginAffected.Add(oldEmail);
                    loginAffected.Add(email);
                }
                else updated++;
                // §216: a RE-PURCHASE restores login — reconcile the final email at the end.
                // §253 G16: and RE-ENGAGES the holder (below).
                if (wasReactivated)
                {
                    loginAffected.Add(a.Email);
                    AddReEngage(reEngage, a.Email, priorCancelledAt);
                }
                toCancel.Remove(t.TicketId);   // still present ⇒ not a cancellation
            }
            else
            {
                var na = new Attendee
                {
                    EventId = eventId, BackstageTicketId = t.TicketId, Email = email,
                    MirrorState = MirrorState.Active, CreatedAt = now, LastSyncedAt = now,
                };
                Apply(na, t);
                _db.Attendees.Add(na);
                created++;
                // §253 G16: a NEW ticket for an email whose every PRIOR ticket was
                // cancelled = a RETURNING attendee — re-engage them (below).
                if (!string.IsNullOrWhiteSpace(email))
                {
                    var priorRows = await _db.Attendees.AsNoTracking()
                        .Where(x => x.EventId == eventId && x.Email.ToLower() == email)
                        .Select(x => new { x.MirrorState, x.CancelledAt })
                        .ToListAsync(ct);
                    if (priorRows.Count > 0 && !priorRows.Any(x => x.MirrorState == MirrorState.Active))
                    {
                        loginAffected.Add(email);
                        AddReEngage(reEngage, email, priorRows.Max(x => x.CancelledAt));
                    }
                }
            }
        }
        await _db.SaveChangesAsync(ct);

        // ---- 3. SOFT-CANCEL this order's attendees that are gone from the pull ---
        foreach (var (_, a) in toCancel)
        {
            if (a.MirrorState != MirrorState.Active) continue;   // already soft-cancelled
            await SoftCancelAttendeeAsync(eventId, a, now, freed, loginAffected, ct);
            cancelled++;
        }
        if (cancelled > 0) await _db.SaveChangesAsync(ct);

        // §216: lock out / restore login Participants touched by this order's reconcile.
        await ReconcileParticipantLoginsAsync(eventId, loginAffected, ct);

        // §253 G16: re-engage returning attendees (see SyncAsync).
        await ReEngageReturningAttendeesAsync(eventId, reEngage, ct);

        return new OrderSyncResult(
            orderId, orderCreated, orderUpdated, orderCancelled, orderReactivated,
            created, updated, reassigned, cancelled, reactivated,
            reassignments, freed);
    }

    /// <summary>
    /// SOFT-CANCEL one attendee (REQUIREMENTS §128): release every Master-Class seat it
    /// holds (the waitlist promotes per §93/§94, promotions collected into
    /// <paramref name="freed"/>), then flip <see cref="MirrorState"/> to Cancelled and stamp
    /// <see cref="Attendee.CancelledAt"/>. The row + history are KEPT (cancellation rides
    /// MirrorState, §126/§128).
    ///
    /// <para>⚠️ §707.34b — THIS METHOD does not touch <see cref="Attendee.TicketStatus"/>, but the
    /// SYNC AS A WHOLE does: <c>Apply</c> runs first on every existing row and writes
    /// <c>a.TicketStatus = t.Status</c>, so a <c>not_attending</c> ticket arrives as
    /// <see cref="TicketStatus.None"/> and the field is already zeroed by the time we get here.
    /// This comment used to claim TicketStatus was "left intact", which is true of this method in
    /// isolation and FALSE of the observable outcome — verified on PROD 2026-07-30 (row 7312 went
    /// 1 → 0 on cancel). <see cref="Attendee.TicketClassName"/> survives, so the class a cancelled
    /// row held is still readable. Harmless for the eligibility checks that consume TicketStatus
    /// (a cancelled holder should not be eligible) and self-healing on reactivation, but do not
    /// rely on TicketStatus to tell you what a CANCELLED row once held.</para>
    ///
    /// Shared by the full <see cref="SyncAsync"/> and the incremental
    /// <see cref="SyncOrderAsync"/> so both reconciles behave identically. Does NOT save.
    /// </summary>
    private async Task SoftCancelAttendeeAsync(
        int eventId, Attendee a, DateTimeOffset now,
        List<MasterClassSignupService.PromotionResult> freed,
        HashSet<string> loginAffected, CancellationToken ct)
    {
        var sessionIds = await _db.MasterClassSignups.AsNoTracking()
            .Where(s => s.EventId == eventId && s.AttendeeId == a.Id)
            .Select(s => s.SessionId).ToListAsync(ct);
        foreach (var sid in sessionIds)
        {
            var promo = await _mc.RemoveAsync(eventId, a.Id, sid, ct);
            if (promo is not null) freed.Add(promo);
        }
        a.MirrorState = MirrorState.Cancelled;
        a.CancelledAt = now;
        a.LastSyncedAt = now;

        // §209: a cancelled ticket's holder no longer attends — RELEASE/CLEAR their party
        // signup too so it stops counting in the headcount (the Master-Class seat was
        // already released above). Applies to BOTH a 2-day and a 1-day cancellation (this
        // path is ticket-class agnostic; a 1-day holder simply has no MC seat to release).
        await CancelPartyRsvpAsync(eventId, a.Email, now, ct);

        // §216: the holder is cancelled — flag their email so the login Participant is
        // LOCKED OUT (deactivated) in the end-of-reconcile pass (unless another active
        // ticket still keeps the email entitled). Keyed by the same email the provisioning
        // used; the actual toggle happens after SaveChanges so it reads committed state.
        if (!string.IsNullOrWhiteSpace(a.Email)) loginAffected.Add(a.Email);
    }

    /// <summary>
    /// §216 — reconcile the login <see cref="Participant"/> for every attendee email whose
    /// ticket was CANCELLED or RE-PURCHASED in this sync. A cancelled attendee is LOCKED OUT
    /// of the hub (their Attendee-role Participant is set <see cref="Participant.IsActive"/> =
    /// false so they cannot sign in) UNTIL they hold an ACTIVE ticket again; a re-purchase
    /// RESTORES access (IsActive = true). The desired state is computed from the committed
    /// mirror — login is active iff the email still has ≥1 <see cref="MirrorState.Active"/>
    /// attendee row in the edition — so it is correct even when an email holds several tickets
    /// and only some are cancelled, and it is IDEMPOTENT (a re-run reconciles to the same
    /// state, toggling nothing when already correct).
    /// <para>
    /// Matched to the EXACT login identity the provisioning created: the Attendee-role
    /// Participant for this Email + EventId. A person who is ALSO another role (e.g. a speaker
    /// who happens to hold a cancelled attendee ticket) has no Attendee Participant here and is
    /// therefore never locked out by this path. No hard delete — only the IsActive switch (the
    /// onboarding LifecycleState is left untouched). Per-email try/catch so a login-gate hiccup
    /// can NEVER break the authoritative mirror reconcile. Saves only when something changed.
    /// </para>
    /// </summary>
    private async Task ReconcileParticipantLoginsAsync(
        int eventId, IReadOnlyCollection<string> emails, CancellationToken ct)
    {
        if (emails.Count == 0) return;
        var norms = emails
            .Select(e => (e ?? string.Empty).Trim().ToLowerInvariant())
            .Where(e => e.Length > 0)
            .Distinct()
            .ToList();
        if (norms.Count == 0) return;

        var changed = false;
        foreach (var norm in norms)
        {
            try
            {
                // The login Participant provisioned FOR this attendee (Attendee role only —
                // never lock out a different-role login that merely shares the email).
                var participant = await _db.Participants
                    .FirstOrDefaultAsync(p => p.EventId == eventId
                        && p.Role == ParticipantRole.Attendee
                        && p.Email.ToLower() == norm, ct);
                if (participant is null) continue;   // no login identity yet → nothing to gate

                // Entitled to sign in iff the email still holds ≥1 ACTIVE attendee ticket.
                var hasActiveTicket = await _db.Attendees.AnyAsync(a =>
                    a.EventId == eventId
                    && a.MirrorState == MirrorState.Active
                    && a.Email.ToLower() == norm, ct);

                if (participant.IsActive != hasActiveTicket)
                {
                    participant.IsActive = hasActiveTicket;
                    changed = true;
                }
            }
            catch
            {
                // Fail-safe (§216): the login gate is a secondary reconcile — never let it
                // break the primary mirror (MirrorState + released Master-Class seats).
            }
        }
        if (changed) await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// RESET (cancel) the party RSVP held under <paramref name="email"/> so that person no
    /// longer counts toward the party headcount (REQUIREMENTS §209). Two transitions use it:
    /// a CANCELLATION (the holder is leaving) and a REASSIGNMENT (the PREVIOUS holder is
    /// gone — called with their OLD email; the new holder, keyed by their new email, has no
    /// row yet and so starts UNANSWERED and must choose their own).
    /// <para>
    /// CEH mirror model — never hard-delete: the row is KEPT, its opt-in flipped to NOT
    /// attending (and any sponsor head count dropped, matching <see cref="PartyRsvpService"/>),
    /// which removes it from the attending count while preserving history. Matched
    /// case-insensitively because attendee emails are lower-cased but a party RSVP keeps the
    /// casing it was submitted with. Idempotent: a missing or already-declined RSVP is a
    /// no-op (so a re-run double-applies nothing). Best-effort + fail-safe: a party-reset
    /// failure is swallowed so it can NEVER break the authoritative mirror reconcile. Does
    /// NOT save — the caller's <c>SaveChanges</c> covers it.
    /// </summary>
    private async Task CancelPartyRsvpAsync(int eventId, string? email, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        var norm = email.Trim().ToLowerInvariant();
        try
        {
            var rsvp = await _db.PartyRsvps
                .FirstOrDefaultAsync(r => r.EventId == eventId && r.Email.ToLower() == norm, ct);
            if (rsvp is null || !rsvp.Attending) return;   // unanswered or already not-attending
            rsvp.Attending = false;
            rsvp.HeadCount = null;
            rsvp.UpdatedAt = now;
        }
        catch
        {
            // Fail-safe (§209): the party reset is a secondary reconcile — never let it
            // break the primary mirror (MirrorState + the released Master-Class seat).
        }
    }

    /// <summary>§253 G16: remember a returning email + the latest cancellation instant
    /// its stale state was stamped at (kept as the MAX across multiple tickets).</summary>
    private static void AddReEngage(
        Dictionary<string, DateTimeOffset?> reEngage, string? email, DateTimeOffset? cancelledAt)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        var norm = email.Trim().ToLowerInvariant();
        if (!reEngage.TryGetValue(norm, out var existing) || (cancelledAt ?? default) > (existing ?? default))
            reEngage[norm] = cancelledAt;
    }

    /// <summary>
    /// §253 G16 — RE-ENGAGE a RETURNING attendee: an email that had NO active ticket
    /// (fully cancelled) and now holds one again (same-ticket reappearance or a brand
    /// new BackstageTicketId). Without this, three pieces of cancel-time state block
    /// every re-engagement email forever:
    /// <list type="bullet">
    /// <item>the auto-cancelled party row (<c>Attending=false</c>, §209) counts as
    /// "answered", so the party task/cadence never re-asks — DELETED here (only when
    /// its <c>UpdatedAt</c> is at/after the cancellation instant, so a genuine
    /// pre-cancellation "No" is preserved) and the person starts UNANSWERED, exactly
    /// like a §230 reassignment's new holder;</item>
    /// <item><see cref="Attendee.MasterClassInviteSentAt"/> keeps its stamp, so the
    /// §241 selection invite (the de-facto 2-day WELCOME, §215) never re-fires —
    /// CLEARED here when the email holds no confirmed Master-Class seat (the seat was
    /// released at cancellation), so the sync's invite sweep re-welcomes them;</item>
    /// <item>the once-ever <c>pendingmc:{email}</c> chaser ledger row — DELETED here so
    /// the chaser can nag the NEW engagement again.</item>
    /// </list>
    /// Also reopens the person's §234-closed party / Master-Class tasks (their signal
    /// is unanswered again) so the reminder cadence resumes without waiting for a
    /// page-load reconcile. Idempotent per state (a re-run finds nothing left to clear)
    /// and best-effort per email — a re-engagement hiccup never breaks the mirror.
    /// </summary>
    private async Task ReEngageReturningAttendeesAsync(
        int eventId, IReadOnlyDictionary<string, DateTimeOffset?> reEngage, CancellationToken ct)
    {
        if (reEngage.Count == 0) return;

        foreach (var (norm, cancelledAt) in reEngage)
        {
            try
            {
                // Only re-engage an email that NOW holds ≥1 ACTIVE ticket (committed).
                var hasActive = await _db.Attendees.AnyAsync(a =>
                    a.EventId == eventId
                    && a.MirrorState == MirrorState.Active
                    && a.Email.ToLower() == norm, ct);
                if (!hasActive) continue;

                var hasConfirmedSeat = await _db.MasterClassSignups.AnyAsync(s =>
                    s.EventId == eventId
                    && s.Status == MasterClassSignupStatus.Confirmed
                    && s.Attendee.Email.ToLower() == norm, ct);

                // (a) delete the auto-cancelled party "No" so they start UNANSWERED.
                var rsvp = await _db.PartyRsvps.FirstOrDefaultAsync(
                    r => r.EventId == eventId && r.Email.ToLower() == norm, ct);
                var partyCleared = false;
                if (rsvp is not null && !rsvp.Attending && rsvp.HeadCount is null
                    && cancelledAt is DateTimeOffset cAt && rsvp.UpdatedAt >= cAt)
                {
                    _db.PartyRsvps.Remove(rsvp);
                    partyCleared = true;
                }

                // (b) re-arm the §241 selection invite (the 2-day welcome) unless a
                // confirmed seat still exists (e.g. an inherited-MC reassignment).
                if (!hasConfirmedSeat)
                {
                    var actives = await _db.Attendees
                        .Where(a => a.EventId == eventId
                                    && a.MirrorState == MirrorState.Active
                                    && a.MasterClassInviteSentAt != null
                                    && a.Email.ToLower() == norm)
                        .ToListAsync(ct);
                    foreach (var row in actives) row.MasterClassInviteSentAt = null;
                }

                // (c) clear the once-ever pendingmc chaser ledger stamp.
                var ledger = await _db.SentReminders
                    .Where(s => s.EventId == eventId
                                && s.ReminderType == "pending-master-class-selection"
                                && s.OccasionKey == "pendingmc:" + norm)
                    .ToListAsync(ct);
                if (ledger.Count > 0) _db.SentReminders.RemoveRange(ledger);

                await _db.SaveChangesAsync(ct);

                // (d) reopen the §234-closed party / MC tasks so the cadence resumes.
                var pids = await _db.Participants
                    .Where(p => p.EventId == eventId
                                && p.Role == ParticipantRole.Attendee
                                && p.Email.ToLower() == norm)
                    .Select(p => p.Id)
                    .ToListAsync(ct);
                var reopened = false;
                foreach (var pid in pids)
                {
                    var partyKey = PartyTaskSeeder.SourceKeyFor(pid);
                    var mcKey = AttendeeMasterClassTaskSeeder.SourceKeyFor(pid);
                    var tasks = await _db.Tasks
                        .Where(t => t.EventId == eventId
                                    && t.AssignedParticipantId == pid
                                    && t.State == TaskState.Done
                                    && (t.SourceKey == partyKey || t.SourceKey == mcKey))
                        .ToListAsync(ct);
                    foreach (var task in tasks)
                    {
                        var answered = task.SourceKey == partyKey
                            ? !partyCleared && await _db.PartyRsvps.AnyAsync(
                                r => r.EventId == eventId && r.ParticipantId == pid, ct)
                            : hasConfirmedSeat;
                        if (answered) continue;
                        task.State = TaskState.Open;
                        task.CompletedAt = null;
                        reopened = true;
                    }
                }
                if (reopened) await _db.SaveChangesAsync(ct);
            }
            catch
            {
                // Fail-safe (§216/§209 pattern): re-engagement is a secondary reconcile —
                // never let it break the authoritative mirror.
            }
        }
    }
}
