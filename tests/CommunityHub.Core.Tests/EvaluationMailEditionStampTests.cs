using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §818 — a speaker's evaluation mail must be logged against the EDITION and the SPEAKER.
/// </summary>
/// <remarks>
/// <para>Both evaluation mail services wrapped ONE <c>EmailContext</c> around the whole recipient
/// loop, on the reasoning that the mail identity is loop-invariant. It is — but the RECIPIENT is
/// not, and neither context named an edition or a participant. <c>LoggingEmailSender</c> writes
/// <c>EventId = ctx?.EventId ?? 0</c>, so eleven real speakers were mailed their evaluation results
/// on 1 Aug 2026 and every row landed on event 0 with no participant: invisible to both organizer
/// views that answer <i>"did she get it"</i>.</para>
///
/// <para>🔒 These tests assert the AMBIENT CONTEXT AT SEND TIME, which is the thing the log row is
/// built from — not the mail body, which was never wrong.</para>
/// </remarks>
public sealed class EvaluationMailEditionStampTests
{
    private static readonly DateTimeOffset Now = new(2027, 3, 5, 9, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"eval-stamp-{Guid.NewGuid():N}")
            .Options);

    /// <summary>Captures the ambient <see cref="EmailContext"/> in force for each send.</summary>
    private sealed class ContextCapturingSender : IEmailSender
    {
        private readonly IEmailContextAccessor _ctx;
        public ContextCapturingSender(IEmailContextAccessor ctx) => _ctx = ctx;

        public readonly List<(string To, EmailContext? Ctx)> Sent = new();

        public Task SendAsync(string to, string s, string h, CancellationToken ct = default)
        {
            Sent.Add((to, _ctx.Current));
            return Task.CompletedTask;
        }
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        public Task SendAsync(string to, string s, string h, EmailReplyTo? replyTo, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string fn, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default)
            => SendAsync(to, s, h, ct);
    }

    private static async Task<Event> SeedEventAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            Code = "EV27", CommunityName = "Eval Stamp", DisplayName = "Eval Stamp 2027",
            StartDate = new DateOnly(2027, 3, 1), EndDate = new DateOnly(2027, 3, 2),
            IsActive = true,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev;
    }

    [Fact]
    public async Task Results_mail_is_stamped_with_the_edition_and_each_speaker()
    {
        using var db = NewDb();
        var ev = await SeedEventAsync(db);

        var one = new Participant
        {
            EventId = ev.Id, Email = "one@example.org", FullName = "One Speaker",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        var two = new Participant
        {
            EventId = ev.Id, Email = "two@example.org", FullName = "Two Speaker",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.AddRange(one, two);
        await db.SaveChangesAsync();

        var session = new Session { EventId = ev.Id, SessionizeId = "sz-1", Title = "Deep dive" };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        db.SessionSpeakers.AddRange(
            new SessionSpeaker { SessionId = session.Id, ParticipantId = one.Id },
            new SessionSpeaker { SessionId = session.Id, ParticipantId = two.Id });
        await db.SaveChangesAsync();

        var ctx = new EmailContextAccessor();
        var sender = new ContextCapturingSender(ctx);
        var svc = new SessionEvaluationMailService(db, sender, new FixedClock(Now), ctx);

        var res = await svc.EmailResultsToSpeakersAsync(session.Id, "Mostly smiles!");
        Assert.True(res.Sent);
        Assert.Equal(2, sender.Sent.Count);

        // 🔒 EACH send carries ITS OWN recipient — the defect was one shared, recipient-less context.
        foreach (var (to, c) in sender.Sent)
        {
            Assert.NotNull(c);
            Assert.Equal(ev.Id, c!.EventId);
            Assert.Equal("session-eval", c.Category);
            // The mail identity is unchanged — the ring it resolves must not move.
            Assert.Equal("session-evaluation-results", c.TemplateName);
            Assert.Equal("session-eval-email", c.FeatureKey);

            var expected = to == "one@example.org" ? one : two;
            Assert.Equal(expected.Id, c.ParticipantId);
            Assert.Equal(expected.FullName, c.RecipientName);
        }
    }

    [Fact]
    public async Task Report_ready_mail_is_stamped_with_the_edition_and_the_speaker()
    {
        using var db = NewDb();
        var ev = await SeedEventAsync(db);

        var speaker = new Participant
        {
            EventId = ev.Id, Email = "kim@example.org", FullName = "Kim Speaker",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(speaker);
        await db.SaveChangesAsync();

        var evalSession = new EvaluationSession
        {
            EventId = ev.Id, Title = "Deep dive",
        };
        db.EvaluationSessions.Add(evalSession);
        await db.SaveChangesAsync();
        db.EvaluationSessionSpeakers.Add(new EvaluationSessionSpeaker
        {
            EvaluationSessionId = evalSession.Id,
            SpeakerEmail = "kim@example.org",
            DisplayName = "Kim Speaker",
        });
        await db.SaveChangesAsync();

        var ctx = new EmailContextAccessor();
        var sender = new ContextCapturingSender(ctx);
        var svc = new EvaluationReportReadyMailService(
            db, sender, NullLogger<EvaluationReportReadyMailService>.Instance, ctx);

        var res = await svc.NotifyAsync(ev.Id, evalSession.Id, superseded: false);
        Assert.Equal(1, res.Sent);

        var (_, c) = Assert.Single(sender.Sent);
        Assert.NotNull(c);
        Assert.Equal(ev.Id, c!.EventId);
        Assert.Equal(speaker.Id, c.ParticipantId);
        Assert.Equal("Kim Speaker", c.RecipientName);
        Assert.Equal(EvaluationReportReadyMailService.TemplateName, c.TemplateName);
        Assert.Equal(EvaluationReportReadyMailService.FeatureKey, c.FeatureKey);
    }
}
