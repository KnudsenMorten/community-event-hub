using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Email;

/// <summary>
/// <see cref="IEmailSender"/> over Brevo SMTP. STARTTLS on port 587.
/// Credentials are injected via <see cref="EmailOptions"/>, which the host
/// populates from Key Vault - no credential is ever in code or config files.
/// </summary>
public sealed class BrevoEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<BrevoEmailSender>? _log;
    private readonly string[] _allowlist;

    public BrevoEmailSender(IOptions<EmailOptions> options, ILogger<BrevoEmailSender>? log = null)
    {
        _options = options.Value;
        _log = log;
        _allowlist = ParseAllowlist(_options.OnlySendTo);
    }

    /// <summary>
    /// Resolve, for one recipient, the address it would ACTUALLY be delivered to
    /// after the DEV redirect, and whether the PROD allowlist would let it
    /// through. Shared with <c>LoggingEmailSender</c> so the audit log records the
    /// real outcome using the exact same gating logic as the send (no drift).
    /// </summary>
    public static (string actualTo, bool allowed) ResolveDelivery(
        EmailOptions options, string toEmail)
    {
        var actualTo = string.IsNullOrWhiteSpace(options.RedirectAllTo)
            ? toEmail
            : options.RedirectAllTo;
        var allowed = IsAllowed(ParseAllowlist(options.OnlySendTo), actualTo);
        return (actualTo, allowed);
    }

    private static bool IsAllowed(string[] allowlist, string actualTo)
    {
        // FAIL-CLOSED (operator directive 2026-06-16): an empty/unconfigured
        // allowlist means send to NOBODY, never "send to everyone". This is the
        // structural guard so a wiped/missing Email__OnlySendTo can never leak
        // mail to real speakers / sponsors / volunteers. Mail flows ONLY to an
        // explicitly-configured allowlist (set in dev AND prod app settings).
        if (allowlist.Length == 0) return false;
        var addr = (actualTo ?? string.Empty).Trim().ToLowerInvariant();
        if (addr.Length == 0) return false;
        foreach (var entry in allowlist)
        {
            if (entry.StartsWith("@", StringComparison.Ordinal))
            {
                if (addr.EndsWith(entry, StringComparison.Ordinal)) return true;
            }
            else if (string.Equals(addr, entry, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static string[] ParseAllowlist(string raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(new[] { ',', ';' },
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(e => e.ToLowerInvariant())
                 .ToArray();

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

    // Production-safe allowlist. Empty list = no gating (normal PROD behaviour).
    // Each entry is either an exact address ("mok@expertslive.dk") or a domain
    // wildcard starting with @ ("@2linkit.net"). Matches are case-insensitive.
    // Returns false -> the caller drops the send silently.
    private bool IsAllowedByAllowlist(string actualTo) =>
        IsAllowed(_allowlist, actualTo);

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

        var (actualTo, finalSubject) = ApplyRedirect(toEmail, subject);

        if (!IsAllowedByAllowlist(actualTo))
        {
            _log?.LogInformation(
                "Email allowlist DROP: original={Original} actual={Actual} subject='{Subject}' (OnlySendTo='{List}')",
                toEmail, actualTo, subject, _options.OnlySendTo);
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
        AddCc(message, cc);

        using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
        {
            EnableSsl = true, // STARTTLS on 587
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = new NetworkCredential(
                _options.SmtpUsername, _options.SmtpKey),
        };

        await client.SendMailAsync(message, cancellationToken);
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
            if (!IsAllowedByAllowlist(actualCc))
            {
                _log?.LogInformation(
                    "Email CC allowlist DROP: original={Original} actual={Actual}",
                    raw, actualCc);
                continue;
            }
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

        var (actualTo, finalSubject) = ApplyRedirect(toEmail, subject);

        if (!IsAllowedByAllowlist(actualTo))
        {
            _log?.LogInformation(
                "Email allowlist DROP (multipart): original={Original} actual={Actual} subject='{Subject}' (OnlySendTo='{List}')",
                toEmail, actualTo, subject, _options.OnlySendTo);
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

        using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
        {
            EnableSsl = true, // STARTTLS on 587
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = new NetworkCredential(
                _options.SmtpUsername, _options.SmtpKey),
        };

        await client.SendMailAsync(message, cancellationToken);
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

        var (actualTo, finalSubject) = ApplyRedirect(toEmail, subject);

        if (!IsAllowedByAllowlist(actualTo))
        {
            _log?.LogInformation(
                "Email allowlist DROP (ics): original={Original} actual={Actual} subject='{Subject}' (OnlySendTo='{List}')",
                toEmail, actualTo, subject, _options.OnlySendTo);
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

        using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
        {
            EnableSsl = true,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = new NetworkCredential(_options.SmtpUsername, _options.SmtpKey),
        };

        try
        {
            await client.SendMailAsync(message, cancellationToken);
        }
        finally
        {
            attachment.Dispose();
            icsStream.Dispose();
        }
    }
}
