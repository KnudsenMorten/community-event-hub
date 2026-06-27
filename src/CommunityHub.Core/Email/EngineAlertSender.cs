using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Email;

/// <summary>
/// Sends OPERATIONAL / ENGINE alert emails to the developer/ops mailbox (operator
/// 2026-06-25: "as developer I need an email if errors happen in any of the engines —
/// otherwise I have no way of knowing it").
///
/// Why this exists separately from a normal <see cref="IEmailSender"/> call: the
/// transport ring-gate (<c>BrevoEmailSender.ShouldRingDropAsync</c>) FAILS CLOSED for
/// any non-participant recipient — so an alert to an ops address (not an imported
/// attendee) was being silently DROPPED ("RING-DROP (unknown recipient)"). That is why
/// no engine/alert emails were arriving. This sender sets an <see cref="EmailContext"/>
/// with <see cref="EmailContext.RingExempt"/> = true so the alert ALWAYS delivers (the
/// global kill switch + redirect still apply). It is for INTERNAL ops mail only — never
/// for participant/attendee mail, which must stay ring-governed.
///
/// Throttled per key so a job failing every tick can't flood the inbox.
/// </summary>
public sealed class EngineAlertSender
{
    private readonly IEmailSender _email;
    private readonly IEmailContextAccessor _ctx;
    private readonly TimeProvider _clock;
    private readonly ILogger<EngineAlertSender> _log;

    /// <summary>The ops/developer mailbox. Matches the convention used by the other
    /// reconcile engines (e.g. ErpWebshopContactSync).</summary>
    public const string Recipient = "mok@expertslive.dk";

    /// <summary>Minimum gap between two alerts that share a throttle key.</summary>
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromHours(6);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new();

    public EngineAlertSender(
        IEmailSender email, IEmailContextAccessor ctx, TimeProvider clock, ILogger<EngineAlertSender> log)
    {
        _email = email;
        _ctx = ctx;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// Email an ops alert. <paramref name="throttleKey"/> (e.g. the engine/function name)
    /// suppresses repeats within <see cref="ThrottleWindow"/>; pass null to never throttle
    /// (e.g. a one-off "records created" notification). Never throws — a mail failure must
    /// not break the engine that is reporting.
    /// </summary>
    public async Task AlertAsync(string subject, string htmlBody, CancellationToken ct, string? throttleKey = null)
    {
        if (throttleKey is not null)
        {
            var now = _clock.GetUtcNow();
            if (_lastSent.TryGetValue(throttleKey, out var last) && now - last < ThrottleWindow)
            {
                _log.LogInformation("EngineAlert throttled ({Key}): {Subject}", throttleKey, subject);
                return;
            }
            _lastSent[throttleKey] = now;
        }

        try
        {
            using var _ = _ctx.Set(new EmailContext("engine-alert", RingExempt: true));
            await _email.SendAsync(Recipient, subject, htmlBody, ct);
            _log.LogInformation("EngineAlert sent to {To}: {Subject}", Recipient, subject);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EngineAlert email to {To} failed: {Subject}", Recipient, subject);
        }
    }
}
