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
    private bool IsAllowedByAllowlist(string actualTo)
    {
        if (_allowlist.Length == 0) return true;
        var addr = (actualTo ?? string.Empty).Trim().ToLowerInvariant();
        if (addr.Length == 0) return false;
        foreach (var entry in _allowlist)
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

    public async Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
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
