using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// Backstop for the Master Class waitlist OFFER hold (REQUIREMENTS §6). Held offers
/// expire lazily on any attendee/organizer interaction; this job guarantees they
/// also expire with no activity — an undecided offer past its window falls back to
/// the operator default (auto-switch), freeing the old seat and promoting its
/// waitlist. Any seat freed that way is notified via the ring-gated promotion email.
/// Runs every 15 minutes.
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

    [Function("WaitlistOfferExpiryJob")]
    public async Task Run([TimerTrigger("0 */15 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var promotions = await _svc.ExpireOffersAsync(_clock.GetUtcNow(), eventId: null, ct);
        if (promotions.Count == 0) return;

        var domain = _config["Hub:CustomDomain"];
        var baseUrl = string.IsNullOrWhiteSpace(domain) ? "https://eldk27.eventhub.expertslive.dk" : $"https://{domain}";

        var sent = 0;
        foreach (var p in promotions)
        {
            if (p.PromotedSignupId is not int id) continue;
            try { if (await _promo.SendPromotionAsync(id, baseUrl, ct)) sent++; } catch { /* retryable next run */ }
        }
        _log.LogInformation(
            "WaitlistOfferExpiryJob: {Expired} offer(s) expired/auto-switched, {Sent} promotion email(s) sent.",
            promotions.Count, sent);
    }
}
