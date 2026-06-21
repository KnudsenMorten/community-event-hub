using System.Collections.Concurrent;
using System.Net.Mail;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// OPERATOR BCC AT THE SENDER. Proves <see cref="EmailOptions.BccAllTo"/> is wired
/// so the operator gets a silent copy of EVERY mail that ACTUALLY SENT — and ONLY
/// those (rings-only model, operator 2026-06-19 — the allowlist was removed):
///   • a mail that passes the ring gate carries the bcc (all overloads: plain,
///     multipart, ics);
///   • a ring-dropped mail produces NO send at all, so no bcc copy goes out;
///   • a kill-switched mail produces NO send, so no bcc copy goes out;
///   • with <c>BccAllTo</c> empty (DEV / test default) the sent message has NO bcc —
///     behaviour is unchanged.
///
/// We observe the EXACT message that would hit the wire by overriding the sender's
/// dispatch tail (the single point every path funnels through AFTER all gating),
/// so nothing touches the network.
/// </summary>
public sealed class BrevoEmailSenderBccTests
{
    private const int EventId = 7;
    private const string Operator = "operator@expertslive.dk";

    [Fact]
    public async Task Bcc_added_on_a_sent_mail_all_overloads()
    {
        await using var h = await Harness.CreateAsync(Ring.Broad, bccAllTo: Operator);
        await h.SeedSpeakerAsync("ok@allowed.test", Ring.Ring0); // passes the ring gate

        await h.Sender.SendAsync("ok@allowed.test", "S", "<p>h</p>");                    // plain
        await h.Sender.SendAsync("ok@allowed.test", "S", "<p>h</p>", "text");            // multipart
        await h.Sender.SendWithIcsAsync("ok@allowed.test", "S", "<p>h</p>", "ICS", "x.ics");

        Assert.Equal(3, h.Sent.Count);
        foreach (var msg in h.Sent)
        {
            Assert.Contains(msg.Bcc, a => string.Equals(a.Address, Operator,
                StringComparison.OrdinalIgnoreCase));
            // The intended recipient is still the real To (bcc is additive).
            Assert.Contains(msg.To, a => string.Equals(a.Address, "ok@allowed.test",
                StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task No_bcc_send_when_the_mail_is_ring_dropped()
    {
        // Email released to Ring1, recipient is broad ⇒ ring-dropped, so the bcc
        // never fires because NOTHING is sent.
        await using var h = await Harness.CreateAsync(Ring.Ring1, bccAllTo: Operator);
        await h.SeedSponsorAsync("broadco@broad.test", Ring.Broad);

        await h.Sender.SendAsync("broadco@broad.test", "Hi", "<p>hi</p>");

        Assert.Empty(h.Sent); // no dispatch ⇒ no bcc copy
        Assert.Contains(h.Logs, l => l.Contains("RING-DROP"));
    }

    [Fact]
    public async Task No_bcc_send_when_the_mail_is_kill_switched()
    {
        // Passes the ring gate but the global kill switch is on ⇒ dropped before
        // dispatch, so the operator gets NO copy of a mail that never went out.
        await using var h = await Harness.CreateAsync(
            Ring.Broad, bccAllTo: Operator, killSwitch: true);
        await h.SeedSpeakerAsync("ring0@in.test", Ring.Ring0);

        await h.Sender.SendAsync("ring0@in.test", "Hi", "<p>hi</p>");

        Assert.Empty(h.Sent);
        Assert.Contains(h.Logs, l => l.Contains("KILL SWITCH"));
    }

    [Fact]
    public async Task No_bcc_when_BccAllTo_is_empty_behaviour_unchanged()
    {
        await using var h = await Harness.CreateAsync(Ring.Broad, bccAllTo: string.Empty);
        await h.SeedSpeakerAsync("ok@allowed.test", Ring.Ring0);

        await h.Sender.SendAsync("ok@allowed.test", "Hi", "<p>hi</p>");

        var msg = Assert.Single(h.Sent);
        Assert.Empty(msg.Bcc); // no operator bcc when the option is empty
    }

    [Fact]
    public async Task Operator_bcc_not_doubled_when_also_the_recipient()
    {
        // De-dup: operator is the To AND the bcc target ⇒ bcc is not re-added.
        await using var h = await Harness.CreateAsync(Ring.Broad, bccAllTo: Operator);
        await h.SeedSpeakerAsync(Operator, Ring.Ring0);

        await h.Sender.SendAsync(Operator, "Hi", "<p>hi</p>");

        var msg = Assert.Single(h.Sent);
        Assert.Empty(msg.Bcc);
        Assert.Contains(msg.To, a => string.Equals(a.Address, Operator,
            StringComparison.OrdinalIgnoreCase));
    }

    // ---- test harness -------------------------------------------------------

    private sealed class Harness : IAsyncDisposable
    {
        public required CapturingSender Sender { get; init; }
        public required ConcurrentQueue<string> LogQueue { get; init; }
        public required ServiceProvider Provider { get; init; }

        public IReadOnlyList<string> Logs => LogQueue.ToArray();
        public IReadOnlyList<MailMessage> Sent => Sender.Captured;

        public static async Task<Harness> CreateAsync(
            Ring emailReleaseRing, string bccAllTo, bool killSwitch = false)
        {
            var dbName = $"sender-bcc-{Guid.NewGuid():N}";
            var services = new ServiceCollection();
            services.AddDbContext<CommunityHubDbContext>(o => o.UseInMemoryDatabase(dbName));
            services.AddScoped<RingResolver>();
            services.AddScoped<FeatureGateService>();
            var provider = services.BuildServiceProvider();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
                var settings = new FeatureSettingsService(
                    db, new FixedClock(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));
                await settings.SetReleasedRingAsync(
                    EventId, FeatureCatalog.OutboundEmailKey, emailReleaseRing, null);
            }

            var logQueue = new ConcurrentQueue<string>();
            var logger = new CapturingLogger<BrevoEmailSender>(logQueue);
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            var ctx = new FixedEmailContext(new EmailContext(Category: "test", EventId: EventId));

            var options = Options.Create(new EmailOptions
            {
                BccAllTo = bccAllTo,
                KillSwitch = killSwitch,
                SmtpHost = "smtp.invalid.localhost",
            });

            var sender = new CapturingSender(options, scopeFactory, ctx, logger);
            return new Harness { Sender = sender, LogQueue = logQueue, Provider = provider };
        }

        public async Task SeedSpeakerAsync(string email, Ring ring)
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
            db.Participants.Add(new Participant
            {
                EventId = EventId, Email = email.ToLowerInvariant(), FullName = "Spk",
                Role = ParticipantRole.Speaker, Ring = ring,
            });
            await db.SaveChangesAsync();
        }

        public async Task SeedSponsorAsync(string email, Ring ring)
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
            db.Participants.Add(new Participant
            {
                EventId = EventId, Email = email.ToLowerInvariant(), FullName = "Sponsor",
                Role = ParticipantRole.Sponsor, SponsorCompanyId = null, Ring = ring,
            });
            await db.SaveChangesAsync();
        }

        public ValueTask DisposeAsync()
        {
            Provider.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A sender that captures the fully-built, fully-gated message at the dispatch
    /// tail instead of hitting SMTP — so we can assert on To/Cc/Bcc without a relay.
    /// We snapshot the addresses since the base <c>using var message</c> disposes it.
    /// </summary>
    private sealed class CapturingSender : BrevoEmailSender
    {
        private readonly List<MailMessage> _captured = new();
        public IReadOnlyList<MailMessage> Captured => _captured;

        public CapturingSender(
            IOptions<EmailOptions> options,
            IServiceScopeFactory scopes,
            IEmailContextAccessor emailContext,
            ILogger<BrevoEmailSender> log)
            : base(options, scopes, emailContext, log) { }

        protected override Task DispatchAsync(MailMessage message, CancellationToken ct)
        {
            // Snapshot To/Cc/Bcc into a detached message so disposal of the original
            // doesn't clear the collections we assert on after the call returns.
            var snap = new MailMessage();
            foreach (var a in message.To) snap.To.Add(a);
            foreach (var a in message.CC) snap.CC.Add(a);
            foreach (var a in message.Bcc) snap.Bcc.Add(a);
            _captured.Add(snap);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedEmailContext : IEmailContextAccessor
    {
        public FixedEmailContext(EmailContext current) => Current = current;
        public EmailContext? Current { get; }
        public IDisposable Set(EmailContext context) => throw new NotSupportedException();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _sink;
        public CapturingLogger(ConcurrentQueue<string> sink) => _sink = sink;
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _sink.Enqueue(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
