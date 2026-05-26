using System.Net;
using System.Net.Mail;
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

    public BrevoEmailSender(IOptions<EmailOptions> options)
    {
        _options = options.Value;
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
