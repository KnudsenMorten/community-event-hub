namespace CommunityHub.Core.Email;

/// <summary>
/// Sends transactional email. One seam used by both the PIN login (Stage 3)
/// and the reminder jobs (Stage 6+). The implementation is Brevo SMTP
/// (CONTEXT.md section 11) - host smtp-relay.brevo.com:587, STARTTLS,
/// credentials from Key Vault.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Send a single HTML email. Throws on hard failure so the caller (e.g.
    /// the PIN flow) can surface a retry; the reminder job catches and logs.
    /// </summary>
    Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a single HTML email with optional CC recipients (e.g. a participant's
    /// secondary email). Each CC is subject to the same redirect/allowlist gating
    /// as the primary recipient. The 4-arg <see cref="SendAsync(string,string,string,CancellationToken)"/>
    /// overload is just this with no CC.
    /// </summary>
    Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        IReadOnlyCollection<string>? cc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Send an email carrying BOTH an HTML body and a plain-text alternative
    /// (a <c>multipart/alternative</c>): clients that prefer plain text (or
    /// strip HTML) render <paramref name="textBody"/>, the rest render
    /// <paramref name="htmlBody"/>. Used by the welcome-with-login email, whose
    /// requirement is HTML + plain-text. The same redirect/allowlist gating as
    /// <see cref="SendAsync(string,string,string,CancellationToken)"/> applies.
    /// </summary>
    Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string textBody,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Send an HTML email with one inline iCalendar (.ics) attachment.
    /// Used for RSVP confirmations that add the event to the participant's calendar.
    /// </summary>
    Task SendWithIcsAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string icsContent,
        string icsFileName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Send an HTML email with one or more arbitrary file attachments
    /// (REQUIREMENTS §48 — travel-reimbursement receipts/invoice to the ERP inbox).
    /// Each attachment is added with its own content type; the same
    /// redirect/allowlist/kill-switch + ring gating as the other overloads applies,
    /// so attaching files never bypasses gating — set an <c>EmailContext</c> with
    /// <c>RingExempt = true</c> when the recipient is a non-participant (e.g. the ERP
    /// mailbox) that must not be ring-dropped.
    /// </summary>
    Task SendWithAttachmentsAsync(
        string toEmail,
        string subject,
        string htmlBody,
        IReadOnlyCollection<EmailAttachment> attachments,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One in-memory file attachment for <see cref="IEmailSender.SendWithAttachmentsAsync"/>.
/// </summary>
public sealed record EmailAttachment(
    string FileName,
    byte[] Content,
    string ContentType);

/// <summary>
/// Brevo SMTP settings, bound from configuration. The username and key come
/// from Key Vault (secret names brevo-smtp-username / brevo-smtp-key); the
/// rest are non-secret and may sit in appsettings.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>
    /// GLOBAL OUTBOUND-EMAIL KILL SWITCH (REQUIREMENTS §23). When true, NO mail
    /// leaves the hub by ANY path — web, jobs, digests — regardless of the
    /// allowlist or redirect. The send is short-circuited before SMTP and logged
    /// as a kill-switch drop. This is the process-wide hard stop set in config
    /// (app setting <c>Email__KillSwitch</c>); the per-edition organizer switch
    /// (the <c>outbound-email</c> feature gate) sits on top for edition-scoped
    /// control. Default false (mail flows, subject to the allowlist).
    /// </summary>
    public bool KillSwitch { get; set; }

    public string SmtpHost { get; set; } = "smtp-relay.brevo.com";
    public int SmtpPort { get; set; } = 587;

    /// <summary>Brevo-issued SMTP username (NOT the account login email).</summary>
    public string SmtpUsername { get; set; } = string.Empty;

    /// <summary>Brevo SMTP key (the SMTP password).</summary>
    public string SmtpKey { get; set; } = string.Empty;

    public string FromAddress { get; set; } = "info@expertslive.dk";
    public string FromDisplayName { get; set; } = "Experts Live Denmark";

    /// <summary>
    /// TEST MODE redirect. When non-empty, every outbound mail's To: is replaced
    /// with this address regardless of the original recipient; the original is
    /// preserved in the subject as "[TEST -> original@example.com]". Set ONLY
    /// in dev (via app setting Email__RedirectAllTo). Leave empty in prod.
    /// </summary>
    public string RedirectAllTo { get; set; } = string.Empty;

    /// <summary>
    /// RING CEILING (REQUIREMENTS §23) — caps the outbound-email audience at a ring,
    /// regardless of a feature's released ring. Primary use is the DEV env: set
    /// <c>Email__MaxReleaseRing=Ring1</c> so DEV onboarding / reminders / schedules
    /// reach only ring0/ring1 participants and NEVER ring2+ (real sponsors, speakers,
    /// volunteers) even if a feature is released to ring2/ring3. The effective email
    /// release ring becomes <c>min(featureReleasedRing, MaxReleaseRing)</c> (lower =
    /// more restrictive). Leave EMPTY in PROD so the feature's released ring rules.
    /// Accepts a <see cref="Settings.Ring"/> name ("Ring0".."Ring3"/"Broad") or its
    /// number ("0".."3"); unparseable / empty = no ceiling.
    /// </summary>
    public string MaxReleaseRing { get; set; } = string.Empty;

    /// <summary>
    /// PROD OPERATOR BCC. When non-empty, this address is added as a BCC on every
    /// mail that ACTUALLY SENDS — i.e. after the ring gate AND the
    /// allowlist/redirect/kill-switch decision have all passed for the primary
    /// recipient. It is NOT added to mail that is ring-dropped, allowlist-dropped,
    /// redirected away, or kill-switched, so the bcc faithfully reflects what truly
    /// went out (a silent archive of real outbound). The bcc itself is not subject
    /// to ring/allowlist filtering — it is the operator (organizer, ring0,
    /// allowlisted) and must always receive a copy of any sent mail — but the rules
    /// for the PRIMARY recipient are never bypassed. Set in PROD via app setting
    /// <c>Email__BccAllTo</c> (e.g. the operator's mailbox); leave empty in DEV /
    /// tests (default) so behaviour is unchanged there.
    /// </summary>
    public string BccAllTo { get; set; } = string.Empty;
}
