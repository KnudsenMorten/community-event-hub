using CommunityHub.Audit;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Settings;
using CommunityHub.Sponsors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Sponsor.Shop;

/// <summary>
/// §1312 — one reservation, self-service: ask for more time (a date and a reason, for the organizers to approve) or cancel
/// it. Operator 2026-09-27: "add a extension request button in the reminder mail where they select a date and give reason -
/// and it is sent to me for approval" · "also give sponsor option to cancel their reservation (button) from email".
/// </summary>
/// <remarks>
/// 🔒 The mail's buttons only OPEN this page; cancelling needs a button press here. A link that cancelled by itself would
/// be triggered by the mail scanners that open every link in a message.
/// </remarks>
[Authorize]
public class ReservationModel : ShopPageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ShopOrderService _orders;
    private readonly ShopReservationService _reservations;
    private readonly ShopMailer _mailer;

    public ReservationModel(
        ICurrentParticipantAccessor participant, FeatureGateService gate, SponsorActingCompanyService acting,
        ShopCompanyService companies, CommunityHubDbContext db, ShopOrderService orders, ShopReservationService reservations,
        ShopMailer mailer)
        : base(participant, gate, acting, companies)
    {
        _db = db;
        _orders = orders;
        _reservations = reservations;
        _mailer = mailer;
    }

    public ShopOrder? Order { get; private set; }
    [TempData] public string? Message { get; set; }
    [TempData] public bool MessageIsError { get; set; }

    public async Task OnGetAsync(int id, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return;
        Order = await LoadAsync(id, ct);
    }

    [Audit("Asked for more time on a reservation", Action = "webshop.reservation-extension-request", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostMoreTimeAsync(int id, DateOnly? until, string? reason, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage(new { id });
        if (Participant.Current!.IsActingAs) return Done(id, ShopResult.Fail("More time is asked for by the sponsor's own contact, not while acting as them."));
        var order = await LoadAsync(id, ct);
        if (order is null) return NotFound();
        return Done(id, await _reservations.RequestExtensionAsync(EventId, CompanyId!, order.Id, ParticipantId, until, reason, ct));
    }

    [Audit("Cancelled a reservation", Action = "webshop.reservation-cancel-by-sponsor", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostCancelAsync(int id, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage(new { id });
        var order = await LoadAsync(id, ct);
        if (order is null) return NotFound();
        if (order.Status is ShopOrderStatus.Signed or ShopOrderStatus.Cancelled)
            return Done(id, ShopResult.Fail("This order is already signed or cancelled."));
        // The sponsor's cancellation mail (sent by CancelAsync) is CC'd to the organizers, so no separate notice here.
        var r = await _orders.CancelAsync(order, ActorEmail, "Cancelled by the sponsor from the reservation page", ct);
        return Done(id, r.Ok ? ShopResult.Success("Your reservation is cancelled and the items are released. Thank you for letting us know.") : r);
    }

    /// <summary>The order under its own company (the person is switched to it when they are linked to it — as the contract page).</summary>
    private async Task<ShopOrder?> LoadAsync(int id, CancellationToken ct)
    {
        var orderCompany = await _db.ShopOrders.AsNoTracking()
            .Where(o => o.EventId == EventId && o.Id == id).Select(o => o.SponsorCompanyId).FirstOrDefaultAsync(ct);
        await FollowOrderCompanyAsync(orderCompany, ct);
        return await _orders.GetAsync(EventId, CompanyId!, id, ct);
    }

    private IActionResult Done(int id, ShopResult r)
    {
        Message = r.Message;
        MessageIsError = !r.Ok;
        return RedirectToPage(new { id });
    }
}
