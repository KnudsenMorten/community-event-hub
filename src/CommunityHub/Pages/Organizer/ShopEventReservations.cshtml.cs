using CommunityHub.Audit;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Webshop;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1308 — reservations for THIS edition, managed by the organizers: company, what is held, requester, deadline and the
/// reminder cadence per reservation. Operator 2026-09-27: "we need to re-introduce reservations for this event, and allow
/// me to manage that as an organizer also". (The next-edition carry-over lives on /Organizer/ShopReservations.)
/// </summary>
[Authorize]
public class ShopEventReservationsModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ShopReservationService _reservations;
    private readonly ShopOrderService _orders;
    private readonly TimeProvider _clock;

    public ShopEventReservationsModel(
        CommunityHubDbContext db, ICurrentParticipantAccessor participant, ShopReservationService reservations,
        ShopOrderService orders, TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _reservations = reservations;
        _orders = orders;
        _clock = clock;
    }

    public sealed record Row(ShopOrder Order, string Company, DateOnly? LastSent, DateOnly? Next, bool RequesterCanSignIn = false);

    public bool AccessDenied { get; private set; }
    [TempData] public string? Message { get; set; }
    public IReadOnlyList<Row> Active { get; private set; } = Array.Empty<Row>();
    public IReadOnlyList<Row> Closed { get; private set; } = Array.Empty<Row>();
    public IReadOnlyList<(string Id, string Name)> Companies { get; private set; } = Array.Empty<(string, string)>();
    public IReadOnlyList<ShopProduct> Products { get; private set; } = Array.Empty<ShopProduct>();
    public DateOnly Today { get; private set; }

    /// <summary>§1312 — a sponsor's open reservation request: the cart, who asked, and the company.</summary>
    public sealed record Request(ShopOrder Order, string Company, string? Who);

    /// <summary>§1312 — carts whose reservation request waits for an answer (Accept with deadline and reminders, or Deny).</summary>
    public IReadOnlyList<Request> Requests { get; private set; } = Array.Empty<Request>();

    /// <summary>§1312 — who asked for more time, by participant id (for the extension boxes).</summary>
    public IReadOnlyDictionary<int, string> People { get; private set; } = new Dictionary<int, string>();

    /// <summary>
    /// Company id → its people (hub contacts + people known from e-conomic / the old webshop), for the requester dropdown
    /// (operator 2026-09-27: "where is the dropdown here so i can select marianne as request").
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(string Name, string Email, string Roles)>> PeopleByCompany { get; private set; } =
        new Dictionary<string, IReadOnlyList<(string Name, string Email, string Roles)>>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return;
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return; }
        var eventId = me.EventId;
        Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), EventTimezone.Tz).DateTime);

        var shop = await _db.ShopCompanies.AsNoTracking().Where(c => c.EventId == eventId).ToListAsync(ct);
        var names = shop.ToDictionary(c => c.SponsorCompanyId, c => c.OrganizerLabel);
        var infos = await _db.SponsorInfos.IgnoreQueryFilters().AsNoTracking().Where(s => s.EventId == eventId && s.CompanyName != null)
            .Select(s => new { s.SponsorCompanyId, s.CompanyName }).ToListAsync(ct);
        foreach (var i in infos) names.TryAdd(i.SponsorCompanyId, i.CompanyName!);
        Companies = names.Select(kv => (kv.Key, kv.Value)).OrderBy(c => c.Value, StringComparer.OrdinalIgnoreCase).ToList();

        // Only what can still be held: published and in stock. Booths first — the usual reservation.
        Products = await _db.ShopProducts.AsNoTracking().Include(p => p.PrimaryCategory)
            .Where(p => p.EventId == eventId && p.Status == ShopProductStatus.Published && p.InStock)
            .ToListAsync(ct);
        Products = Products.OrderBy(p => p.PrimaryCategory?.Group == ShopGroup.Booths ? 0 : 1)
            .ThenBy(p => p.PrimaryCategory?.Group).ThenBy(p => p.BoothId ?? p.Name).ThenBy(p => p.Name).ToList();

        // 🔴 §1312 — the requester list shows BOTH the company's signers AND its event coordinators, each labelled with its
        // role (operator 2026-09-29: "requesters in reservation must show both signers and coordinators"). The hub's
        // contacts and the old webshop's known people are merged per e-mail, and a role from either source counts.
        var hubPeople = await _db.SponsorCompanyContacts.AsNoTracking().Where(l => l.EventId == eventId && l.Participant.IsActive)
            .Select(l => new { l.SponsorCompanyId, l.Participant.FullName, l.Participant.Email, l.IsSigner, l.IsEventCoordinator }).ToListAsync(ct);
        var knownPeople = await _db.ShopKnownContacts.AsNoTracking().Where(k => k.EventId == eventId)
            .Select(k => new { k.SponsorCompanyId, k.FullName, k.Email, k.IsSigner, k.IsEventCoordinator }).ToListAsync(ct);
        PeopleByCompany = hubPeople.Select(p => (p.SponsorCompanyId, p.FullName, Email: p.Email.ToLowerInvariant(), p.IsSigner, p.IsEventCoordinator))
            .Concat(knownPeople.Select(k => (k.SponsorCompanyId, k.FullName, Email: k.Email.ToLowerInvariant(), k.IsSigner, k.IsEventCoordinator)))
            .GroupBy(x => x.SponsorCompanyId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<(string Name, string Email, string Roles)>)g
                .GroupBy(x => x.Email)
                .Select(e => (Name: e.First().FullName, Email: e.Key,
                    Signer: e.Any(x => x.IsSigner), Coordinator: e.Any(x => x.IsEventCoordinator)))
                .OrderByDescending(x => x.Signer || x.Coordinator)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => (x.Name, x.Email, Roles: string.Join(", ",
                    new[] { x.Signer ? "signer" : null, x.Coordinator ? "event coordinator" : null }.Where(r => r is not null))))
                .ToList());

        var last = await _reservations.LastSentAsync(eventId, ct);
        // Which requesters are already a contact of their company (can sign in and sign).
        var contacts = await _db.Participants.AsNoTracking()
            .Where(p => p.EventId == eventId && p.Role == ParticipantRole.Sponsor && p.IsActive && p.SponsorCompanyId != null)
            .Select(p => new { p.Email, p.SponsorCompanyId }).ToListAsync(ct);
        var access = contacts.Select(c => (c.Email.ToLowerInvariant(), c.SponsorCompanyId!)).ToHashSet();
        var rows = (await _reservations.ListAsync(eventId, ct))
            .Select(o => new Row(o, names.GetValueOrDefault(o.SponsorCompanyId) ?? o.SponsorCompanyId,
                last.TryGetValue(o.Id, out var d) ? d : null,
                ShopReservationService.NextReminder(o, last.TryGetValue(o.Id, out var d2) ? d2 : null, Today),
                access.Contains(((o.ReservationRequesterEmail ?? "").Trim().ToLowerInvariant(), o.SponsorCompanyId))))
            .ToList();
        Active = rows.Where(r => r.Order.Status is ShopOrderStatus.Reserved or ShopOrderStatus.AwaitingSignature).ToList();
        Closed = rows.Except(Active).ToList();

        var requests = await _reservations.OpenRequestsAsync(eventId, ct);
        var askerIds = requests.Select(o => o.ReservationRequestedByParticipantId)
            .Concat(Active.Select(r => r.Order.ExtensionRequestedByParticipantId))
            .Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        People = await _db.Participants.AsNoTracking().Where(p => askerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => $"{p.FullName} ({p.Email})", ct);
        Requests = requests.Select(o => new Request(o, names.GetValueOrDefault(o.SponsorCompanyId) ?? o.SponsorCompanyId,
            o.ReservationRequestedByParticipantId is int w ? People.GetValueOrDefault(w) : null)).ToList();
    }

    [Audit("Accepted a reservation request", Category = AuditCategory.Admin, Action = "webshop.reservation-request-accept", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostAcceptAsync(
        int orderId, DateOnly? deadline, int reminderWeeks, DateOnly? remindFrom, string? note, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        Message = (await _reservations.AcceptRequestAsync(me.EventId, orderId, deadline, reminderWeeks, remindFrom, note, ct)).Message;
        return RedirectToPage();
    }

    [Audit("Denied a reservation request", Category = AuditCategory.Admin, Action = "webshop.reservation-request-deny", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostDenyAsync(int orderId, string? note, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        Message = (await _reservations.DenyRequestAsync(me.EventId, orderId, note, ct)).Message;
        return RedirectToPage();
    }

    [Audit("Decided a request for more time", Category = AuditCategory.Admin, Action = "webshop.reservation-extension-decide", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostExtensionAsync(int orderId, bool approve, DateOnly? deadline, string? note, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        Message = (await _reservations.DecideExtensionAsync(me.EventId, orderId, approve, deadline, note, ct)).Message;
        return RedirectToPage();
    }

    [Audit("Created a reservation", Category = AuditCategory.Admin, Action = "webshop.reservation-create", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostCreateAsync(
        string? companyId, string? newCompanyName, int[]? productIds, string requesterName, string requesterEmail,
        DateOnly? deadline, int reminderWeeks, DateOnly? remindFrom, string? note, bool stockAlreadyHeld, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var r = await _reservations.CreateAsync(me.EventId,
            new ShopReservationInput(companyId, newCompanyName, productIds ?? Array.Empty<int>(), requesterName ?? "", requesterEmail ?? "",
                deadline, reminderWeeks, remindFrom, note, stockAlreadyHeld), me.Email, ct);
        Message = r.Message;
        return RedirectToPage();
    }

    [Audit("Changed a reservation", Category = AuditCategory.Admin, Action = "webshop.reservation-update", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostUpdateAsync(
        int orderId, string requesterName, string requesterEmail, DateOnly? deadline, int reminderWeeks, DateOnly? remindFrom,
        string? note, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var r = await _reservations.UpdateAsync(me.EventId, orderId, requesterName ?? "", requesterEmail ?? "", deadline, reminderWeeks, remindFrom, note, ct);
        Message = r.Message;
        return RedirectToPage();
    }

    [Audit("Gave a reservation requester sign-in access", Category = AuditCategory.Admin, Action = "webshop.reservation-access", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostAccessAsync(int orderId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        Message = (await _reservations.GrantRequesterAccessAsync(me.EventId, orderId, ct)).Message;
        return RedirectToPage();
    }

    [Audit("Sent a reservation reminder by hand", Category = AuditCategory.Admin, Action = "webshop.reservation-remind", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostRemindAsync(int orderId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        Message = (await _reservations.SendNowAsync(me.EventId, orderId, ct)).Message;
        return RedirectToPage();
    }

    /// <summary>§1312 — merge this reservation into another of the same company (one contract for both; stock stays held).</summary>
    [Audit("Merged two reservations", Category = AuditCategory.Admin, Action = "webshop.reservation-merge", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostMergeAsync(int orderId, int intoOrderId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        Message = (await _reservations.MergeAsync(me.EventId, orderId, intoOrderId, me.Email, ct)).Message;
        return RedirectToPage();
    }

    [Audit("Released a reservation", Category = AuditCategory.Admin, Action = "webshop.reservation-release", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostReleaseAsync(int orderId, string? reason, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        var order = await _orders.GetAnyAsync(me.EventId, orderId, ct);
        if (order is null || !order.IsOrganizerReservation) return NotFound();
        var r = await _orders.CancelAsync(order, me.Email, string.IsNullOrWhiteSpace(reason) ? "Reservation released by the organizers" : reason, ct, byOrganizers: true);
        Message = r.Ok ? $"{order.Number} released — its items are available again." : r.Message;
        return RedirectToPage();
    }
}
