using System.Net;
using System.Net.Mail;
using CommunityHub.Core.Data;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Email;

/// <summary>
/// <see cref="IEmailSender"/> over Brevo SMTP. STARTTLS on port 587.
/// Credentials are injected via <see cref="EmailOptions"/>, which the host
/// populates from Key Vault - no credential is ever in code or config files.
///
/// RING ENFORCEMENT (REQUIREMENTS §23): this single sender is the chokepoint for
/// EVERY outbound mail (broadcast, welcome-with-login, reminders, digests, PIN —
/// every call site funnels through one of the <c>SendAsync</c> overloads). Before
/// any send it ring-gates the recipient: it resolves the address → participant in
/// the active edition, computes the effective ring, and DROPS the send when that
/// ring is outside the <c>outbound-email</c> feature's released ring (read from
/// the gate service, never hardcoded). The ring gate sits IN FRONT of the existing
/// fail-closed <c>Email:OnlySendTo</c> allowlist + <c>RedirectAllTo</c> redirect,
/// which are left fully intact: unknown (non-participant) addresses are not
/// ring-gated and fall through to the allowlist floor.
/// </summary>
// Not sealed: a test double overrides the single dispatch tail (DispatchAsync) to
// capture the fully-gated message without a real SMTP relay. Production uses the
// base implementation unchanged.
public class BrevoEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<BrevoEmailSender>? _log;
    // The sender is a singleton, but RingResolver + FeatureGateService (and their
    // DbContext) are scoped — so a fresh scope is opened per send to ring-gate,
    // exactly as LoggingEmailSender opens a scope per audit-log write. Null in
    // legacy/test wiring that constructs the sender without ring enforcement: the
    // gate then no-ops (rings unenforced) and only the kill switch + redirect apply.
    private readonly IServiceScopeFactory? _scopes;
    private readonly IEmailContextAccessor? _emailContext;

    public BrevoEmailSender(IOptions<EmailOptions> options, ILogger<BrevoEmailSender>? log = null)
        : this(options, scopes: null, emailContext: null, log)
    {
    }

    public BrevoEmailSender(
        IOptions<EmailOptions> options,
        IServiceScopeFactory? scopes,
        IEmailContextAccessor? emailContext,
        ILogger<BrevoEmailSender>? log = null)
    {
        _options = options.Value;
        _log = log;
        _scopes = scopes;
        _emailContext = emailContext;
    }

    /// <summary>
    /// Resolve, for one recipient, the address it would ACTUALLY be delivered to
    /// after the DEV redirect, and whether it may send. Shared with
    /// <c>LoggingEmailSender</c> so the audit log records the real outcome with the
    /// same gating as the send. Audience control is RINGS-ONLY (no static allowlist):
    /// the ring gate inside the sender decides send-vs-drop, so here a non-kill-
    /// switched send is "allowed" (a ring drop, if any, is logged as RING-DROP).
    /// </summary>
    public static (string actualTo, bool allowed) ResolveDelivery(
        EmailOptions options, string toEmail)
    {
        var actualTo = string.IsNullOrWhiteSpace(options.RedirectAllTo)
            ? toEmail
            : options.RedirectAllTo;
        // GLOBAL KILL SWITCH (REQUIREMENTS §23): when on, nothing ever sends.
        return (actualTo, !options.KillSwitch);
    }

    // Test-mode redirect: if Email:RedirectAllTo is set (dev only), swap the
    // recipient and prefix the subject so the original target is preserved.
    private (string actualTo, string finalSubject) ApplyRedirect(string toEmail, string subject)
    {
        if (string.IsNullOrWhiteSpace(_options.RedirectAllTo))
        {
            return (toEmail, subject);
        }
        return (_options.RedirectAllTo, $"[TEST -> {toEmail}] {subject}");
    }

    // Optional RING CEILING (Email:MaxReleaseRing) — caps the effective email
    // release ring (DEV uses Ring1 so ring2+ real people never get DEV mail).
    // Empty/unparseable => null (no ceiling). Accepts a Ring name or number.
    private static Ring? ParseMaxReleaseRing(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (Enum.TryParse<Ring>(s, ignoreCase: true, out var byName)) return byName;
        if (int.TryParse(s, out var n) && Enum.IsDefined(typeof(Ring), n)) return (Ring)n;
        return null;
    }

    /// <summary>
    /// Pure ring-gate decision (REQUIREMENTS §23): does a recipient whose effective
    /// ring is <paramref name="effectiveRing"/> get DROPPED when outbound email is
    /// released to <paramref name="emailReleaseRing"/>? A KNOWN participant is
    /// dropped when its ring is OUTSIDE the release ring (later than it,
    /// <c>effectiveRing &gt; emailReleaseRing</c>). An UNKNOWN address
    /// (<paramref name="found"/> false) is NOT ring-gated here — it is not a
    /// participant, so the decision is left to the allowlist floor downstream.
    /// </summary>
    public static bool IsRingDropped(bool found, Ring effectiveRing, Ring emailReleaseRing) =>
        found && !Rings.IsActiveForRing(effectiveRing, emailReleaseRing);

    /// <summary>
    /// The ring gate that sits IN FRONT of the allowlist for one recipient address.
    /// Resolves the address → participant in the active edition, computes its
    /// effective ring, reads the <c>outbound-email</c> feature's released ring from
    /// the gate service (NOT hardcoded), and returns true to DROP a known recipient
    /// that is out of the rollout.
    ///
    /// EDITION SCOPE: most call sites set an explicit <see cref="EmailContext.EventId"/>
    /// (reminders, templated participant mail, calendar invites, digests). The MANY
    /// that don't — Broadcast, PIN sign-in, invitations, sponsor/lead mail — must
    /// STILL be ring-gated, otherwise real people on those paths are protected only
    /// by the allowlist. So when no edition context is set we FALL BACK to the single
    /// active edition (<see cref="Domain.Event.IsActive"/>), the same "current event"
    /// the PIN login / role hub resolve. This makes the gate fire on EVERY send path.
    ///
    /// Returns false (do not ring-drop) when:
    ///   • the address is UNKNOWN (no participant) — deferred to the allowlist, OR
    ///   • ring enforcement is not wired (no scope factory), OR
    ///   • no active edition can be resolved (so the lookup can't be scoped) —
    ///     deferred to the (fail-closed) allowlist floor, OR
    ///   • anything throws while resolving — FAIL-SOFT on errors but FAIL-CLOSED on
    ///     outcome: a resolve error DROPS the send (returns true) rather than
    ///     leaking mail, and never crashes the send loop.
    /// Logs every ring-drop, mirroring the allowlist-drop log, for auditability.
    /// </summary>
    private async Task<bool> ShouldRingDropAsync(
        string originalTo, CancellationToken ct)
    {
        // Ring enforcement not wired (legacy/test ctor) ⇒ no ring gate; the
        // allowlist remains the only gate (unchanged behaviour).
        if (_scopes is null) return false;

        // SIGN-IN EXEMPTION (operator 2026-06-22): the on-demand sign-in/PIN email has
        // direct, user-initiated impact and MUST reach a user at ANY ring — never ring-
        // drop it. (The global kill switch still applies downstream.) Every other email
        // stays ring-governed by its feature below.
        if (_emailContext?.Current?.RingExempt == true) return false;

        try
        {
            using var scope = _scopes.CreateScope();
            var sp = scope.ServiceProvider;

            // Prefer the caller's explicit edition; otherwise fall back to the
            // single active edition so un-instrumented paths (Broadcast, PIN,
            // invitations, …) are ring-gated too. No active edition ⇒ we cannot
            // scope an email→participant lookup: defer to the allowlist floor
            // normally, but FAIL-CLOSED (drop) under rings-sole-control, where the
            // allowlist is retired and the ring layer is the only floor.
            var eventId = _emailContext?.Current?.EventId ?? 0;
            if (eventId <= 0)
            {
                eventId = await ResolveActiveEventIdAsync(sp, ct);
            }
            if (eventId <= 0)
            {
                // Rings are the SOLE audience control: no edition to scope the
                // recipient lookup ⇒ FAIL-CLOSED (drop), audibly.
                _log?.LogInformation(
                    "Email RING-DROP (no active edition): {Addr}", originalTo);
                return true;
            }

            var rings = sp.GetRequiredService<RingResolver>();
            var gate = sp.GetRequiredService<FeatureGateService>();

            var (found, effectiveRing) =
                await rings.TryGetEffectiveRingByEmailAsync(eventId, originalTo, ct);

            // Unknown address ⇒ not a participant. Normally not ring-gated (the
            // fail-closed allowlist floor covers strangers / organizer addresses).
            // Under rings-sole-control the allowlist is retired, so the ring layer
            // becomes the floor: FAIL-CLOSED (drop) an unknown address so a typo'd /
            // external / never-imported recipient is never mailed.
            if (!found)
            {
                // Unknown / non-participant ⇒ FAIL-CLOSED (rings are the only floor):
                // a typo'd / external / never-imported recipient is never mailed.
                _log?.LogInformation(
                    "Email RING-DROP (unknown recipient): {Addr}", originalTo);
                return true;
            }

            var emailReleaseRing = await gate.GetReleasedRingAsync(
                FeatureCatalog.OutboundEmailKey, eventId, ct);

            // PER-FEATURE GATE (REQUIREMENTS §23a): if the caller tagged the send with
            // the TRIGGERING feature (EmailContext.FeatureKey) and that feature is
            // ring-scoped, tighten to the MORE RESTRICTIVE (lower) of its released ring
            // and the transport's — so a feature still in testing (Ring1) never mails
            // ring-2+ recipients even when outbound-email is Broad. Engine/untagged
            // sends are unaffected (transport ring alone), so this never LOOSENS.
            var featureKey = _emailContext?.Current?.FeatureKey;
            if (!string.IsNullOrWhiteSpace(featureKey)
                && FeatureCatalog.Find(featureKey)?.IsRingScoped == true)
            {
                var featureRing = await gate.GetReleasedRingAsync(featureKey, eventId, ct);
                if ((int)featureRing < (int)emailReleaseRing)
                {
                    emailReleaseRing = featureRing;
                }
            }

            // Optional ring CEILING (e.g. DEV Email:MaxReleaseRing=Ring1): the
            // effective release is the MORE RESTRICTIVE (lower) of the feature's ring
            // and the ceiling, so DEV never reaches ring2+ even if a feature is
            // released broadly.
            var ceiling = ParseMaxReleaseRing(_options.MaxReleaseRing);
            if (ceiling.HasValue && (int)ceiling.Value < (int)emailReleaseRing)
            {
                emailReleaseRing = ceiling.Value;
            }

            if (IsRingDropped(found, effectiveRing, emailReleaseRing))
            {
                _log?.LogInformation(
                    "Email RING-DROP: {Addr} effectiveRing={Effective} > emailReleaseRing={Release}",
                    originalTo, (int)effectiveRing, (int)emailReleaseRing);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            // FAIL-CLOSED on outcome: an exception resolving the ring DROPS the
            // send (never sends to an unverified recipient) and is swallowed so the
            // send loop is never crashed by one bad lookup.
            _log?.LogWarning(ex,
                "Email RING-DROP (resolve error, fail-closed): {Addr}", originalTo);
            return true;
        }
    }

    /// <summary>
    /// Resolve the single active edition (<see cref="Domain.Event.IsActive"/>) to
    /// scope an email→participant ring lookup when the caller set no explicit
    /// <see cref="EmailContext.EventId"/>. Returns 0 when there is no active edition,
    /// in which case the sender treats the recipient as not-ring-gated and defers to
    /// the allowlist floor. Same "current event" query the PIN login uses.
    /// </summary>
    private static async Task<int> ResolveActiveEventIdAsync(
        IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<CommunityHubDbContext>();
        return await db.Events
            .Where(e => e.IsActive)
            .Select(e => e.Id)
            .FirstOrDefaultAsync(ct); // 0 when no active edition
    }

    public Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default)
        => SendAsync(toEmail, subject, htmlBody, cc: null, cancellationToken);

    public async Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        IReadOnlyCollection<string>? cc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            throw new ArgumentException("Recipient address is required.", nameof(toEmail));
        }

        // RING GATE (REQUIREMENTS §23) — in front of the allowlist. Gate the
        // INTENDED recipient (pre-redirect), so a ring-2/3 participant is dropped
        // even when a dev RedirectAllTo would have funnelled it to a test inbox.
        if (await ShouldRingDropAsync(toEmail, cancellationToken)) return;

        var (actualTo, finalSubject) = ApplyRedirect(toEmail, subject);

        if (_options.KillSwitch)
        {
            _log?.LogInformation(
                "Email DROP (KILL SWITCH): original={Original} actual={Actual} subject='{Subject}'",
                toEmail, actualTo, subject);
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromDisplayName),
            Subject = finalSubject,
            Body = htmlBody,
            IsBodyHtml = true,
        };
        message.To.Add(actualTo);
        // Each CC is its own recipient ⇒ ring-gate it too (a known out-of-ring CC
        // is dropped before the allowlist; unknown CCs defer to the allowlist).
        AddCc(message, await RingFilterCcAsync(cc, cancellationToken));
        // The mail is ACTUALLY sending now (it passed the ring gate + allowlist):
        // add the operator BCC so it mirrors only what really goes out.
        AddOperatorBcc(message);

        await DispatchAsync(message, cancellationToken);
    }

    /// <summary>
    /// Hand the fully-built, already-gated <paramref name="message"/> to Brevo SMTP.
    /// This is the single dispatch tail every send path funnels through AFTER the
    /// ring gate, allowlist/redirect/kill-switch, CC filtering and operator-BCC have
    /// been applied — so a test can override it to capture the exact message that
    /// would go on the wire without touching the network. Virtual + protected for
    /// exactly that (tests subclass and short-circuit); production always uses this.
    /// </summary>
    protected virtual async Task DispatchAsync(
        MailMessage message, CancellationToken cancellationToken)
    {
        // Retry transient SMTP failures (connection blips, Brevo throttling /
        // 4xx, socket/IO errors) with a short backoff, on a FRESH connection each
        // attempt. Permanent failures (bad address 5xx) are NOT retried — they
        // throw on the first try so the caller logs the real error. 3 attempts:
        // immediate, +1s, +2s.
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
                {
                    EnableSsl = true, // STARTTLS on 587
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    Credentials = new NetworkCredential(
                        _options.SmtpUsername, _options.SmtpKey),
                };

                await client.SendMailAsync(message, cancellationToken);
                return;
            }
            catch (Exception ex) when (
                attempt < maxAttempts
                && !cancellationToken.IsCancellationRequested
                && IsTransientSmtpError(ex))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }
    }

    /// <summary>
    /// True for SMTP errors worth retrying on a fresh connection: service
    /// unavailable / mailbox busy / transaction failed / general failure (Brevo
    /// throttling + transient relay errors) and connection-level socket/IO/timeout
    /// faults. A permanent rejection (e.g. 550 bad address) is NOT transient.
    /// </summary>
    private static bool IsTransientSmtpError(Exception ex)
    {
        if (ex is SmtpException smtp)
        {
            switch (smtp.StatusCode)
            {
                case SmtpStatusCode.ServiceNotAvailable:   // 421
                case SmtpStatusCode.MailboxBusy:           // 450
                case SmtpStatusCode.MailboxUnavailable:    // 550 can be transient on relays
                case SmtpStatusCode.TransactionFailed:     // 451/554
                case SmtpStatusCode.GeneralFailure:
                case SmtpStatusCode.ClientNotPermitted:
                case SmtpStatusCode.InsufficientStorage:   // 452
                    return true;
            }
            return smtp.InnerException is IOException or System.Net.Sockets.SocketException;
        }
        return ex is IOException or System.Net.Sockets.SocketException or TimeoutException;
    }

    // Ring-gate each CC the same way as the primary recipient (REQUIREMENTS §23):
    // a KNOWN out-of-ring CC is dropped (and logged) before the message is built;
    // unknown CCs pass through to the allowlist floor in AddCc. Returns the kept
    // subset (null in ⇒ null out).
    private async Task<IReadOnlyCollection<string>?> RingFilterCcAsync(
        IReadOnlyCollection<string>? cc, CancellationToken ct)
    {
        if (cc is null || cc.Count == 0) return cc;
        var kept = new List<string>(cc.Count);
        foreach (var raw in cc)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (await ShouldRingDropAsync(raw.Trim(), ct)) continue; // logged inside
            kept.Add(raw);
        }
        return kept;
    }

    // Add each CC, each independently subject to the same redirect/allowlist gate
    // as the primary recipient. A CC redirected to the same dev inbox as the To
    // would duplicate, so de-dup against addresses already on the message.
    private void AddCc(MailMessage message, IReadOnlyCollection<string>? cc)
    {
        if (cc is null) return;
        foreach (var raw in cc)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var (actualCc, _) = ApplyRedirect(raw.Trim(), string.Empty);
            // CCs are already ring-filtered upstream (RingFilterCcAsync) and the kill
            // switch dropped the whole send before we got here, so just add the CC.
            var already = message.To.Concat(message.CC)
                .Any(a => string.Equals(a.Address, actualCc,
                    StringComparison.OrdinalIgnoreCase));
            if (already) continue;
            try { message.CC.Add(actualCc); }
            catch (FormatException)
            {
                _log?.LogInformation("Email CC skipped (bad format): {Cc}", raw);
            }
        }
    }

    // PROD operator BCC. Called ONLY from the actually-sending tail of each send
    // path — after the ring gate AND the allowlist/redirect/kill-switch decision
    // have already let the mail through — so the bcc never lands on a dropped,
    // redirected-away, or kill-switched mail (it reflects exactly what went out).
    // The bcc recipient is the operator (organizer, ring0, allowlisted), so it is
    // intentionally NOT ring/allowlist filtered; it is simply added to the message.
    // De-dup against addresses already on the message so it is not doubled when the
    // operator is also the To/CC (e.g. under a dev RedirectAllTo to the same inbox).
    private void AddOperatorBcc(MailMessage message)
    {
        var bcc = _options.BccAllTo?.Trim();
        if (string.IsNullOrEmpty(bcc)) return;

        var already = message.To.Concat(message.CC).Concat(message.Bcc)
            .Any(a => string.Equals(a.Address, bcc, StringComparison.OrdinalIgnoreCase));
        if (already) return;

        try { message.Bcc.Add(bcc); }
        catch (FormatException)
        {
            // Bad operator address in config: skip the bcc rather than fail the
            // send. Log without the value's content beyond the address itself.
            _log?.LogInformation("Email BCC skipped (bad format): {Bcc}", bcc);
        }
    }

    public async Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string textBody,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            throw new ArgumentException("Recipient address is required.", nameof(toEmail));
        }

        // RING GATE (REQUIREMENTS §23) — in front of the allowlist (multipart path).
        if (await ShouldRingDropAsync(toEmail, cancellationToken)) return;

        var (actualTo, finalSubject) = ApplyRedirect(toEmail, subject);

        if (_options.KillSwitch)
        {
            _log?.LogInformation(
                "Email DROP (KILL SWITCH, multipart): original={Original} actual={Actual} subject='{Subject}'",
                toEmail, actualTo, subject);
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromDisplayName),
            Subject = finalSubject,
            // Body + IsBodyHtml left at text defaults; both representations are
            // added as AlternateViews so the message is a true
            // multipart/alternative (text + HTML).
            Body = textBody,
            IsBodyHtml = false,
        };
        message.To.Add(actualTo);

        var plainView = AlternateView.CreateAlternateViewFromString(
            textBody, System.Text.Encoding.UTF8, "text/plain");
        var htmlView = AlternateView.CreateAlternateViewFromString(
            htmlBody, System.Text.Encoding.UTF8, "text/html");
        // Order matters: least-preferred first, most-preferred last.
        message.AlternateViews.Add(plainView);
        message.AlternateViews.Add(htmlView);
        // Actually sending (passed ring gate + allowlist) ⇒ add the operator BCC.
        AddOperatorBcc(message);

        await DispatchAsync(message, cancellationToken);
    }

    public async Task SendWithIcsAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string icsContent,
        string icsFileName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            throw new ArgumentException("Recipient address is required.", nameof(toEmail));
        }

        // RING GATE (REQUIREMENTS §23) — in front of the allowlist (ics path).
        if (await ShouldRingDropAsync(toEmail, cancellationToken)) return;

        var (actualTo, finalSubject) = ApplyRedirect(toEmail, subject);

        if (_options.KillSwitch)
        {
            _log?.LogInformation(
                "Email DROP (KILL SWITCH, ics): original={Original} actual={Actual} subject='{Subject}'",
                toEmail, actualTo, subject);
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromDisplayName),
            Subject = finalSubject,
            Body = htmlBody,
            IsBodyHtml = true,
        };
        message.To.Add(actualTo);

        var icsBytes = System.Text.Encoding.UTF8.GetBytes(icsContent);
        var icsStream = new MemoryStream(icsBytes);
        var attachment = new Attachment(icsStream, icsFileName, "text/calendar; method=REQUEST");
        message.Attachments.Add(attachment);
        // Actually sending (passed ring gate + allowlist) ⇒ add the operator BCC.
        AddOperatorBcc(message);

        try
        {
            await DispatchAsync(message, cancellationToken);
        }
        finally
        {
            attachment.Dispose();
            icsStream.Dispose();
        }
    }

    public async Task SendWithAttachmentsAsync(
        string toEmail,
        string subject,
        string htmlBody,
        IReadOnlyCollection<EmailAttachment> attachments,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            throw new ArgumentException("Recipient address is required.", nameof(toEmail));
        }

        // RING GATE (REQUIREMENTS §23) — in front of the allowlist (attachments path).
        // A RingExempt EmailContext (e.g. the ERP-inbox send) bypasses this, exactly
        // as it does for every other overload.
        if (await ShouldRingDropAsync(toEmail, cancellationToken)) return;

        var (actualTo, finalSubject) = ApplyRedirect(toEmail, subject);

        if (_options.KillSwitch)
        {
            _log?.LogInformation(
                "Email DROP (KILL SWITCH, attachments): original={Original} actual={Actual} subject='{Subject}'",
                toEmail, actualTo, subject);
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromDisplayName),
            Subject = finalSubject,
            Body = htmlBody,
            IsBodyHtml = true,
        };
        message.To.Add(actualTo);

        var streams = new List<MemoryStream>();
        var added = new List<Attachment>();
        try
        {
            foreach (var a in attachments ?? Array.Empty<EmailAttachment>())
            {
                if (a is null || a.Content is null || a.Content.Length == 0) continue;
                var stream = new MemoryStream(a.Content);
                streams.Add(stream);
                var fileName = string.IsNullOrWhiteSpace(a.FileName) ? "attachment" : a.FileName;
                var contentType = string.IsNullOrWhiteSpace(a.ContentType)
                    ? "application/octet-stream"
                    : a.ContentType;
                var attachment = new Attachment(stream, fileName, contentType);
                message.Attachments.Add(attachment);
                added.Add(attachment);
            }

            // Actually sending (passed ring gate + allowlist) ⇒ add the operator BCC.
            AddOperatorBcc(message);

            await DispatchAsync(message, cancellationToken);
        }
        finally
        {
            foreach (var a in added) a.Dispose();
            foreach (var s in streams) s.Dispose();
        }
    }
}
