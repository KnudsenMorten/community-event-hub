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
/// RINGS ARE THE SOLE EMAIL AUDIENCE CONTROL (REQUIREMENTS §23). The static
/// OnlySendTo allowlist was removed; the ring gate inside <see cref="BrevoEmailSender"/>
/// (the one chokepoint every send path funnels through) decides send vs drop:
///   • a KNOWN in-ring participant SENDS (no allowlist needed);
///   • a KNOWN out-of-ring participant is ring-dropped;
///   • an UNKNOWN / non-participant address FAILS CLOSED (dropped);
///   • no resolvable active edition FAILS CLOSED (dropped);
///   • the email-release ring is READ from the outbound-email feature, then capped
///     by the optional Email:MaxReleaseRing ceiling (DEV safety);
///   • the global kill switch still hard-stops everything.
/// A capturing dispatch tail lets us assert sent-vs-dropped without touching SMTP.
/// </summary>
public sealed class BrevoEmailSenderRingGateTests
{
    private const int EventId = 7;

    // ---- pure ring-drop rule -----------------------------------------------

    [Fact]
    public void IsRingDropped_drops_known_recipient_outside_release_ring()
    {
        Assert.False(BrevoEmailSender.IsRingDropped(found: true, Ring.Ring0, Ring.Ring1));
        Assert.False(BrevoEmailSender.IsRingDropped(found: true, Ring.Ring1, Ring.Ring1));
        Assert.True(BrevoEmailSender.IsRingDropped(found: true, Ring.Ring2, Ring.Ring1));
        Assert.True(BrevoEmailSender.IsRingDropped(found: true, Ring.Broad, Ring.Ring1));
    }

    [Fact]
    public void IsRingDropped_pure_helper_returns_false_for_unknown()
    {
        // The PURE helper returns false for found=false; the SENDER fail-closes an
        // unknown address itself (covered by the DB-backed test below).
        Assert.False(BrevoEmailSender.IsRingDropped(found: false, Ring.Broad, Ring.Ring1));
    }

    // ---- §326au: the MASTER SWITCH actually stops mail ---------------------
    // The 2026-07-25 audit found FeatureGateService.IsOutboundEmailEnabledAsync had ZERO
    // production callers: the transport read only the released RING, so the "Outbound
    // email" toggle in /Organizer/Settings was decorative. These pin it for good.

    [Fact]
    public async Task Outbound_email_switched_OFF_stops_an_otherwise_perfect_send()
    {
        await using var h = await Harness.CreateAsync(Ring.Broad, outboundEnabled: false);
        await h.SeedSpeakerAsync("ring0@in.test", Ring.Ring0);   // in-ring, known, would send

        await h.Sender.SendAsync("ring0@in.test", "Hi", "<p>hi</p>");

        Assert.Empty(h.Sent);
        Assert.Contains(h.Logs, l => l.Contains("switched OFF"));
    }

    [Fact]
    public async Task Outbound_email_defaults_ON_so_the_switch_never_silences_a_fresh_edition()
    {
        await using var h = await Harness.CreateAsync(Ring.Broad);   // nothing persisted
        await h.SeedSpeakerAsync("ring0@in.test", Ring.Ring0);

        await h.Sender.SendAsync("ring0@in.test", "Hi", "<p>hi</p>");

        Assert.Single(h.Sent);
    }

    // ---- §326av: the per-hour ceiling --------------------------------------

    [Fact]
    public async Task Sends_are_declined_once_the_hourly_ceiling_is_reached()
    {
        // 5 already sent this hour, ceiling 5 ⇒ the next one is refused.
        await using var h = await Harness.CreateAsync(
            Ring.Broad, maxSendsPerHour: 5, seedSentLastHour: 5);
        await h.SeedSpeakerAsync("ring0@in.test", Ring.Ring0);

        await h.Sender.SendAsync("ring0@in.test", "Hi", "<p>hi</p>");

        Assert.Empty(h.Sent);
        Assert.Contains(h.Logs, l => l.Contains("CEILING HIT"));
    }

    [Fact]
    public async Task Under_the_ceiling_sends_normally()
    {
        await using var h = await Harness.CreateAsync(
            Ring.Broad, maxSendsPerHour: 5, seedSentLastHour: 4);
        await h.SeedSpeakerAsync("ring0@in.test", Ring.Ring0);

        await h.Sender.SendAsync("ring0@in.test", "Hi", "<p>hi</p>");

        Assert.Single(h.Sent);
    }

    [Fact]
    public async Task Interactive_ring_exempt_mail_ignores_the_ceiling()
    {
        // Nobody may be locked out of the hub because a welcome run is in progress:
        // PIN sign-in and engine alerts are RingExempt and must always get through.
        await using var h = await Harness.CreateAsync(
            Ring.Broad, ringExempt: true, maxSendsPerHour: 5, seedSentLastHour: 500);
        await h.SeedSpeakerAsync("ring0@in.test", Ring.Ring0);

        await h.Sender.SendAsync("ring0@in.test", "Your code", "<p>123456</p>");

        Assert.Single(h.Sent);
        Assert.DoesNotContain(h.Logs, l => l.Contains("CEILING HIT"));
    }

    [Fact]
    public async Task A_ceiling_of_zero_disables_the_cap()
    {
        await using var h = await Harness.CreateAsync(
            Ring.Broad, maxSendsPerHour: 0, seedSentLastHour: 5000);
        await h.SeedSpeakerAsync("ring0@in.test", Ring.Ring0);

        await h.Sender.SendAsync("ring0@in.test", "Hi", "<p>hi</p>");

        Assert.Single(h.Sent);
    }

    // ---- DB-backed sender gate (rings only) --------------------------------

    [Fact]
    public async Task Ring1_and_ring0_recipients_send_no_allowlist_needed()
    {
        await using var h = await Harness.CreateAsync(Ring.Ring1);
        await h.SeedSpeakerAsync("ring1@in.test", Ring.Ring1);
        await h.SeedSpeakerAsync("ring0@in.test", Ring.Ring0);

        await h.Sender.SendAsync("ring1@in.test", "Hi", "<p>hi</p>");
        await h.Sender.SendAsync("ring0@in.test", "Hi", "<p>hi</p>");

        Assert.Equal(2, h.Sent.Count);
        Assert.DoesNotContain(h.Logs, l => l.Contains("RING-DROP"));
    }

    [Fact]
    public async Task Ring3_sponsor_is_dropped_at_the_sender_even_called_directly()
    {
        await using var h = await Harness.CreateAsync(Ring.Ring1);
        await h.SeedSponsorAsync("broadco@broad.test", Ring.Broad);

        await h.Sender.SendAsync("broadco@broad.test", "Hi", "<p>hi</p>");

        Assert.Empty(h.Sent);
        Assert.Contains(h.Logs, l => l.Contains("RING-DROP") && l.Contains("broadco@broad.test"));
    }

    [Fact]
    public async Task Ring3_speaker_dropped_on_multipart_and_ics_paths_too()
    {
        await using var h = await Harness.CreateAsync(Ring.Ring1);
        await h.SeedSpeakerAsync("spk@broad.test", Ring.Broad);

        await h.Sender.SendAsync("spk@broad.test", "S", "<p>h</p>", "text");
        await h.Sender.SendWithIcsAsync("spk@broad.test", "S", "<p>h</p>", "ICS", "x.ics");

        Assert.Empty(h.Sent);
        Assert.Equal(2, h.Logs.Count(l => l.Contains("RING-DROP") && l.Contains("spk@broad.test")));
    }

    [Fact]
    public async Task Unknown_address_fails_closed()
    {
        await using var h = await Harness.CreateAsync(Ring.Ring1);
        await h.Sender.SendAsync("stranger@nowhere.test", "Hi", "<p>hi</p>");

        Assert.Empty(h.Sent);
        Assert.Contains(h.Logs, l => l.Contains("RING-DROP") && l.Contains("unknown"));
    }

    [Fact]
    public async Task No_context_falls_back_to_active_edition_and_sends_in_ring()
    {
        // Broadcast / PIN set no EmailContext.EventId; the sender falls back to the
        // single active edition and still ring-gates -> a ring1 participant sends.
        await using var h = await Harness.CreateAsync(
            Ring.Ring1, contextEventId: 0, seedActiveEdition: true);
        await h.SeedSpeakerAsync("ring1@in.test", Ring.Ring1);

        await h.Sender.SendAsync("ring1@in.test", "Hi", "<p>hi</p>");

        Assert.Single(h.Sent);
        Assert.DoesNotContain(h.Logs, l => l.Contains("RING-DROP"));
    }

    [Fact]
    public async Task No_context_and_no_active_edition_fails_closed()
    {
        await using var h = await Harness.CreateAsync(
            Ring.Ring1, contextEventId: 0, seedActiveEdition: false);
        await h.SeedSpeakerAsync("ring1@in.test", Ring.Ring1);

        await h.Sender.SendAsync("ring1@in.test", "Hi", "<p>hi</p>");

        Assert.Empty(h.Sent);
        Assert.Contains(h.Logs, l => l.Contains("RING-DROP") && l.Contains("no active edition"));
    }

    [Fact]
    public async Task Email_release_ring_is_read_from_the_feature_not_hardcoded()
    {
        // Same ring-broad recipient: dropped at email@Ring1, sent at email@Broad.
        await using var dropped = await Harness.CreateAsync(Ring.Ring1);
        await dropped.SeedSpeakerAsync("u@broad.test", Ring.Broad);
        await dropped.Sender.SendAsync("u@broad.test", "Hi", "<p>hi</p>");
        Assert.Empty(dropped.Sent);

        await using var allowed = await Harness.CreateAsync(Ring.Broad);
        await allowed.SeedSpeakerAsync("u@broad.test", Ring.Broad);
        await allowed.Sender.SendAsync("u@broad.test", "Hi", "<p>hi</p>");
        Assert.Single(allowed.Sent);
    }

    [Fact]
    public async Task MaxReleaseRing_ceiling_caps_the_audience_below_the_feature_ring()
    {
        // Feature released to Broad, but the DEV ceiling Ring1 caps it: a ring2
        // recipient is dropped even though the feature itself is broad.
        await using var h = await Harness.CreateAsync(Ring.Broad, maxReleaseRing: "Ring1");
        await h.SeedSpeakerAsync("ring2@in.test", Ring.Ring2);
        await h.SeedSpeakerAsync("ring1@in.test", Ring.Ring1);

        await h.Sender.SendAsync("ring2@in.test", "Hi", "<p>hi</p>");   // capped out
        await h.Sender.SendAsync("ring1@in.test", "Hi", "<p>hi</p>");   // within ceiling

        Assert.Single(h.Sent);
        Assert.Equal("ring1@in.test", h.Sent[0].To[0].Address);
        Assert.Contains(h.Logs, l => l.Contains("RING-DROP") && l.Contains("ring2@in.test"));
    }

    /// <summary>
    /// 🔒 §707.6 — THE 17 EMAIL FEATURE RINGS ARE GONE, and this is the test that pins it.
    /// </summary>
    /// <remarks>
    /// This REPLACES <c>Per_feature_ring_tightens_below_the_transport_ring</c>, which asserted the
    /// exact opposite and was correct until the operator asked for the feature rings to go
    /// (2026-07-29: *"i want INDIVIDUAL EMAIL RING GATES - one for each ! and remove features gates
    /// relevant for emails"*). The coverage is INVERTED rather than deleted, so the removal cannot be
    /// quietly undone by a future change.
    ///
    /// <para>A send naming a ring-scoped FEATURE but carrying no registered mail identity now FAILS
    /// CLOSED instead of inheriting that feature's ring. Falling through to the outbound-email
    /// ceiling would widen it to everyone — the §699.2 hazard.</para>
    /// </remarks>
    [Fact]
    public async Task A_feature_ring_no_longer_governs_a_send_and_an_unidentified_mail_fails_closed()
    {
        // Transport is Broad and the feature is released to Ring1. Under the OLD model the feature
        // ring tightened to Ring1 and the ring1 recipient WOULD have received this.
        await using var h = await Harness.CreateAsync(
            Ring.Broad, featureKey: "welcome-email", featureRing: Ring.Ring1);
        await h.SeedSpeakerAsync("broad@in.test", Ring.Broad);
        await h.SeedSpeakerAsync("ring1@in.test", Ring.Ring1);

        await h.Sender.SendAsync("broad@in.test", "Hi", "<p>hi</p>");
        await h.Sender.SendAsync("ring1@in.test", "Hi", "<p>hi</p>");

        // NEITHER sends: the feature ring supplies no audience any more, and with no registered
        // TemplateName there is no mail ring either — so the send fails closed, audibly.
        Assert.Empty(h.Sent);
        Assert.Contains(
            h.Logs,
            l => l.Contains("no registered mail identity") && l.Contains("welcome-email"));
    }

    [Fact]
    public async Task Per_feature_ring_never_loosens_the_transport_ring()
    {
        // Transport Ring1, feature Broad -> still capped at Ring1 (a feature ring can
        // only TIGHTEN, never widen, the transport gate). A Broad recipient is dropped.
        await using var h = await Harness.CreateAsync(
            Ring.Ring1, featureKey: "welcome-email", featureRing: Ring.Broad);
        await h.SeedSpeakerAsync("broad@in.test", Ring.Broad);

        await h.Sender.SendAsync("broad@in.test", "Hi", "<p>hi</p>");

        Assert.Empty(h.Sent);
        Assert.Contains(h.Logs, l => l.Contains("RING-DROP") && l.Contains("broad@in.test"));
    }

    [Fact]
    public async Task Sign_in_exempt_email_sends_to_a_broad_recipient_even_at_ring1()
    {
        // The on-demand sign-in/PIN email is RingExempt — it must reach a Broad user
        // even though outbound-email is Ring1 (would otherwise be dropped). This is the
        // "sign-in always sends" exception (operator 2026-06-22).
        await using var h = await Harness.CreateAsync(Ring.Ring1, ringExempt: true);
        await h.SeedSpeakerAsync("broad@in.test", Ring.Broad);

        await h.Sender.SendAsync("broad@in.test", "Your sign-in code", "<p>123456</p>");

        Assert.Single(h.Sent);
        Assert.DoesNotContain(h.Logs, l => l.Contains("RING-DROP"));
    }

    [Fact]
    public async Task Sign_in_exempt_still_honours_the_global_kill_switch()
    {
        // Exempt from RING gating, but the master kill switch still hard-stops.
        await using var h = await Harness.CreateAsync(Ring.Ring1, killSwitch: true, ringExempt: true);
        await h.SeedSpeakerAsync("broad@in.test", Ring.Broad);

        await h.Sender.SendAsync("broad@in.test", "Your sign-in code", "<p>123456</p>");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task Kill_switch_hard_stops_even_an_in_ring_recipient()
    {
        await using var h = await Harness.CreateAsync(Ring.Ring1, killSwitch: true);
        await h.SeedSpeakerAsync("ring1@in.test", Ring.Ring1);

        await h.Sender.SendAsync("ring1@in.test", "Hi", "<p>hi</p>");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task Resolve_error_fails_closed()
    {
        await using var h = await Harness.CreateAsync(Ring.Ring1);
        await h.SeedSpeakerAsync("x@in.test", Ring.Ring0);
        h.BreakScopeFactory();

        await h.Sender.SendAsync("x@in.test", "Hi", "<p>hi</p>");

        Assert.Empty(h.Sent);
        Assert.Contains(h.Logs, l => l.Contains("RING-DROP") && l.Contains("fail-closed"));
    }

    // ---- harness ------------------------------------------------------------

    private sealed class Harness : IAsyncDisposable
    {
        public required CapturingSender Sender { get; init; }
        public required ConcurrentQueue<string> LogQueue { get; init; }
        public required ServiceProvider Provider { get; init; }
        public required BreakableScopeFactory ScopeFactory { get; init; }

        public IReadOnlyList<string> Logs => LogQueue.ToArray();
        public IReadOnlyList<MailMessage> Sent => Sender.Captured;

        public static async Task<Harness> CreateAsync(
            Ring emailReleaseRing, string? maxReleaseRing = null, bool killSwitch = false,
            int contextEventId = EventId, bool seedActiveEdition = false,
            string? featureKey = null, Ring featureRing = Ring.Broad, bool ringExempt = false,
            // §326au/§326av
            bool outboundEnabled = true, int maxSendsPerHour = 600, int seedSentLastHour = 0)
        {
            var dbName = $"sender-rings-{Guid.NewGuid():N}";
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
                // Optionally release the TRIGGERING feature to its own ring so the
                // per-feature gate can be exercised independently of the transport ring.
                if (featureKey is not null)
                    await settings.SetReleasedRingAsync(EventId, featureKey, featureRing, null);
                // §326au: the persisted MASTER switch (catalog default is ON).
                if (!outboundEnabled)
                    await settings.SetEnabledAsync(
                        EventId, FeatureCatalog.OutboundEmailKey, false, null);
            }
            // §326av: pretend this many sends already went out in the last hour.
            if (seedSentLastHour > 0)
            {
                using var scope = provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
                for (var i = 0; i < seedSentLastHour; i++)
                {
                    db.EmailLogs.Add(new EmailLog
                    {
                        EventId = EventId, ToEmail = $"prior{i}@in.test",
                        ActualToEmail = $"prior{i}@in.test", Subject = "prior",
                        Success = true, SentAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                    });
                }
                await db.SaveChangesAsync();
            }
            if (seedActiveEdition)
            {
                using var scope = provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
                db.Events.Add(new Event { Id = EventId, Code = "TEST", IsActive = true });
                await db.SaveChangesAsync();
            }

            var logQueue = new ConcurrentQueue<string>();
            var logger = new CapturingLogger<BrevoEmailSender>(logQueue);
            var scopeFactory = new BreakableScopeFactory(provider.GetRequiredService<IServiceScopeFactory>());
            var ctx = new FixedEmailContext(new EmailContext(Category: "test", EventId: contextEventId, FeatureKey: featureKey, RingExempt: ringExempt));
            var options = Options.Create(new EmailOptions
            {
                MaxReleaseRing = maxReleaseRing ?? string.Empty,
                KillSwitch = killSwitch,
                SmtpHost = "smtp.invalid.localhost",
                MaxSendsPerHour = maxSendsPerHour,
            });
            var sender = new CapturingSender(options, scopeFactory, ctx, logger);
            return new Harness { Sender = sender, LogQueue = logQueue, Provider = provider, ScopeFactory = scopeFactory };
        }

        public void BreakScopeFactory() => ScopeFactory.Break();

        public async Task SeedSpeakerAsync(string email, Ring ring) => await Seed(email, ring, ParticipantRole.Speaker, null);
        public async Task SeedSponsorAsync(string email, Ring ring) => await Seed(email, ring, ParticipantRole.Sponsor, null);

        private async Task Seed(string email, Ring ring, ParticipantRole role, string? companyId)
        {
            using var scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
            db.Participants.Add(new Participant
            {
                EventId = EventId, Email = email.ToLowerInvariant(), FullName = "Test",
                Role = role, Ring = ring, SponsorCompanyId = companyId,
            });
            await db.SaveChangesAsync();
        }

        public ValueTask DisposeAsync() { Provider.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class CapturingSender : BrevoEmailSender
    {
        private readonly List<MailMessage> _captured = new();
        public IReadOnlyList<MailMessage> Captured => _captured;
        public CapturingSender(IOptions<EmailOptions> o, IServiceScopeFactory s, IEmailContextAccessor e, ILogger<BrevoEmailSender> l) : base(o, s, e, l) { }
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

    private sealed class BreakableScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner; private bool _broken;
        public BreakableScopeFactory(IServiceScopeFactory inner) => _inner = inner;
        public void Break() => _broken = true;
        public IServiceScope CreateScope() => _broken ? throw new InvalidOperationException("scope broken (test)") : _inner.CreateScope();
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
        public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _sink.Enqueue(formatter(state, exception));
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
}
