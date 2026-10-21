using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Settings;
using CommunityHub.Sponsors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.ViewComponents;

/// <summary>What the "your order is ready" banner shows.</summary>
public sealed record ShopOrderReadyView(int OrderId, string? Number, ShopOrderStatus Status, int Items, bool ViewerIsSigner, string? SignerName);

/// <summary>
/// §1305 — tells a sponsor, the moment they sign in, that an order is waiting for them: one button straight to the
/// contract (or the cart), instead of having to find it in the shop.
/// </summary>
/// <remarks>
/// Operator 2026-09-26: <i>"i as an organizer must be able to prestage things into a cart so a sponsor can login and
/// accept and quickly sign … instead of having to find the options first"</i>. Shown on the start page and on every shop
/// page while the CEH shop is on for the viewer and their company has an unsigned order with items. Best-effort: it
/// never breaks the page it sits on.
/// </remarks>
public sealed class ShopOrderReadyViewComponent : ViewComponent
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly FeatureGateService _gate;
    private readonly SponsorActingCompanyService _acting;

    public ShopOrderReadyViewComponent(
        CommunityHubDbContext db, ICurrentParticipantAccessor participant, FeatureGateService gate, SponsorActingCompanyService acting)
    {
        _db = db;
        _participant = participant;
        _gate = gate;
        _acting = acting;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            var me = _participant.Current;
            if (me is null || !await _gate.IsFeatureEnabledAsync(ShopMailer.FeatureKey, me.EventId, ct))
                return Content(string.Empty);
            var companyId = await _acting.CurrentAsync(me.ParticipantId, me.EventId, ct);
            if (string.IsNullOrWhiteSpace(companyId)) return Content(string.Empty);

            // The order furthest along wins: waiting for signature, then reserved, then a cart with items.
            var order = await _db.ShopOrders.AsNoTracking()
                .Where(o => o.EventId == me.EventId && o.SponsorCompanyId == companyId && o.Lines.Any()
                            && (o.Status == ShopOrderStatus.AwaitingSignature || o.Status == ShopOrderStatus.Reserved
                                || o.Status == ShopOrderStatus.Cart))
                .OrderByDescending(o => o.Status == ShopOrderStatus.AwaitingSignature)
                .ThenByDescending(o => o.Status == ShopOrderStatus.Reserved)
                .ThenByDescending(o => o.UpdatedAt ?? o.CreatedAt)
                .Select(o => new { o.Id, o.Number, o.Status, Items = o.Lines.Sum(l => l.Quantity), o.SignerParticipantId })
                .FirstOrDefaultAsync(ct);
            if (order is null) return Content(string.Empty);

            var signerName = order.SignerParticipantId is int sid
                ? await _db.Participants.AsNoTracking().Where(p => p.Id == sid).Select(p => p.FullName).FirstOrDefaultAsync(ct)
                : null;
            return View(new ShopOrderReadyView(order.Id, order.Number, order.Status, order.Items,
                order.SignerParticipantId == me.ParticipantId, signerName));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Content(string.Empty);
        }
    }
}
