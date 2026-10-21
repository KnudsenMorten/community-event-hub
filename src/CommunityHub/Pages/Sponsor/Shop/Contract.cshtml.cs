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
/// §1305 item 7 — read, download and sign the contract. The magic link in the signing mail lands here.
/// </summary>
/// <remarks>
/// 🔒 Signing is refused while an organizer is ACTING AS someone: pre-staging a cart for a sponsor is the
/// point of act-as, signing a legal agreement on their behalf is not.
/// </remarks>
[Authorize]
public class ContractModel : ShopPageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ShopOrderService _orders;
    private readonly ShopSigningService _signing;

    public ContractModel(
        ICurrentParticipantAccessor participant, FeatureGateService gate, SponsorActingCompanyService acting,
        ShopCompanyService companies, CommunityHubDbContext db, ShopOrderService orders, ShopSigningService signing)
        : base(participant, gate, acting, companies)
    {
        _db = db;
        _orders = orders;
        _signing = signing;
    }

    public ShopOrder? Order { get; private set; }
    public ShopContractData? Data { get; private set; }
    public bool MaySign { get; private set; }
    public string? SignerName { get; private set; }
    public string? WhyNotSign { get; private set; }
    [TempData] public string? Message { get; set; }
    [TempData] public bool MessageIsError { get; set; }

    public async Task OnGetAsync(int id, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return;
        Order = await LoadOrderAsync(id, ct);
        if (Order is null) return;
        Data = await _signing.BuildAsync(Order, ct);
        SignerName = Data.Signer.Name;

        var me = Participant.Current!;
        if (Order.Status is ShopOrderStatus.Signed or ShopOrderStatus.Cancelled) return;
        if (me.IsActingAs) { WhyNotSign = "You are acting as this contact. A contract can only be signed by the signer themselves."; return; }
        if (Order.SignerParticipantId is int s && s != me.ParticipantId)
        {
            WhyNotSign = $"This contract is waiting for {SignerName} to sign. You can send them the link from the cart.";
            return;
        }
        if (Order.SignerParticipantId is null)
        {
            var isSigner = await _db.SponsorCompanyContacts.AnyAsync(l => l.EventId == EventId
                && l.SponsorCompanyId == CompanyId && l.ParticipantId == me.ParticipantId && l.IsSigner, ct);
            if (!isSigner) { WhyNotSign = "Choose a signer on the cart page first."; return; }
        }
        MaySign = true;
    }

    public async Task<IActionResult> OnGetPdfAsync(int id, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage(new { id });
        var order = await LoadOrderAsync(id, ct);
        if (order is null) return NotFound();
        var pdf = await _signing.RenderAsync(order, ct);
        var suffix = order.Status == ShopOrderStatus.Signed ? "signed" : "unsigned";
        return File(pdf, "application/pdf", $"Event-Sponsor-Contract-{order.Number}-{suffix}.pdf");
    }

    [Audit("Signed a sponsor contract", Action = "webshop.contract-sign", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostSignAsync(int id, string kind, string signatureData, bool agree, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage(new { id });
        var me = Participant.Current!;
        if (me.IsActingAs) return Fail(id, "A contract cannot be signed while acting as someone.");
        if (!agree) return Fail(id, "Please tick that you agree to the terms and are authorised to sign.");

        var order = await LoadOrderAsync(id, ct);
        if (order is null) return NotFound();

        var bytes = DecodeDataUrl(signatureData);
        if (bytes is null) return Fail(id, "Please draw, type or upload your signature first.");
        var sigKind = kind switch
        {
            "typed" => ShopSignatureKind.Typed,
            "uploaded" => ShopSignatureKind.Uploaded,
            _ => ShopSignatureKind.Drawn,
        };
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var r = await _signing.SignAsync(order, me.ParticipantId, sigKind, bytes, ip, ct);
        Message = r.Message;
        MessageIsError = !r.Ok;
        return RedirectToPage(new { id });
    }

    /// <summary>Cancel an unsigned order from the contract itself; its items are given back to both shops.</summary>
    [Audit("Cancelled a sponsor shop order", Action = "webshop.order-cancel", TargetType = "ShopOrder")]
    public async Task<IActionResult> OnPostCancelAsync(int id, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage(new { id });
        var order = await LoadOrderAsync(id, ct);
        if (order is null) return NotFound();
        if (order.Status == ShopOrderStatus.Signed)
            return Fail(id, "A signed contract can only be cancelled by the organizers.");
        var r = await _orders.CancelAsync(order, ActorEmail, "Cancelled by the sponsor", ct);
        Message = r.Message;
        MessageIsError = !r.Ok;
        return RedirectToPage(new { id });
    }

    /// <summary>
    /// The order, under the company it belongs to: §1312 — a person linked to that company is switched to it first, so the
    /// signing mail's link works whatever the switcher stood on. Anyone else still gets null ("not found for your company").
    /// </summary>
    private async Task<ShopOrder?> LoadOrderAsync(int id, CancellationToken ct)
    {
        var orderCompany = await _db.ShopOrders.AsNoTracking()
            .Where(o => o.EventId == EventId && o.Id == id).Select(o => o.SponsorCompanyId).FirstOrDefaultAsync(ct);
        await FollowOrderCompanyAsync(orderCompany, ct);
        return await _orders.GetAsync(EventId, CompanyId!, id, ct);
    }

    private IActionResult Fail(int id, string message)
    {
        Message = message;
        MessageIsError = true;
        return RedirectToPage(new { id });
    }

    /// <summary>"data:image/png;base64,…" → bytes, or null. Size is bounded before decoding.</summary>
    public static byte[]? DecodeDataUrl(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl)) return null;
        var comma = dataUrl.IndexOf(',');
        if (!dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || comma < 0) return null;
        var b64 = dataUrl[(comma + 1)..];
        if (b64.Length > ShopSigningService.MaxSignatureBytes * 4 / 3 + 8) return null;
        try { return Convert.FromBase64String(b64); } catch (FormatException) { return null; }
    }
}
