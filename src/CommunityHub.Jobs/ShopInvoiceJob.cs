using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §1305 — signed CEH shop orders → e-conomic draft invoices (see <see cref="ShopDraftInvoiceService"/>). Hourly;
/// gated by <c>sponsor-webshop</c>. Creating an invoice is PROD-only and honours <c>Invoicing:DryRun</c>, so on DEV
/// every run only reports what it WOULD create (visible per order on /Organizer/ShopOrders).
/// </summary>
public sealed class ShopInvoiceJob
{
    private readonly CommunityHubDbContext _db;
    private readonly ShopDraftInvoiceService _invoicing;
    private readonly FeatureGateService _gate;
    private readonly ILogger<ShopInvoiceJob> _log;

    public ShopInvoiceJob(CommunityHubDbContext db, ShopDraftInvoiceService invoicing, FeatureGateService gate, ILogger<ShopInvoiceJob> log)
    {
        _db = db;
        _invoicing = invoicing;
        _gate = gate;
        _log = log;
    }

    // BASE TICK ONLY — the real cadence (hourly) is the JobCatalog interval, editable on /Organizer/Jobs.
    [Function("ShopInvoiceJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        foreach (var eventId in await _db.Events.Where(e => e.IsActive).Select(e => e.Id).ToListAsync(ct))
        {
            if (!await _gate.IsFeatureEnabledAsync(ShopMailer.FeatureKey, eventId, ct)) continue;
            var r = await _invoicing.RunAsync(eventId, ct);
            foreach (var p in r.Problems) _log.LogWarning("§1305 shop invoicing: {Problem}", p);
        }
    }
}
