namespace CommunityHub.Core.Email;

/// <summary>
/// §234 — the DELIVERED-vs-DROPPED seam between the email transport and the
/// idempotency ledgers. <see cref="BrevoEmailSender"/> records the REAL outcome of
/// the last send on the current logical flow: delivered (dispatched to SMTP — a DEV
/// <c>Email:RedirectAllTo</c> redirect counts as delivered), ring-dropped, or
/// kill-switched. Ledger callers (the welcome <c>WelcomeWithLoginSentAt</c> stamp,
/// the <c>SentReminder</c> rows) consult it AFTER a send so a DROPPED send is never
/// recorded as sent — the recipient is retried automatically once rings widen.
/// The audit decorator (<see cref="LoggingEmailSender"/>) consults it too, so a
/// ring-dropped send is logged as a DROP, never as a successful send.
/// </summary>
public interface IEmailDeliveryOutcome
{
    /// <summary>True when the LAST send on this logical flow was actually dispatched.</summary>
    bool LastSendDelivered { get; }

    /// <summary>Why the last send was NOT delivered (e.g. <c>ring-drop</c>,
    /// <c>kill-switch</c>); null when it was delivered.</summary>
    string? LastDropReason { get; }

    /// <summary>Record the outcome of a send. Called only by the transport.</summary>
    void Record(bool delivered, string? reason = null);
}

/// <summary>
/// <see cref="IEmailDeliveryOutcome"/> backed by a per-logical-flow (AsyncLocal)
/// holder, mirroring how <see cref="EmailContextAccessor"/> flows context INTO the
/// singleton sender — this flows the outcome back OUT.
///
/// <para>WHY AsyncLocal: the transport (<see cref="BrevoEmailSender"/> /
/// <see cref="LoggingEmailSender"/>) is a SINGLETON while the ledger callers are
/// SCOPED, so a plain scoped instance could never be the same object on both sides.
/// Instead, the SCOPED instance (default ctor — resolved in the request/job flow,
/// BEFORE any send) installs a mutable holder in the current async context; the
/// transport's <see cref="Record"/> deeper in the SAME flow mutates that holder, and
/// the caller reads the result through it. Writes are therefore per-flow: two
/// concurrent requests never see each other's outcome.</para>
///
/// <para>The singleton senders hold a <see cref="Detached"/> instance: it installs
/// nothing (a construction-time install at app startup could leak ONE shared holder
/// into every request), only writes into the ambient holder when one exists, and
/// no-ops otherwise. <see cref="LoggingEmailSender"/> calls
/// <see cref="EnsureAmbient"/> per send so a holder always exists for the duration
/// of a send even when no caller injected the scoped service.</para>
/// </summary>
public sealed class EmailDeliveryOutcome : IEmailDeliveryOutcome
{
    private sealed class State
    {
        public bool Delivered;
        public string? Reason;
    }

    private static readonly AsyncLocal<State?> _ambient = new();

    // The holder this instance installed (default ctor) — null for Detached.
    private readonly State? _own;

    /// <summary>
    /// Scoped/DI ctor: install a per-flow holder in the CONSTRUCTING async context
    /// (scope resolution happens in the request/job flow, before the send) so a
    /// send deeper in the same flow records into it and this instance sees the
    /// result. Reuses an already-installed ambient holder (outer scope) if present.
    /// </summary>
    public EmailDeliveryOutcome()
    {
        _own = _ambient.Value;
        if (_own is null)
        {
            _own = new State();
            _ambient.Value = _own;
        }
    }

    private EmailDeliveryOutcome(bool detached) => _own = null;

    /// <summary>
    /// An instance for SINGLETON holders (the senders): never installs an ambient
    /// holder (so app-startup construction cannot leak one shared holder into every
    /// request); reads/writes only the ambient holder of the current flow.
    /// </summary>
    public static EmailDeliveryOutcome Detached() => new(detached: true);

    /// <summary>
    /// Make sure the CURRENT async context has an outcome holder — called by
    /// <see cref="LoggingEmailSender"/> at the start of each send so the transport
    /// always has somewhere to record, even when no caller injected the scoped
    /// service. Installed inside the decorator's send frame, it flows down into the
    /// inner sender and is discarded when the send completes (never leaks upward).
    /// </summary>
    public static void EnsureAmbient() => _ambient.Value ??= new State();

    private State? Current => _ambient.Value ?? _own;

    public bool LastSendDelivered => Current?.Delivered ?? false;

    public string? LastDropReason => Current?.Reason;

    public void Record(bool delivered, string? reason = null)
    {
        var s = Current;
        if (s is null) return; // detached with no ambient flow: nothing to record into
        s.Delivered = delivered;
        s.Reason = delivered ? null : reason;
    }
}
