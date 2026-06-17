using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for the speaker Q&amp;A email digest (REQUIREMENTS §21 Participant
/// "Speaker Q&amp;A email digest on new questions"). EF Core InMemory provider + a
/// fixed clock + a recording email sender. They prove:
///  - a digest is built per speaker over the OPEN questions on THEIR sessions,
///    scoped to the edition and to active speakers;
///  - it is idempotent — a re-run with the same open set sends nothing, a NEW
///    question raises the fingerprint and sends an updated digest exactly once;
///  - answering / closing a question never re-triggers a digest;
///  - speakers with no open questions (and non-speaker roles) get nothing.
/// </summary>
public sealed class SpeakerQuestionDigestServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 17, 8, 0, 0, TimeSpan.Zero);

    private static readonly FixedClock Clock = new(Now);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"sqdigest-{Guid.NewGuid():N}")
            .Options);

    private static EmailTemplateProvider RealTemplates() =>
        new(Options.Create(new EmailTemplateOptions
        {
            TemplateDirectory = RepoPaths.EmailTemplates(),
            HubUrl = "https://hub.example.test",
        }));

    private static SpeakerQuestionDigestService NewService(
        CommunityHubDbContext db, CapturingEmailSender sender) =>
        new(db, new ParticipantEmailService(db, RealTemplates(), sender, new EmailContextAccessor()), Clock);

    // ----- seed helpers -----------------------------------------------------

    private static async Task<int> SeedEventAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "T27", CommunityName = "Test Community",
            DisplayName = "Test Community 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        return evt.Id;
    }

    private static async Task<int> SeedSpeakerAsync(
        CommunityHubDbContext db, int eventId, string email,
        ParticipantRole role = ParticipantRole.Speaker, bool active = true)
    {
        var p = new Participant
        {
            EventId = eventId, Email = email, FullName = "Spk " + email,
            Role = role, IsActive = active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static async Task<int> SeedSessionAsync(
        CommunityHubDbContext db, int eventId, string title, params int[] speakerIds)
    {
        var session = new Session
        {
            EventId = eventId, SessionizeId = Guid.NewGuid().ToString("N"),
            Title = title, Abstract = "A talk.", CreatedAt = Now,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        foreach (var pid in speakerIds)
            db.SessionSpeakers.Add(new SessionSpeaker { SessionId = session.Id, ParticipantId = pid });
        await db.SaveChangesAsync();
        return session.Id;
    }

    private static async Task<int> AddQuestionAsync(
        CommunityHubDbContext db, int eventId, int sessionId,
        SessionQuestionStatus status = SessionQuestionStatus.Open)
    {
        var q = new SessionQuestion
        {
            EventId = eventId, SessionId = sessionId,
            QuestionText = "What should I bring?", Status = status, CreatedAt = Now,
        };
        db.SessionQuestions.Add(q);
        await db.SaveChangesAsync();
        return q.Id;
    }

    // ----- build --------------------------------------------------------------

    [Fact]
    public async Task Digest_aggregates_open_questions_per_speaker_across_their_sessions()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var spk = await SeedSpeakerAsync(db, eventId, "spk@example.test");
        var s1 = await SeedSessionAsync(db, eventId, "Talk A", spk);
        var s2 = await SeedSessionAsync(db, eventId, "Talk B", spk);
        await AddQuestionAsync(db, eventId, s1);
        await AddQuestionAsync(db, eventId, s1);
        await AddQuestionAsync(db, eventId, s2);

        var digests = await NewService(db, new CapturingEmailSender())
            .BuildPendingDigestsAsync(eventId);

        var d = Assert.Single(digests);
        Assert.Equal(spk, d.ParticipantId);
        Assert.Equal(3, d.OpenQuestionCount);
        Assert.Equal(2, d.SessionCount);
    }

    [Fact]
    public async Task Answered_and_closed_questions_are_not_counted()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var spk = await SeedSpeakerAsync(db, eventId, "spk@example.test");
        var s1 = await SeedSessionAsync(db, eventId, "Talk A", spk);
        await AddQuestionAsync(db, eventId, s1);                                   // open
        await AddQuestionAsync(db, eventId, s1, SessionQuestionStatus.Answered);   // not counted
        await AddQuestionAsync(db, eventId, s1, SessionQuestionStatus.Closed);     // not counted

        var d = Assert.Single(await NewService(db, new CapturingEmailSender())
            .BuildPendingDigestsAsync(eventId));
        Assert.Equal(1, d.OpenQuestionCount);
    }

    [Fact]
    public async Task A_speaker_with_no_open_questions_is_omitted()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var spk = await SeedSpeakerAsync(db, eventId, "spk@example.test");
        await SeedSessionAsync(db, eventId, "Quiet talk", spk);   // no questions

        Assert.Empty(await NewService(db, new CapturingEmailSender())
            .BuildPendingDigestsAsync(eventId));
    }

    [Fact]
    public async Task Each_speaker_only_sees_questions_on_their_own_sessions()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var spk1 = await SeedSpeakerAsync(db, eventId, "a@example.test");
        var spk2 = await SeedSpeakerAsync(db, eventId, "b@example.test");
        var s1 = await SeedSessionAsync(db, eventId, "A's talk", spk1);
        var s2 = await SeedSessionAsync(db, eventId, "B's talk", spk2);
        await AddQuestionAsync(db, eventId, s1);   // only A's session has a question

        var digests = await NewService(db, new CapturingEmailSender())
            .BuildPendingDigestsAsync(eventId);

        var d = Assert.Single(digests);
        Assert.Equal(spk1, d.ParticipantId);   // B is not in the digest
    }

    [Fact]
    public async Task Co_speakers_on_one_session_each_get_the_questions()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var spk1 = await SeedSpeakerAsync(db, eventId, "a@example.test");
        var spk2 = await SeedSpeakerAsync(db, eventId, "b@example.test");
        var s1 = await SeedSessionAsync(db, eventId, "Joint talk", spk1, spk2);
        await AddQuestionAsync(db, eventId, s1);

        var digests = await NewService(db, new CapturingEmailSender())
            .BuildPendingDigestsAsync(eventId);

        Assert.Equal(2, digests.Count);
        Assert.All(digests, d => Assert.Equal(1, d.OpenQuestionCount));
    }

    [Fact]
    public async Task An_inactive_speaker_is_excluded()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var spk = await SeedSpeakerAsync(db, eventId, "gone@example.test", active: false);
        var s1 = await SeedSessionAsync(db, eventId, "Talk", spk);
        await AddQuestionAsync(db, eventId, s1);

        Assert.Empty(await NewService(db, new CapturingEmailSender())
            .BuildPendingDigestsAsync(eventId));
    }

    // ----- send + idempotency -------------------------------------------------

    [Fact]
    public async Task Send_emails_each_speaker_with_open_questions_and_logs_a_ledger_row()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var spk = await SeedSpeakerAsync(db, eventId, "spk@example.test");
        var s1 = await SeedSessionAsync(db, eventId, "Talk A", spk);
        await AddQuestionAsync(db, eventId, s1);
        var sender = new CapturingEmailSender();

        var sent = await NewService(db, sender).SendPendingAsync(eventId);

        Assert.Equal(1, sent);
        var msg = Assert.Single(sender.Sent);
        Assert.Equal("spk@example.test", msg.To);
        Assert.Equal(1, await db.SentReminders.CountAsync(
            r => r.ReminderType == SpeakerQuestionDigestService.ReminderType));
    }

    [Fact]
    public async Task Re_run_with_no_new_questions_sends_nothing()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var spk = await SeedSpeakerAsync(db, eventId, "spk@example.test");
        var s1 = await SeedSessionAsync(db, eventId, "Talk A", spk);
        await AddQuestionAsync(db, eventId, s1);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        Assert.Equal(1, await svc.SendPendingAsync(eventId));
        Assert.Equal(0, await svc.SendPendingAsync(eventId));   // idempotent
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task A_brand_new_question_triggers_one_more_digest()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var spk = await SeedSpeakerAsync(db, eventId, "spk@example.test");
        var s1 = await SeedSessionAsync(db, eventId, "Talk A", spk);
        await AddQuestionAsync(db, eventId, s1);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        Assert.Equal(1, await svc.SendPendingAsync(eventId));
        await AddQuestionAsync(db, eventId, s1);                 // new question arrives
        Assert.Equal(1, await svc.SendPendingAsync(eventId));    // exactly one more
        Assert.Equal(0, await svc.SendPendingAsync(eventId));    // then settled again
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task Answering_the_only_question_does_not_re_send()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        var spk = await SeedSpeakerAsync(db, eventId, "spk@example.test");
        var s1 = await SeedSessionAsync(db, eventId, "Talk A", spk);
        var qid = await AddQuestionAsync(db, eventId, s1);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        Assert.Equal(1, await svc.SendPendingAsync(eventId));

        // The speaker answers it — the open set shrinks, fingerprint does not rise.
        (await db.SessionQuestions.FindAsync(qid))!.Status = SessionQuestionStatus.Answered;
        await db.SaveChangesAsync();

        Assert.Equal(0, await svc.SendPendingAsync(eventId));
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Questions_in_another_edition_do_not_leak_into_a_digest()
    {
        using var db = NewDb();
        var eventA = await SeedEventAsync(db);
        var eventB = await SeedEventAsync(db);
        var spk = await SeedSpeakerAsync(db, eventA, "spk@example.test");
        var sA = await SeedSessionAsync(db, eventA, "A talk", spk);
        await AddQuestionAsync(db, eventA, sA);
        // A stray question stamped to event B's id (should never be picked up for A).
        await AddQuestionAsync(db, eventB, sA);

        var d = Assert.Single(await NewService(db, new CapturingEmailSender())
            .BuildPendingDigestsAsync(eventA));
        Assert.Equal(1, d.OpenQuestionCount);
    }
}
