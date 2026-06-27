using CommunityHub.Core.Assistant;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The AiHelper INTAKE service (REQUIREMENTS §137). Offline. Proves:
///   • a bug/feature report is CAPTURED to the feed (with the right Kind + server-resolved
///     identity) AND emailed to the dev mailbox (mok@…);
///   • an explicit organizer question is captured (Kind=Question) AND emailed to the
///     organizers (info@…);
///   • an ordinary question trips nothing (no capture, no email);
///   • the intake email is RING-EXEMPT (ops/organizer mailbox, never ring-dropped);
///   • the durable capture survives an email failure.
/// </summary>
public sealed class FeedbackIntakeServiceTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"feedback-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-27T09:00:00Z");
    }

    // Captures every send + the Reply-To + the ambient EmailContext that was in scope.
    private sealed class CapturingSender : IEmailSender
    {
        private readonly IEmailContextAccessor _ctx;
        public CapturingSender(IEmailContextAccessor ctx) => _ctx = ctx;

        public readonly List<(string To, string Subject, string Html, EmailReplyTo? ReplyTo, EmailContext? Ctx)> Sent = new();

        public Task SendAsync(string to, string s, string h, CancellationToken ct = default)
        {
            Sent.Add((to, s, h, null, _ctx.Current));
            return Task.CompletedTask;
        }
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        // The intake path the service uses — capture the Reply-To.
        public Task SendAsync(string to, string s, string h, EmailReplyTo? replyTo, CancellationToken ct = default)
        {
            Sent.Add((to, s, h, replyTo, _ctx.Current));
            return Task.CompletedTask;
        }
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string fn, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
    }

    private sealed class ThrowingSender : IEmailSender
    {
        public Task SendAsync(string to, string s, string h, CancellationToken ct = default)
            => throw new InvalidOperationException("smtp down");
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default)
            => throw new InvalidOperationException("smtp down");
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default)
            => throw new InvalidOperationException("smtp down");
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string fn, CancellationToken ct = default)
            => throw new InvalidOperationException("smtp down");
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default)
            => throw new InvalidOperationException("smtp down");
    }

    private static readonly FeedbackIntakeOptions Options = new();

    // The signed-in asker the origin points at (server-resolved name + email).
    private const int AskerEventId = 7;
    private const int AskerId = 42;
    private const string AskerName = "Asker Person";
    private const string AskerEmail = "asker.person@example.test";

    private static (FeedbackIntakeService svc, CapturingSender sender, CommunityHubDbContext db) Build()
    {
        var db = NewDb();
        // Seed the asker so the service can resolve their name + email server-side.
        db.Participants.Add(new Participant
        {
            Id = AskerId, EventId = AskerEventId, FullName = AskerName,
            Email = AskerEmail, Role = ParticipantRole.Speaker,
        });
        db.SaveChanges();
        var ctx = new EmailContextAccessor();
        var sender = new CapturingSender(ctx);
        var svc = new FeedbackIntakeService(
            db, new FeedbackIntakeDetector(Options), Options, sender, ctx, new FixedClock());
        return (svc, sender, db);
    }

    private static readonly FeedbackOrigin Origin = new(EventId: AskerEventId, ParticipantId: AskerId, Role: ParticipantRole.Speaker, PageUrl: "/Speaker/Index");

    [Fact]
    public async Task Bug_report_is_captured_and_emailed_to_the_dev_mailbox()
    {
        var (svc, sender, db) = Build();
        using var _ = db;

        var result = await svc.TryIntakeAsync("there is a bug in the agenda", Origin);

        Assert.True(result.Captured);
        Assert.Equal(FeedbackKind.Bug, result.Kind);

        var item = await db.FeedbackItems.SingleAsync();
        Assert.Equal(FeedbackKind.Bug, item.Kind);
        Assert.Equal(7, item.EventId);
        Assert.Equal(42, item.ParticipantId);                  // server-resolved identity from origin
        Assert.Equal(ParticipantRole.Speaker, item.Role);
        Assert.Equal("/Speaker/Index", item.PageUrl);
        Assert.Equal("mok@expertslive.dk", item.RoutedTo);

        var mail = Assert.Single(sender.Sent);
        Assert.Equal("mok@expertslive.dk", mail.To);           // To stays the dev mailbox
        Assert.Contains("bug", mail.Subject);
        // SUBJECT carries the asker's NAME, and NEVER the page path.
        Assert.Contains(AskerName, mail.Subject);
        Assert.DoesNotContain("/Speaker/Index", mail.Subject);
        // BODY shows the asker's name + email clearly.
        Assert.Contains(AskerName, mail.Html);
        Assert.Contains(AskerEmail, mail.Html);
        // REPLY-TO = the asker, so a "Reply" reaches the person (not info@/the From).
        Assert.NotNull(mail.ReplyTo);
        Assert.Equal(AskerEmail, mail.ReplyTo!.Email);
        Assert.Equal(AskerName, mail.ReplyTo.Name);
        Assert.True(mail.Ctx?.RingExempt);                     // ops mailbox: never ring-dropped
    }

    [Fact]
    public async Task Feature_request_is_captured_with_kind_feature_and_emailed_dev()
    {
        var (svc, sender, db) = Build();
        using var _ = db;

        var result = await svc.TryIntakeAsync("please add a feature for dark mode", Origin);

        Assert.Equal(FeedbackKind.Feature, result.Kind);
        Assert.Equal(FeedbackKind.Feature, (await db.FeedbackItems.SingleAsync()).Kind);
        Assert.Equal("mok@expertslive.dk", Assert.Single(sender.Sent).To);
    }

    [Fact]
    public async Task Ordinary_question_captures_nothing_and_sends_no_mail()
    {
        var (svc, sender, db) = Build();
        using var _ = db;

        var result = await svc.TryIntakeAsync("what time is the keynote?", Origin);

        Assert.False(result.Captured);
        Assert.Null(result.Kind);
        Assert.Equal(0, await db.FeedbackItems.CountAsync());
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Contact_organizers_captures_question_and_emails_the_organizers()
    {
        var (svc, sender, db) = Build();
        using var _ = db;

        var result = await svc.SendToOrganizersAsync("Can a human call me about parking?", Origin);

        Assert.True(result.Captured);
        Assert.Equal(FeedbackKind.Question, result.Kind);

        var item = await db.FeedbackItems.SingleAsync();
        Assert.Equal(FeedbackKind.Question, item.Kind);
        Assert.Equal("info@expertslive.dk", item.RoutedTo);

        var mail = Assert.Single(sender.Sent);
        Assert.Equal("info@expertslive.dk", mail.To);          // organizer mailbox
        // Reply-To = the asker so the organizer replies to the PERSON, not info@.
        Assert.NotNull(mail.ReplyTo);
        Assert.Equal(AskerEmail, mail.ReplyTo!.Email);
        Assert.Contains(AskerName, mail.Html);
        Assert.Contains(AskerEmail, mail.Html);
        Assert.True(mail.Ctx?.RingExempt);
    }

    [Fact]
    public async Task Capture_survives_an_email_failure()
    {
        var db = NewDb();
        using var _ = db;
        var ctx = new EmailContextAccessor();
        var svc = new FeedbackIntakeService(
            db, new FeedbackIntakeDetector(Options), Options, new ThrowingSender(), ctx, new FixedClock());

        var result = await svc.TryIntakeAsync("there is an error here", Origin);

        Assert.True(result.Captured);                          // never throws to the caller
        Assert.Equal(1, await db.FeedbackItems.CountAsync());  // the durable record is the source of truth
    }

    [Fact]
    public async Task Disabled_intake_is_a_noop_for_keyword_path()
    {
        var db = NewDb();
        using var _ = db;
        var ctx = new EmailContextAccessor();
        var sender = new CapturingSender(ctx);
        var opts = new FeedbackIntakeOptions { Enabled = false };
        var svc = new FeedbackIntakeService(db, new FeedbackIntakeDetector(opts), opts, sender, ctx, new FixedClock());

        var result = await svc.TryIntakeAsync("there is a bug", Origin);

        Assert.False(result.Captured);
        Assert.Equal(0, await db.FeedbackItems.CountAsync());
        Assert.Empty(sender.Sent);
    }
}
