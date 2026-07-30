namespace CommunityHub.Core.Email;

/// <summary>
/// Ambient metadata about the email currently being sent, set by a caller just
/// before it calls <see cref="IEmailSender"/> so the logging decorator
/// (<c>LoggingEmailSender</c>) can record a rich <c>EmailLog</c> row WITHOUT
/// every call site having to pass extra arguments. Set it via
/// <see cref="IEmailContextAccessor.Set"/> (ideally in a <c>using</c> scope);
/// when nothing is set the decorator falls back to category "other".
/// </summary>
/// <param name="TemplateName">
/// The branded template this send rendered, when the caller sent via a template
/// (e.g. the per-participant <c>ParticipantEmailService</c> path). Recorded on
/// the <c>EmailLog</c> row so an organizer can <b>re-send the exact same email</b>
/// after a failure without re-typing it. Null for raw/ad-hoc sends (broadcast,
/// PIN) that did not come from a named template.
/// </param>
/// <param name="FeatureKey">
/// The <see cref="Settings.FeatureCatalog"/> key of the FEATURE that triggered this
/// send (e.g. <c>reminder-jobs</c>, <c>welcome-email</c>, <c>broadcast-email</c>).
/// When set AND that feature is ring-scoped, <c>BrevoEmailSender</c> tightens the
/// recipient ring gate to the MORE RESTRICTIVE of the feature's released ring and the
/// <c>outbound-email</c> transport ring — so a feature still in testing (Ring 1) never
/// mails ring-2+ recipients even when the transport is Broad. Null ⇒ the transport
/// ring (outbound-email) alone applies, as before. Never LOOSENS the transport gate.
/// </param>
/// <param name="RingExempt">
/// When true this send BYPASSES ring gating entirely — it always sends regardless of
/// the recipient's ring (the global kill switch + allowlist/redirect still apply). This
/// is ONLY for the on-demand SIGN-IN email (the PIN code a user requests to log in):
/// it has direct, user-initiated impact and must reach a user at any ring. Every other
/// email is ring-governed by its feature (operator 2026-06-22). Do NOT set this on
/// broadcast/welcome/invitation blasts — those are ring-scoped.
/// </param>
/// <param name="AttendeeWelcome">
/// §217 (F3) — marks this send as an ATTENDEE WELCOME email (the 1-day
/// <c>AttendeeOneDayWelcomeEmailService</c> + the 2-day attendee provisioning welcome).
/// When true, <c>BrevoEmailSender</c> applies the dedicated
/// <c>Email:AttendeeWelcomeMaxReleaseRing</c> ceiling (default Ring1) ON TOP of the
/// normal email/feature gate, so the ~1500-recipient attendee welcome is released to
/// the broad attendee audience only when the operator deliberately raises that setting.
/// Only ever TIGHTENS the gate; set ONLY by the attendee-welcome services. Default
/// false (no extra cap) for every other send.
/// </param>
/// <param name="Welcome">
/// §516 — marks this send as a WELCOME email for ANY persona (speaker, sponsor, volunteer,
/// media, event partner). When true, <c>BrevoEmailSender</c> applies the dedicated
/// <c>Email:WelcomeMaxReleaseRing</c> ceiling — <b>default Ring1 in CODE</b> — on top of the
/// normal email/feature gate.
///
/// <para><b>Why a second cap when the welcome-email feature already has a ring.</b> That ring is
/// an ordinary picker on the Settings page: one mis-click widens every persona's welcome with
/// nothing beneath it. §217 gave ATTENDEE welcomes exactly this protection; the operator asked
/// for the same before going live with SPEAKERS. The default lives in code, not in an app
/// setting, because app settings swap with the deployment slot (§462) — a cap that can vanish on
/// a swap is not a cap.</para>
///
/// <para>Only ever TIGHTENS. Set ONLY by the welcome services. <see cref="AttendeeWelcome"/>
/// stacks its own (usually equal or tighter) ceiling on top for the attendee blast.</para>
/// </param>
public sealed record EmailContext(
    string Category,
    int EventId = 0,
    int? ParticipantId = null,
    string? RecipientName = null,
    string? TemplateName = null,
    string? FeatureKey = null,
    bool RingExempt = false,
    bool AttendeeWelcome = false,
    bool Welcome = false);

/// <summary>
/// Holds the current <see cref="EmailContext"/> for the logical async flow.
/// Scoped per request/job; backed by <c>AsyncLocal</c> so a background thread
/// inside the same flow sees it too.
/// </summary>
public interface IEmailContextAccessor
{
    EmailContext? Current { get; }

    /// <summary>
    /// Set the ambient context and return an <see cref="IDisposable"/> that
    /// restores the previous value when disposed — use in a <c>using</c> so the
    /// context never leaks past the send it describes.
    /// </summary>
    IDisposable Set(EmailContext context);
}

/// <inheritdoc />
public sealed class EmailContextAccessor : IEmailContextAccessor
{
    private static readonly AsyncLocal<EmailContext?> _current = new();

    public EmailContext? Current => _current.Value;

    public IDisposable Set(EmailContext context)
    {
        var previous = _current.Value;
        _current.Value = context;
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly EmailContext? _previous;
        private bool _done;
        public Restore(EmailContext? previous) => _previous = previous;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _current.Value = _previous;
        }
    }
}
