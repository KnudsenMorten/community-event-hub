using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for the organizer "EmailLog resend-on-failure" recovery
/// (REQUIREMENTS §20 Participant). Covers the <see cref="EmailResendService"/>
/// outcome matrix + the <see cref="EmailResendService.IsResendable"/> gate, plus
/// the template-name capture that makes a faithful re-send possible. EF Core
/// InMemory + a recording / throwing sender.
/// </summary>
public sealed class EmailResendTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 18, 9, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"resend-{Guid.NewGuid():N}")
            .Options);

    private static EmailTemplateProvider RealTemplates() =>
        new(Options.Create(new EmailTemplateOptions
        {
            TemplateDirectory = RepoPaths.EmailTemplates(),
            HubUrl = "https://hub.example.test",
        }));

    private static async Task<(int eventId, Participant p)> SeedAsync(
        CommunityHubDbContext db, string email = "p@example.test")
    {
        var evt = new Event
        {
            Code = "T27", CommunityName = "Test Community", DisplayName = "Test Community 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var p = new Participant
        {
            EventId = evt.Id, Email = email, FullName = "Test Person",
            Role = ParticipantRole.Volunteer, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return (evt.Id, p);
    }

    private static EmailResendService NewService(CommunityHubDbContext db, IEmailSender sender) =>
        new(db, new ParticipantEmailService(db, RealTemplates(), sender, new EmailContextAccessor()));

    private static EmailLog FailedRow(int eventId, int? participantId, string? template) => new()
    {
        EventId = eventId, Category = "welcome", ToEmail = "p@example.test",
        Subject = "Welcome", Success = false, Error = "smtp 500",
        ParticipantId = participantId, TemplateName = template, SentAt = Now,
    };

    // ----- the capture that makes a faithful re-send possible -----------------

    [Fact]
    public async Task ParticipantEmail_path_captures_the_template_name_on_the_log_row()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedAsync(db);
        var ctx = new EmailContextAccessor();
        var inner = new CapturingEmailSender();
        var logger = new LoggingEmailSender(
            inner, new SingleContextScopeFactory(db), ctx,
            Options.Create(new EmailOptions()),
            new FixedClock(Now), log: null);
        var svc = new ParticipantEmailService(db, RealTemplates(), logger, ctx);

        await svc.SendTemplateToParticipantAsync(eventId, p.Id, "welcome", "manual-resend");

        var log = await db.EmailLogs.SingleAsync();
        Assert.Equal("welcome", log.TemplateName);   // template captured for a faithful re-send
        Assert.Equal("manual-resend", log.Category);
    }

    // ----- IsResendable gate --------------------------------------------------

    [Fact]
    public void IsResendable_only_for_a_failed_row_with_participant_and_template()
    {
        Assert.True(EmailResendService.IsResendable(FailedRow(1, 7, "welcome")));
        Assert.False(EmailResendService.IsResendable(FailedRow(1, 7, null)));      // no template (broadcast/PIN)
        Assert.False(EmailResendService.IsResendable(FailedRow(1, null, "welcome"))); // no participant

        var succeeded = FailedRow(1, 7, "welcome");
        succeeded.Success = true;
        Assert.False(EmailResendService.IsResendable(succeeded));                  // not a failure
    }

    // ----- ResendAsync outcome matrix -----------------------------------------

    [Fact]
    public async Task Resend_a_failed_row_sends_again_to_the_effective_address()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedAsync(db);
        var sender = new CapturingEmailSender();
        var row = FailedRow(eventId, p.Id, "welcome");
        db.EmailLogs.Add(row);
        await db.SaveChangesAsync();

        var result = await NewService(db, sender).ResendAsync(eventId, row.Id);

        Assert.Equal(EmailResendOutcome.Sent, result.Outcome);
        Assert.Equal(p.Email, result.ToEmail);
        Assert.Equal("welcome", result.TemplateName);
        Assert.Single(sender.Sent);                  // the retry really went out
    }

    [Fact]
    public async Task Resend_a_successful_row_is_a_noop_not_a_resend()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedAsync(db);
        var sender = new CapturingEmailSender();
        var row = FailedRow(eventId, p.Id, "welcome");
        row.Success = true;
        db.EmailLogs.Add(row);
        await db.SaveChangesAsync();

        var result = await NewService(db, sender).ResendAsync(eventId, row.Id);

        Assert.Equal(EmailResendOutcome.NotFailed, result.Outcome);
        Assert.Empty(sender.Sent);                   // never re-sends a good mail
    }

    [Fact]
    public async Task Resend_a_row_without_a_template_is_not_resendable()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedAsync(db);
        var sender = new CapturingEmailSender();
        var row = FailedRow(eventId, p.Id, template: null);   // e.g. a broadcast / PIN
        db.EmailLogs.Add(row);
        await db.SaveChangesAsync();

        var result = await NewService(db, sender).ResendAsync(eventId, row.Id);

        Assert.Equal(EmailResendOutcome.NotResendable, result.Outcome);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Resend_for_a_deleted_participant_reports_participant_gone()
    {
        using var db = NewDb();
        var (eventId, _) = await SeedAsync(db);
        var sender = new CapturingEmailSender();
        var row = FailedRow(eventId, participantId: 9999, template: "welcome"); // no such participant
        db.EmailLogs.Add(row);
        await db.SaveChangesAsync();

        var result = await NewService(db, sender).ResendAsync(eventId, row.Id);

        Assert.Equal(EmailResendOutcome.ParticipantGone, result.Outcome);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Resend_is_edition_scoped()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedAsync(db);
        var sender = new CapturingEmailSender();
        var row = FailedRow(eventId, p.Id, "welcome");
        db.EmailLogs.Add(row);
        await db.SaveChangesAsync();

        // Asking for the row from a DIFFERENT edition finds nothing.
        var result = await NewService(db, sender).ResendAsync(eventId + 1, row.Id);

        Assert.Equal(EmailResendOutcome.NotFound, result.Outcome);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Resend_that_throws_is_reported_as_failed_with_the_reason()
    {
        using var db = NewDb();
        var (eventId, p) = await SeedAsync(db);
        var row = FailedRow(eventId, p.Id, "welcome");
        db.EmailLogs.Add(row);
        await db.SaveChangesAsync();

        var result = await NewService(db, new ThrowingSender()).ResendAsync(eventId, row.Id);

        Assert.Equal(EmailResendOutcome.Failed, result.Outcome);
        Assert.Contains("kaboom", result.Error);
    }

    // ----- helpers ------------------------------------------------------------

    private sealed class ThrowingSender : IEmailSender
    {
        public Task SendAsync(string to, string s, string h, CancellationToken ct = default)
            => throw new InvalidOperationException("kaboom");
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default)
            => throw new InvalidOperationException("kaboom");
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default)
            => throw new InvalidOperationException("kaboom");
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string fn, CancellationToken ct = default)
            => throw new InvalidOperationException("kaboom");
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct = default)
            => throw new InvalidOperationException("kaboom");
    }

    private sealed class SingleContextScopeFactory
        : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory,
          Microsoft.Extensions.DependencyInjection.IServiceScope,
          IServiceProvider
    {
        private readonly CommunityHubDbContext _db;
        public SingleContextScopeFactory(CommunityHubDbContext db) => _db = db;
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) =>
            serviceType == typeof(CommunityHubDbContext) ? _db : null;
        public void Dispose() { }
    }
}
