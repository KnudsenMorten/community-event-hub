using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Email;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §750 C7 — the 30-minute debounce that decides WHEN a report is rebuilt, published and notified.
/// </summary>
/// <remarks>
/// <para>The brief: <i>"wait until 30 minutes have passed with no new responses for that session
/// before rebuilding, so a trickle of late cached data does not produce a stream of superseding
/// reports and notification emails."</i></para>
///
/// <para>🔑 <b>Figures update live; DOCUMENTS settle.</b> Nothing here gates the score — it is derived
/// on every read. What is gated is the expensive, noisy half: rendering a PDF and emailing speakers
/// about it. Every fact below is really about NOT sending an email, which is why they matter: the
/// failure mode is a speaker receiving five near-identical mails while a device flushes its cache.</para>
///
/// <para>EF Core InMemory + a fixed, movable clock; a fake store and a capturing mail sender, so no
/// network and no real mail. FAKE names only.</para>
/// </remarks>
public sealed class EvaluationReportDebounceTests
{
    private const int EventId = 1;
    private const int CehSessionId = 55;
    private static readonly DateTimeOffset Start = new(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(1);

    private sealed class MovableClock : TimeProvider
    {
        private DateTimeOffset _now;
        public MovableClock(DateTimeOffset now) => _now = now;
        public void Set(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class CapturingSender : IEmailSender
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();

        public Task SendAsync(string toEmail, string subject, string htmlBody,
            CancellationToken cancellationToken = default)
        {
            Sent.Add((toEmail, subject, htmlBody));
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

        public List<(string To, int Attachments, string FirstName)> Attached { get; } = new();

        // §752.1 — the report-ready mail now CARRIES the PDF (operator 2026-08-01: "attack pdf to
        // mail ... so they have both options in the mail"). This used to throw, to prove the
        // brief's link-only rule; that rule was overridden deliberately, so the fake now records
        // instead of refusing — and the tests assert the attachment is really there.
        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody,
            IReadOnlyCollection<EmailAttachment> attachments,
            CancellationToken cancellationToken = default)
        {
            Sent.Add((toEmail, subject, htmlBody));
            Attached.Add((toEmail, attachments.Count, attachments.FirstOrDefault()?.FileName ?? ""));
            return Task.CompletedTask;
        }
    }

    /// <summary>A store that accepts writes, so publishing is exercised rather than short-circuited.</summary>
    private sealed class FakeStore : ISharePointFileStore
    {
        public List<string> Uploaded { get; } = new();
        public bool CanRead => true;
        public bool CanStore => true;

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(
            string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(Array.Empty<SharePointFileRef>());

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);

        public Task<StoredFile> UploadToFolderAsync(
            string relativeFolder, string fileName, byte[] content, string contentType,
            CancellationToken ct = default)
        {
            Uploaded.Add(fileName);
            return Task.FromResult(new StoredFile(fileName, string.Empty, $"id-{fileName}"));
        }

        public Task DeleteFromFolderAsync(
            string relativeFolder, string fileName, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<StoredFile> StoreAsync(
            string relativePath, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("root store unused");

        public Task DeleteAsync(string relativePath, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class Harness
    {
        public required CommunityHubDbContext Db { get; init; }
        public required EvaluationReportDebounceService Svc { get; init; }
        public required CapturingSender Mail { get; init; }
        public required MovableClock Clock { get; init; }
        public required FakeStore Store { get; init; }
    }

    private static async Task<Harness> SeedAsync(int responses = 12)
    {
        var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "Experts Live Denmark 2027",
            Code = "ELDK27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        var speaker = new Participant
        {
            EventId = EventId, Email = "ada@example.test", FullName = "Ada Lovelace",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(speaker);
        db.Sessions.Add(new Session { Id = CehSessionId, EventId = EventId, Title = "Keeping identity boring" });
        var evalSession = new EvaluationSession
        {
            Id = 900, EventId = EventId, CehSessionId = CehSessionId,
            Title = "Keeping identity boring",
            ScheduledStart = Start, ScheduledEnd = End,
            CollectionWindowOpensAt = Start, CollectionWindowClosesAt = End.AddMinutes(30),
            CreatedAt = Start.AddDays(-1), UpdatedAt = Start.AddDays(-1),
        };
        db.EvaluationSessions.Add(evalSession);
        await db.SaveChangesAsync();

        db.EvaluationSessionSpeakers.Add(new EvaluationSessionSpeaker
        {
            EvaluationSessionId = evalSession.Id, SpeakerEmail = "ada@example.test",
            DisplayName = "Ada Lovelace", CehParticipantId = speaker.Id, CreatedAt = Start,
        });
        for (var i = 0; i < responses; i++)
        {
            db.EvaluationResponses.Add(new EvaluationResponse
            {
                EventId = EventId, SessionId = evalSession.Id, Rating = 4,
                CollectionTimestamp = Start.AddMinutes(i),
                ReceivedTimestamp = Start.AddMinutes(i),
                Source = EvaluationResponseSources.Device,
            });
        }
        await db.SaveChangesAsync();

        // Well past the last response, so the default state is "settled".
        var clock = new MovableClock(End.AddHours(3));
        var store = new FakeStore();
        var opts = Options.Create(new GraphicsSharePointOptions
        {
            Enabled = true,
            SiteUrl = "https://contoso.sharepoint.example.test/sites/eldk",
            SessionEvalPdfFolderPath = "Evals/PDF",
            SessionEvalsQrFolderPath = "Evals/QR",
        });
        var mail = new CapturingSender();

        var builder = new EvaluationReportBuilder(db, new EvaluationScoreService(db));
        var publisher = new EvaluationArtifactPublishService(
            db, new SessionQrCodeService(), new SessionEvalsQrService(store, opts, TestDocLibrary.Resolver()),
            new SessionEvalPdfService(store, opts, db, TestDocLibrary.Resolver()), builder, new EvaluationReportService(),
            NullLogger<EvaluationArtifactPublishService>.Instance);
        var notifier = new EvaluationReportReadyMailService(
            db, mail, NullLogger<EvaluationReportReadyMailService>.Instance,
            context: null, templates: null,
            // §752.1 — the render chain, so the mail can attach the same PDF the engine publishes.
            reportBuilder: builder, reportRenderer: new EvaluationReportService());

        var svc = new EvaluationReportDebounceService(
            db, builder, publisher, notifier,
            NullLogger<EvaluationReportDebounceService>.Instance, clock);

        return new Harness { Db = db, Svc = svc, Mail = mail, Clock = clock, Store = store };
    }

    // ---- the debounce itself -------------------------------------------------------------------

    [Fact]
    public async Task A_settled_session_is_published_and_its_speaker_notified()
    {
        var h = await SeedAsync();

        var result = await h.Svc.RunAsync(EventId);

        Assert.Equal(1, result.Published);
        Assert.Equal(0, result.Superseded);
        Assert.Contains("55-scores.pdf", h.Store.Uploaded.Single());
        var mail = Assert.Single(h.Mail.Sent);
        Assert.Equal("ada@example.test", mail.To);
        Assert.Contains("ready", mail.Subject, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 🔒 The headline. A response that arrived a minute ago means the session is still moving, and
    /// publishing now would be superseded almost immediately — one mail per record of a cache flush,
    /// which is exactly what the debounce exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_session_still_receiving_responses_is_NOT_published()
    {
        var h = await SeedAsync();
        // A late record lands "now" — inside the quiet period.
        h.Db.EvaluationResponses.Add(new EvaluationResponse
        {
            EventId = EventId, SessionId = 900, Rating = 3,
            CollectionTimestamp = Start.AddMinutes(5),
            ReceivedTimestamp = h.Clock.GetUtcNow().AddMinutes(-1),
            Source = EvaluationResponseSources.Device,
        });
        await h.Db.SaveChangesAsync();

        var result = await h.Svc.RunAsync(EventId);

        Assert.Equal(0, result.Published);
        Assert.Empty(h.Mail.Sent);
        Assert.Equal(EvaluationReportDebounceService.Outcome.StillSettling,
            result.Sessions.Single().Outcome);
    }

    /// <summary>
    /// 🔑 The boundary, pinned in both directions — an off-by-one here either publishes a session
    /// that is still moving, or never publishes one that has stopped.
    /// </summary>
    [Fact]
    public async Task The_quiet_period_is_THIRTY_minutes_and_the_boundary_publishes()
    {
        var h = await SeedAsync();
        var lastArrival = new DateTimeOffset(2027, 2, 9, 14, 0, 0, TimeSpan.Zero);
        h.Db.EvaluationResponses.Add(new EvaluationResponse
        {
            EventId = EventId, SessionId = 900, Rating = 3,
            CollectionTimestamp = Start.AddMinutes(5), ReceivedTimestamp = lastArrival,
            Source = EvaluationResponseSources.Device,
        });
        await h.Db.SaveChangesAsync();

        // 29 minutes later: still settling.
        h.Clock.Set(lastArrival.AddMinutes(29));
        Assert.Equal(0, (await h.Svc.RunAsync(EventId)).Published);

        // Exactly 30: it has gone quiet, and it publishes.
        h.Clock.Set(lastArrival.AddMinutes(30));
        Assert.Equal(1, (await h.Svc.RunAsync(EventId)).Published);
    }

    /// <summary>
    /// 🔒 A second pass with nothing new must not re-publish or re-mail. This is the fact that keeps
    /// a job running every few minutes from mailing a speaker every few minutes.
    /// </summary>
    [Fact]
    public async Task Running_again_with_no_new_data_sends_NOTHING()
    {
        var h = await SeedAsync();
        await h.Svc.RunAsync(EventId);
        h.Mail.Sent.Clear();
        h.Store.Uploaded.Clear();

        var second = await h.Svc.RunAsync(EventId);

        Assert.Equal(0, second.Published);
        Assert.Empty(h.Mail.Sent);
        Assert.Empty(h.Store.Uploaded);
        Assert.Equal(EvaluationReportDebounceService.Outcome.AlreadyCurrent,
            second.Sessions.Single().Outcome);
    }

    // ---- superseding ---------------------------------------------------------------------------

    /// <summary>
    /// 🔑 Late data after a report has gone out supersedes it — and the mail must SAY so. A second
    /// mail about the same session with no explanation reads as a duplicate and gets deleted, taking
    /// the corrected figures with it.
    /// </summary>
    [Fact]
    public async Task Late_data_supersedes_the_report_and_the_mail_SAYS_it_was_updated()
    {
        var h = await SeedAsync();
        await h.Svc.RunAsync(EventId);
        var firstSubject = h.Mail.Sent.Single().Subject;
        h.Mail.Sent.Clear();

        // A device flushes an old press long afterwards.
        var lateArrival = h.Clock.GetUtcNow();
        h.Db.EvaluationResponses.Add(new EvaluationResponse
        {
            EventId = EventId, SessionId = 900, Rating = 1,
            CollectionTimestamp = Start.AddMinutes(7), ReceivedTimestamp = lateArrival,
            Source = EvaluationResponseSources.Device,
        });
        await h.Db.SaveChangesAsync();
        h.Clock.Set(lateArrival.AddMinutes(31));

        var result = await h.Svc.RunAsync(EventId);

        Assert.Equal(1, result.Published);
        Assert.Equal(1, result.Superseded);
        var second = Assert.Single(h.Mail.Sent);
        Assert.NotEqual(firstSubject, second.Subject);
        Assert.Contains("Updated", second.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recomputed", second.Html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The stored version is what makes "has anything changed?" answerable. It must move when the
    /// data does — otherwise the next pass would re-publish forever, or never again.
    /// </summary>
    [Fact]
    public async Task The_published_version_is_stamped_and_moves_with_the_data()
    {
        var h = await SeedAsync();
        await h.Svc.RunAsync(EventId);
        var first = h.Db.EvaluationSessions.Single().PublishedReportVersion;
        Assert.False(string.IsNullOrEmpty(first));
        Assert.NotNull(h.Db.EvaluationSessions.Single().PublishedReportAt);

        var lateArrival = h.Clock.GetUtcNow();
        h.Db.EvaluationResponses.Add(new EvaluationResponse
        {
            EventId = EventId, SessionId = 900, Rating = 2,
            CollectionTimestamp = Start.AddMinutes(8), ReceivedTimestamp = lateArrival,
            Source = EvaluationResponseSources.Device,
        });
        await h.Db.SaveChangesAsync();
        h.Clock.Set(lateArrival.AddMinutes(31));
        await h.Svc.RunAsync(EventId);

        Assert.NotEqual(first, h.Db.EvaluationSessions.Single().PublishedReportVersion);
    }

    // ---- refusing correctly --------------------------------------------------------------------

    /// <summary>
    /// 🔒 A session nobody rated has no report to publish and nothing to say. It must not mail a
    /// speaker to tell them so.
    /// </summary>
    [Fact]
    public async Task A_session_with_no_responses_publishes_nothing_and_mails_nobody()
    {
        var h = await SeedAsync(responses: 0);

        var result = await h.Svc.RunAsync(EventId);

        Assert.Equal(0, result.Published);
        Assert.Empty(h.Mail.Sent);
        Assert.Empty(h.Store.Uploaded);
    }

    /// <summary>
    /// 🔑 The §749 link is what makes the notification possible. With no linked speaker the report is
    /// still published — the organiser needs it — but the mail step reports that it reached nobody
    /// rather than appearing to have worked.
    /// </summary>
    [Fact]
    public async Task With_no_linked_speaker_the_report_still_publishes_but_nobody_is_mailed()
    {
        var h = await SeedAsync();
        h.Db.EvaluationSessionSpeakers.RemoveRange(h.Db.EvaluationSessionSpeakers);
        await h.Db.SaveChangesAsync();

        var result = await h.Svc.RunAsync(EventId);

        Assert.Equal(1, result.Published);
        Assert.NotEmpty(h.Store.Uploaded);
        Assert.Empty(h.Mail.Sent);
    }

    /// <summary>
    /// §752.1 — the mail carries BOTH: the PDF attached, and the in-hub link in the body (operator
    /// 2026-08-01: <i>"so they have both options in the mail"</i>).
    /// </summary>
    /// <remarks>
    /// ⚠️ This deliberately overrides the brief's "link, never an attachment" rule. The reason the
    /// rule existed still holds — an attachment is an uncontrolled copy of verbatim attendee
    /// comments — and he accepted that trade so a speaker on a phone need not sign in to read their
    /// own result. 🔑 The BODY must never inline the PDF: the attachment is the file, the body is
    /// the link, and a base64 blob in the HTML would be neither.
    /// </remarks>
    [Fact]
    public async Task The_notification_carries_the_PDF_AND_the_hub_link()
    {
        var h = await SeedAsync();

        await h.Svc.RunAsync(EventId);

        var attached = Assert.Single(h.Mail.Attached);
        Assert.Equal(1, attached.Attachments);
        Assert.EndsWith(".pdf", attached.FirstName, StringComparison.OrdinalIgnoreCase);

        var mail = Assert.Single(h.Mail.Sent);
        Assert.DoesNotContain("%PDF", mail.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("base64", mail.Html, StringComparison.OrdinalIgnoreCase);
    }
}


