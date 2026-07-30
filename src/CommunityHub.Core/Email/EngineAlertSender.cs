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

    /// <summary>§702 — which environment is shouting. Prefixed to every alert subject.</summary>
    private readonly CommunityHub.Core.Diagnostics.HubEnvironment _env;

    /// <summary>The ops/developer mailbox. Matches the convention used by the other
    /// reconcile engines (e.g. ErpWebshopContactSync).</summary>
    public const string Recipient = "mok@expertslive.dk";

    /// <summary>Minimum gap between two alerts that share a throttle key.</summary>
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromHours(6);

    /// <summary>
    /// Last-sent stamp per throttle key. INSTANCE state, not static.
    /// </summary>
    /// <remarks>
    /// 🔒 This was <c>static</c>. Behaviourally identical in production — both hosts register
    /// <see cref="EngineAlertSender"/> as a SINGLETON, so there is exactly one instance per process
    /// either way — but static made the throttle a hidden global that leaked ACROSS TESTS: two tests
    /// exercising the same throttle key in one test process would interfere, and the second would
    /// see a suppressed send depending purely on execution order. §701.1 introduced a deliberately
    /// SHARED key (<c>engine-fail:state-store-unavailable</c>), which would have made that latent
    /// hazard a real, order-dependent flake.
    /// </remarks>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new();

    /// <summary>
    /// <paramref name="env"/> (§702) is OPTIONAL and defaults to the UNKNOWN label. That is
    /// deliberate: a missing registration then produces <c>[UNKNOWN]</c> — visibly unresolved —
    /// rather than a confident <c>[PROD]</c> on a DEV alert, which is the failure mode §702.1
    /// exists to prevent. Both hosts register it, so the default is a floor, not the norm.
    /// </summary>
    public EngineAlertSender(
        IEmailSender email, IEmailContextAccessor ctx, TimeProvider clock, ILogger<EngineAlertSender> log,
        CommunityHub.Core.Diagnostics.HubEnvironment? env = null)
    {
        _email = email;
        _ctx = ctx;
        _clock = clock;
        _log = log;
        _env = env ?? new CommunityHub.Core.Diagnostics.HubEnvironment(null, null);
    }

    /// <summary>
    /// Email an ops alert. <paramref name="throttleKey"/> (e.g. the engine/function name)
    /// suppresses repeats within <see cref="ThrottleWindow"/>; pass null to never throttle
    /// (e.g. a one-off "records created" notification). <paramref name="recipient"/> targets
    /// a different ops mailbox than the default developer one (e.g. §203 sends the
    /// pending-approvals digest to <c>info@expertslive.dk</c>); null/blank uses the default
    /// <see cref="Recipient"/>. Stays ring-exempt either way. Never throws — a mail failure
    /// must not break the engine that is reporting.
    /// </summary>
    public async Task AlertAsync(
        string subject, string htmlBody, CancellationToken ct,
        string? throttleKey = null, string? recipient = null)
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

        var to = string.IsNullOrWhiteSpace(recipient) ? Recipient : recipient.Trim();

        // §702 (operator 2026-07-29: "include [DEV] and [PROD] in all alert mails so i can see which
        // env is sending the alert / impacted. only for alerts of course, not to roles").
        //
        // 🔒 TAGGED HERE AND ONLY HERE. This method is the single choke point for ops mail — every
        // alert call site goes through it — and the class is internal/ring-exempt by construction,
        // so participant + role mail can never pick the tag up. That is what makes "only for alerts"
        // true structurally rather than by convention.
        //
        // §701 is why it exists: three engine-failure mails arrived and could only be attributed to
        // DEV by the accidental "[TEST -> …]" redirect prefix. A PROD alert carries no such marker.
        var tagged = ApplyEnvironmentTag(subject);

        try
        {
            using var _ = _ctx.Set(new EmailContext("engine-alert", RingExempt: true));
            await _email.SendAsync(to, tagged, htmlBody, ct);
            _log.LogInformation("EngineAlert sent to {To}: {Subject}", to, tagged);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EngineAlert email to {To} failed: {Subject}", to, tagged);
        }
    }

    /// <summary>
    /// §702 — prefix the subject with <c>[DEV]</c> / <c>[PROD]</c>, idempotently.
    /// </summary>
    /// <remarks>
    /// Idempotent on purpose: a caller that has already tagged its own subject (or a retry that
    /// re-enters with an alerted subject) must not produce <c>[DEV] [DEV] …</c>. The tag goes FIRST
    /// so it survives a mail client truncating a long subject — the environment is the part he scans
    /// for, and the existing <c>[ELDK27]</c> edition tag sits at the end where it can be cut off.
    /// </remarks>
    internal string ApplyEnvironmentTag(string subject)
    {
        var s = subject ?? string.Empty;
        return s.StartsWith(_env.SubjectTag, StringComparison.OrdinalIgnoreCase)
            ? s
            : $"{_env.SubjectTag} {s}".TrimEnd();
    }
}
