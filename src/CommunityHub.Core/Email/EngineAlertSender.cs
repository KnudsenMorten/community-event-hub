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
    /// <param name="devSilent">
    /// §752.9 — <c>true</c> for an alert that reports CONFIGURATION or INTEGRATION state which is
    /// deliberately different on DEV, and is therefore news only in PROD.
    /// </param>
    /// <param name="alsoTo">
    /// §1121 — extra ops addresses that must get this alert ALONGSIDE <paramref name="recipient"/>,
    /// on the SAME mail rather than a copy each.
    ///
    /// <para>🔒 <b>They go on the To: line, NOT CC</b> (operator 2026-08-25: *"put in to field"* ·
    /// *"not cc"*). These are co-owners of the job the mail describes, not people being kept in the
    /// loop, and a CC reads as "for your information" — which is exactly the wrong instruction for a
    /// speaker sitting held in the Zoho queue.</para>
    ///
    /// <para>🔑 <b>One mail, several To — never a send each.</b> These alerts start conversations
    /// ("I've done it"); two independent sends give two threads in which neither person can see the
    /// other already acted. That is the very failure the shared <c>info@</c> inbox exists to prevent,
    /// so a send-per-recipient would undo it one address at a time.</para>
    ///
    /// <para>🔒 Every extra rides the same <see cref="EmailContext.RingExempt"/> context as the
    /// primary, so a NON-PARTICIPANT address delivers. Without that it is dropped by
    /// <c>BrevoEmailSender.ShouldRingDropAsync</c>, which fails closed on an unknown recipient — the
    /// same trap that made engine alerts vanish before this class existed.</para>
    /// </param>
    public async Task AlertAsync(
        string subject, string htmlBody, CancellationToken ct,
        string? throttleKey = null, string? recipient = null, bool devSilent = false,
        IReadOnlyCollection<string>? alsoTo = null)
    {
        // 🔑 §752.9 (operator 2026-08-01: *"i still get alerts from dev env which i thought we
        // disabled"*). §716 silenced the "Engine INACTIVE" family by guarding ONE call site. It
        // worked — that family stopped dead on 31 Jul and has not returned — and it did not
        // generalise: two others ("Background jobs: N look asleep", "Stage-2 CEH→Zoho push:
        // failures") kept arriving, because a per-call-site guard only fixes the sites someone
        // remembered to visit. He reasonably read the first fix as "DEV alerts are off".
        //
        // ⇒ The decision now lives HERE, at the single choke point every alert already passes
        // through for its §702 [DEV]/[PROD] tag. A call site declares intent with one flag instead
        // of re-implementing an environment check.
        //
        // 🔒 OPT-IN, deliberately. Defaulting to "silent on DEV" would silence every alert nobody
        // has reviewed — including the ones §716 was careful to KEEP, like an engine that actually
        // threw. Suppression must be a decision someone made about a specific alert, never
        // something inherited by omission.
        //
        // 🔒 DEV is matched POSITIVELY (§716's rule): UNKNOWN still alerts. An unrecognised host is
        // not evidence of DEV, and guessing "probably dev" is how a real PROD alert goes missing.
        if (devSilent && string.Equals(
                _env.Label, CommunityHub.Core.Diagnostics.HubEnvironment.Dev, StringComparison.Ordinal))
        {
            _log.LogInformation(
                "EngineAlert suppressed on DEV (§752.9): {Subject}. This alert reports state that is "
                + "intentionally different here; it still fires in PROD.", subject);
            return;
        }

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

        // §1121 — the primary FIRST, then the extras, de-duplicated case-insensitively so a caller
        // that names an address already on the line cannot produce it twice. Order matters only for
        // legibility: the shared ops inbox stays the address the mail is visibly addressed to.
        var recipients = new List<string> { to };
        if (alsoTo is not null)
        {
            foreach (var raw in alsoTo)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var extra = raw.Trim();
                if (recipients.Any(r => string.Equals(r, extra, StringComparison.OrdinalIgnoreCase)))
                    continue;
                recipients.Add(extra);
            }
        }

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
            // 🔒 One address ⇒ the ORIGINAL 4-arg call, byte for byte. Every existing alert and every
            // test double that only implements SendAsync keeps the exact path it had; the multi-To
            // overload is entered only by a caller that actually asked for extra recipients.
            if (recipients.Count == 1)
                await _email.SendAsync(to, tagged, htmlBody, ct);
            else
                await _email.SendToManyAsync(recipients, tagged, htmlBody, ct);

            _log.LogInformation(
                "EngineAlert sent to {To}: {Subject}", string.Join(", ", recipients), tagged);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex, "EngineAlert email to {To} failed: {Subject}", string.Join(", ", recipients), tagged);
        }
    }

    /// <summary>
    /// §1124 — send one ops alert to a WHOLE recipient list (typically
    /// <see cref="EmailOptions.SpeakerSessionRecipients"/>), first address as the primary.
    /// </summary>
    /// <remarks>
    /// <para>🔑 Exists so the seven speaker/session pending-task mails read IDENTICALLY at their
    /// call sites — <c>AlertToAsync(opts.SpeakerSessionRecipients(), …)</c> — instead of each
    /// re-deriving a primary and a tail. The drift this replaces was not hypothetical: those seven
    /// had five different answers for who should be told.</para>
    ///
    /// <para>An empty or null list sends nothing and says so in the log, rather than falling back to
    /// the developer mailbox. A speaker/session mail that quietly reverts to <c>mok@</c> is exactly
    /// what §1124 was asked to remove.</para>
    /// </remarks>
    public Task AlertToAsync(
        IReadOnlyList<string>? recipients, string subject, string htmlBody, CancellationToken ct,
        string? throttleKey = null, bool devSilent = false)
    {
        if (recipients is null || recipients.Count == 0)
        {
            _log.LogWarning(
                "§1124: no speaker/session recipients configured — alert NOT sent: {Subject}", subject);
            return Task.CompletedTask;
        }

        return AlertAsync(
            subject, htmlBody, ct,
            throttleKey: throttleKey,
            recipient: recipients[0],
            devSilent: devSilent,
            alsoTo: recipients.Count > 1 ? recipients.Skip(1).ToList() : null);
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
