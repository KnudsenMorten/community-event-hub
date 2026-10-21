using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §1305 — the CEH sponsor shop's two reminders:
/// <list type="bullet">
///   <item>item 7c, DAILY: an unsigned order with items → the default signer + default event coordinator,
///   until it is emptied, cancelled or signed;</item>
///   <item>§1312, DAILY: a contract waiting for its signer → the signing mail itself (link + contract PDF) to the signer;</item>
///   <item>item 10, WEEKLY: unreserved items from the previous edition, while the reservation window is open.</item>
/// </list>
/// </summary>
/// <remarks>
/// The tick is hourly; the per-day / per-ISO-week occasion keys in <see cref="ReminderEngine"/> make each mail
/// go at most once per period per person. Gated by <c>sponsor-webshop</c> (off by default, DEV-only).
/// </remarks>
public sealed class ShopRemindersJob
{
    private readonly CommunityHubDbContext _db;
    private readonly ShopMailer _mailer;
    private readonly ReminderEngine _engine;
    private readonly FeatureGateService _gate;
    private readonly TimeProvider _clock;
    private readonly ILogger<ShopRemindersJob> _log;
    private readonly ShopReservationService? _reservations;
    private readonly ShopSigningService? _signing;

    public ShopRemindersJob(
        CommunityHubDbContext db, ShopMailer mailer, ReminderEngine engine, FeatureGateService gate,
        TimeProvider clock, ILogger<ShopRemindersJob> log, ShopReservationService? reservations = null,
        ShopSigningService? signing = null)
    {
        _reservations = reservations;
        _signing = signing;
        _db = db;
        _mailer = mailer;
        _engine = engine;
        _gate = gate;
        _clock = clock;
        _log = log;
    }

    [Function("ShopRemindersJob")]
    // BASE TICK ONLY — the real cadence (hourly) is the JobCatalog interval, editable on /Organizer/Jobs.
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        // "Daily" and "weekly" are counted in EVENT-LOCAL days (Danish time), not UTC.
        var today = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), Core.Integrations.EventTimezone.Tz).DateTime);
        var eventIds = await _db.Events.Where(e => e.IsActive).Select(e => e.Id).ToListAsync(ct);
        foreach (var eventId in eventIds)
        {
            if (!await _gate.IsFeatureEnabledAsync(ShopMailer.FeatureKey, eventId, ct)) continue;

            var cart = await _mailer.BuildCartRemindersAsync(eventId, today, ct);
            var reservation = await _mailer.BuildReservationRemindersAsync(eventId, today, ct);
            var sent = await _engine.SendDueAsync(eventId, cart.Concat(reservation).ToList(), ct);
            // §1308 — organizer reservations: each on its own cadence, to its requester; organizers told once at the deadline.
            if (_reservations is not null)
            {
                var resv = await _reservations.SendDueAsync(eventId, today, ct);
                if (resv > 0) _log.LogInformation("§1308 reservation reminders (event {EventId}): {Sent} sent.", eventId, resv);
            }
            // §1312 — a contract waiting for its signer: the signing mail again, daily, to the signer.
            if (_signing is not null)
            {
                var signReminders = await _signing.SendSignRemindersAsync(eventId, today, ct);
                if (signReminders > 0) _log.LogInformation("§1312 signing reminders (event {EventId}): {Sent} sent.", eventId, signReminders);
            }
            if (sent > 0)
                _log.LogInformation("§1305 shop reminders (event {EventId}): {Sent} sent ({Cart} cart, {Res} reservation candidates).",
                    eventId, sent, cart.Count, reservation.Count);
        }
    }
}
