using System.Collections.Concurrent;
using System.Net.Mail;
using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §234 — CRITICAL email-safety fixes.
///
/// <para><b>Fix 1 (delivered-vs-dropped seam):</b> a ring-dropped send must NEVER be
/// recorded as sent. The transport records the real outcome on
/// <see cref="IEmailDeliveryOutcome"/>; the ledger callers
/// (<see cref="WelcomeEmailService"/> SentReminder row,
/// <see cref="WelcomeWithLoginEmailService"/> / <see cref="AttendeeOneDayWelcomeEmailService"/>
/// WelcomeWithLoginSentAt stamp, <see cref="ReminderEngine"/> SentReminder rows) only
/// record when DELIVERED — so a gated recipient is retried automatically once the
/// operator widens the ring. The audit decorator (<see cref="LoggingEmailSender"/>)
/// records a drop as Success=false, never as a successful send.</para>
///
/// <para><b>Fix 3 (participant-keyed ring gate):</b> mail to an address that is not a
/// participant address (a speaker's ContactEmailOverride, a SecondaryEmail CC) is
/// gated by the PERSON's ring via the ambient <see cref="EmailContext.ParticipantId"/>
/// instead of being fail-dropped as an unknown recipient. Unknown address with NO
/// participant context stays fail-closed.</para>
/// </summary>
public sealed class EmailDeliveryOutcomeLedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 7, 9, 0, 0, TimeSpan.Zero);

    // ---- Fix 3: ring gate keyed on participant, not just address ------------

    [Fact]
    public async Task Unknown_address_with_in_ring_participant_context_sends()
    {
        await using var h = await Harness.CreateAsync(outboundRing: Ring.Ring1);
        var pid = await h.SeedParticipantAsync("speaker@in.test", Ring.Ring1, ParticipantRole.Speaker);
        var outcome = new EmailDeliveryOutcome();

        // The delivery address (a speaker ContactEmailOverride) is NOT a participant
        // address — but the context identifies the participant, whose ring gates it.
        using (h.Context.Set(new EmailContext("test", h.EventId, ParticipantId: pid)))
        {
            await h.Sender.SendAsync("override@elsewhere.test", "Hi", "<p>hi</p>");
        }

        Assert.Single(h.Sent);
        Assert.True(outcome.LastSendDelivered);
        Assert.DoesNotContain(h.Logs, l => l.Contains("RING-DROP"));
    }

    [Fact]
    public async Task Unknown_address_with_out_of_ring_participant_context_is_ring_dropped()
    {
        await using var h = await Harness.CreateAsync(outboundRing: Ring.Ring1);
        var pid = await h.SeedParticipantAsync("speaker@out.test", Ring.Ring2, ParticipantRole.Speaker);
        var outcome = new EmailDeliveryOutcome();

        using (h.Context.Set(new EmailContext("test", h.EventId, ParticipantId: pid)))
        {
            await h.Sender.SendAsync("override@elsewhere.test", "Hi", "<p>hi</p>");
        }

        Assert.Empty(h.Sent);
        Assert.False(outcome.LastSendDelivered);
        Assert.Equal("ring-drop", outcome.LastDropReason);
        Assert.Contains(h.Logs, l => l.Contains("RING-DROP"));
    }

    [Fact]
    public async Task Unknown_address_without_participant_context_stays_fail_closed()
    {
        await using var h = await Harness.CreateAsync(outboundRing: Ring.Ring1);
        var outcome = new EmailDeliveryOutcome();

        using (h.Context.Set(new EmailContext("test", h.EventId)))
        {
            await h.Sender.SendAsync("stranger@nowhere.test", "Hi", "<p>hi</p>");
        }

        Assert.Empty(h.Sent);
        Assert.False(outcome.LastSendDelivered);
        Assert.Equal("ring-drop", outcome.LastDropReason);
        Assert.Contains(h.Logs, l => l.Contains("RING-DROP") && l.Contains("unknown"));
    }

    [Fact]
    public async Task Secondary_email_cc_is_gated_by_the_participants_ring()
    {
        // Primary address IS the participant's; the CC (their SecondaryEmail) is not
        // a participant address — with the participant context it now rides along
        // instead of being silently dropped as an unknown recipient.
        await using var h = await Harness.CreateAsync(outboundRing: Ring.Ring1);
        var pid = await h.SeedParticipantAsync("primary@in.test", Ring.Ring1, ParticipantRole.Speaker);

        using (h.Context.Set(new EmailContext("test", h.EventId, ParticipantId: pid)))
        {
            await h.Sender.SendAsync(
                "primary@in.test", "Hi", "<p>hi</p>", new[] { "secondary@private.test" });
        }

        var msg = Assert.Single(h.Sent);
        Assert.Contains(msg.CC, a => a.Address == "secondary@private.test");
    }

    // ---- Fix 1: outcome recording at the transport ---------------------------

    [Fact]
    public async Task Kill_switch_drop_is_recorded_as_not_delivered()
    {
        await using var h = await Harness.CreateAsync(outboundRing: Ring.Ring1, killSwitch: true);
        var pid = await h.SeedParticipantAsync("speaker@in.test", Ring.Ring1, ParticipantRole.Speaker);
        var outcome = new EmailDeliveryOutcome();

        using (h.Context.Set(new EmailContext("test", h.EventId, ParticipantId: pid)))
        {
            await h.Sender.SendAsync("speaker@in.test", "Hi", "<p>hi</p>");
        }

        Assert.Empty(h.Sent);
        Assert.False(outcome.LastSendDelivered);
        Assert.Equal("kill-switch", outcome.LastDropReason);
    }

    [Fact]
    public async Task Redirected_dev_send_counts_as_delivered()
    {
        await using var h = await Harness.CreateAsync(
            outboundRing: Ring.Ring1, redirectAllTo: "dev@redirect.test");
        await h.SeedParticipantAsync("speaker@in.test", Ring.Ring1, ParticipantRole.Speaker);
        var outcome = new EmailDeliveryOutcome();

        using (h.Context.Set(new EmailContext("test", h.EventId)))
        {
            await h.Sender.SendAsync("speaker@in.test", "Hi", "<p>hi</p>");
        }

        var msg = Assert.Single(h.Sent);
        Assert.Equal("dev@redirect.test", msg.To[0].Address);
        Assert.True(outcome.LastSendDelivered);   // dispatched — just to the redirect inbox
    }

    // ---- Fix 1: WelcomeEmailService (SentReminder "welcome" ledger) ----------

    [Fact]
    public async Task Ring_dropped_welcome_writes_no_ledger_row_and_is_resendable_once_rings_widen()
    {
        await using var h = await Harness.CreateAsync(outboundRing: Ring.Ring1);
        await h.SetFeatureRingAsync("welcome-email", Ring.Ring1);
        var pid = await h.SeedParticipantAsync("late@ring2.test", Ring.Ring2, ParticipantRole.Speaker);
        var outcome = new EmailDeliveryOutcome();

        var svc = new WelcomeEmailService(
            h.Db, Harness.Templates(), h.Sender, new FixedClock(Now),
            context: h.Context, outcome: outcome);

        // OUT of ring: dropped at the transport ⇒ NOT recorded as welcomed.
        Assert.False(await svc.SendWelcomeAsync(pid));
        Assert.Empty(h.Sent);
        Assert.Equal(0, await h.Db.SentReminders.CountAsync());

        // The operator widens the rings ⇒ the SAME call now delivers AND records.
        await h.SetFeatureRingAsync(FeatureCatalog.OutboundEmailKey, Ring.Ring2);
        await h.SetFeatureRingAsync("welcome-email", Ring.Ring2);

        Assert.True(await svc.SendWelcomeAsync(pid));
        Assert.Single(h.Sent);
        var row = Assert.Single(await h.Db.SentReminders.ToListAsync());
        Assert.Equal("welcome", row.ReminderType);
    }

    // ---- Fix 1: WelcomeWithLoginEmailService (WelcomeWithLoginSentAt stamp) ---

    [Fact]
    public async Task Ring_dropped_provisioning_welcome_does_not_stamp_and_retries_once_rings_widen()
    {
        await using var h = await Harness.CreateAsync(outboundRing: Ring.Ring1);
        await h.SetFeatureRingAsync("welcome-email", Ring.Ring1);
        var pid = await h.SeedParticipantAsync("spk@ring2.test", Ring.Ring2, ParticipantRole.Speaker);
        var outcome = new EmailDeliveryOutcome();

        // DEV SendAsync path (no §217 attendee cap in play) — both paths share
        // SendCoreAsync, where the §234 gated-⇒-no-stamp check lives.
        var svc = new WelcomeWithLoginEmailService(
            h.Db, Harness.Templates(), h.Sender, autoLogin: null!,
            new StubEnv(true, "Development"), new FixedClock(Now),
            context: h.Context, outcome: outcome);

        // OUT of ring ⇒ gated, NOT stamped — retried later.
        var gated = await svc.SendAsync(pid, "https://hub.example");
        Assert.False(gated.Sent);
        Assert.Contains("Gated by ring", gated.Reason);
        Assert.Empty(h.Sent);
        Assert.Null((await h.Db.Participants.FindAsync(pid))!.WelcomeWithLoginSentAt);

        // Rings widen ⇒ the retry DELIVERS and stamps.
        await h.SetFeatureRingAsync(FeatureCatalog.OutboundEmailKey, Ring.Ring2);
        await h.SetFeatureRingAsync("welcome-email", Ring.Ring2);

        var sent = await svc.SendAsync(pid, "https://hub.example");
        Assert.True(sent.Sent);
        Assert.Single(h.Sent);
        Assert.Equal(Now, (await h.Db.Participants.FindAsync(pid))!.WelcomeWithLoginSentAt);
    }

    // ---- Fix 1: AttendeeOneDayWelcomeEmailService -----------------------------

    [Fact]
    public async Task One_day_welcome_is_retired_even_with_ring_cap_raised()
    {
        // §299 OPEN-26 (operator 2026-07-23): the 1-day welcome is retired — no ring/cap
        // combination makes it send. (The §234 capped-not-stamped seam this test previously
        // exercised via the 1-day service is still covered for the 2-day welcome + reminders
        // by the neighbouring tests.)
        await using var h = await Harness.CreateAsync(outboundRing: Ring.Broad);
        await h.SetFeatureRingAsync("welcome-email", Ring.Broad);
        var pid = await h.SeedParticipantAsync("att@ring2.test", Ring.Ring2, ParticipantRole.Attendee);
        var outcome = new EmailDeliveryOutcome();

        var raisedSender = h.NewSender(attendeeWelcomeCap: "Ring2");
        var raisedSvc = new AttendeeOneDayWelcomeEmailService(
            h.Db, Harness.Templates(), raisedSender, new FixedClock(Now),
            context: h.Context, outcome: outcome);

        Assert.False(await raisedSvc.SendForProvisioningAsync(pid));
        Assert.Empty(raisedSender.Captured);
        Assert.Null((await h.Db.Participants.FindAsync(pid))!.WelcomeWithLoginSentAt);
    }

    // ---- Fix 1: ReminderEngine (SentReminder ledger) ---------------------------

    [Fact]
    public async Task Ring_dropped_reminder_is_not_ledgered_and_is_retried_on_a_later_run()
    {
        await using var h = await Harness.CreateAsync(outboundRing: Ring.Ring1);
        // §707.6 — the reminder's audience is now its OWN (mail × role) ring, not "reminder-jobs".
        // §707.1 gave every shipped builder a MailKey, so a realistic ReminderMessage carries one;
        // without it the send has no mail identity at all and now fails closed by design.
        await h.SetMailRingAsync("task-deadline-reminder", Ring.Ring1);
        var pid = await h.SeedParticipantAsync("rem@ring2.test", Ring.Ring2, ParticipantRole.Speaker);
        var outcome = new EmailDeliveryOutcome();

        var engine = new ReminderEngine(
            h.Db, h.Sender, new FixedClock(Now),
            emailContext: h.Context, pacer: null, outcome: outcome);
        var due = new[]
        {
            new ReminderMessage(
                "rem@ring2.test", "task-due", "occ-1", "Due", "<p>due</p>",
                ParticipantId: pid, MailKey: "task-deadline-reminder"),
        };

        // OUT of ring ⇒ dropped, NOT ledgered — the next run must retry it.
        Assert.Equal(0, await engine.SendDueAsync(h.EventId, due));
        Assert.Empty(h.Sent);
        Assert.Equal(0, await h.Db.SentReminders.CountAsync());

        // Rings widen ⇒ the SAME reminder now delivers and is ledgered exactly once.
        await h.SetFeatureRingAsync(FeatureCatalog.OutboundEmailKey, Ring.Ring2);
        await h.SetMailRingAsync("task-deadline-reminder", Ring.Ring2);

        Assert.Equal(1, await engine.SendDueAsync(h.EventId, due));
        Assert.Single(h.Sent);
        Assert.Equal(1, await h.Db.SentReminders.CountAsync());

        // And it stays idempotent: a re-run sends nothing new.
        Assert.Equal(0, await engine.SendDueAsync(h.EventId, due));
        Assert.Single(h.Sent);
    }

    // ---- Fix 1: LoggingEmailSender records drops as drops ----------------------

    [Fact]
    public async Task Logging_decorator_records_ring_drop_as_failure_and_delivery_as_success()
    {
        await using var h = await Harness.CreateAsync(outboundRing: Ring.Ring1);
        await h.SeedParticipantAsync("in@ring1.test", Ring.Ring1, ParticipantRole.Speaker);
        await h.SeedParticipantAsync("out@ring2.test", Ring.Ring2, ParticipantRole.Speaker);

        var logging = new LoggingEmailSender(
            h.Sender, h.ScopeFactory, h.Context,
            Options.Create(new EmailOptions()), new FixedClock(Now),
            log: null, outcome: EmailDeliveryOutcome.Detached());

        using (h.Context.Set(new EmailContext("test", h.EventId)))
        {
            await logging.SendAsync("out@ring2.test", "Dropped", "<p>x</p>");
            await logging.SendAsync("in@ring1.test", "Sent", "<p>x</p>");
        }

        var logs = await h.Db.EmailLogs.OrderBy(l => l.Id).ToListAsync();
        Assert.Equal(2, logs.Count);

        Assert.False(logs[0].Success);                       // the DROP is a drop …
        Assert.Contains("Ring-dropped", logs[0].Error);      // … with the reason
        Assert.True(logs[1].Success);                        // the delivery is a success
        Assert.Null(logs[1].Error);
        Assert.Single(h.Sent);                               // only ONE mail really left
    }

    // ---- harness ---------------------------------------------------------------

    private sealed class Harness : IAsyncDisposable
    {
        public required CapturingSender Sender { get; set; }
        public required ConcurrentQueue<string> LogQueue { get; init; }
        public required ServiceProvider Provider { get; init; }
        public required IEmailContextAccessor Context { get; init; }
        public required CommunityHubDbContext Db { get; init; }
        public required int EventId { get; init; }
        public required IOptions<EmailOptions> SenderOptions { get; init; }

        public IServiceScopeFactory ScopeFactory =>
            Provider.GetRequiredService<IServiceScopeFactory>();
        public IReadOnlyList<string> Logs => LogQueue.ToArray();
        public IReadOnlyList<MailMessage> Sent => Sender.Captured;

        public static async Task<Harness> CreateAsync(
            Ring outboundRing,
            bool killSwitch = false,
            string redirectAllTo = "")
        {
            var dbName = $"outcome-ledger-{Guid.NewGuid():N}";
            var services = new ServiceCollection();
            services.AddDbContext<CommunityHubDbContext>(o => o.UseInMemoryDatabase(dbName));
            services.AddScoped<RingResolver>();
            services.AddScoped<FeatureGateService>();
            var provider = services.BuildServiceProvider();

            // A long-lived context ON THE SAME in-memory store for the services +
            // assertions (the sender resolves its own scoped contexts per send).
            var db = new CommunityHubDbContext(
                new DbContextOptionsBuilder<CommunityHubDbContext>()
                    .UseInMemoryDatabase(dbName).Options);

            var ev = new Event
            {
                CommunityName = "Test Community", DisplayName = "Test Community 2027",
                Code = "TC27", IsActive = true,
                StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            };
            db.Events.Add(ev);
            await db.SaveChangesAsync();

            var logQueue = new ConcurrentQueue<string>();
            var options = Options.Create(new EmailOptions
            {
                KillSwitch = killSwitch,
                RedirectAllTo = redirectAllTo,
                SmtpHost = "smtp.invalid.localhost",
                // §516: these tests are about the LEDGER (gated ⇒ no row / no stamp, resendable
                // once rings widen), not about the welcome cap. That cap now defaults to Ring1,
                // which would hold the "operator widens to Ring2 ⇒ it delivers" phase and fail
                // these for an unrelated reason. Opened here so the ledger is what is proven; the
                // cap has its own suite (WelcomeEmailReleaseCapTests).
                WelcomeMaxReleaseRing = "Broad",
            });
            var harness = new Harness
            {
                Sender = null!,
                LogQueue = logQueue,
                Provider = provider,
                Context = new EmailContextAccessor(),
                Db = db,
                EventId = ev.Id,
                SenderOptions = options,
            };
            harness.Sender = harness.NewSender();
            await harness.SetFeatureRingAsync(FeatureCatalog.OutboundEmailKey, outboundRing);
            return harness;
        }

        /// <summary>A fresh ring-gating capturing sender (optionally with a new §217
        /// attendee-welcome cap) — the transport records into the ambient outcome.</summary>
        public CapturingSender NewSender(string? attendeeWelcomeCap = null)
        {
            var o = SenderOptions.Value;
            var options = attendeeWelcomeCap is null
                ? SenderOptions
                : Options.Create(new EmailOptions
                {
                    KillSwitch = o.KillSwitch,
                    RedirectAllTo = o.RedirectAllTo,
                    SmtpHost = o.SmtpHost,
                    AttendeeWelcomeMaxReleaseRing = attendeeWelcomeCap,
                    // §516: carry the opened welcome cap across, or a sender built for an
                    // ATTENDEE-cap test would silently re-impose the Ring1 welcome cap.
                    WelcomeMaxReleaseRing = o.WelcomeMaxReleaseRing,
                });
            return new CapturingSender(
                options, ScopeFactory, Context,
                new CapturingLogger<BrevoEmailSender>(LogQueue),
                EmailDeliveryOutcome.Detached());
        }

        public async Task SetFeatureRingAsync(string featureKey, Ring ring)
        {
            using var scope = Provider.CreateScope();
            var scopedDb = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
            var settings = new FeatureSettingsService(scopedDb, new FixedClock(Now));
            await settings.SetReleasedRingAsync(EventId, featureKey, ring, null);
        }

        /// <summary>
        /// §707.6 — set a MAIL's own ring. Since the 17 email feature rings were removed this is the
        /// ONLY thing that decides a mail's audience (under the outbound-email ceiling), so a test
        /// that wants to move recipients must move this, not a feature ring.
        /// </summary>
        public async Task SetMailRingAsync(string templateKey, Ring ring)
        {
            using var scope = Provider.CreateScope();
            var sp = scope.ServiceProvider;
            var scopedDb = sp.GetRequiredService<CommunityHubDbContext>();
            var svc = new EmailTemplateRingService(
                scopedDb, new FeatureGateService(scopedDb), new FixedClock(Now));
            await svc.SetRingAsync(EventId, templateKey, ring, "test@harness");
        }

        public async Task<int> SeedParticipantAsync(string email, Ring ring, ParticipantRole role)
        {
            var p = new Participant
            {
                EventId = EventId, Email = email.ToLowerInvariant(),
                FullName = "Test Person", Role = role, Ring = ring,
                IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
            };
            Db.Participants.Add(p);
            await Db.SaveChangesAsync();
            return p.Id;
        }

        public static EmailTemplateProvider Templates() =>
            new(Options.Create(new EmailTemplateOptions
            {
                TemplateDirectory = Scenario.RepoPaths.EmailTemplates(),
                PrivateTemplateDirectory = Path.Combine(Path.GetTempPath(), "ceh-no-private-templates"),
                HubUrl = "https://hub.example.test",
            }));

        public ValueTask DisposeAsync()
        {
            Db.Dispose();
            Provider.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingSender : BrevoEmailSender
    {
        private readonly List<MailMessage> _captured = new();
        public IReadOnlyList<MailMessage> Captured => _captured;

        public CapturingSender(
            IOptions<EmailOptions> o, IServiceScopeFactory s, IEmailContextAccessor e,
            ILogger<BrevoEmailSender> l, IEmailDeliveryOutcome outcome)
            : base(o, s, e, l, outcome) { }

        protected override Task DispatchAsync(MailMessage message, CancellationToken ct)
        {
            var snap = new MailMessage();
            foreach (var a in message.To) snap.To.Add(a);
            foreach (var a in message.CC) snap.CC.Add(a);
            foreach (var a in message.Bcc) snap.Bcc.Add(a);
            _captured.Add(snap);
            return Task.CompletedTask;
        }
    }

    private sealed class StubEnv : IEnvironmentInfo
    {
        public StubEnv(bool isDev, string name) { IsDevelopment = isDev; EnvironmentName = name; }
        public bool IsDevelopment { get; }
        public string EnvironmentName { get; }
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _sink;
        public CapturingLogger(ConcurrentQueue<string> sink) => _sink = sink;
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _sink.Enqueue(formatter(state, exception));
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
