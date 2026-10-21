using System.Security.Cryptography;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>What the organizer types for a reservation.</summary>
public sealed record ShopReservationInput(
    string? CompanyId, string? NewCompanyName, IReadOnlyList<int> ProductIds,
    string RequesterName, string RequesterEmail, DateOnly? Deadline, int ReminderWeeks, DateOnly? RemindFrom, string? Note,
    bool StockAlreadyHeld = false);

/// <summary>
/// §1308 — organizer reservations for the CURRENT edition. Operator 2026-09-27: <i>"I have multiple companies like Red Hat,
/// that have reserved a booth. I need the booth to be marked as RESERVED and not available to select … weekly email where
/// they will have a deadline date … Organizers must be able to control the deadline on a per reservation basis … I must
/// have a requester that will get email … define the cadence, if email should not go out weekly but fx byweekly or disabled
/// until after a specific date."</i>
/// </summary>
/// <remarks>
/// <para>🔑 A reservation IS a <see cref="ShopOrderStatus.Reserved"/> order with a requester: it holds the stock in both
/// shops (the booth map shows RESERVED, the shop and teaser drop it), it can be sent for signature like any order, and
/// cancelling it gives the stock back through <see cref="ShopOrderService.CancelAsync"/>. Its reminder follows its own
/// cadence and replaces the daily cart reminder for that order.</para>
/// <para>🔒 Nothing is ever deleted: a reservation whose stock could not be taken is kept as a CANCELLED order with the
/// reason.</para>
/// </remarks>
/// <summary>A typed company name that does not identify ONE legal entity.</summary>
public sealed class AmbiguousCompanyException(string name) : Exception(name)
{
    public string Name { get; } = name;
}

public sealed class ShopReservationService
{
    public const string ReminderType = "shop-organizer-reservation";

    private readonly CommunityHubDbContext _db;
    private readonly ShopOrderService _orders;
    private readonly ShopMailer _mail;
    private readonly TimeProvider _clock;
    private readonly ILogger<ShopReservationService>? _log;

    public ShopReservationService(
        CommunityHubDbContext db, ShopOrderService orders, ShopMailer mail, TimeProvider clock, ILogger<ShopReservationService>? log = null)
    {
        _db = db;
        _orders = orders;
        _mail = mail;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// Of <paramref name="productIds"/>, those held by a reserved / waiting-for-signature order — shown as "Reserved"
    /// rather than "Sold out" (operator 2026-09-27: "I need the booth to be marked as RESERVED").
    /// </summary>
    public static async Task<IReadOnlySet<int>> HeldProductIdsAsync(
        CommunityHubDbContext db, int eventId, IReadOnlyCollection<int> productIds, CancellationToken ct = default)
    {
        if (productIds.Count == 0) return new HashSet<int>();
        var ids = await db.ShopOrderLines.AsNoTracking()
            // §1312 — only a line that actually HOLDS the stock: a contract that is merely sent holds nothing.
            .Where(l => productIds.Contains(l.ProductId) && l.Order.EventId == eventId && l.StockTaken
                        && (l.Order.Status == ShopOrderStatus.Reserved || l.Order.Status == ShopOrderStatus.AwaitingSignature))
            .Select(l => l.ProductId).Distinct().ToListAsync(ct);
        return ids.ToHashSet();
    }

    /// <summary>The edition's organizer reservations, newest first.</summary>
    public Task<List<ShopOrder>> ListAsync(int eventId, CancellationToken ct = default) =>
        _db.ShopOrders.AsNoTracking().Include(o => o.Lines).ThenInclude(l => l.Product)
            .Where(o => o.EventId == eventId && o.ReservationRequesterEmail != null)
            .OrderByDescending(o => o.CreatedAt).ToListAsync(ct);

    /// <summary>Create a reservation: its own order (never the company's open cart), then reserve it — which takes the stock.</summary>
    public async Task<ShopResult> CreateAsync(int eventId, ShopReservationInput i, string byEmail, CancellationToken ct = default)
    {
        var problem = Validate(i.RequesterEmail, i.ReminderWeeks);
        if (problem is not null) return ShopResult.Fail(problem);
        var productIds = i.ProductIds.Where(id => id > 0).Distinct().ToList();
        if (productIds.Count == 0) return ShopResult.Fail("Choose what is reserved.");

        var now = _clock.GetUtcNow();
        string? companyId;
        try { companyId = await CompanyIdAsync(eventId, i.CompanyId, i.NewCompanyName, byEmail, now, ct); }
        catch (AmbiguousCompanyException ex)
        {
            return ShopResult.Fail($"\"{ex.Name}\" matches an existing company's public name or several companies. Pick the right legal company from the list.");
        }
        if (companyId is null) return ShopResult.Fail("Choose a company, or type the name of a new one.");

        var products = await _db.ShopProducts.Where(p => p.EventId == eventId && productIds.Contains(p.Id)).ToListAsync(ct);
        if (products.Count != productIds.Count) return ShopResult.Fail("A chosen product no longer exists.");

        var company = await _db.ShopCompanies.AsNoTracking().FirstOrDefaultAsync(c => c.EventId == eventId && c.SponsorCompanyId == companyId, ct);
        var order = new ShopOrder
        {
            EventId = eventId,
            SponsorCompanyId = companyId,
            Status = ShopOrderStatus.Cart,
            Currency = company?.Currency ?? "EUR",
            CreatedAt = now,
            UpdatedAt = now,
            ReservationRequesterName = i.RequesterName.Trim(),
            ReservationRequesterEmail = i.RequesterEmail.Trim(),
            ReservationDeadline = i.Deadline,
            ReservationReminderWeeks = i.ReminderWeeks,
            ReservationRemindFrom = i.RemindFrom,
            ReservationNote = string.IsNullOrWhiteSpace(i.Note) ? null : i.Note.Trim(),
        };
        foreach (var p in products)
            order.Lines.Add(new ShopOrderLine
            {
                ProductId = p.Id, Product = p, Quantity = 1, UnitPriceEur = p.EffectivePriceEur, ProductName = p.Name, AddedAt = now,
            });
        _db.ShopOrders.Add(order);
        await _db.SaveChangesAsync(ct);

        var reserved = await _orders.ReserveAsync(order, ct, i.StockAlreadyHeld);
        if (!reserved.Ok)
        {
            // Kept, never deleted: the attempt and why it failed stay visible.
            order.Status = ShopOrderStatus.Cancelled;
            order.CancelledAt = _clock.GetUtcNow();
            order.CancelledByEmail = byEmail;
            order.CancelReason = "The reservation could not be made: " + reserved.Message;
            await _db.SaveChangesAsync(ct);
            return ShopResult.Fail(reserved.Message);
        }
        // Sign-in by default (operator 2026-09-27): the requester can sign in, flip the reservation to an order and sign.
        var access = await GrantRequesterAccessAsync(eventId, order.Id, ct);
        return ShopResult.Success($"Reserved as {order.Number} for {company?.OrganizerLabel ?? companyId}. "
            + (access.Ok ? "The requester can sign in and sign." : "Requester sign-in NOT given: " + access.Message));
    }

    /// <summary>Change the requester, deadline and cadence of a reservation.</summary>
    public async Task<ShopResult> UpdateAsync(
        int eventId, int orderId, string requesterName, string requesterEmail, DateOnly? deadline, int reminderWeeks,
        DateOnly? remindFrom, string? note, CancellationToken ct = default)
    {
        var problem = Validate(requesterEmail, reminderWeeks);
        if (problem is not null) return ShopResult.Fail(problem);
        var order = await _db.ShopOrders.FirstOrDefaultAsync(o => o.EventId == eventId && o.Id == orderId && o.ReservationRequesterEmail != null, ct);
        if (order is null) return ShopResult.Fail("That reservation no longer exists.");
        order.ReservationRequesterName = requesterName.Trim();
        order.ReservationRequesterEmail = requesterEmail.Trim();
        order.ReservationDeadline = deadline;
        order.ReservationReminderWeeks = reminderWeeks;
        order.ReservationRemindFrom = remindFrom;
        order.ReservationNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        order.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        // Sign-in by default: the (new) requester becomes a signer contact straight away.
        var access = await GrantRequesterAccessAsync(eventId, order.Id, ct);
        return ShopResult.Success($"Reservation {order.Number} saved. "
            + (access.Ok ? "The requester can sign in and sign." : "Requester sign-in NOT given: " + access.Message));
    }

    /// <summary>
    /// 🔴 §1312 — merge one reservation INTO another of the same company, so one contract covers both (operator 2026-09-30:
    /// "i have two reservations for red hat, how i can merge them together into one contract?" · "build the merge
    /// reservation").
    /// </summary>
    /// <remarks>
    /// <para>The source's lines MOVE to the target as they are — same prices, and 🔒 the stock stays HELD throughout: nothing
    /// is released, so a booth or slot is never back on sale, not even for a moment. Entitlements point at the LINE, so they
    /// travel with it; a swag credit spent on the source is re-pointed to the target.</para>
    /// <para>The target keeps its number, requester and reminder cadence; its deadline becomes the EARLIER of the two. The
    /// emptied source is closed as "merged into …" — no stock is given back and NO cancellation mail is sent.</para>
    /// <para>Refused unless both are RESERVED (not sent for signature, not signed) and belong to the same company.</para>
    /// </remarks>
    public async Task<ShopResult> MergeAsync(int eventId, int sourceOrderId, int targetOrderId, string byEmail, CancellationToken ct = default)
    {
        if (sourceOrderId == targetOrderId) return ShopResult.Fail("Choose another reservation to merge into.");
        var orders = await _db.ShopOrders.Include(o => o.Lines)
            .Where(o => o.EventId == eventId && (o.Id == sourceOrderId || o.Id == targetOrderId))
            .ToListAsync(ct);
        var source = orders.FirstOrDefault(o => o.Id == sourceOrderId);
        var target = orders.FirstOrDefault(o => o.Id == targetOrderId);
        if (source is null || target is null) return ShopResult.Fail("One of the reservations no longer exists.");
        if (source.Status != ShopOrderStatus.Reserved || target.Status != ShopOrderStatus.Reserved)
            return ShopResult.Fail("Only two reservations that are still RESERVED can be merged — not one sent for signature or signed.");
        if (!string.Equals(source.SponsorCompanyId, target.SponsorCompanyId, StringComparison.Ordinal))
            return ShopResult.Fail("The two reservations belong to different companies.");
        if (source.Lines.Count == 0) return ShopResult.Fail($"{source.Number} has nothing to merge.");

        var now = _clock.GetUtcNow();
        var moved = source.Lines.Count;
        foreach (var line in source.Lines.ToList())
        {
            source.Lines.Remove(line);
            line.OrderId = target.Id;
            target.Lines.Add(line);
        }
        foreach (var credit in await _db.SwagCatalogCredits.Where(c => c.UsedOnShopOrderId == source.Id).ToListAsync(ct))
            credit.UsedOnShopOrderId = target.Id;
        target.SwagCreditEur += source.SwagCreditEur;
        source.SwagCreditEur = 0;

        target.ReservationDeadline = (source.ReservationDeadline, target.ReservationDeadline) switch
        {
            (DateOnly s, DateOnly t) => s < t ? s : t,
            (DateOnly s, null) => s,
            (_, var t) => t,
        };
        if (!string.IsNullOrWhiteSpace(source.ReservationNote))
            target.ReservationNote = string.IsNullOrWhiteSpace(target.ReservationNote)
                ? source.ReservationNote
                : $"{target.ReservationNote} · from {source.Number}: {source.ReservationNote}";
        target.UpdatedAt = now;

        // Closed WITHOUT CancelAsync: that path gives stock back and mails the sponsor — neither applies, the items live on
        // in the target. The source keeps its number, so the history reads "ELDK27-0007 merged into ELDK27-0001".
        source.Status = ShopOrderStatus.Cancelled;
        source.CancelledAt = now;
        source.CancelledByEmail = byEmail;
        source.CancelReason = $"Merged into {target.Number} (one contract for both).";
        source.ExtensionRequestedDeadline = null;
        source.ExtensionRequestReason = null;
        source.ExtensionRequestedAt = null;
        source.ExtensionRequestedByParticipantId = null;
        source.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        _log?.LogInformation("§1312 reservation {Source} merged into {Target} by {By}: {Lines} line(s) moved, stock kept held.",
            source.Number, target.Number, byEmail, moved);
        return ShopResult.Success($"{source.Number} merged into {target.Number}: {moved} item(s) moved, stock still held"
            + (target.ReservationDeadline is DateOnly d ? $", deadline {d:d MMMM yyyy}" : "")
            + $". Send one contract for {target.Number}.");
    }

    /// <summary>
    /// Give the requester sign-in access: they become a SIGNER contact of the company (never an event coordinator — the
    /// sponsor welcome goes to coordinators only, so this sends no welcome) and the reservation's signer. They then sign in
    /// with the normal e-mail PIN, see the reservation in their cart page, add items and sign.
    /// </summary>
    /// <remarks>🔒 Refused when the address already belongs to someone else in this edition (another role or another
    /// company) — an organizer's own address typed as a placeholder must never become a sponsor contact.</remarks>
    public async Task<ShopResult> GrantRequesterAccessAsync(int eventId, int orderId, CancellationToken ct = default)
    {
        var order = await _db.ShopOrders.FirstOrDefaultAsync(o => o.EventId == eventId && o.Id == orderId && o.ReservationRequesterEmail != null, ct);
        if (order is null) return ShopResult.Fail("That reservation no longer exists.");
        var email = order.ReservationRequesterEmail!.Trim();
        var now = _clock.GetUtcNow();

        var person = await _db.Participants.FirstOrDefaultAsync(p => p.EventId == eventId && p.Email == email, ct);
        // A sponsor contact of ANOTHER company is linked to this one too — one person can be CFO / signer / coordinator
        // for several companies (operator 2026-09-27: "arrow have same event coordinator in all 4 countries" · "same with
        // signer, he can be cfo for multiple companies"); they then switch company in the shop. Any other role (an
        // organizer's own address typed as a placeholder, a speaker, …) is refused.
        if (person is not null && person.Role != ParticipantRole.Sponsor)
            return ShopResult.Fail($"{email} is already in the hub as {person.Role} — set the real requester's address first.");
        if (person is null)
        {
            person = new Participant
            {
                EventId = eventId, Email = email, FullName = string.IsNullOrWhiteSpace(order.ReservationRequesterName) ? email : order.ReservationRequesterName!.Trim(),
                Role = ParticipantRole.Sponsor, SponsorCompanyId = order.SponsorCompanyId, IsActive = true, IsSigner = true,
                LifecycleState = ParticipantLifecycleState.Active, CreatedAt = now,
            };
            _db.Participants.Add(person);
            await _db.SaveChangesAsync(ct);
        }
        if (!await _db.SponsorCompanyContacts.AnyAsync(c => c.EventId == eventId && c.ParticipantId == person.Id && c.SponsorCompanyId == order.SponsorCompanyId, ct))
            _db.SponsorCompanyContacts.Add(new SponsorCompanyContact
            {
                EventId = eventId, ParticipantId = person.Id, SponsorCompanyId = order.SponsorCompanyId, IsSigner = true, IsEventCoordinator = false,
            });
        // 🔒 NO MAIL because of this (operator 2026-09-27: "we can not have any emails due to this … critical"): a company
        // without a sponsor record gets a HIDDEN one (IsSponsor = false) so the sponsor welcome skips it (§1309.0), whatever
        // role anyone has there. A company that is already a sponsor keeps its record.
        if (!await _db.SponsorInfos.IgnoreQueryFilters().AnyAsync(s => s.EventId == eventId && s.SponsorCompanyId == order.SponsorCompanyId, ct))
        {
            var company = await _db.ShopCompanies.AsNoTracking()
                .FirstOrDefaultAsync(c => c.EventId == eventId && c.SponsorCompanyId == order.SponsorCompanyId, ct);
            _db.SponsorInfos.Add(new SponsorInfo
            {
                EventId = eventId, SponsorCompanyId = order.SponsorCompanyId,
                CompanyName = company?.PublicLabel, IsSponsor = company?.State == ShopCompanyState.Sponsor, CreatedAt = now,
            });
        }
        order.SignerParticipantId = person.Id;
        order.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return ShopResult.Success($"{email} can now sign in (e-mail PIN), see {order.Number} and sign it.");
    }

    /// <summary>When the next reminder goes out, or null when none will (off, paused past the deadline, deadline passed, not reserved).</summary>
    public static DateOnly? NextReminder(ShopOrder o, DateOnly? lastSent, DateOnly today)
    {
        if (o.Status != ShopOrderStatus.Reserved || o.ReservationReminderWeeks <= 0 || !o.IsOrganizerReservation) return null;
        var next = lastSent is DateOnly last ? last.AddDays(7 * o.ReservationReminderWeeks) : today;
        if (o.ReservationRemindFrom is DateOnly from && next < from) next = from;
        if (next < today) next = today;
        if (o.ReservationDeadline is DateOnly deadline && next > deadline) return null;
        return next;
    }

    /// <summary>
    /// Send every reservation reminder that is due today, and tell the organizers ONCE when a deadline has passed.
    /// Each send is recorded (one per reservation per day at most), so a re-run never repeats it.
    /// </summary>
    public async Task<int> SendDueAsync(int eventId, DateOnly today, CancellationToken ct = default)
    {
        var reservations = await _db.ShopOrders.Include(o => o.Lines).ThenInclude(l => l.Product)
            .Where(o => o.EventId == eventId && o.ReservationRequesterEmail != null && o.Status == ShopOrderStatus.Reserved)
            .ToListAsync(ct);
        if (reservations.Count == 0) return 0;
        var last = await LastSentAsync(eventId, ct);
        var sent = 0;
        foreach (var o in reservations)
        {
            try
            {
                if (o.ReservationDeadline is DateOnly deadline && today > deadline)
                {
                    var key = $"expired:{o.Id}";
                    if (await AlreadyAsync(eventId, key, ct)) continue;
                    var company = await CompanyNameAsync(o, ct);
                    await _mail.NotifyOrganizersAsync(eventId, $"Reservation deadline passed: {company} ({o.Number})",
                        $"<p>The reservation <b>{System.Net.WebUtility.HtmlEncode(o.Number ?? "")}</b> for <b>{System.Net.WebUtility.HtmlEncode(company)}</b> "
                        + $"passed its deadline {Day(deadline)} without being signed. It still holds its items; the requester is no longer reminded.</p>"
                        // §1312 — THIS edition's reservations page (it linked the next-edition rights import).
                        + $"<p><a href=\"{_mail.HubOrigin}/Organizer/ShopEventReservations\">Extend it, send it for signature, or cancel it</a></p>", ct);
                    Record(eventId, "organizers", key);
                    await _db.SaveChangesAsync(ct);
                    continue;
                }

                var next = NextReminder(o, last.GetValueOrDefault(o.Id), today);
                if (next is null || next > today) continue;
                var occasion = $"{o.Id}:{today:yyyy-MM-dd}";
                if (await AlreadyAsync(eventId, occasion, ct)) continue;

                if (await SendOneAsync(o, ct))
                {
                    Record(eventId, o.ReservationRequesterEmail!, occasion);
                    await _db.SaveChangesAsync(ct);
                    sent++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.LogWarning(ex, "§1308 reservation reminder for order {Id} failed; retried next run.", o.Id);
            }
        }
        return sent;
    }

    /// <summary>"Send the reminder now" from the organizer page — recorded like a scheduled one, so the cadence counts from it.</summary>
    public async Task<ShopResult> SendNowAsync(int eventId, int orderId, CancellationToken ct = default)
    {
        var o = await _db.ShopOrders.Include(x => x.Lines).ThenInclude(l => l.Product)
            .FirstOrDefaultAsync(x => x.EventId == eventId && x.Id == orderId && x.ReservationRequesterEmail != null, ct);
        if (o is null || o.Status != ShopOrderStatus.Reserved) return ShopResult.Fail("Only an active reservation can be reminded.");
        if (!await SendOneAsync(o, ct)) return ShopResult.Fail("The reminder could not be sent (mail switched off or failed).");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), EventTimezone.Tz).DateTime);
        var occasion = $"{o.Id}:{today:yyyy-MM-dd}";
        if (!await AlreadyAsync(eventId, occasion, ct)) Record(eventId, o.ReservationRequesterEmail!, occasion);
        await _db.SaveChangesAsync(ct);
        return ShopResult.Success($"Reminder sent to {o.ReservationRequesterEmail}.");
    }

    // ── §1312 — a SPONSOR's reservation request and request for more time, decided by the organizers ────────────────
    // Operator 2026-09-27: "in the offer can you make 2 buttons: reservation request (which i can approve) and sign/order"
    // · "only thing i have to do is to accept or deny reservation request and i can add deadline, reminder, etc" · "i must
    // get an email with direct link to this so i can finalize on the reservation request" · "add a extension request
    // button in the reminder mail where they select a date and give reason - and it is sent to me for approval".

    /// <summary>"15 October 2026" — invariant, whatever the server's language (a Danish host wrote "15. oktober 2026").</summary>
    private static string Day(DateOnly? d) =>
        d?.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>The organizers' page for this edition's reservations, where requests are decided.</summary>
    public string ReservationsPageUrl(string anchor) => $"{_mail.HubOrigin}/Organizer/ShopEventReservations#{anchor}";

    /// <summary>Carts whose reservation request waits for the organizers, oldest first.</summary>
    public Task<List<ShopOrder>> OpenRequestsAsync(int eventId, CancellationToken ct = default) =>
        _db.ShopOrders.AsNoTracking().Include(o => o.Lines)
            .Where(o => o.EventId == eventId && o.Status == ShopOrderStatus.Cart && o.ReservationRequestedAt != null)
            .OrderBy(o => o.ReservationRequestedAt).ToListAsync(ct);

    /// <summary>
    /// A contact asks the organizers to reserve their cart. Nothing is reserved while it waits (first come, first served);
    /// the organizers get one mail with a direct link to decide.
    /// </summary>
    public async Task<ShopResult> RequestReservationAsync(int eventId, string companyId, int orderId, int participantId, CancellationToken ct = default)
    {
        var order = await _db.ShopOrders.Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.EventId == eventId && o.Id == orderId && o.SponsorCompanyId == companyId, ct);
        if (order is null) return ShopResult.Fail("That order no longer exists.");
        if (order.Status != ShopOrderStatus.Cart || order.Lines.Count == 0)
            return ShopResult.Fail("Only an open order with products can be reserved.");
        if (order.ReservationRequestedAt is not null)
            return ShopResult.Success("Your reservation request is already with the organizers.");
        var person = await _db.Participants.AsNoTracking().FirstOrDefaultAsync(p => p.Id == participantId, ct);
        if (person is null) return ShopResult.Fail("Please sign in again.");

        var now = _clock.GetUtcNow();
        order.ReservationRequestedAt = now;
        order.ReservationRequestedByParticipantId = participantId;
        order.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        var company = await CompanyNameAsync(order, ct);
        static string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        var items = string.Concat(order.Lines.Select(l => $"<li>{E(l.ProductName)}{(l.Quantity > 1 ? $" × {l.Quantity}" : "")}</li>"));
        await _mail.NotifyOrganizersAsync(eventId, $"Reservation request: {company}",
            $"<p><b>{E(person.FullName)}</b> ({E(person.Email)}) asks you to reserve this for <b>{E(company)}</b>:</p><ul>{items}</ul>"
            + "<p>Nothing is reserved until you accept it.</p>"
            + $"<p><a href=\"{ReservationsPageUrl($"request-{order.Id}")}\">Accept or deny the request</a> — set the deadline and reminders when you accept.</p>", ct);
        return ShopResult.Success("Your reservation request is sent to the organizers. Nothing is reserved until they accept it — you get an e-mail either way.");
    }

    /// <summary>
    /// Accept: the cart becomes an organizer reservation for the person who asked — the stock is taken, the deadline and
    /// cadence are the organizer's — and the reservation mail goes out at once (it IS the answer).
    /// </summary>
    public async Task<ShopResult> AcceptRequestAsync(
        int eventId, int orderId, DateOnly? deadline, int reminderWeeks, DateOnly? remindFrom, string? note, CancellationToken ct = default)
    {
        if (reminderWeeks is < 0 or > 8) return ShopResult.Fail("Choose a reminder cadence.");
        var order = await _db.ShopOrders.Include(o => o.Lines).ThenInclude(l => l.Product)
            .FirstOrDefaultAsync(o => o.EventId == eventId && o.Id == orderId, ct);
        if (order is null || !order.HasOpenReservationRequest) return ShopResult.Fail("That request is no longer open.");
        var person = await _db.Participants.AsNoTracking().FirstOrDefaultAsync(p => p.Id == order.ReservationRequestedByParticipantId, ct);
        if (person is null) return ShopResult.Fail("The person who asked is no longer in the hub.");

        order.ReservationRequesterName = person.FullName;
        order.ReservationRequesterEmail = person.Email;
        order.ReservationDeadline = deadline;
        order.ReservationReminderWeeks = reminderWeeks;
        order.ReservationRemindFrom = remindFrom;
        order.ReservationNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        // The company's signer signs; the person who asked, when the company has none.
        order.SignerParticipantId ??= await _db.SponsorCompanyContacts.AsNoTracking()
            .Where(l => l.EventId == eventId && l.SponsorCompanyId == order.SponsorCompanyId && l.IsSigner && l.Participant.IsActive)
            .Select(l => (int?)l.ParticipantId).FirstOrDefaultAsync(ct) ?? person.Id;
        order.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        var reserved = await _orders.ReserveAsync(order, ct);
        if (!reserved.Ok)
        {
            // Not reserved ⇒ still the request it was; the organizer sees why.
            order.ReservationRequesterName = null;
            order.ReservationRequesterEmail = null;
            order.ReservationDeadline = null;
            await _db.SaveChangesAsync(ct);
            return ShopResult.Fail("It could not be reserved: " + reserved.Message);
        }
        var sent = await SendNowAsync(eventId, order.Id, ct);
        return ShopResult.Success($"Reserved as {order.Number}. "
            + (sent.Ok ? $"{person.Email} has the reservation mail." : "The reservation mail could not be sent: " + sent.Message));
    }

    /// <summary>Deny: the request is cleared (the cart stays theirs — they can still order and sign) and they are told.</summary>
    public async Task<ShopResult> DenyRequestAsync(int eventId, int orderId, string? note, CancellationToken ct = default)
    {
        var order = await _db.ShopOrders.FirstOrDefaultAsync(o => o.EventId == eventId && o.Id == orderId, ct);
        if (order is null || !order.HasOpenReservationRequest) return ShopResult.Fail("That request is no longer open.");
        var person = await _db.Participants.AsNoTracking().FirstOrDefaultAsync(p => p.Id == order.ReservationRequestedByParticipantId, ct);
        order.ReservationRequestedAt = null;
        order.ReservationRequestedByParticipantId = null;
        order.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        if (person is null) return ShopResult.Success("Request denied.");
        var sent = await _mail.SendReservationAnswerAsync(order, person, "Your reservation request",
            "We cannot reserve your order right now. It is still in your cart: you can order and sign it yourself at any time "
            + "— availability is first come, first served.", note, ct);
        return ShopResult.Success($"Request denied. {(sent ? $"{person.Email} was told." : "The answer mail could not be sent.")}");
    }

    /// <summary>
    /// A contact asks for MORE TIME on their reservation: a later date and a reason, sent to the organizers with a direct
    /// link. The deadline does not change until they approve.
    /// </summary>
    public async Task<ShopResult> RequestExtensionAsync(
        int eventId, string companyId, int orderId, int participantId, DateOnly? wanted, string? reason, CancellationToken ct = default)
    {
        var order = await _db.ShopOrders.FirstOrDefaultAsync(o => o.EventId == eventId && o.Id == orderId && o.SponsorCompanyId == companyId, ct);
        if (order is null || !order.CanAskForMoreTime || order.ReservationDeadline is not DateOnly current)
            return ShopResult.Fail("Only a reservation with a deadline can be extended.");
        if (wanted is not DateOnly date || date <= current) return ShopResult.Fail($"Choose a date after the current deadline ({Day(current)}).");
        if (string.IsNullOrWhiteSpace(reason)) return ShopResult.Fail("Please tell us why you need more time.");
        var person = await _db.Participants.AsNoTracking().FirstOrDefaultAsync(p => p.Id == participantId, ct);
        if (person is null) return ShopResult.Fail("Please sign in again.");

        order.ExtensionRequestedDeadline = date;
        order.ExtensionRequestReason = reason.Trim().Length > 1000 ? reason.Trim()[..1000] : reason.Trim();
        order.ExtensionRequestedAt = _clock.GetUtcNow();
        order.ExtensionRequestedByParticipantId = participantId;
        order.UpdatedAt = order.ExtensionRequestedAt;
        await _db.SaveChangesAsync(ct);

        var company = await CompanyNameAsync(order, ct);
        static string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        await _mail.NotifyOrganizersAsync(eventId, $"More time requested: {company} ({order.Number})",
            $"<p><b>{E(person.FullName)}</b> ({E(person.Email)}) asks to move the deadline of <b>{E(order.Number)}</b> for "
            + $"<b>{E(company)}</b> from <b>{Day(current)}</b> to <b>{Day(date)}</b>.</p>"
            + $"<p style=\"white-space:pre-line;\"><b>Reason:</b> {E(order.ExtensionRequestReason)}</p>"
            + $"<p><a href=\"{ReservationsPageUrl($"extension-{order.Id}")}\">Approve or deny the request</a></p>", ct);
        return ShopResult.Success($"Your request for more time is sent to the organizers. Until they answer, the deadline stays {Day(current)}.");
    }

    /// <summary>
    /// Approve (the deadline moves — to the requested date, or the one the organizer sets — and the reservation mail with
    /// the new deadline goes out) or deny (the deadline stays, and the person who asked is told).
    /// </summary>
    public async Task<ShopResult> DecideExtensionAsync(
        int eventId, int orderId, bool approve, DateOnly? deadline, string? note, CancellationToken ct = default)
    {
        var order = await _db.ShopOrders.FirstOrDefaultAsync(o => o.EventId == eventId && o.Id == orderId, ct);
        if (order is null || !order.HasOpenExtensionRequest) return ShopResult.Fail("That request is no longer open.");
        var person = await _db.Participants.AsNoTracking().FirstOrDefaultAsync(p => p.Id == order.ExtensionRequestedByParticipantId, ct);
        var asked = order.ExtensionRequestedDeadline!.Value;
        order.ExtensionRequestedDeadline = null;
        order.ExtensionRequestReason = null;
        order.ExtensionRequestedAt = null;
        order.ExtensionRequestedByParticipantId = null;
        if (approve) order.ReservationDeadline = deadline ?? asked;
        order.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        if (approve)
        {
            // A reservation gets its reservation mail with the new deadline; a contract already sent for signature gets
            // the short answer (its signing mail stays valid).
            if (order.Status == ShopOrderStatus.Reserved && order.IsOrganizerReservation)
            {
                var sent = await SendNowAsync(eventId, order.Id, ct);
                return ShopResult.Success($"Deadline moved to {Day(order.ReservationDeadline)}. "
                    + (sent.Ok ? "The reservation mail with the new deadline was sent." : "The mail could not be sent: " + sent.Message));
            }
            var ok = person is not null && await _mail.SendReservationAnswerAsync(order, person, "More time for your contract",
                $"Your deadline is now <strong>{Day(order.ReservationDeadline)}</strong>. The items stay reserved for you until then.", note, ct);
            return ShopResult.Success($"Deadline moved to {Day(order.ReservationDeadline)}. {(ok ? $"{person!.Email} was told." : "Nobody could be told by mail.")}");
        }
        if (person is null) return ShopResult.Success("Request denied.");
        var told = await _mail.SendReservationAnswerAsync(order, person, "Your request for more time",
            $"We cannot move the deadline of your reservation <strong>{System.Net.WebUtility.HtmlEncode(order.Number)}</strong> to "
            + $"{Day(asked)}. The deadline stays <strong>{Day(order.ReservationDeadline)}</strong> — please sign before then, "
            + "or the products are released to other sponsors.", note, ct);
        return ShopResult.Success($"Request denied — the deadline stays. {(told ? $"{person.Email} was told." : "The answer mail could not be sent.")}");
    }

    /// <summary>Reservation id → the last day a reminder was sent.</summary>
    public async Task<Dictionary<int, DateOnly>> LastSentAsync(int eventId, CancellationToken ct = default)
    {
        var keys = await _db.SentReminders.AsNoTracking()
            .Where(s => s.EventId == eventId && s.ReminderType == ReminderType && !s.OccasionKey.StartsWith("expired:"))
            .Select(s => s.OccasionKey).ToListAsync(ct);
        var result = new Dictionary<int, DateOnly>();
        foreach (var k in keys)
        {
            var parts = k.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var id) && DateOnly.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var d)
                && (!result.TryGetValue(id, out var cur) || d > cur))
                result[id] = d;
        }
        return result;
    }

    private async Task<bool> SendOneAsync(ShopOrder o, CancellationToken ct)
    {
        var company = await _db.ShopCompanies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.EventId == o.EventId && c.SponsorCompanyId == o.SponsorCompanyId, ct);
        ShopTotals? totals = company is null ? null : await _orders.PriceAsync(o, company, ct);
        return await _mail.SendOrganizerReservationReminderAsync(o, company?.LegalName ?? o.SponsorCompanyId, totals, ct);
    }

    private async Task<string> CompanyNameAsync(ShopOrder o, CancellationToken ct) =>
        await _db.ShopCompanies.AsNoTracking().Where(c => c.EventId == o.EventId && c.SponsorCompanyId == o.SponsorCompanyId)
            .Select(c => c.LegalName).FirstOrDefaultAsync(ct) ?? o.SponsorCompanyId;

    private Task<bool> AlreadyAsync(int eventId, string occasion, CancellationToken ct) =>
        _db.SentReminders.AnyAsync(s => s.EventId == eventId && s.ReminderType == ReminderType && s.OccasionKey == occasion, ct);

    private void Record(int eventId, string email, string occasion) =>
        _db.SentReminders.Add(new SentReminder
        {
            EventId = eventId, RecipientEmail = email, ReminderType = ReminderType, OccasionKey = occasion, SentAt = _clock.GetUtcNow(),
        });

    private static string? Validate(string requesterEmail, int reminderWeeks)
    {
        if (!System.Net.Mail.MailAddress.TryCreate(requesterEmail?.Trim(), out _)) return "Enter the requester's e-mail address.";
        if (reminderWeeks is < 0 or > 8) return "Choose how often to remind (every 1–8 weeks, or off).";
        return null;
    }

    /// <summary>An existing company, or a NEW candidate created from the typed name (as self-registration would).</summary>
    private async Task<string?> CompanyIdAsync(int eventId, string? companyId, string? newName, string byEmail, DateTimeOffset now, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(companyId))
        {
            if (await _db.ShopCompanies.AnyAsync(c => c.EventId == eventId && c.SponsorCompanyId == companyId, ct)) return companyId;
            // A Company Manager company not yet copied into the shop.
            var info = await _db.SponsorInfos.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);
            if (info is null) return null;
            _db.ShopCompanies.Add(new ShopCompany
            {
                EventId = eventId, SponsorCompanyId = companyId, Source = ShopCompanySource.CompanyManager,
                State = info.IsSponsor ? ShopCompanyState.Sponsor : ShopCompanyState.Candidate,
                LegalName = info.CompanyName ?? $"Company {companyId}", PublicName = info.CompanyName, CreatedAt = now,
            });
            await _db.SaveChangesAsync(ct);
            return companyId;
        }
        if (string.IsNullOrWhiteSpace(newName)) return null;
        var name = newName.Trim();
        // 🔒 The correct LEGAL ENTITY (operator 2026-09-27): only an EXACT legal-name match is the same company. A name that
        // matches several companies, or only a public name ("ARROW" = four legal entities), is refused — pick from the list.
        var sameLegal = await _db.ShopCompanies.Where(c => c.EventId == eventId && c.LegalName == name)
            .Select(c => c.SponsorCompanyId).ToListAsync(ct);
        if (sameLegal.Count == 1) return sameLegal[0];
        if (sameLegal.Count > 1 || await _db.ShopCompanies.AnyAsync(c => c.EventId == eventId && c.PublicName == name, ct))
            throw new AmbiguousCompanyException(name);
        var id = "ceh-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        _db.ShopCompanies.Add(new ShopCompany
        {
            EventId = eventId, SponsorCompanyId = id, Source = ShopCompanySource.Registered, State = ShopCompanyState.Candidate,
            // §1312 — the typed name is the LEGAL name; the public one (SoMe, Zoho, site) is it without its legal form.
            LegalName = name, PublicName = CompanyLegalForm.Strip(name), CreatedAt = now, RegisteredByEmail = byEmail,
        });
        if (!await _db.SponsorInfos.IgnoreQueryFilters().AnyAsync(s => s.EventId == eventId && s.SponsorCompanyId == id, ct))
            _db.SponsorInfos.Add(new SponsorInfo { EventId = eventId, SponsorCompanyId = id, CompanyName = CompanyLegalForm.Strip(name), IsSponsor = false, CreatedAt = now });
        await _db.SaveChangesAsync(ct);
        return id;
    }
}
