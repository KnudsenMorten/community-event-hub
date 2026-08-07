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
        /// <summary>§701.1 — the coalesced-alert body has to SAY it is coalesced.</summary>
        public string? LastHtml { get; private set; }

        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
        {
            Sends++;
            LastSubject = subject;
            LastTo = toEmail;
            LastHtml = htmlBody;
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

    /// <summary>
    /// §702 — the sender stamps the environment on every alert subject, so these tests pin a
    /// KNOWN environment (DEV) and assert the tag is present. Leaving it unset would assert
    /// "[UNKNOWN]", which pins the fallback rather than the behaviour the operator asked for.
    /// </summary>
    private static EngineAlertSender NewAlertSender(RecordingEmailSender mail) =>
        new(mail, new EmailContextAccessor(), TimeProvider.System, NullLogger<EngineAlertSender>.Instance,
            new CommunityHub.Core.Diagnostics.HubEnvironment("DEV", null));

    /// <summary>§702 — the expected alert subject, environment tag included.</summary>
    private static string ExpectedSubject(string fn) => $"[DEV] Engine FAILED: {fn} [ELDK27]";

    private static EngineFailureAlertGate NewGate(
        CommunityHubDbContext db, RecordingEmailSender mail,
        // §716 — null keeps the pre-existing behaviour (env resolves to UNKNOWN, which alerts),
        // so every test above this line is unchanged.
        CommunityHub.Core.Diagnostics.HubEnvironment? env = null) =>
        new(
            new JobFailureTracker(db, TimeProvider.System, NullLogger<JobFailureTracker>.Instance),
            NewAlertSender(mail),
            NullLogger<EngineFailureAlertGate>.Instance,
            env);

    /// <summary>An environment resolved from a real App Service site name, as in production.</summary>
    private static CommunityHub.Core.Diagnostics.HubEnvironment EnvOf(string siteName) =>
        new(configuredLabel: null, siteName: siteName);

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
    public async Task Third_consecutive_failure_alerts_with_unchanged_subject()
    {
        using var db = NewDb();
        var mail = new RecordingEmailSender();
        var gate = NewGate(db, mail);
        var fn = NewFn();

        await gate.OnFailureAsync(fn, new InvalidOperationException("503"));   // #1 suppressed
        Assert.Equal(0, mail.Sends);

        // 🔴 §784.4 — the alert used to fire HERE, on #2. A 503 spanning two ticks is still a blip.
        await gate.OnFailureAsync(fn, new InvalidOperationException("503 again"));
        Assert.Equal(0, mail.Sends);

        await gate.OnFailureAsync(fn, new InvalidOperationException("503 a third time")); // #3 alerts

        Assert.Equal(1, mail.Sends);
        // §702 — the subject now leads with the ENVIRONMENT. The rest of the contract is unchanged.
        Assert.Equal(ExpectedSubject(fn), mail.LastSubject);
        Assert.Equal(EngineAlertSender.Recipient, mail.LastTo);          // ring-exempt ops mailbox
        Assert.Equal(3, (await db.JobHealthMarkers.SingleAsync(m => m.JobKey == fn)).ConsecutiveFailures);
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
    public void Threshold_const_is_three()
    {
        // Operator agreement is the source of truth; assert the tunable const matches.
        // 🔴 §784.4 — raised from 2 to 3 (2026-08-03): "only alert when it has happened for 3
        // consequtive times … you are warning of something which we cannto do anything about (503
        // service unavailable)". An upstream 503 routinely spans two ticks, so a threshold of 2
        // fired on exactly the class of blip this gate exists to absorb.
        Assert.Equal(3, EngineFailureAlertGate.ConsecutiveFailureAlertThreshold);
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
        // 🔒 §701 — THIS is the alert the operator actually received: the state store was
        // unreadable (SQL blip), so the gate failed OPEN and alerted on the FIRST failure of
        // every job at once. The environment tag is what tells him which edition is shouting.
        Assert.Equal(ExpectedSubject(fn), mail.LastSubject);
    }

    /// <summary>
    /// §701.1 — the fix for *"3 erors per mail"*. When the state store is unreachable the cause is
    /// the DATABASE, so every engine fails open in the same minute. Under the old per-function
    /// throttle key each one sent its own mail; they must now coalesce onto one shared key.
    /// </summary>
    [Fact]
    public async Task A_store_outage_sends_ONE_mail_not_one_per_engine()
    {
        var mail = new RecordingEmailSender();
        // A disposed context makes every state-store read throw — i.e. "the database is down".
        var db = NewDb();
        db.Dispose();
        var gate = NewGate(db, mail);

        // Three different engines all fail in the same window, exactly as on 2026-07-29.
        await gate.OnFailureAsync("ErpSyncCustomerContactJob", new Exception("login failed"));
        await gate.OnFailureAsync("WooCommercePullJob", new Exception("login failed"));
        await gate.OnFailureAsync("SessionChangeDetectionJob", new Exception("login failed"));

        // ONE mail, not three. The first engine to notice names itself in the subject...
        Assert.Equal(1, mail.Sends);
        Assert.Equal(ExpectedSubject("ErpSyncCustomerContactJob"), mail.LastSubject);
        // ...and the body must say the others are suppressed, so it cannot read as "only this broke".
        Assert.Contains("coalesced", mail.LastHtml ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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

        // §784.4 — jobA needs a THIRD consecutive failure before it alerts; jobB stays at one.
        await gate.OnFailureAsync(jobA, new Exception("a2"));
        Assert.Equal(0, mail.Sends);

        await gate.OnFailureAsync(jobA, new Exception("a3"));
        Assert.Equal(1, mail.Sends);
        Assert.Equal(ExpectedSubject(jobA), mail.LastSubject);
    }

    // ---------- §707.42 — a gate turning a job away is a SETTING, not news ----------

    /// <summary>
    /// 🔒 §707.42 — a job skipped because its FEATURE IS OFF must never mail, at any streak length.
    /// </summary>
    /// <remarks>
    /// Operator 2026-07-30, on the *"run 300 times in a row WITHOUT DOING ANYTHING"* mail:
    /// <i>"this is too much info - and not relevant"</i>. The alert was reporting his OWN
    /// configuration back to him as an anomaly, then arguing with itself — <i>"If that is deliberate,
    /// nothing needs doing."</i> An alert that cannot tell a setting from a fault is one he learns to
    /// delete, taking the next real one with it.
    /// </remarks>
    [Fact]
    public async Task A_feature_gated_skip_never_alerts_however_long_the_streak()
    {
        using var db = NewDb();
        var mail = new RecordingEmailSender();
        var gate = NewGate(db, mail);
        var fn = NewFn();

        // Three times past the threshold, every run turned away by a gate.
        for (var i = 0; i < EngineFailureAlertGate.ConsecutiveNoOpAlertThreshold * 3; i++)
        {
            await gate.OnActivityAsync(
                fn, "The 'session-change-alerts' feature is switched off.", isDataStarvation: false);
        }

        Assert.Equal(0, mail.Sends);

        // 🔑 …but the STREAK IS STILL RECORDED, so the Jobs page can show how long it has been idle
        // and why. Suppressing the mail must not also blind the page — a state belongs on a page.
        var marker = await db.JobHealthMarkers.SingleAsync(m => m.JobKey == fn);
        Assert.True(marker.ConsecutiveNoOps >= EngineFailureAlertGate.ConsecutiveNoOpAlertThreshold);
    }

    /// <summary>
    /// ⚰️ §951 (operator 2026-08-07) — DATA STARVATION NO LONGER ALERTS EITHER. The "engine inactive"
    /// mail is now off in every environment, for every reason.
    /// </summary>
    /// <remarks>
    /// <para>This asserted the OPPOSITE until today, and was right to: starvation was the one case
    /// where nothing was misconfigured and something might genuinely be wrong (§545/§585). What
    /// changed is evidence, not taste. He quoted the notice back — *"The background engine
    /// CouponInvoiceJob has now run 300 times in a row WITHOUT DOING ANYTHING"* — and said it
    /// *"doesnt add value to me"*. For a pre-event edition there ARE no claimed coupon tickets and
    /// will not be for months, so the last surviving case fires hardest exactly when it is least
    /// informative, and repeats every 100 runs for ever.</para>
    ///
    /// <para>🔑 The mail argued itself out of existence: it ends *"If that is deliberate, nothing
    /// needs doing"*. An alert that has to ask the reader whether it matters is not an alert — it is
    /// a thing he learns to delete, which then costs him the ones that DO matter.</para>
    ///
    /// <para>🔒 What survives is in <c>Failures_still_alert...</c>: an engine that THROWS still pages,
    /// in every environment. A silent stop and a crash are not the same event.</para>
    /// </remarks>
    [Fact]
    public async Task Data_starvation_no_longer_alerts()
    {
        using var db = NewDb();
        var mail = new RecordingEmailSender();
        var gate = NewGate(db, mail);
        var fn = NewFn();

        for (var i = 0; i < EngineFailureAlertGate.ConsecutiveNoOpAlertThreshold; i++)
        {
            await gate.OnActivityAsync(
                fn, "Ran normally but found NO orders in Zoho at all.", isDataStarvation: true);
        }

        Assert.Equal(0, mail.Sends);
    }

    /// <summary>
    /// 🔒 §951 — the STREAK IS STILL RECORDED. Suppressing the mail must not suppress the state:
    /// `/Organizer/Jobs` still answers "how long has this been idle, and why" when he chooses to ask.
    /// That is §707.42's rule, and it is what makes retiring the mail safe rather than blind.
    /// </summary>
    [Fact]
    public async Task The_streak_is_still_recorded_even_though_nothing_is_mailed()
    {
        using var db = NewDb();
        var mail = new RecordingEmailSender();
        var gate = NewGate(db, mail);
        var fn = NewFn();

        for (var i = 0; i < EngineFailureAlertGate.ConsecutiveNoOpAlertThreshold; i++)
        {
            await gate.OnActivityAsync(fn, "Nothing arrived.", isDataStarvation: true);
        }

        Assert.Equal(0, mail.Sends);
        var marker = await db.JobHealthMarkers.SingleAsync(m => m.JobKey == fn);
        Assert.True(marker.ConsecutiveNoOps >= EngineFailureAlertGate.ConsecutiveNoOpAlertThreshold);
    }

    // ---------- §716 — DEV never sends the INACTIVE alert ----------

    /// <summary>
    /// 🔒 §716 — in DEV the INACTIVE alert is silence, even for real data starvation.
    /// </summary>
    /// <remarks>
    /// Operator 2026-07-31: <i>"turn off these alerts in dev for me, sa we have turned off this in
    /// dev"</i>, showing five <c>[DEV] Engine INACTIVE: …</c> mails in ten minutes. In DEV the
    /// features these jobs need are switched off ON PURPOSE, so the mail reports his own
    /// configuration back to him — for ever, every 100 runs. Note this suppresses even the
    /// data-starvation case that <see cref="Data_starvation_still_alerts_at_the_threshold"/> proves
    /// still fires in PROD: DEV has no real data to starve of, so there is nothing to learn from it.
    /// </remarks>
    [Fact]
    public async Task Dev_never_sends_the_inactive_alert_but_still_records_the_streak()
    {
        using var db = NewDb();
        var mail = new RecordingEmailSender();
        var gate = NewGate(db, mail, EnvOf("eldk27hub-fn-devz237e"));
        var fn = NewFn();

        for (var i = 0; i < EngineFailureAlertGate.ConsecutiveNoOpAlertThreshold * 2; i++)
        {
            await gate.OnActivityAsync(
                fn, "The 'session-change-alerts' feature is switched off.", isDataStarvation: true);
        }

        Assert.Equal(0, mail.Sends);

        // 🔑 The state still belongs on the Jobs page — §707.42's rule. Only the MAIL is suppressed.
        var marker = await db.JobHealthMarkers.SingleAsync(m => m.JobKey == fn);
        Assert.True(marker.ConsecutiveNoOps >= EngineFailureAlertGate.ConsecutiveNoOpAlertThreshold);
    }

    /// <summary>
    /// ⚰️ §951 — PROD no longer sends it either. This is the test that named the change: the alert
    /// was kept alive FOR prod, and prod is exactly where he was receiving it and deleting it.
    /// </summary>
    [Fact]
    public async Task Prod_no_longer_sends_the_inactive_alert()
    {
        using var db = NewDb();
        var mail = new RecordingEmailSender();
        var gate = NewGate(db, mail, EnvOf("eldk27hub-web-prodpdrq"));
        var fn = NewFn();

        for (var i = 0; i < EngineFailureAlertGate.ConsecutiveNoOpAlertThreshold; i++)
        {
            await gate.OnActivityAsync(fn, "Nothing arrived.", isDataStarvation: true);
        }

        Assert.Equal(0, mail.Sends);
    }

    /// <summary>
    /// 🔒 An UNRECOGNISED host is silent too (§951). The §702 principle it encoded — never
    /// GUESS an environment — still holds; there is simply no longer an inactivity mail for either
    /// answer to gate. Kept as a test so the retirement is proven to be unconditional.
    /// </summary>
    [Fact]
    public async Task An_unknown_environment_is_silent_too()
    {
        using var db = NewDb();
        var mail = new RecordingEmailSender();
        var gate = NewGate(db, mail, EnvOf("some-host-we-do-not-recognise"));
        var fn = NewFn();

        for (var i = 0; i < EngineFailureAlertGate.ConsecutiveNoOpAlertThreshold; i++)
        {
            await gate.OnActivityAsync(fn, "Nothing arrived.", isDataStarvation: true);
        }

        Assert.Equal(0, mail.Sends);
    }

    /// <summary>
    /// ⚠️ DEV suppression is scoped to the INACTIVE alert. An engine that THROWS in DEV is a genuine
    /// fault and must still page — that is a different signal and he did not ask to lose it.
    /// </summary>
    [Fact]
    public async Task A_dev_engine_that_fails_still_alerts()
    {
        using var db = NewDb();
        var mail = new RecordingEmailSender();
        var gate = NewGate(db, mail, EnvOf("eldk27hub-fn-devz237e"));
        var fn = NewFn();

        await gate.OnFailureAsync(fn, new InvalidOperationException("boom"));
        await gate.OnFailureAsync(fn, new InvalidOperationException("boom again"));
        await gate.OnFailureAsync(fn, new InvalidOperationException("boom a third time"));  // §784.4

        Assert.Equal(1, mail.Sends);
    }
}
