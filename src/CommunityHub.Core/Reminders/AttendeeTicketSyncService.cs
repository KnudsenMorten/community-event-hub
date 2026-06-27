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
        string? CustomFieldsJson = null);

    /// <summary>An order-level mirror row pulled from Backstage (REQUIREMENTS §125).</summary>
    public sealed record OrderRow(
        string OrderId, string? BuyerName, string? BuyerEmail, string? CompanyName,
        string? Country, string? CountryCode, string? City, string? Postcode,
        string? TaxId, string? OrderStatus, DateTimeOffset? SourceCreatedAt, string? RawJson);

    /// <summary>Map an enriched Backstage attendee to a sync row (Master Class eligibility
    /// detected from the ticket name via the shared <see cref="MasterClassTicketPolicy"/> —
    /// the ONE 2-day definition, §125).</summary>
    public static TicketRow FromBackstage(CommunityHub.Core.Integrations.BackstageAttendee a)
    {
        var isTwoDay = MasterClassTicketPolicy.IncludesMasterClass(a.TicketClassName);
        var status = !a.Attending ? TicketStatus.None
            : isTwoDay ? TicketStatus.TwoDay : TicketStatus.Other;
        return new TicketRow(a.TicketId, a.FirstName, a.LastName, a.Email, status, a.TicketClassName,
            a.OrderId, a.CompanyName, a.JobTitle, a.Phone, a.Country, a.CountryCode, a.City, a.Postcode,
            a.TaxId, a.CustomFieldsJson);
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
        IReadOnlyList<MasterClassSignupService.PromotionResult> FreedPromotions);

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
        var knownOrderIds = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, Order>? existingOrders = null;
        if (orders is not null)
        {
            existingOrders = await _db.Orders
                .Where(o => o.EventId == eventId)
                .ToDictionaryAsync(o => o.BackstageOrderId, o => o, ct);
            var seenOrders = new HashSet<string>(StringComparer.Ordinal);
            foreach (var o in orders)
            {
                if (string.IsNullOrWhiteSpace(o.OrderId)) continue;
                seenOrders.Add(o.OrderId);
                knownOrderIds.Add(o.OrderId);
                if (existingOrders.TryGetValue(o.OrderId, out var row))
                {
                    if (row.MirrorState == MirrorState.Cancelled)
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
            // Soft-cancel any local order no longer in the pull (history kept, §128).
            foreach (var (oid, row) in existingOrders)
            {
                if (seenOrders.Contains(oid)) continue;
                if (row.MirrorState != MirrorState.Active) continue;
                row.MirrorState = MirrorState.Cancelled;
                row.CancelledAt = now;
                ordersCancelled++;
            }
        }

        // ---- 2. ATTENDEES: upsert (keyed on stable ticket id) --------------------
        var existing = await _db.Attendees
            .Where(a => a.EventId == eventId && a.BackstageTicketId != null)
            .ToDictionaryAsync(a => a.BackstageTicketId!, a => a, ct);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int created = 0, updated = 0, reassigned = 0, cancelled = 0, reactivated = 0;
        var reassignments = new List<Reassignment>();
        var freed = new List<MasterClassSignupService.PromotionResult>();

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

            if (existing.TryGetValue(t.TicketId, out var a))
            {
                // REAPPEAR: a previously soft-cancelled ticket is back in the pull → Active.
                if (a.MirrorState == MirrorState.Cancelled)
                { a.MirrorState = MirrorState.Active; a.CancelledAt = null; reactivated++; }

                var isReassign = !string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)
                                 && !string.IsNullOrWhiteSpace(email);
                Apply(a, t);
                a.LastSyncedAt = now;
                if (isReassign)
                {
                    a.Email = email;
                    reassigned++;
                    // The inherited Master Class is KEPT (never cancelled on reassign) —
                    // the MasterClassSignup stays linked to this same attendee row, so it
                    // transfers to the new holder automatically.
                    var mcTitle = await _db.MasterClassSignups.AsNoTracking()
                        .Where(s => s.EventId == eventId && s.AttendeeId == a.Id
                                    && s.Status == MasterClassSignupStatus.Confirmed)
                        .Select(s => s.Session.Title).FirstOrDefaultAsync(ct);
                    // If they inherited an MC, don't re-prompt selection (the
                    // reassignment-VALIDATION email covers them); only re-open the
                    // selection invite when there's nothing inherited to validate.
                    a.MasterClassInviteSentAt = string.IsNullOrEmpty(mcTitle) ? null : now;
                    reassignments.Add(new Reassignment(a.Id, email, $"{t.FirstName} {t.LastName}".Trim(), mcTitle));
                }
                else updated++;
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
            }
        }
        await _db.SaveChangesAsync(ct);

        // ---- 3. SOFT-CANCEL attendees no longer in the pull (§128) ---------------
        // A ticket we held is gone from Zoho's active set → release its MC seat (waitlist
        // promotes per §93/§94), flip MirrorState to Cancelled and stamp CancelledAt. The
        // row + history are KEPT; TicketStatus is left intact (cancellation rides
        // MirrorState, not TicketStatus — §126/§128). A reappearance flips it back (above).
        foreach (var (ticketId, a) in existing)
        {
            if (seen.Contains(ticketId)) continue;
            if (a.MirrorState != MirrorState.Active) continue;   // already soft-cancelled
            await SoftCancelAttendeeAsync(eventId, a, now, freed, ct);
            cancelled++;
        }
        if (cancelled > 0) await _db.SaveChangesAsync(ct);

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
            reassignments, freed);
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
                if (a.MirrorState == MirrorState.Cancelled)
                { a.MirrorState = MirrorState.Active; a.CancelledAt = null; reactivated++; }

                var isReassign = !string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)
                                 && !string.IsNullOrWhiteSpace(email);
                Apply(a, t);
                a.LastSyncedAt = now;
                if (isReassign)
                {
                    a.Email = email;
                    reassigned++;
                    var mcTitle = await _db.MasterClassSignups.AsNoTracking()
                        .Where(s => s.EventId == eventId && s.AttendeeId == a.Id
                                    && s.Status == MasterClassSignupStatus.Confirmed)
                        .Select(s => s.Session.Title).FirstOrDefaultAsync(ct);
                    a.MasterClassInviteSentAt = string.IsNullOrEmpty(mcTitle) ? null : now;
                    reassignments.Add(new Reassignment(a.Id, email, $"{t.FirstName} {t.LastName}".Trim(), mcTitle));
                }
                else updated++;
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
            }
        }
        await _db.SaveChangesAsync(ct);

        // ---- 3. SOFT-CANCEL this order's attendees that are gone from the pull ---
        foreach (var (_, a) in toCancel)
        {
            if (a.MirrorState != MirrorState.Active) continue;   // already soft-cancelled
            await SoftCancelAttendeeAsync(eventId, a, now, freed, ct);
            cancelled++;
        }
        if (cancelled > 0) await _db.SaveChangesAsync(ct);

        return new OrderSyncResult(
            orderId, orderCreated, orderUpdated, orderCancelled, orderReactivated,
            created, updated, reassigned, cancelled, reactivated,
            reassignments, freed);
    }

    /// <summary>
    /// SOFT-CANCEL one attendee (REQUIREMENTS §128): release every Master-Class seat it
    /// holds (the waitlist promotes per §93/§94, promotions collected into
    /// <paramref name="freed"/>), then flip <see cref="MirrorState"/> to Cancelled and stamp
    /// <see cref="Attendee.CancelledAt"/>. The row + history are KEPT and
    /// <see cref="Attendee.TicketStatus"/> is left intact (cancellation rides MirrorState,
    /// §126/§128). Shared by the full <see cref="SyncAsync"/> and the incremental
    /// <see cref="SyncOrderAsync"/> so both reconciles behave identically. Does NOT save.
    /// </summary>
    private async Task SoftCancelAttendeeAsync(
        int eventId, Attendee a, DateTimeOffset now,
        List<MasterClassSignupService.PromotionResult> freed, CancellationToken ct)
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
    }
}
