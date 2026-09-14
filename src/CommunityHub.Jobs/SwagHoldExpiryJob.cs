using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §1165b — release swag-catalogue holds that have run out, and warn before they do.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-01, choosing how reservations work: <b>organizer-only, with an expiry and
/// a notice</b>.</para>
///
/// <para>🔴 <b>This job is what makes the expiry real, not housekeeping.</b> The "one live hold per
/// item" unique index filters on <c>ReleasedAt</c>, because an index cannot compare against "now".
/// So a hold merely PAST its expiry still occupies the item — without this sweep the item would be
/// blocked for ever, which is the precise failure the expiry was added to prevent.</para>
///
/// <para>🔑 <b>The notice is the half that respects the sponsor.</b> A promise that lapses in silence
/// is discovered when somebody else buys the item, and by then it is an apology rather than a
/// decision. Warning first turns it into "do you still want this?".</para>
/// </remarks>
public class SwagHoldExpiryJob
{
    /// <summary>How long before a hold lapses the warning goes out.</summary>
    /// <remarks>
    /// Three days: long enough to act on, short enough that the hold is still the live question.
    /// A warning a month early is filed and forgotten.
    /// </remarks>
    private static readonly TimeSpan NoticeWindow = TimeSpan.FromDays(3);

    private readonly CommunityHubDbContext _db;
    private readonly SwagCatalogHoldService _holds;
    private readonly EngineAlertSender _alerts;
    private readonly TimeProvider _clock;
    private readonly ILogger<SwagHoldExpiryJob> _log;

    public SwagHoldExpiryJob(
        CommunityHubDbContext db,
        SwagCatalogHoldService holds,
        EngineAlertSender alerts,
        TimeProvider clock,
        ILogger<SwagHoldExpiryJob> log)
    {
        _db = db;
        _holds = holds;
        _alerts = alerts;
        _clock = clock;
        _log = log;
    }

    [Function("SwagHoldExpiryJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events
            .Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null) return;

        var now = _clock.GetUtcNow();

        // 1) WARN FIRST, then release. Order matters: a hold that lapses this very tick would
        // otherwise be released and then warned about, which reads as "we took it back, by the way".
        var dueSoon = await _holds.DueForExpiryNoticeAsync(eventId.Value, NoticeWindow, now, ct);
        if (dueSoon.Count > 0)
        {
            var rows = string.Join(string.Empty, dueSoon.Select(h =>
                $"<li><b>{System.Net.WebUtility.HtmlEncode(h.ProductName)}</b> — held for "
                + $"{System.Net.WebUtility.HtmlEncode(h.CompanyName)}, lapses "
                + $"{h.ExpiresAt:yyyy-MM-dd}"
                + (string.IsNullOrWhiteSpace(h.Note)
                    ? string.Empty
                    : $" · <i>{System.Net.WebUtility.HtmlEncode(h.Note)}</i>")
                + "</li>"));

            try
            {
                await _alerts.AlertAsync(
                    $"Swag catalogue: {dueSoon.Count} reservation(s) about to lapse",
                    "<p>These catalogue items are reserved and the hold runs out within three days. "
                    + "When it does they go back on sale automatically.</p>"
                    + $"<ul>{rows}</ul>"
                    + "<p>Either confirm the order with the sponsor, extend the hold, or let it "
                    + "lapse — but the sponsor does not know it is about to, so a word now is worth "
                    + "more than an apology later.</p>",
                    ct,
                    throttleKey: "swag-hold-expiring");

                // 🔒 Stamped only AFTER the mail is away. Stamping first would silence the notice
                // for a mail that never went — the hold would then lapse with nobody warned, which
                // is the one outcome this job exists to prevent.
                await _holds.MarkExpiryNoticeSentAsync(dueSoon, now, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "§1165b: lapse notice failed; will retry next tick.");
            }
        }

        // 2) Release what has actually run out.
        var released = await _holds.ReleaseExpiredAsync(eventId.Value, now, ct);
        if (released.Count == 0) return;

        var lapsed = string.Join(string.Empty, released.Select(h =>
            $"<li><b>{System.Net.WebUtility.HtmlEncode(h.ProductName)}</b> — was held for "
            + $"{System.Net.WebUtility.HtmlEncode(h.CompanyName)}</li>"));

        try
        {
            await _alerts.AlertAsync(
                $"Swag catalogue: {released.Count} reservation(s) lapsed",
                "<p>These holds ran out, so the items are available again:</p>"
                + $"<ul>{lapsed}</ul>"
                + "<p>Nothing was cancelled in the webshop — a hold is only a promise, and an order "
                + "already placed is unaffected.</p>",
                ct,
                throttleKey: "swag-hold-lapsed");
        }
        catch (Exception ex)
        {
            // The release already happened and is the important part; the mail is the courtesy.
            _log.LogWarning(ex, "§1165b: lapsed-hold mail failed; the holds were still released.");
        }
    }
}
