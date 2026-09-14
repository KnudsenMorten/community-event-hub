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
/// the active edition (falling back to the ambient <see cref="EmailContext.ParticipantId"/>
/// when the delivery address itself is not a participant address, §234), computes
/// the effective ring, and DROPS the send when that ring is outside the
/// <c>outbound-email</c> feature's released ring (read from the gate service, never
/// hardcoded). Audience control is RINGS-ONLY by design (operator decision — there
/// is no static allowlist and none will be added): the layers are
/// the ring gate (fail-closed for unknown recipients), the DEV
/// <c>Email:RedirectAllTo</c> redirect, and the global <c>Email:KillSwitch</c>.
/// Every send's real outcome (delivered vs ring-/kill-switch-dropped) is recorded
/// on the optional <see cref="IEmailDeliveryOutcome"/> seam so ledgers and the
/// audit log never mistake a drop for a delivery (§234).
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
    // §234: the delivered-vs-dropped seam. Optional (null in legacy/test wiring ⇒
    // outcomes simply aren't recorded); when wired, EVERY public send path records
    // ring-drop / kill-switch / delivered so ledger callers + the audit decorator
    // never mistake a drop for a delivery.
    private readonly IEmailDeliveryOutcome? _outcome;

    public BrevoEmailSender(IOptions<EmailOptions> options, ILogger<BrevoEmailSender>? log = null)
        : this(options, scopes: null, emailContext: null, log)
    {
    }

    public BrevoEmailSender(
        IOptions<EmailOptions> options,
        IServiceScopeFactory? scopes,
        IEmailContextAccessor? emailContext,
        ILogger<BrevoEmailSender>? log = null,
        IEmailDeliveryOutcome? outcome = null)
    {
        _options = options.Value;
        _log = log;
        _scopes = scopes;
        _emailContext = emailContext;
        _outcome = outcome;
    }

    /// <summary>
    /// Resolve, for one recipient, the address it would ACTUALLY be delivered to
    /// after the DEV redirect, and whether it may send. Shared with
    /// <c>LoggingEmailSender</c> so the audit log records the real outcome with the
    /// same gating as the send. Audience control is RINGS-ONLY by design (no static
    /// allowlist exists or is planned): the ring gate inside the sender decides
    /// send-vs-drop, so here a non-kill-switched send is "allowed" (a ring drop, if
    /// any, is reported through <see cref="IEmailDeliveryOutcome"/> + the RING-DROP log).
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
    //
    // §180: EVERY send overload funnels through here, so this is also where the subject
    // is NORMALISED — the edition code is forced to a single POSTFIX at the END of the
    // subject (idempotent, [EXT]-preserving) BEFORE the message is built/dispatched, so
    // inline-built emails get it just like template-rendered ones.
    private (string actualTo, string finalSubject) ApplyRedirect(string toEmail, string subject)
    {
        var normalized = NormalizeSubject(subject, _options.EventCode);
        if (string.IsNullOrWhiteSpace(_options.RedirectAllTo))
        {
            return (toEmail, normalized);
        }
        return (_options.RedirectAllTo, $"[TEST -> {toEmail}] {normalized}");
    }

    /// <summary>
    /// §302 default e-mail font (operator 2026-07-24: "the chosen font make it impossible
    /// to read the mail … change all email fonts to use aptos or ariel as default").
    /// Wraps EVERY body in an Aptos→Segoe UI→Arial container so nothing falls back to the
    /// client's serif default. Inner declarations still win — this only sets a floor.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>§1089 — THIS USED TO SKIP ANY BODY CONTAINING THE STRING "font-family", AND THAT
    /// IS WHY THE SPEAKER-APPROVAL MAIL ARRIVED IN TIMES NEW ROMAN</b> (operator 2026-08-19:
    /// *"email looks hard to read + font"*).</para>
    ///
    /// <para>The guard was asking a semantic question — <i>"does this mail style itself?"</i> — with
    /// a substring test. A mail whose PROSE carries no font at all, but which contains one
    /// <see cref="Organizer.SpeakerApprovalService"/> button (and every button declares a
    /// <c>font-family</c> for its own label), answered <b>yes</b>. So the wrapper was skipped for the
    /// whole document and every paragraph fell back to serif — while the buttons, carrying their own
    /// font, stayed sans. That mismatch is exactly what his screenshot showed, and it is the tell:
    /// <b>one styled element deep in the body silently opted the entire mail out.</b></para>
    ///
    /// <para>🔑 The fix is to stop asking the question. Wrapping is a DEFAULT, not an override: an
    /// inline <c>font-family</c> on any descendant beats one inherited from an ancestor, so a
    /// self-styling templated mail renders exactly as before, and a partly-styled one now gets a
    /// sane floor for the parts nobody styled. There is no case where "no font at all" was wanted.
    /// ⇒ Cheaper and safer than a smarter detector, which would have been the same bug with a better
    /// regex — and would have failed again the next time a helper grew a style attribute.</para>
    ///
    /// <para>🔒 Safe to apply unconditionally: no mail body in this codebase is a full
    /// <c>&lt;html&gt;</c> document (checked 2026-08-19) — they are all fragments, which is what the
    /// send path has always assumed.</para>
    /// </remarks>
    public static string ApplyDefaultFont(string? htmlBody)
    {
        if (string.IsNullOrWhiteSpace(htmlBody)) return htmlBody ?? string.Empty;
        return "<div style=\"font-family:Aptos,'Segoe UI',Arial,Helvetica,sans-serif;"
             + "font-size:14px;line-height:1.5;color:#111827;\">" + htmlBody + "</div>";
    }

    /// <summary>
    /// §180 subject normalisation — the SINGLE chokepoint that guarantees the edition
    /// code is a POSTFIX, exactly ONCE, at the very END of every outbound subject
    /// (operator: "[ELDK27] must be a postfix, once, on every email"). It strips any
    /// existing "[<paramref name="eventCode"/>]" token wherever it appears (prefix or
    /// inline), collapses runs of whitespace, then appends a single " [code]" at the
    /// end. Idempotent: a subject already ending in " [code]" is returned unchanged and
    /// the tag is never doubled. Empty/blank <paramref name="eventCode"/> falls back to
    /// "ELDK27". (CEH does NOT add or touch a recipient-side "[EXT]" tag — that is added
    /// by the recipient's own mail system for external senders, outside our control.)
    /// </summary>
    public static string NormalizeSubject(string? subject, string? eventCode)
    {
        var code = string.IsNullOrWhiteSpace(eventCode) ? "ELDK27" : eventCode.Trim();
        var tag = $"[{code}]";
        var s = subject ?? string.Empty;

        // Remove every existing edition tag (anywhere), case-insensitive, then collapse.
        s = System.Text.RegularExpressions.Regex.Replace(
            s,
            System.Text.RegularExpressions.Regex.Escape(tag),
            " ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();

        return s.Length == 0 ? tag : $"{s} {tag}";
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
    /// (<paramref name="found"/> false) is NOT ring-gated by this PURE helper — the
    /// sender itself FAIL-CLOSES an unresolvable recipient (rings are the sole
    /// audience control; there is no allowlist).
    /// </summary>
    public static bool IsRingDropped(bool found, Ring effectiveRing, Ring emailReleaseRing) =>
        found && !Rings.IsActiveForRing(effectiveRing, emailReleaseRing);

    /// <summary>
    /// The ring gate — the SOLE audience control — for one recipient address.
    /// Resolves the address → participant in the active edition (or, when the
    /// address is unknown but the ambient <see cref="EmailContext.ParticipantId"/>
    /// identifies the person, by participant id — §234 Fix 3), computes its
    /// effective ring, reads the <c>outbound-email</c> feature's released ring from
    /// the gate service (NOT hardcoded), and returns true to DROP a known recipient
    /// that is out of the rollout.
    ///
    /// EDITION SCOPE: most call sites set an explicit <see cref="EmailContext.EventId"/>
    /// (reminders, templated participant mail, calendar invites, digests). The MANY
    /// that don't — Broadcast, PIN sign-in, invitations, sponsor/lead mail — must
    /// STILL be ring-gated, otherwise real people on those paths would be unprotected.
    /// So when no edition context is set we FALL BACK to the single
    /// active edition (<see cref="Domain.Event.IsActive"/>), the same "current event"
    /// the PIN login / role hub resolve. This makes the gate fire on EVERY send path.
    ///
    /// Returns false (do not ring-drop) only when:
    ///   • the recipient resolves (by address or participant id) to a ring INSIDE
    ///     the released ring, OR
    ///   • ring enforcement is not wired (no scope factory — legacy/test ctor), OR
    ///   • the send is <see cref="EmailContext.RingExempt"/> (sign-in/PIN).
    /// Everything else FAILS CLOSED (drops): an unknown recipient with no
    /// participant context, no resolvable active edition, or an error while
    /// resolving — FAIL-SOFT on errors but FAIL-CLOSED on outcome: a resolve error
    /// DROPS the send (returns true) rather than leaking mail, and never crashes
    /// the send loop. Logs every ring-drop for auditability.
    /// </summary>
    private async Task<bool> ShouldRingDropAsync(
        string originalTo, CancellationToken ct)
    {
        // Ring enforcement not wired (legacy/test ctor) ⇒ no ring gate; only the
        // kill switch + redirect apply (unchanged behaviour).
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
            // scope an email→participant lookup: FAIL-CLOSED (drop) — rings are
            // the sole audience control, so the ring layer is the only floor.
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

            // §326au — THE MASTER SWITCH, finally wired up. FeatureCatalog documents
            // outbound-email as "the global kill switch every send path honours", and
            // FeatureGateService.IsOutboundEmailEnabledAsync existed for it — but the
            // 2026-07-25 audit found it had ZERO production callers: the transport consulted
            // only the released RING, so flipping "Outbound email" OFF in /Organizer/Settings
            // did nothing whatsoever. The only stop that worked was the Email__KillSwitch app
            // setting, which needs a restart on three hosts. Defaults ON (FeatureCatalog), so
            // this can only ever stop mail an organizer has deliberately switched off.
            if (!await gate.IsFeatureEnabledAsync(FeatureCatalog.OutboundEmailKey, eventId, ct))
            {
                _log?.LogWarning(
                    "Email DROPPED — outbound e-mail is switched OFF for event {EventId}: {Addr}",
                    eventId, originalTo);
                return true;
            }

            // §326av — the per-hour ceiling (see EmailOptions.MaxSendsPerHour). Interactive
            // ring-exempt mail (PIN sign-in, engine alerts) is never capped.
            if (_options.MaxSendsPerHour > 0 && _emailContext?.Current?.RingExempt != true)
            {
                var db = sp.GetRequiredService<Data.CommunityHubDbContext>();
                var since = DateTimeOffset.UtcNow.AddHours(-1);
                var sentLastHour = await db.EmailLogs.CountAsync(
                    l => l.EventId == eventId && l.Success && l.SentAt >= since, ct);
                if (sentLastHour >= _options.MaxSendsPerHour)
                {
                    _log?.LogError(
                        "Email CEILING HIT — {Sent} sends in the last hour for event {EventId} "
                        + "reaches Email:MaxSendsPerHour ({Max}). Declining: {Addr}. Reminder sends "
                        + "retry on the next run; raise the ceiling only if this blast is intended.",
                        sentLastHour, eventId, _options.MaxSendsPerHour, originalTo);
                    return true;
                }
            }

            var (found, effectiveRing) =
                await rings.TryGetEffectiveRingByEmailAsync(eventId, originalTo, ct);

            // §234 (Fix 3): the ADDRESS alone may be unknown even though the send IS
            // for a known participant — a speaker's ContactEmailOverride, or a
            // participant's SecondaryEmail riding along as CC. When the ambient
            // EmailContext identifies the participant, gate on THAT PERSON's
            // effective ring instead of dropping the address as a stranger.
            if (!found && _emailContext?.Current?.ParticipantId is int participantId
                && participantId > 0)
            {
                (found, effectiveRing) = await rings.TryGetEffectiveRingByParticipantAsync(
                    eventId, participantId, ct);
            }

            // Unknown address AND no (or unresolvable) participant context ⇒
            // FAIL-CLOSED (rings are the only floor): a typo'd / external /
            // never-imported recipient is never mailed.
            if (!found)
            {
                _log?.LogInformation(
                    "Email RING-DROP (unknown recipient): {Addr}", originalTo);
                return true;
            }

            var emailReleaseRing = await gate.GetReleasedRingAsync(
                FeatureCatalog.OutboundEmailKey, eventId, ct);

            // §566 — THE FEATURE RING NO LONGER CLAMPS A SEND (design signed off 2026-07-28).
            //
            // A mail's audience is now its OWN ring, under the single outbound-email ceiling. The
            // feature keeps its on/off (which still stops whole jobs, e.g. WelcomeReconcileJob), but
            // its RING is no longer a third competing number on a page that could already display an
            // audience it would not honour.
            //
            // 🔒 §707.6 — THE 17 EMAIL FEATURE RINGS ARE GONE. The transport no longer asks a FEATURE
            // for a ring under any circumstance (operator 2026-07-29: *"i want INDIVIDUAL EMAIL RING
            // GATES - one for each ! and remove features gates relevant for emails"*).
            //
            // The audience is now exactly two layers and no more:
            //     audience = MIN(outbound-email ceiling, that mail's own (mail × role) ring)
            //
            // The feature keeps its ON/OFF — that still stops whole jobs (e.g. WelcomeReconcileJob) —
            // and `FeatureKey` is still carried on the context for grouping and for that on/off. It
            // simply no longer contributes a RING.
            //
            // 🔒 WHY THIS IS SAFE TO DO NOW, AND WAS NOT BEFORE: the branch removed here was reached
            // only by a send that declared a ring-scoped FeatureKey but NO registered TemplateName.
            // §707.1/§707.2a/§707.2b wired every live send site, so the only code path that still fits
            // that shape is the retired month-reminder (unscheduled since §244). Every other
            // identity-less send is RingExempt and returned long before this point.
            var templateKey = _emailContext?.Current?.TemplateName;
            if (!string.IsNullOrWhiteSpace(templateKey)
                && EmailTemplateCatalog.Map.ContainsKey(templateKey))
            {
                var templateRings = sp.GetService<EmailTemplateRingService>();
                if (templateRings is not null)
                {
                    // §705.3b — the ring is per (MAIL × ROLE), so the recipient's role decides
                    // which row applies. Operator: *"reminder to speaker is NOT the same as
                    // reminder to organizer or reminder to sponsor."*
                    //
                    // A null role (an attendee with no participant row, an ad-hoc address) is not an
                    // error: only the all-roles row can apply then, which is the right conservative
                    // answer for someone we cannot classify.
                    var recipientRole = await rings.TryGetRoleAsync(
                        eventId, originalTo, _emailContext?.Current?.ParticipantId, ct);

                    var mailRing = await templateRings.GetEffectiveRingAsync(
                        eventId, templateKey, ct, recipientRole);

                    if ((int)mailRing < (int)emailReleaseRing)
                    {
                        emailReleaseRing = mailRing;
                    }
                }
            }
            else if (_emailContext?.Current is not null
                     && !string.IsNullOrWhiteSpace(_emailContext.Current.FeatureKey))
            {
                // 🔒 FAIL CLOSED — a send that declares a FeatureKey but carries NO registered mail
                // identity used to inherit that feature's ring. With the feature ring gone there is
                // nothing legitimate left to inherit, and falling through to the outbound-email
                // ceiling would WIDEN it to everyone (§699.2 — the worst possible place for that).
                //
                // Today this catches only the retired month-reminder. Its real job is the NEXT mail
                // someone adds without registering it: it goes silent and audibly, instead of
                // silently reaching the whole event.
                _log?.LogWarning(
                    "Email RING-DROP (no registered mail identity; feature '{Feature}' no longer "
                    + "supplies a ring): {Addr}",
                    _emailContext.Current.FeatureKey, originalTo);
                return true;
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

            // §566 — BOTH HIDDEN CAPS REMOVED (design signed off 2026-07-28).
            //
            // The §217 attendee-welcome ceiling and the §516 welcome ceiling used to sit here and
            // narrow the audience AFTER everything the Settings page displayed. That is precisely
            // what destroyed confidence in that screen (§564): it could show "Ring 2" for a mail
            // that reached only Ring 1, and nothing on the page said why.
            //
            // The agreed model is TWO layers and no more:
            //     audience = MIN(outbound-email ceiling, that mail's own ring)
            // Both numbers are visible on the page, so the screen can state the OUTCOME rather than
            // a set of ingredients. Operator: "yes to ring 3", "drop the attendee cap".
            //
            // Per-mail control now covers EVERY template (§515), which is what he actually asked
            // for — not a welcome-only special case, which is where §516 went wrong.

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
    /// in which case the sender FAILS CLOSED (the recipient lookup cannot be scoped,
    /// so the send is dropped). Same "current event" query the PIN login uses.
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

    public Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        IReadOnlyCollection<string>? cc,
        CancellationToken cancellationToken = default)
        => SendInternalAsync(toEmail, subject, htmlBody, cc, replyTo: null, cancellationToken);

    public Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        EmailReplyTo? replyTo,
        CancellationToken cancellationToken = default)
        => SendInternalAsync(toEmail, subject, htmlBody, cc: null, replyTo, cancellationToken);

    /// <summary>
    /// §1121 — ONE mail, SEVERAL To: recipients (see <see cref="IEmailSender.SendToManyAsync"/>).
    /// </summary>
    /// <remarks>
    /// 🔒 Delegates to <see cref="SendInternalAsync"/> with the FIRST address as the primary and the
    /// rest as <c>additionalTo</c>, so the ring gate, the kill switch, the DEV redirect, the outcome
    /// seam and the operator BCC are the SAME code the single-recipient path runs. A parallel send
    /// routine for "the multi-recipient case" is how one of those silently stops applying to half
    /// the mail.
    /// </remarks>
    public Task SendToManyAsync(
        IReadOnlyCollection<string> toEmails,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default)
    {
        var addresses = (toEmails ?? Array.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (addresses.Count == 0)
        {
            throw new ArgumentException("At least one recipient address is required.", nameof(toEmails));
        }

        return SendInternalAsync(
            addresses[0], subject, htmlBody, cc: null, replyTo: null, cancellationToken,
            additionalTo: addresses.Skip(1).ToList());
    }

    private async Task SendInternalAsync(
        string toEmail,
        string subject,
        string htmlBody,
        IReadOnlyCollection<string>? cc,
        EmailReplyTo? replyTo,
        CancellationToken cancellationToken,
        // §1121 — extra PRIMARY (To:) recipients. Null/empty ⇒ the original single-recipient
        // behaviour, unchanged for every existing caller.
        IReadOnlyCollection<string>? additionalTo = null)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            throw new ArgumentException("Recipient address is required.", nameof(toEmail));
        }

        // §234: reset the per-flow outcome so a stale "delivered" from an earlier
        // send can never be read as this send's result.
        _outcome?.Record(delivered: false, reason: "not-sent");

        // RING GATE (REQUIREMENTS §23) — rings are the sole audience control. Gate
        // the INTENDED recipient (pre-redirect), so a ring-2/3 participant is dropped
        // even when a dev RedirectAllTo would have funnelled it to a test inbox.
        if (await ShouldRingDropAsync(toEmail, cancellationToken))
        {
            _outcome?.Record(delivered: false, reason: "ring-drop");
            return;
        }

        var (actualTo, finalSubject) = ApplyRedirect(toEmail, subject);

        if (_options.KillSwitch)
        {
            _outcome?.Record(delivered: false, reason: "kill-switch");
            _log?.LogInformation(
                "Email DROP (KILL SWITCH): original={Original} actual={Actual} subject='{Subject}'",
                toEmail, actualTo, subject);
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromDisplayName),
            Subject = finalSubject,
            Body = ApplyDefaultFont(htmlBody),
            IsBodyHtml = true,
        };
        message.To.Add(actualTo);
        // §1121 — extra PRIMARY recipients. Each is its own recipient, so it gets the SAME ring gate
        // and the SAME redirect as the primary; de-duplicated against what is already on the message
        // because a DEV RedirectAllTo collapses every address onto one inbox.
        AddAdditionalTo(message, await RingFilterAddressesAsync(additionalTo, cancellationToken));
        // Optional Reply-To (e.g. the AiHelper asker) so a "Reply" reaches the person,
        // not the From mailbox. Never ring-gated — it is a header, not a recipient.
        AddReplyTo(message, replyTo);
        // Each CC is its own recipient ⇒ ring-gate it too (an out-of-ring or
        // unresolvable CC is dropped; the primary send is unaffected).
        AddCc(message, await RingFilterAddressesAsync(cc, cancellationToken));
        // The mail is ACTUALLY sending now (it passed the ring gate + kill switch):
        // add the operator BCC so it mirrors only what really goes out.
        AddOperatorBcc(message);

        await DispatchAsync(message, cancellationToken);
        // §234: the mail actually left (a DEV RedirectAllTo redirect counts as
        // delivered — it was dispatched, just to the redirect inbox).
        _outcome?.Record(delivered: true);
    }

    // Add an optional Reply-To header (name optional). A bad address is skipped
    // (logged) rather than failing the send. This is a header, not a recipient,
    // so it is intentionally not ring-filtered.
    private void AddReplyTo(MailMessage message, EmailReplyTo? replyTo)
    {
        if (replyTo is null || string.IsNullOrWhiteSpace(replyTo.Email)) return;
        try
        {
            message.ReplyToList.Add(string.IsNullOrWhiteSpace(replyTo.Name)
                ? new MailAddress(replyTo.Email.Trim())
                : new MailAddress(replyTo.Email.Trim(), replyTo.Name.Trim()));
        }
        catch (FormatException)
        {
            _log?.LogInformation("Email Reply-To skipped (bad format): {ReplyTo}", replyTo.Email);
        }
    }

    /// <summary>
    /// Hand the fully-built, already-gated <paramref name="message"/> to Brevo SMTP.
    /// This is the single dispatch tail every send path funnels through AFTER the
    /// ring gate, redirect/kill-switch, CC filtering and operator-BCC have
    /// been applied — so a test can override it to capture the exact message that
    /// would go on the wire without touching the network. Virtual + protected for
    /// exactly that (tests subclass and short-circuit); production always uses this.
    /// </summary>
    protected virtual async Task DispatchAsync(
        MailMessage message, CancellationToken cancellationToken)
    {
        // §219 (Risk-4): retry transient SMTP failures (connection blips, Brevo
        // throttling / rate-limit 4xx, 5xx, socket/IO errors) with a backoff, on a
        // FRESH connection each attempt. Permanent failures (bad address 5xx) are NOT
        // retried — they throw on the first try so the caller logs the real error.
        // Attempts + backoff are CONFIG-DRIVEN (Email:SendMaxAttempts /
        // Email:SendRetryBaseDelayMs) so Brevo limits can be tuned without a code change.
        var maxAttempts = Math.Max(1, _options.SendMaxAttempts);
        var baseDelayMs = Math.Max(0, _options.SendRetryBaseDelayMs);

        await SendWithRetryAsync(
            sendAttempt: async (_, ct) =>
            {
                using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
                {
                    EnableSsl = true, // STARTTLS on 587
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    Credentials = new NetworkCredential(
                        _options.SmtpUsername, _options.SmtpKey),
                };

                await client.SendMailAsync(message, ct);
            },
            isTransient: IsTransientSmtpError,
            // Linear-growing backoff: attempt 1 → 1×base, attempt 2 → 2×base, …
            // (default 1s, 2s — matching the prior hard-coded behaviour).
            backoff: (attempt, ct) => baseDelayMs <= 0
                ? Task.CompletedTask
                : Task.Delay(TimeSpan.FromMilliseconds((double)baseDelayMs * attempt), ct),
            maxAttempts: maxAttempts,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// §219 (Risk-4) — the reusable, transport-agnostic retry-with-backoff loop. Runs
    /// <paramref name="sendAttempt"/> (1-based attempt number); on a TRANSIENT failure
    /// (<paramref name="isTransient"/> true) it awaits <paramref name="backoff"/> and
    /// retries, up to <paramref name="maxAttempts"/> total tries; a non-transient
    /// failure (or the last attempt) throws so the caller sees the real error. A
    /// cancellation is never retried. Extracted as a <c>public static</c> seam so a unit
    /// test can drive the retry/backoff deterministically (an injected no-op backoff,
    /// no real SMTP, no real sleep). Production wires the SMTP send + a Task.Delay.
    /// </summary>
    public static async Task SendWithRetryAsync(
        Func<int, CancellationToken, Task> sendAttempt,
        Func<Exception, bool> isTransient,
        Func<int, CancellationToken, Task> backoff,
        int maxAttempts,
        CancellationToken cancellationToken = default)
    {
        var attempts = Math.Max(1, maxAttempts);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await sendAttempt(attempt, cancellationToken);
                return;
            }
            catch (Exception ex) when (
                attempt < attempts
                && !cancellationToken.IsCancellationRequested
                && isTransient(ex))
            {
                await backoff(attempt, cancellationToken);
            }
        }
    }

    /// <summary>
    /// True for SMTP errors worth retrying on a fresh connection: service
    /// unavailable / mailbox busy / transaction failed / general failure (Brevo
    /// throttling + transient relay errors) and connection-level socket/IO/timeout
    /// faults. A permanent rejection (e.g. 550 MailboxUnavailable — no such mailbox
    /// or policy reject) is NOT transient and throws on the first attempt.
    ///
    /// §219: Brevo enforces its per-second/hour transactional caps over SMTP by
    /// returning a transient 4xx (421 service-not-available / 450 mailbox-busy /
    /// 451 transaction-failed) — the HTTP analogue of a 429 — so those codes ARE
    /// treated as transient and re-attempted with backoff (every recipient is
    /// ultimately retried rather than dropped on a throttle). <c>public</c> so the
    /// retry seam + tests can share the same classification.
    /// </summary>
    public static bool IsTransientSmtpError(Exception ex)
    {
        if (ex is SmtpException smtp)
        {
            switch (smtp.StatusCode)
            {
                case SmtpStatusCode.ServiceNotAvailable:   // 421 (Brevo throttle / rate-limit)
                case SmtpStatusCode.MailboxBusy:           // 450 (Brevo throttle / rate-limit)
                // NOT MailboxUnavailable: 550 is a PERMANENT rejection (no such
                // mailbox / policy reject) — retrying it hammers the relay and can
                // hurt sender reputation (§234). It throws on the first attempt.
                case SmtpStatusCode.TransactionFailed:     // 451/554 (Brevo throttle / rate-limit)
                case SmtpStatusCode.GeneralFailure:
                case SmtpStatusCode.ClientNotPermitted:
                case SmtpStatusCode.InsufficientStorage:   // 452
                    return true;
            }
            return smtp.InnerException is IOException or System.Net.Sockets.SocketException;
        }
        return ex is IOException or System.Net.Sockets.SocketException or TimeoutException;
    }

    // Ring-gate each SECONDARY address (a CC, or a §1121 extra To:) the same way as the primary
    // recipient (REQUIREMENTS §23): an out-of-ring or unresolvable address is dropped (and logged)
    // before the message is built (§234: an address unknown BY ADDRESS still resolves via the
    // ambient EmailContext.ParticipantId — e.g. a participant's SecondaryEmail — so it is gated by
    // the PERSON's ring). Returns the kept subset (null in ⇒ null out).
    //
    // A dropped secondary never fails the primary send and is not recorded as a drop on the
    // IEmailDeliveryOutcome seam (that seam reflects the primary recipient).
    private async Task<IReadOnlyCollection<string>?> RingFilterAddressesAsync(
        IReadOnlyCollection<string>? addresses, CancellationToken ct)
    {
        if (addresses is null || addresses.Count == 0) return addresses;
        var kept = new List<string>(addresses.Count);
        foreach (var raw in addresses)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (await ShouldRingDropAsync(raw.Trim(), ct)) continue; // logged inside
            kept.Add(raw);
        }
        return kept;
    }

    // §1121 — add each EXTRA PRIMARY recipient to the To: line. Same shape as AddCc, and
    // deliberately so: ring-filtering happened upstream (RingFilterAddressesAsync), the kill switch
    // dropped the whole send before we got here, and each address takes the SAME redirect as the
    // primary. De-dup against what is already on the message, because a DEV RedirectAllTo collapses
    // every address onto one inbox and would otherwise put it on the To: line three times.
    private void AddAdditionalTo(MailMessage message, IReadOnlyCollection<string>? extra)
    {
        if (extra is null) return;
        foreach (var raw in extra)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var (actualTo, _) = ApplyRedirect(raw.Trim(), string.Empty);
            var already = message.To.Concat(message.CC)
                .Any(a => string.Equals(a.Address, actualTo,
                    StringComparison.OrdinalIgnoreCase));
            if (already) continue;
            try { message.To.Add(actualTo); }
            catch (FormatException)
            {
                _log?.LogInformation("Email extra recipient skipped (bad format): {To}", raw);
            }
        }
    }

    // Add each CC, each independently subject to the same redirect as the primary
    // recipient (ring-gating already happened in RingFilterAddressesAsync). A CC redirected
    // to the same dev inbox as the To would duplicate, so de-dup against addresses
    // already on the message.
    private void AddCc(MailMessage message, IReadOnlyCollection<string>? cc)
    {
        if (cc is null) return;
        foreach (var raw in cc)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var (actualCc, _) = ApplyRedirect(raw.Trim(), string.Empty);
            // CCs are already ring-filtered upstream (RingFilterAddressesAsync) and the kill
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
    // path — after the ring gate AND the redirect/kill-switch decision have already
    // let the mail through — so the bcc never lands on a dropped, redirected-away,
    // or kill-switched mail (it reflects exactly what went out). The bcc recipient
    // is the operator (organizer, ring0), so it is intentionally NOT ring-filtered;
    // it is simply added to the message. De-dup against addresses already on the
    // message so it is not doubled when the operator is also the To/CC (e.g. under
    // a dev RedirectAllTo to the same inbox).
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

        // §234: reset the per-flow outcome for this send.
        _outcome?.Record(delivered: false, reason: "not-sent");

        // RING GATE (REQUIREMENTS §23) — rings-only audience control (multipart path).
        if (await ShouldRingDropAsync(toEmail, cancellationToken))
        {
            _outcome?.Record(delivered: false, reason: "ring-drop");
            return;
        }

        var (actualTo, finalSubject) = ApplyRedirect(toEmail, subject);

        if (_options.KillSwitch)
        {
            _outcome?.Record(delivered: false, reason: "kill-switch");
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
            ApplyDefaultFont(htmlBody), System.Text.Encoding.UTF8, "text/html");
        // Order matters: least-preferred first, most-preferred last.
        message.AlternateViews.Add(plainView);
        message.AlternateViews.Add(htmlView);
        // Actually sending (passed ring gate + kill switch) ⇒ add the operator BCC.
        AddOperatorBcc(message);

        await DispatchAsync(message, cancellationToken);
        _outcome?.Record(delivered: true); // §234: dispatched (redirects count)
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

        // §234: reset the per-flow outcome for this send.
        _outcome?.Record(delivered: false, reason: "not-sent");

        // RING GATE (REQUIREMENTS §23) — rings-only audience control (ics path).
        if (await ShouldRingDropAsync(toEmail, cancellationToken))
        {
            _outcome?.Record(delivered: false, reason: "ring-drop");
            return;
        }

        var (actualTo, finalSubject) = ApplyRedirect(toEmail, subject);

        if (_options.KillSwitch)
        {
            _outcome?.Record(delivered: false, reason: "kill-switch");
            _log?.LogInformation(
                "Email DROP (KILL SWITCH, ics): original={Original} actual={Actual} subject='{Subject}'",
                toEmail, actualTo, subject);
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromDisplayName),
            Subject = finalSubject,
            Body = ApplyDefaultFont(htmlBody),
            IsBodyHtml = true,
        };
        message.To.Add(actualTo);

        var icsBytes = System.Text.Encoding.UTF8.GetBytes(icsContent);
        var icsStream = new MemoryStream(icsBytes);
        var attachment = new Attachment(icsStream, icsFileName, "text/calendar; method=REQUEST");
        message.Attachments.Add(attachment);
        // Actually sending (passed ring gate + kill switch) ⇒ add the operator BCC.
        AddOperatorBcc(message);

        try
        {
            await DispatchAsync(message, cancellationToken);
            _outcome?.Record(delivered: true); // §234: dispatched (redirects count)
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

        // §234: reset the per-flow outcome for this send.
        _outcome?.Record(delivered: false, reason: "not-sent");

        // RING GATE (REQUIREMENTS §23) — rings-only audience control (attachments
        // path). A RingExempt EmailContext (e.g. the ERP-inbox send) bypasses this,
        // exactly as it does for every other overload.
        if (await ShouldRingDropAsync(toEmail, cancellationToken))
        {
            _outcome?.Record(delivered: false, reason: "ring-drop");
            return;
        }

        var (actualTo, finalSubject) = ApplyRedirect(toEmail, subject);

        if (_options.KillSwitch)
        {
            _outcome?.Record(delivered: false, reason: "kill-switch");
            _log?.LogInformation(
                "Email DROP (KILL SWITCH, attachments): original={Original} actual={Actual} subject='{Subject}'",
                toEmail, actualTo, subject);
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromDisplayName),
            Subject = finalSubject,
            Body = ApplyDefaultFont(htmlBody),
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

            // Actually sending (passed ring gate + kill switch) ⇒ add the operator BCC.
            AddOperatorBcc(message);

            await DispatchAsync(message, cancellationToken);
            _outcome?.Record(delivered: true); // §234: dispatched (redirects count)
        }
        finally
        {
            foreach (var a in added) a.Dispose();
            foreach (var s in streams) s.Dispose();
        }
    }
}
