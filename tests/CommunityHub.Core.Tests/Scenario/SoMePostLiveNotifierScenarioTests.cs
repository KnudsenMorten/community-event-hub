using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// §1290 — "your announcement is live": the speaker a published post is about gets ONE mail with the
/// link to the post.
/// </summary>
/// <remarks>
/// Operator 2026-09-21, asking whether speakers are told when their social media is scheduled "and
/// where they can be seen". Scheduled and day-before existed; nothing said "it is live, here it is".
/// Real <see cref="ReminderEngine"/> and ledger; only the transport is a spy. The ring gate lives in
/// the transport (<c>BrevoEmailSender</c>) and, for a new template, fails closed — not exercised here.
/// </remarks>
public sealed class SoMePostLiveNotifierScenarioTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2027, 1, 20, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Spy : IEmailSender
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();
        public Task SendAsync(string to, string s, string h, CancellationToken ct = default)
        {
            Sent.Add((to, s, h));
            return Task.CompletedTask;
        }
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string fn, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => SendAsync(to, s, h, ct);
    }

    private static async Task<Data.CommunityHubDbContext> SeedAsync()
    {
        var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "Experts Live Denmark 2027", Code = "ELDK27",
            IsActive = true, StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        var speaker = new Participant
        {
            EventId = EventId, Email = "ada@example.test", FullName = "Ada Lovelace",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(speaker);
        var session = new Session { Id = 55, EventId = EventId, SessionizeId = "s55", Title = "Keeping identity boring" };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = 55, ParticipantId = speaker.Id });

        // Published an hour ago — due.
        db.SoMePosts.Add(new SoMePost
        {
            Id = 101, EventId = EventId, TemplateKind = SoMeTemplateKind.Session, SubjectKey = "session:55",
            AutoText = "…", Status = SoMePostStatus.Published, IsActive = true,
            ScheduledAtUtc = Now.AddHours(-1), PublishedAtUtc = Now.AddHours(-1),
            ExternalPostId = "urn:li:share:7000000000000000001",
        });
        // Published three days ago — outside the lookback: switching the mail on must not dig it up.
        db.SoMePosts.Add(new SoMePost
        {
            Id = 102, EventId = EventId, TemplateKind = SoMeTemplateKind.Session, SubjectKey = "session:55",
            AutoText = "…", Status = SoMePostStatus.Published, IsActive = true,
            ScheduledAtUtc = Now.AddDays(-3), PublishedAtUtc = Now.AddDays(-3),
            ExternalPostId = "urn:li:share:7000000000000000002",
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static SoMeAnnouncementNotifier Notifier(Data.CommunityHubDbContext db, Spy spy)
    {
        var clock = new Clock(Now);
        var templates = new EmailTemplateProvider(Options.Create(new EmailTemplateOptions
        {
            TemplateDirectory = RepoPaths.EmailTemplates(),
            PrivateTemplateDirectory = Path.Combine(Path.GetTempPath(), "ceh-no-private-some-live"),
            HubUrl = "https://hub.example.test",
        }));
        return new SoMeAnnouncementNotifier(db, new ReminderEngine(db, spy, clock), templates, clock);
    }

    [Fact]
    public async Task A_post_that_just_went_live_mails_its_speaker_once_with_the_link()
    {
        using var db = await SeedAsync();
        var spy = new Spy();

        await Notifier(db, spy).RunAsync(EventId);
        await Notifier(db, spy).RunAsync(EventId);   // a second pass must not repeat it

        var mail = Assert.Single(spy.Sent);
        Assert.Equal("ada@example.test", mail.To);
        Assert.Contains("is live", mail.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://www.linkedin.com/feed/update/urn:li:share:7000000000000000001", mail.Html);
        // 🔒 The old post (outside the 48 h lookback) is never mentioned.
        Assert.DoesNotContain("7000000000000000002", mail.Html);
    }

    [Fact]
    public void The_live_stage_has_its_own_occasion()
    {
        Assert.NotEqual(
            SoMeAnnouncementNotifier.OccasionKeyFor(7, SoMeAnnouncementStage.Scheduled),
            SoMeAnnouncementNotifier.OccasionKeyFor(7, SoMeAnnouncementStage.Live));
        Assert.NotEqual(
            SoMeAnnouncementNotifier.OccasionKeyFor(7, SoMeAnnouncementStage.DayBefore),
            SoMeAnnouncementNotifier.OccasionKeyFor(7, SoMeAnnouncementStage.Live));
    }
}
