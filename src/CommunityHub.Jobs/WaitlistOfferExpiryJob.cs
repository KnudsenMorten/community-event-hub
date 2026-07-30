using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// RETIRED (§252 gap audit F2, 2026-07-07). This job was the backstop for the
/// Master Class waitlist OFFER hold (REQUIREMENTS §6) — but the <c>Offered</c>
/// signup state is RESERVED/UNUSED: no code path ever assigns
/// <c>Status = Offered</c> (only comparison reads exist), and the
/// PromotionMode/OfferHoldHours settings have a save helper but no UI writes and
/// no engine reads. Promotion happens directly; no offers ever occur, so the
/// 15-minute timer polled for a state that cannot exist. The <c>[Function]</c>
/// timer trigger has been REMOVED so the Functions host never discovers or
/// schedules it — the class is kept compiling (and manually invokable) so the
/// expiry path and its semantics remain intact if offers are ever introduced.
/// </summary>
public sealed class WaitlistOfferExpiryJob
{
    private readonly CommunityHubDbContext _db;
    private readonly MasterClassSignupService _svc;
    private readonly MasterClassPromotionEmailService _promo;
    private readonly IConfiguration _config;
    private readonly TimeProvider _clock;
    private readonly ILogger<WaitlistOfferExpiryJob> _log;

    public WaitlistOfferExpiryJob(
        CommunityHubDbContext db, MasterClassSignupService svc,
        MasterClassPromotionEmailService promo, IConfiguration config,
        TimeProvider clock, ILogger<WaitlistOfferExpiryJob> log)
    {
        _db = db; _svc = svc; _promo = promo; _config = config; _clock = clock; _log = log;
    }

    // §252 F2: NO [Function]/[TimerTrigger] attribute — the job is retired and must
    // never be scheduled (the Offered state it polls for is reserved/unused).
    // (Was: every 15 minutes, "0 */15 * * * *".)
    public async Task Run(TimerInfo timer, CancellationToken ct)
    {
        var promotions = await _svc.ExpireOffersAsync(_clock.GetUtcNow(), eventId: null, ct);
        if (promotions.Count == 0) return;

        var domain = _config["Hub:CustomDomain"];
        var baseUrl = string.IsNullOrWhiteSpace(domain) ? "https://eldk27.eventhub.expertslive.dk" : $"https://{domain}";

        var sent = 0;
        foreach (var p in promotions)
        {
            if (p.PromotedSignupId is not int id) continue;
            try { if (await _promo.SendPromotionAsync(id, baseUrl, ct, p.ReleasedTitle)) sent++; } catch { /* retryable next run */ }
        }
        _log.LogInformation(
            "WaitlistOfferExpiryJob: {Expired} offer(s) expired/auto-switched, {Sent} promotion email(s) sent.",
            promotions.Count, sent);
    }
}
