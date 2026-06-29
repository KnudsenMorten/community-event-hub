using CommunityHub.Core.Data;
using CommunityHub.Core.Diagnostics;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The central "alert only on the 2nd+ CONSECUTIVE failure" gate that
/// <c>EngineErrorAlertMiddleware</c> delegates to for EVERY background function
/// (operator 2026-06-27, REQUIREMENTS §138, the ErpWebshopReconcile 503 incident:
/// "a single failure is likely a transient platform/upstream glitch — only page on
/// the 2nd in a row"). Proves: a first failure does NOT email; a 2nd consecutive
/// failure DOES (subject unchanged); a SUCCESS between failures resets the counter so
/// the next single failure is again suppressed; the gate NEVER throws (so the
/// middleware's unconditional re-throw always runs — a state-store problem can never
/// mask/replace the real job failure); and a state-store read error FAILS OPEN (alerts)
/// so a genuine outage is never silently swallowed.
///
/// Each test uses a UNIQUE function name: <see cref="EngineAlertSender"/>'s 6h throttle
/// is a process-wide static keyed by function name, so a shared name would let one test's
/// send suppress another's. The unique name also isolates the per-job durable counter.
/// </summary>
public sealed class EngineFailureAlertGateTests
{
    private static string NewFn() => $"Fn-{Guid.NewGuid():N}";

    /// <summary>Records every successful low-level send so a test can assert "alerted N times".</summary>
    private sealed class RecordingEmailSender : IEmailSender
    {
        public int Sends { get; private set; }
        public string? LastSubject { get; private set; }
        public string? LastTo { get; private set; }

        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
        {
            Sends++;
            LastSubject = subject;
            LastTo = toEmail;
            return Task.CompletedTask;
        }

        public Task SendAsync(string toEmail, string subject, string htmlBody,
            IReadOnlyCollection<string>? cc, CancellationToken cancellationToken = default) =>
            SendAsync(toEmail, subject, htmlBody, cancellationToken);

        public Task SendAsync(string toEmail, string subject, string htmlBody,
            string textBody, CancellationToken cancellationToken = default) =>
            SendAsync(toEmail, subject, htmlBody, cancellationToken);

        public Task SendWithIcsAsync(string toEmail, string subject, string htmlBody,
            string icsContent, string icsFileName, CancellationToken cancellationToken = default) =>
            SendAsync(toEmail, subject, htmlBody, cancellationToken);

        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody,
            IReadOnlyCollection<EmailAttachment> attachments, CancellationToken cancellationToken = default) =>
            SendAsync(toEmail, subject, htmlBody, cancellationToken);
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"enginegate-{Guid.NewGuid():N}")
            .Options);

    private static EngineAlertSender NewAlertSender(RecordingEmailSender mail) =>
        new(mail, new EmailContextAccessor(), TimeProvider.System, NullLogger<EngineAlertSender>.Instance);

    private static EngineFailureAlertGate NewGate(CommunityHubDbContext db, RecordingEmailSender mail) =>
        new(
            new JobFailureTracker(db, TimeProvider.System, NullLogger<JobFailureTracker>.Instance),
            NewAlertSender(mail),
            NullLogger<EngineFailureAlertGate>.Instance);

    [Fact]
    public async Task First_failure_records_but_does_not_alert()
    {
        using var db = NewDb();
        var mail = new RecordingEmailSender();
        var gate = NewGate(db, mail);
        var fn = NewFn();

        await gate.OnFailureAsync(fn, new InvalidOperationException("WooCommerce 503 Service Unavailable"));

        Assert.Equal(0, mail.Sends); // single blip — no email
        var marker = await db.JobHealthMarkers.SingleAsync(m => m.JobKey == fn);
        Assert.Equal(1, marker.ConsecutiveFailures); // but the failure IS recorded
        Assert.NotNull(marker.LastFailureAt);
    }

    [Fact]
    public async Task Second_consecutive_failure_alerts_with_unchanged_subject()
    {
        using var db = NewDb();
        var mail = new RecordingEmailSender();
        var gate = NewGate(db, mail);
        var fn = NewFn();

        await gate.OnFailureAsync(fn, new InvalidOperationException("503"));   // #1 suppressed
        Assert.Equal(0, mail.Sends);

        await gate.OnFailureAsync(fn, new InvalidOperationException("503 again")); // #2 alerts

        Assert.Equal(1, mail.Sends);
        Assert.Equal($"Engine FAILED: {fn} [ELDK27]", mail.LastSubject); // subject contract unchanged
        Assert.Equal(EngineAlertSender.Recipient, mail.LastTo);          // ring-exempt ops mailbox
        Assert.Equal(2, (await db.JobHealthMarkers.SingleAsync(m => m.JobKey == fn)).ConsecutiveFailures);
    }

    [Fact]
    public async Task Success_between_failures_resets_so_next_single_failure_does_not_alert()
    {
        using var db = NewDb();
        var mail = new RecordingEmailSender();
        var gate = NewGate(db, mail);
        var fn = NewFn();

        await gate.OnFailureAsync(fn, new InvalidOperationException("blip 1")); // #1 suppressed
        await gate.OnSuccessAsync(fn);                                          // recovers — reset
        await gate.OnFailureAsync(fn, new InvalidOperationException("blip 2")); // back to #1 — suppressed

        Assert.Equal(0, mail.Sends); // no alert anywhere across the non-consecutive failures
        var marker = await db.JobHealthMarkers.SingleAsync(m => m.JobKey == fn);
        Assert.Equal(1, marker.ConsecutiveFailures);
        Assert.NotNull(marker.LastSuccessAt);
    }

    [Fact]
    public void Threshold_const_is_two()
    {
        // Operator agreement is the source of truth; assert the tunable const matches.
        Assert.Equal(2, EngineFailureAlertGate.ConsecutiveFailureAlertThreshold);
    }

    [Fact]
    public async Task State_store_read_error_does_not_throw_and_fails_open_with_an_alert()
    {
        // A disposed context makes every EF query throw — simulates the durable store being
        // unavailable. The gate must (a) NOT throw (so the middleware's unconditional re-throw
        // still runs and the REAL job exception is never masked/replaced), and (b) FAIL OPEN by
        // alerting, so a genuine outage is never silently swallowed by a database problem.
        var db = NewDb();
        var mail = new RecordingEmailSender();
        var gate = NewGate(db, mail);
        var fn = NewFn();
        await db.DisposeAsync(); // store now unavailable

        var ex = await Record.ExceptionAsync(() =>
            gate.OnFailureAsync(fn, new InvalidOperationException("real outage")));

        Assert.Null(ex);          // never throws -> caller is free to re-throw the original
        Assert.Equal(1, mail.Sends); // failed OPEN: alerted despite the unreadable counter
        Assert.Equal($"Engine FAILED: {fn} [ELDK27]", mail.LastSubject);
    }

    [Fact]
    public async Task Success_bookkeeping_error_is_swallowed_and_never_fails_a_good_run()
    {
        // A success path must never be turned into a failure by a state-store hiccup.
        var db = NewDb();
        var mail = new RecordingEmailSender();
        var gate = NewGate(db, mail);
        await db.DisposeAsync();

        var ex = await Record.ExceptionAsync(() => gate.OnSuccessAsync(NewFn()));

        Assert.Null(ex);           // swallowed
        Assert.Equal(0, mail.Sends); // a success never emails
    }

    [Fact]
    public async Task Each_function_is_counted_independently()
    {
        using var db = NewDb();
        var mail = new RecordingEmailSender();
        var gate = NewGate(db, mail);
        var jobA = NewFn();
        var jobB = NewFn();

        // Two functions each fail once: neither reaches the threshold, so no alert.
        await gate.OnFailureAsync(jobA, new Exception("a"));
        await gate.OnFailureAsync(jobB, new Exception("b"));
        Assert.Equal(0, mail.Sends);

        // jobA fails a 2nd consecutive time -> only jobA alerts.
        await gate.OnFailureAsync(jobA, new Exception("a2"));
        Assert.Equal(1, mail.Sends);
        Assert.Equal($"Engine FAILED: {jobA} [ELDK27]", mail.LastSubject);
    }
}
