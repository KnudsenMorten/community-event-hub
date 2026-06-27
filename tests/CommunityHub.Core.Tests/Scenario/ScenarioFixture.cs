using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// Shared plumbing for the scenario tests: a fresh in-memory DbContext per test
/// (isolated by a unique database name), a deterministic clock, and a captured
/// e-mail sender so "real send" side-effects can be asserted without SMTP.
/// </summary>
public static class ScenarioFixture
{
    /// <summary>A deterministic clock anchored to the seed's reference time.</summary>
    public static readonly TimeProvider Clock =
        new FixedClock(new DateTimeOffset(2026, 6, 14, 9, 0, 0, TimeSpan.Zero));

    /// <summary>A brand-new isolated in-memory context (unique DB name per call).</summary>
    public static CommunityHubDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"ceh-scenario-{Guid.NewGuid():N}")
            .EnableSensitiveDataLogging()
            .Options;
        return new CommunityHubDbContext(options);
    }

    /// <summary>
    /// The shared Sessionize import service wired with a capturing email sender,
    /// so a test can both run the importer and inspect which welcome mails it
    /// would have sent.
    /// </summary>
    public static (SessionizeImportService Import, CapturingEmailSender Sender)
        NewImporter(CommunityHubDbContext db)
    {
        var sender = new CapturingEmailSender();
        var templates = new EmailTemplateProvider(
            Options.Create(new EmailTemplateOptions
            {
                // Render against the REAL shipped templates so a welcome send
                // exercises the same markup production uses.
                TemplateDirectory = RepoPaths.EmailTemplates(),
            }));
        var welcome = new WelcomeEmailService(db, templates, sender, Clock);
        // The scenario drives ImportSpeakersAsync directly with parsed speakers.
        var import = new SessionizeImportService(db, welcome, Clock);
        return (import, sender);
    }

    /// <summary>The session importer wired with the deterministic clock.</summary>
    public static SessionImportService NewSessionImporter(CommunityHubDbContext db) =>
        new(db, Clock);

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}

/// <summary>An <see cref="IEmailSender"/> that records sends instead of sending.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    public List<(string To, string Subject)> Sent { get; } = new();

    /// <summary>Full capture incl. both bodies, for the multipart welcome send.</summary>
    public List<(string To, string Subject, string Html, string? Text)> Messages { get; } = new();

    /// <summary>Per-send CC capture (the secondary-email CC, 10a-5).</summary>
    public List<(string To, string Subject, IReadOnlyCollection<string> Cc)> CcSent { get; } = new();

    /// <summary>The .ics content of the most recent SendWithIcsAsync call (calendar invite).</summary>
    public string? LastIcs { get; private set; }

    /// <summary>Full capture (To, Subject, Html) of every SendWithIcsAsync call —
    /// kept separate from <see cref="Messages"/> so .ics sends don't change the
    /// non-ics message count that existing tests assert on.</summary>
    public List<(string To, string Subject, string Html)> IcsMessages { get; } = new();

    public Task SendAsync(
        string toEmail, string subject, string htmlBody,
        CancellationToken ct = default)
        => SendAsync(toEmail, subject, htmlBody, cc: null, ct);

    public Task SendAsync(
        string toEmail, string subject, string htmlBody,
        IReadOnlyCollection<string>? cc,
        CancellationToken ct = default)
    {
        Sent.Add((toEmail, subject));
        Messages.Add((toEmail, subject, htmlBody, null));
        CcSent.Add((toEmail, subject, cc ?? Array.Empty<string>()));
        return Task.CompletedTask;
    }

    public Task SendAsync(
        string toEmail, string subject, string htmlBody, string textBody,
        CancellationToken ct = default)
    {
        Sent.Add((toEmail, subject));
        Messages.Add((toEmail, subject, htmlBody, textBody));
        CcSent.Add((toEmail, subject, Array.Empty<string>()));
        return Task.CompletedTask;
    }

    public Task SendWithIcsAsync(
        string toEmail, string subject, string htmlBody,
        string icsContent, string icsFileName,
        CancellationToken ct = default)
    {
        Sent.Add((toEmail, subject));
        IcsMessages.Add((toEmail, subject, htmlBody));
        LastIcs = icsContent;
        return Task.CompletedTask;
    }

    /// <summary>Full capture (To, Subject, Html, attachments) of every
    /// SendWithAttachmentsAsync call — kept separate so attachment sends don't
    /// change the non-attachment message count existing tests assert on.</summary>
    public List<(string To, string Subject, string Html, IReadOnlyCollection<EmailAttachment> Attachments)> AttachmentMessages { get; } = new();

    public Task SendWithAttachmentsAsync(
        string toEmail, string subject, string htmlBody,
        IReadOnlyCollection<EmailAttachment> attachments,
        CancellationToken ct = default)
    {
        Sent.Add((toEmail, subject));
        AttachmentMessages.Add((toEmail, subject, htmlBody, attachments ?? Array.Empty<EmailAttachment>()));
        return Task.CompletedTask;
    }
}
