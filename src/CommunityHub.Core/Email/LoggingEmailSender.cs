using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Email;

/// <summary>
/// An <see cref="IEmailSender"/> DECORATOR that records an <see cref="EmailLog"/>
/// row for EVERY send before delegating to the real sender
/// (<see cref="BrevoEmailSender"/>). Registering this as the <c>IEmailSender</c>
/// the rest of the app resolves means no call site can bypass the audit log —
/// welcome, PIN, reminders, broadcast, onboarding, manual re-send … all flow
/// through here (requirement 10a-3).
///
/// It enriches each row from the ambient <see cref="EmailContext"/> (category,
/// edition, participant, name) when a caller set one, and computes the
/// actually-delivered address + drop outcome via
/// <see cref="BrevoEmailSender.ResolveDelivery"/> so the log mirrors the real
/// redirect/allowlist behaviour. Logging never breaks a send: a send failure is
/// recorded then re-thrown (so callers that depend on the throw still see it),
/// and a log-write failure is swallowed (an audit miss must not drop mail).
///
/// The decorator is a singleton (like the inner sender), so it opens a fresh
/// scoped <see cref="CommunityHubDbContext"/> per write via
/// <see cref="IServiceScopeFactory"/> rather than capturing one.
/// </summary>
public sealed class LoggingEmailSender : IEmailSender
{
    private readonly IEmailSender _inner;
    private readonly IServiceScopeFactory _scopes;
    private readonly IEmailContextAccessor _context;
    private readonly EmailOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<LoggingEmailSender>? _log;

    public LoggingEmailSender(
        IEmailSender inner,
        IServiceScopeFactory scopes,
        IEmailContextAccessor context,
        IOptions<EmailOptions> options,
        TimeProvider clock,
        ILogger<LoggingEmailSender>? log = null)
    {
        _inner = inner;
        _scopes = scopes;
        _context = context;
        _options = options.Value;
        _clock = clock;
        _log = log;
    }

    public Task SendAsync(
        string toEmail, string subject, string htmlBody,
        CancellationToken cancellationToken = default)
        => SendAsync(toEmail, subject, htmlBody, cc: null, cancellationToken);

    public async Task SendAsync(
        string toEmail, string subject, string htmlBody,
        IReadOnlyCollection<string>? cc,
        CancellationToken cancellationToken = default)
    {
        await SendWithLogAsync(
            toEmail, subject, cc,
            () => _inner.SendAsync(toEmail, subject, htmlBody, cc, cancellationToken));
    }

    public async Task SendAsync(
        string toEmail, string subject, string htmlBody, EmailReplyTo? replyTo,
        CancellationToken cancellationToken = default)
    {
        await SendWithLogAsync(
            toEmail, subject, cc: null,
            () => _inner.SendAsync(toEmail, subject, htmlBody, replyTo, cancellationToken));
    }

    public async Task SendAsync(
        string toEmail, string subject, string htmlBody, string textBody,
        CancellationToken cancellationToken = default)
    {
        await SendWithLogAsync(
            toEmail, subject, cc: null,
            () => _inner.SendAsync(toEmail, subject, htmlBody, textBody, cancellationToken));
    }

    public async Task SendWithIcsAsync(
        string toEmail, string subject, string htmlBody,
        string icsContent, string icsFileName,
        CancellationToken cancellationToken = default)
    {
        await SendWithLogAsync(
            toEmail, subject, cc: null,
            () => _inner.SendWithIcsAsync(
                toEmail, subject, htmlBody, icsContent, icsFileName, cancellationToken));
    }

    public async Task SendWithAttachmentsAsync(
        string toEmail, string subject, string htmlBody,
        IReadOnlyCollection<EmailAttachment> attachments,
        CancellationToken cancellationToken = default)
    {
        await SendWithLogAsync(
            toEmail, subject, cc: null,
            () => _inner.SendWithAttachmentsAsync(
                toEmail, subject, htmlBody, attachments, cancellationToken));
    }

    private async Task SendWithLogAsync(
        string toEmail, string subject, IReadOnlyCollection<string>? cc,
        Func<Task> send)
    {
        var ctx = _context.Current;
        var (actualTo, allowed) = BrevoEmailSender.ResolveDelivery(_options, toEmail);

        bool success;
        string? error;
        try
        {
            await send();
            // Audience is rings-only now; ResolveDelivery's "allowed" reflects the
            // kill switch (a ring drop is logged separately by the sender as
            // RING-DROP). A silent kill-switch drop is not a real delivery.
            success = allowed;
            error = allowed ? null : "Dropped by global email kill switch (Email:KillSwitch).";
        }
        catch (Exception ex)
        {
            success = false;
            error = ex.Message;
            await WriteLogAsync(ctx, toEmail, actualTo, cc, subject, success, error);
            throw;                          // preserve the original throw-on-failure contract
        }

        await WriteLogAsync(ctx, toEmail, actualTo, cc, subject, success, error);
    }

    private async Task WriteLogAsync(
        EmailContext? ctx, string toEmail, string actualTo,
        IReadOnlyCollection<string>? cc, string subject, bool success, string? error)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
            db.EmailLogs.Add(new EmailLog
            {
                EventId = ctx?.EventId ?? 0,
                Category = ctx?.Category ?? "other",
                ToEmail = Trim(toEmail, 320),
                ActualToEmail = Trim(actualTo, 320),
                CcEmails = cc is null
                    ? string.Empty
                    : Trim(string.Join(", ", cc.Where(c => !string.IsNullOrWhiteSpace(c))), 1000),
                ParticipantId = ctx?.ParticipantId,
                RecipientName = ctx?.RecipientName is { } n ? Trim(n, 200) : null,
                TemplateName = ctx?.TemplateName is { } t ? Trim(t, 200) : null,
                Subject = Trim(subject, 998),
                Success = success,
                Error = error is null ? null : Trim(error, 2000),
                SentAt = _clock.GetUtcNow(),
            });
            await db.SaveChangesAsync();

            // UNIFIED AUDIT TRAIL (REQUIREMENTS §24): surface the send as an engine
            // event in the one trail organizers review, alongside user actions. The
            // EmailLog above keeps the rich re-send detail; this is the trail line.
            var audit = scope.ServiceProvider
                .GetService(typeof(CommunityHub.Core.Audit.IAuditTrail))
                as CommunityHub.Core.Audit.IAuditTrail;
            if (audit is not null)
            {
                await audit.RecordAsync(new Domain.AuditEntry
                {
                    EventId = ctx?.EventId ?? 0,
                    Category = Domain.AuditCategory.Email,
                    Action = CommunityHub.Core.Audit.AuditActions.EmailSent,
                    ActorEmail = "system",
                    Source = Domain.AuditSource.System,
                    TargetType = "Email",
                    TargetId = Trim(toEmail, 128),
                    Summary = $"Email “{Trim(subject, 120)}” to {Trim(toEmail, 120)}",
                    Detail = ctx?.Category,
                    Outcome = success ? Domain.AuditOutcome.Success : Domain.AuditOutcome.Failure,
                    OccurredUtc = _clock.GetUtcNow(),
                });
            }
        }
        catch (Exception ex)
        {
            // An audit-log write failure must never break (or re-break) a send.
            _log?.LogWarning(ex, "EmailLog write failed for To={To} subject='{Subject}'",
                toEmail, subject);
        }
    }

    private static string Trim(string? s, int max)
    {
        s ??= string.Empty;
        return s.Length <= max ? s : s[..max];
    }
}
