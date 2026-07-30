using System.Net.Mail;
using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §219 (Risk-4) — pace + retry bulk email sends for Brevo limits.
/// <list type="bullet">
/// <item>PACING: a bulk loop (the <see cref="ReminderEngine"/> batch) inserts a delay
/// BETWEEN consecutive sends (configurable, injectable so the test asserts the pace
/// WITHOUT sleeping); a single send never paces.</item>
/// <item>RETRY: the transport retry loop (<see cref="BrevoEmailSender.SendWithRetryAsync"/>)
/// re-attempts a transient / rate-limit failure with backoff then succeeds; a permanent
/// failure is not retried.</item>
/// <item>RESILIENCE: one recipient's hard failure does NOT abort the batch — every
/// recipient is attempted and failures are simply not counted as sent.</item>
/// </list>
/// </summary>
public sealed class EmailBulkSendPacingRetryTests
{
    private const int EventId = 5;

    // ---- PACING (ReminderEngine bulk loop) ---------------------------------

    [Fact]
    public async Task Bulk_loop_paces_between_sends_not_before_the_first()
    {
        using var db = NewDb();
        var sender = new FakeSender();
        var pacer = new RecordingPacer(delayMs: 150);
        var engine = new ReminderEngine(db, sender, Clock, emailContext: null, pacer: pacer);

        var sent = await engine.SendDueAsync(EventId, Due("a@x.test", "b@x.test", "c@x.test"));

        Assert.Equal(3, sent);
        Assert.Equal(3, sender.Sends.Count);
        // 3 sends ⇒ paced TWICE (between 1-2 and 2-3), never before the first.
        Assert.Equal(2, pacer.PaceCalls);
    }

    [Fact]
    public async Task Single_send_does_not_pace()
    {
        using var db = NewDb();
        var sender = new FakeSender();
        var pacer = new RecordingPacer(delayMs: 150);
        var engine = new ReminderEngine(db, sender, Clock, emailContext: null, pacer: pacer);

        await engine.SendDueAsync(EventId, Due("solo@x.test"));

        Assert.Single(sender.Sends);
        Assert.Equal(0, pacer.PaceCalls);
    }

    [Fact]
    public async Task No_pacer_wired_still_sends_unpaced()
    {
        // Optional dependency: a caller (or existing test) that constructs the engine
        // without a pacer is unchanged — everything still sends, just without a delay.
        using var db = NewDb();
        var sender = new FakeSender();
        var engine = new ReminderEngine(db, sender, Clock);

        var sent = await engine.SendDueAsync(EventId, Due("a@x.test", "b@x.test"));

        Assert.Equal(2, sent);
    }

    // ---- RESILIENCE (one hard failure doesn't abort the rest) --------------

    [Fact]
    public async Task One_recipient_hard_failure_does_not_abort_the_batch()
    {
        using var db = NewDb();
        var sender = new FakeSender { ThrowFor = "boom@x.test" };
        var pacer = new RecordingPacer(delayMs: 150);
        var engine = new ReminderEngine(db, sender, Clock, emailContext: null, pacer: pacer);

        var sent = await engine.SendDueAsync(
            EventId, Due("a@x.test", "boom@x.test", "c@x.test"));

        // ALL three attempted (the failing one threw); only the two good ones counted.
        Assert.Equal(3, sender.Attempts.Count);
        Assert.Contains("boom@x.test", sender.Attempts);
        Assert.Equal(2, sent);
        // The failed recipient is NOT recorded (so it retries next run); the others are.
        var ledger = await db.SentReminders.Select(s => s.RecipientEmail).ToListAsync();
        Assert.Equal(2, ledger.Count);
        Assert.DoesNotContain("boom@x.test", ledger);
        // Pacing still ran between every dispatch attempt (2 gaps across 3 dispatches).
        Assert.Equal(2, pacer.PaceCalls);
    }

    // ---- RETRY (transport SendWithRetryAsync) ------------------------------

    [Fact]
    public async Task Retry_succeeds_after_two_transient_rate_limit_failures()
    {
        var attempts = 0;
        var backoffs = new List<int>();

        await BrevoEmailSender.SendWithRetryAsync(
            sendAttempt: (attempt, _) =>
            {
                attempts++;
                // Brevo throttle / rate-limit surfaces over SMTP as a transient 4xx
                // (the HTTP-429 analogue) — fail twice, then succeed on the 3rd.
                if (attempts < 3) throw new SmtpException(SmtpStatusCode.ServiceNotAvailable);
                return Task.CompletedTask;
            },
            isTransient: BrevoEmailSender.IsTransientSmtpError,
            backoff: (attempt, _) => { backoffs.Add(attempt); return Task.CompletedTask; },
            maxAttempts: 3);

        Assert.Equal(3, attempts);
        // Backed off after attempt 1 and attempt 2 (then attempt 3 succeeded).
        Assert.Equal(new[] { 1, 2 }, backoffs);
    }

    [Fact]
    public async Task Retry_does_not_retry_a_permanent_failure()
    {
        var attempts = 0;
        var backoffs = 0;

        await Assert.ThrowsAsync<SmtpException>(() =>
            BrevoEmailSender.SendWithRetryAsync(
                sendAttempt: (_, _) =>
                {
                    attempts++;
                    // 553 mailbox-name-not-allowed = permanent ⇒ not transient ⇒ no retry.
                    throw new SmtpException(SmtpStatusCode.MailboxNameNotAllowed);
                },
                isTransient: BrevoEmailSender.IsTransientSmtpError,
                backoff: (_, _) => { backoffs++; return Task.CompletedTask; },
                maxAttempts: 5));

        Assert.Equal(1, attempts);   // thrown on the first try
        Assert.Equal(0, backoffs);   // never backed off
    }

    [Fact]
    public async Task Retry_gives_up_after_maxAttempts_and_rethrows()
    {
        var attempts = 0;
        var backoffs = 0;

        await Assert.ThrowsAsync<SmtpException>(() =>
            BrevoEmailSender.SendWithRetryAsync(
                sendAttempt: (_, _) =>
                {
                    attempts++;
                    throw new SmtpException(SmtpStatusCode.ServiceNotAvailable);
                },
                isTransient: BrevoEmailSender.IsTransientSmtpError,
                backoff: (_, _) => { backoffs++; return Task.CompletedTask; },
                maxAttempts: 2));

        Assert.Equal(2, attempts);   // initial + 1 retry, then gave up
        Assert.Equal(1, backoffs);   // one backoff between the two attempts
    }

    // ---- transient classification ------------------------------------------

    [Fact]
    public void Transient_classification_covers_rate_limit_and_transport_faults()
    {
        // Brevo rate-limit / throttle 4xx are transient (retried).
        Assert.True(BrevoEmailSender.IsTransientSmtpError(new SmtpException(SmtpStatusCode.ServiceNotAvailable)));
        Assert.True(BrevoEmailSender.IsTransientSmtpError(new SmtpException(SmtpStatusCode.MailboxBusy)));
        Assert.True(BrevoEmailSender.IsTransientSmtpError(new SmtpException(SmtpStatusCode.TransactionFailed)));
        // Connection-level faults are transient.
        Assert.True(BrevoEmailSender.IsTransientSmtpError(new IOException("blip")));
        Assert.True(BrevoEmailSender.IsTransientSmtpError(new TimeoutException()));
        // A permanent rejection / non-transport error is NOT retried. §234: 550
        // MailboxUnavailable (no such mailbox / policy reject) is PERMANENT —
        // retrying it hammers the relay and can hurt sender reputation.
        Assert.False(BrevoEmailSender.IsTransientSmtpError(new SmtpException(SmtpStatusCode.MailboxUnavailable)));
        Assert.False(BrevoEmailSender.IsTransientSmtpError(new SmtpException(SmtpStatusCode.MailboxNameNotAllowed)));
        Assert.False(BrevoEmailSender.IsTransientSmtpError(new ArgumentException("nope")));
    }

    // ---- BulkSendPacer default ---------------------------------------------

    [Fact]
    public async Task Pacer_with_zero_delay_is_a_noop()
    {
        var pacer = new BulkSendPacer(Options.Create(new EmailOptions { BulkSendDelayMs = 0 }));
        Assert.Equal(0, pacer.DelayMs);
        // Completes immediately without sleeping.
        await pacer.PaceAsync();
    }

    [Fact]
    public void Pacer_exposes_configured_delay()
    {
        var pacer = new BulkSendPacer(Options.Create(new EmailOptions { BulkSendDelayMs = 200 }));
        Assert.Equal(200, pacer.DelayMs);
    }

    [Fact]
    public void BulkSendDelay_default_is_a_sane_sub_second_value()
    {
        var d = new EmailOptions().BulkSendDelayMs;
        Assert.InRange(d, 1, 1000);
    }

    // ---- helpers ------------------------------------------------------------

    private static readonly TimeProvider Clock =
        new FixedClock(new DateTimeOffset(2026, 6, 30, 9, 0, 0, TimeSpan.Zero));

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"pace-retry-{Guid.NewGuid():N}")
            .Options);

    private static IReadOnlyCollection<ReminderMessage> Due(params string[] emails) =>
        emails.Select(e => new ReminderMessage(
            e, "task-deadline", $"occ:{e}", "Subject", "<p>body</p>")).ToList();

    private sealed class RecordingPacer : IBulkSendPacer
    {
        public RecordingPacer(int delayMs) => DelayMs = delayMs;
        public int DelayMs { get; }
        public int PaceCalls { get; private set; }
        public Task PaceAsync(CancellationToken cancellationToken = default)
        {
            PaceCalls++;                 // record, never sleep
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSender : IEmailSender
    {
        public List<string> Attempts { get; } = new();
        public List<string> Sends { get; } = new();
        public string? ThrowFor { get; init; }

        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
            => Record(toEmail);
        public Task SendAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<string>? cc, CancellationToken ct = default)
            => Record(toEmail);
        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken ct = default)
            => Record(toEmail);
        public Task SendWithIcsAsync(string toEmail, string subject, string htmlBody, string icsContent, string icsFileName, CancellationToken ct = default)
            => Record(toEmail);
        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct = default)
            => Record(toEmail);

        private Task Record(string toEmail)
        {
            Attempts.Add(toEmail);
            if (ThrowFor is not null && string.Equals(toEmail, ThrowFor, StringComparison.OrdinalIgnoreCase))
            {
                throw new SmtpException(SmtpStatusCode.MailboxNameNotAllowed);
            }
            Sends.Add(toEmail);
            return Task.CompletedTask;
        }
    }
}
