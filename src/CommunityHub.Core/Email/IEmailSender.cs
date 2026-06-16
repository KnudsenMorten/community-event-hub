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
}

/// <summary>
/// Brevo SMTP settings, bound from configuration. The username and key come
/// from Key Vault (secret names brevo-smtp-username / brevo-smtp-key); the
/// rest are non-secret and may sit in appsettings.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

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
    /// PRODUCTION-SAFE ALLOWLIST. When non-empty, outbound mail is sent only if
    /// the (post-redirect) recipient matches an entry; non-matching recipients
    /// are silently DROPPED (logged at Information). Empty = no allowlist =
    /// every recipient gets mail (normal PROD behaviour).
    ///
    /// Format: comma- or semicolon-separated list of either:
    ///   - exact addresses     "mok@expertslive.dk"
    ///   - domain wildcards    "@2linkit.net"           (any address at this domain)
    ///
    /// Use case: deploy new functionality to PROD with allowlist set to
    /// internal staff only, smoke-test against real PROD data without spamming
    /// external recipients, then clear the setting once happy. ALWAYS remove
    /// (or leave empty) for normal operation -- otherwise the hub silently
    /// drops mail to sponsors / volunteers / speakers.
    /// </summary>
    public string OnlySendTo { get; set; } = string.Empty;
}
