using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1263 — a session marked TEST (<see cref="Session.UsedForTesting"/>) mails only the operator
/// accounts (ring 0/1), never a real speaker.
/// </summary>
/// <remarks>
/// Audit 2026-09-21: the flag already kept a test session out of Zoho, the public agenda and SoMe,
/// but the two speaker mails ignored it. The operator moved a test session to "now" to try the
/// kiosk; one press in the room would have sent the report — Ring 3 in PROD — to every linked
/// speaker. Each test pairs a ring-1 operator with a ring-3 speaker on the SAME session, so a guard
/// that silenced everyone would fail as surely as one that silenced no one.
/// </remarks>
public sealed class TestSessionMailGuardTests
{
    private static readonly DateTimeOffset Now = new(2027, 3, 5, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingSender : IEmailSender
    {
        public readonly List<string> To = new();
        public Task SendAsync(string to, string s, string h, CancellationToken ct = default)
        {
            To.Add(to);
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

    private static CommunityHubDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>().UseInMemoryDatabase(name).Options);

    /// <summary>An edition, a session, and two speakers on it: the operator (ring 1) and a real one.</summary>
    private static async Task<(Event Ev, Session Session)> SeedAsync(CommunityHubDbContext db, bool usedForTesting)
    {
        var ev = new Event
        {
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var session = new Session
        {
            EventId = ev.Id, SessionizeId = "hub-test", Title = "Test Session",
            UsedForTesting = usedForTesting,
        };
        db.Sessions.Add(session);
        var op = new Participant
        {
            EventId = ev.Id, Email = "operator@example.org", FullName = "Op Erator",
            Role = ParticipantRole.Speaker, IsActive = true, Ring = Ring.Ring1,
        };
        var real = new Participant
        {
            EventId = ev.Id, Email = "real@example.org", FullName = "Real Speaker",
            Role = ParticipantRole.Speaker, IsActive = true, Ring = Ring.Broad,
        };
        db.Participants.AddRange(op, real);
        await db.SaveChangesAsync();

        db.SessionSpeakers.AddRange(
            new SessionSpeaker { SessionId = session.Id, ParticipantId = op.Id },
            new SessionSpeaker { SessionId = session.Id, ParticipantId = real.Id });
        await db.SaveChangesAsync();
        return (ev, session);
    }

    private static async Task<List<string>> ReportReadyRecipientsAsync(bool usedForTesting)
    {
        using var db = NewDb($"guard-report-{Guid.NewGuid():N}");
        var (ev, session) = await SeedAsync(db, usedForTesting);

        var evalSession = new EvaluationSession { EventId = ev.Id, Title = session.Title, CehSessionId = session.Id };
        db.EvaluationSessions.Add(evalSession);
        await db.SaveChangesAsync();
        db.EvaluationSessionSpeakers.AddRange(
            new EvaluationSessionSpeaker { EvaluationSessionId = evalSession.Id, SpeakerEmail = "operator@example.org" },
            new EvaluationSessionSpeaker { EvaluationSessionId = evalSession.Id, SpeakerEmail = "real@example.org" });
        await db.SaveChangesAsync();

        var sender = new RecordingSender();
        await new EvaluationReportReadyMailService(
                db, sender, NullLogger<EvaluationReportReadyMailService>.Instance, new EmailContextAccessor())
            .NotifyAsync(ev.Id, evalSession.Id, superseded: false);
        return sender.To;
    }

    [Fact]
    public async Task A_TEST_sessions_report_reaches_the_operator_and_NOT_the_real_speaker()
    {
        Assert.Equal(new[] { "operator@example.org" }, await ReportReadyRecipientsAsync(usedForTesting: true));
    }

    [Fact]
    public async Task A_real_sessions_report_still_reaches_every_speaker()
    {
        var to = await ReportReadyRecipientsAsync(usedForTesting: false);
        Assert.Equal(new[] { "operator@example.org", "real@example.org" }, to.OrderBy(x => x));
    }

    private static async Task<List<string>> ScheduleNoticeRecipientsAsync(bool usedForTesting)
    {
        var dbName = $"guard-notice-{Guid.NewGuid():N}";
        using var sp = new ServiceCollection()
            .AddDbContext<CommunityHubDbContext>(o => o.UseInMemoryDatabase(dbName))
            .BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
        var (ev, session) = await SeedAsync(db, usedForTesting);

        // Past the §1001 quiet period, so the only thing that can hold a mail back is the guard.
        db.SessionSourceSettings.Add(new SessionSourceSetting
        {
            EventId = ev.Id, SpeakerScheduleNoticeFrom = new DateOnly(2027, 1, 1),
        });
        await db.SaveChangesAsync();

        var sender = new RecordingSender();
        var templates = new EmailTemplateProvider(
            Options.Create(new EmailTemplateOptions { HubUrl = "https://hub.example" }),
            sp.GetRequiredService<IServiceScopeFactory>(), emailContext: null);
        await new SessionScheduleChangeNotifier(db, sender, templates, new EmailContextAccessor(), new FixedClock(Now))
            .NotifyAsync(ev.Id, session.Id,
                Now.AddDays(10), Now.AddDays(10).AddHours(1), "Room 16",
                Now, Now.AddHours(1), "Room 16");
        return sender.To;
    }

    [Fact]
    public async Task Moving_a_TEST_session_notifies_the_operator_and_NOT_the_real_speaker()
    {
        Assert.Equal(new[] { "operator@example.org" }, await ScheduleNoticeRecipientsAsync(usedForTesting: true));
    }

    [Fact]
    public async Task Moving_a_real_session_still_notifies_every_speaker()
    {
        var to = await ScheduleNoticeRecipientsAsync(usedForTesting: false);
        Assert.Equal(new[] { "operator@example.org", "real@example.org" }, to.OrderBy(x => x));
    }
}
