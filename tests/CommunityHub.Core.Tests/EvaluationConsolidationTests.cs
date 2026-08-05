using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Email;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §6.6 — post-event consolidation: copy every session result into the event folder, rebuild the
/// combined summary, notify once.
/// </summary>
/// <remarks>
/// <para>🔒 The three things that would be expensive to get wrong, each with a test: consolidating
/// BEFORE the event ends, MOVING the speaker's file instead of copying it, and mailing the
/// organizers every single day about nothing.</para>
///
/// <para>NO real names — Ada / Grace only.</para>
/// </remarks>
public sealed class EvaluationConsolidationTests
{
    private const int EventId = 1;
    private static readonly DateOnly EventEnd = new(2027, 2, 10);

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class CapturingSender : IEmailSender
    {
        public List<(string To, string Subject)> Sent { get; } = new();
        public Task SendAsync(string to, string subject, string html, CancellationToken ct = default)
        {
            Sent.Add((to, subject));
            return Task.CompletedTask;
        }
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendAsync(string to, string s, string h, string t, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithIcsAsync(string to, string s, string h, string i, string n, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => SendAsync(to, s, h, ct);
    }

    private sealed class FakeStore : ISharePointFileStore
    {
        private readonly Dictionary<string, Dictionary<string, byte[]>> _folders = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Folder, string Name)> Uploads { get; } = new();
        public List<string> Deletes { get; } = new();

        public bool CanStore => true;
        public bool CanRead => true;

        public void Seed(string folder, string name, byte[] content)
        {
            if (!_folders.TryGetValue(folder, out var f)) _folders[folder] = f = new(StringComparer.OrdinalIgnoreCase);
            f[name] = content;
        }

        public IReadOnlyCollection<string> NamesIn(string folder) =>
            _folders.TryGetValue(folder, out var f) ? f.Keys.ToList() : [];

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string folder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(
                _folders.TryGetValue(folder, out var f)
                    ? f.Keys.Select(n => new SharePointFileRef($"{folder}|{n}", n, $"https://sp.test/{folder}/{n}")).ToList()
                    : []);

        public Task<StoredFile> UploadToFolderAsync(string folder, string fileName, byte[] content, string contentType, CancellationToken ct = default)
        {
            Uploads.Add((folder, fileName));
            Seed(folder, fileName, content);
            return Task.FromResult(new StoredFile($"{folder}/{fileName}", $"https://sp.test/{folder}/{fileName}", "id"));
        }

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default)
        {
            var parts = itemId.Split('|', 2);
            if (parts.Length == 2 && _folders.TryGetValue(parts[0], out var f) && f.TryGetValue(parts[1], out var b))
                return Task.FromResult<byte[]?>(b);
            return Task.FromResult<byte[]?>(null);
        }

        public Task<StoredFile> StoreAsync(string p, byte[] c, string t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string p, CancellationToken ct = default) { Deletes.Add(p); return Task.CompletedTask; }
        public Task DeleteFromFolderAsync(string f, string n, CancellationToken ct = default) { Deletes.Add($"{f}/{n}"); return Task.CompletedTask; }
    }

    private static async Task<CommunityHubDbContext> SeedAsync(int responses = 12)
    {
        var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "C", DisplayName = "ELDK 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = EventEnd, IsActive = true,
        });
        db.EvaluationSessions.Add(new EvaluationSession
        {
            Id = 900, EventId = EventId, CehSessionId = 10, Title = "Keeping identity boring",
            ScheduledStart = new DateTimeOffset(2027, 2, 10, 10, 0, 0, TimeSpan.Zero),
            ScheduledEnd = new DateTimeOffset(2027, 2, 10, 11, 0, 0, TimeSpan.Zero),
            CreatedAt = new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero),
        });
        for (var i = 0; i < responses; i++)
        {
            db.EvaluationResponses.Add(new EvaluationResponse
            {
                EventId = EventId, SessionId = 900, Rating = 4,
                CollectionTimestamp = new DateTimeOffset(2027, 2, 10, 10, 30, 0, TimeSpan.Zero),
                ReceivedTimestamp = new DateTimeOffset(2027, 2, 10, 10, 30, 0, TimeSpan.Zero),
                Source = EvaluationResponseSources.Device,
            });
        }
        await db.SaveChangesAsync();
        return db;
    }

    private static (EvaluationConsolidationService Svc, FakeStore Store, CapturingSender Mail, string Source, string Target)
        NewService(CommunityHubDbContext db, MovableClock clock)
    {
        var paths = TestDocLibrary.Resolver();
        var store = new FakeStore();
        var mail = new CapturingSender();

        paths.TryResolve(DocLibraryPaths.SessionEvaluationResults, out var source);
        paths.TryResolve(DocLibraryPaths.EventEvalDuringSessions, out var target);

        var svc = new EvaluationConsolidationService(
            db, store, paths, new DocLibraryFilePublisher(store, paths),
            new EvaluationScoreService(db), new EvaluationSummaryPdfService(),
            mail, emailContext: null, clock);

        return (svc, store, mail, source!, target!);
    }

    private static MovableClock AfterTheEvent() =>
        new(new DateTimeOffset(EventEnd.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

    /// <summary>
    /// 🔒 NOT during the event. Reports are still being superseded by late data, and consolidating
    /// mid-event would publish a snapshot into the folder meant to BE the record — and mail somebody
    /// to say the final results were ready when they were not.
    /// </summary>
    [Fact]
    public async Task It_does_NOTHING_until_the_event_has_ended()
    {
        using var db = await SeedAsync();
        // The last day of the event: responses are still arriving this evening.
        var clock = new MovableClock(new DateTimeOffset(EventEnd.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        var (svc, store, mail, source, _) = NewService(db, clock);
        store.Seed(source, "session-10-score.pdf", [1, 2, 3]);

        var result = await svc.RunAsync(EventId);

        Assert.False(result.Ran);
        Assert.Contains("has not ended", result.InactiveReason);
        Assert.Empty(store.Uploads);
        Assert.Empty(mail.Sent);
    }

    /// <summary>
    /// 🔒 A COPY, NOT A MOVE. A speaker reaches their own report through the speaker folder; moving
    /// it would break every one of those links to build a convenience view for organizers.
    /// </summary>
    [Fact]
    public async Task It_COPIES_the_results_and_never_removes_the_speakers_own_file()
    {
        using var db = await SeedAsync();
        var (svc, store, _, source, target) = NewService(db, AfterTheEvent());
        store.Seed(source, "session-10-score.pdf", [1, 2, 3]);
        store.Seed(source, "session-10-open.pdf", [4, 5, 6]);

        var result = await svc.RunAsync(EventId);

        Assert.True(result.Ran);
        Assert.Equal(2, result.Copied);

        // Both files are now in the EVENT folder...
        Assert.Contains("session-10-score.pdf", store.NamesIn(target));
        Assert.Contains("session-10-open.pdf", store.NamesIn(target));

        // ...and BOTH are still in the speaker folder, with nothing deleted anywhere.
        Assert.Contains("session-10-score.pdf", store.NamesIn(source));
        Assert.Contains("session-10-open.pdf", store.NamesIn(source));
        Assert.Empty(store.Deletes);
    }

    [Fact]
    public async Task It_writes_the_combined_all_session_summary()
    {
        using var db = await SeedAsync();
        var (svc, store, _, source, target) = NewService(db, AfterTheEvent());
        store.Seed(source, "session-10-score.pdf", [1, 2, 3]);

        var result = await svc.RunAsync(EventId);

        Assert.True(result.SummaryWritten);
        Assert.Contains(
            EvaluationSummaryPdfService.FileNameFor("ELDK27"),
            store.NamesIn(target));
    }

    /// <summary>
    /// ⚠️ Idempotent, and it MAILS ONLY ON CHANGE. A daily "your results are consolidated" mail
    /// about nothing is how a real notification stops being read.
    /// </summary>
    [Fact]
    public async Task A_second_run_over_unchanged_data_copies_nothing_and_mails_nobody()
    {
        using var db = await SeedAsync();
        var clock = AfterTheEvent();
        var (svc, store, mail, source, _) = NewService(db, clock);
        store.Seed(source, "session-10-score.pdf", [1, 2, 3]);

        var first = await svc.RunAsync(EventId);
        Assert.True(first.Notified);
        var uploadsAfterFirst = store.Uploads.Count;

        clock.Advance(TimeSpan.FromDays(1));
        var second = await svc.RunAsync(EventId);

        Assert.Equal(0, second.Copied);
        Assert.False(second.SummaryWritten);
        Assert.False(second.Notified);
        Assert.Equal(uploadsAfterFirst, store.Uploads.Count);
        Assert.Single(mail.Sent);            // still just the first run's mail
    }

    /// <summary>A NEW result arriving later is copied, and that is worth a mail.</summary>
    [Fact]
    public async Task A_late_result_is_copied_and_notified()
    {
        using var db = await SeedAsync();
        var clock = AfterTheEvent();
        var (svc, store, mail, source, target) = NewService(db, clock);
        store.Seed(source, "session-10-score.pdf", [1, 2, 3]);
        await svc.RunAsync(EventId);

        clock.Advance(TimeSpan.FromDays(1));
        store.Seed(source, "session-11-score.pdf", [7, 8, 9]);
        var second = await svc.RunAsync(EventId);

        Assert.Equal(1, second.Copied);
        Assert.True(second.Notified);
        Assert.Contains("session-11-score.pdf", store.NamesIn(target));
        Assert.Equal(2, mail.Sent.Count);
    }

    [Fact]
    public async Task The_notification_goes_to_the_organizers_address()
    {
        using var db = await SeedAsync();
        var (svc, store, mail, source, _) = NewService(db, AfterTheEvent());
        store.Seed(source, "session-10-score.pdf", [1, 2, 3]);

        await svc.RunAsync(EventId);

        Assert.Equal(EvaluationConsolidationService.NotifyAddress, mail.Sent.Single().To);
    }

    /// <summary>Non-PDF clutter in the source folder is not dragged along.</summary>
    [Fact]
    public async Task Only_PDFs_are_copied()
    {
        using var db = await SeedAsync();
        var (svc, store, _, source, target) = NewService(db, AfterTheEvent());
        store.Seed(source, "session-10-score.pdf", [1, 2, 3]);
        store.Seed(source, "notes.txt", [9]);

        await svc.RunAsync(EventId);

        Assert.Contains("session-10-score.pdf", store.NamesIn(target));
        Assert.DoesNotContain("notes.txt", store.NamesIn(target));
    }
}
