using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §730 — a deck belongs to a SESSION, so its upload task closes for every speaker on that session.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-31: <i>"as some sessions have multiple speakers, we need to validate that
/// this tasks + final presentation is shared status wise across the speakers. once 1 speaker upload
/// the preview presentation then it is shown for all linked speakers. and once both are uploaded
/// the task completes for all linked speakers"</i>.</para>
///
/// <para>🔴 <b>The artefact was already per-session; only the COMPLETION was per-person.</b> The
/// deck is stored as <c>FileNameFor(sessionId, …)</c> and read back by session, so a co-speaker
/// could always SEE it — but the task closed on <c>speakerdl:{uploaderId}:…</c> alone, leaving every
/// co-speaker an open task, an overdue badge and a reminder cadence chasing them for a file that had
/// already been delivered.</para>
/// </remarks>
public class CoSpeakerDeckTaskSharedTests
{
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-07-31T10:00:00Z");
    }

    /// <summary>Storage is never touched here — these tests are about the TASK, not the file.</summary>
    private sealed class InertStore : ISharePointFileStore
    {
        public bool CanStore => true;
        public bool CanRead => true;
        public Task<StoredFile> StoreAsync(string relativePath, byte[] content, string contentType, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SharePointFileRef>>(Array.Empty<SharePointFileRef>());
        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default)
            => Task.FromResult<byte[]?>(null);
        public Task<StoredFile> UploadToFolderAsync(string relativeFolder, string fileName, byte[] content, string contentType, CancellationToken ct = default)
            => Task.FromResult(new StoredFile($"{relativeFolder}/{fileName}", "https://sp.example/" + fileName, "item-1"));
        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static SpeakerPresentationService NewService(CommunityHub.Core.Data.CommunityHubDbContext db) =>
        new(new InertStore(),
            Microsoft.Extensions.Options.Options.Create(new GraphicsSharePointOptions
            {
                Enabled = true,
                SiteUrl = "https://sp.example/site",
                PresentationPreviewFolderPath = $"Ev/{Guid.NewGuid():N}/Preview",
                PresentationFinalFolderPath = $"Ev/{Guid.NewGuid():N}/Final",
            }),
            db, new FixedClock(), TestDocLibrary.Resolver($"General/TEST-{Guid.NewGuid():N}/EventHub"));

    private static async Task<(int eventId, int a, int b, int sessionId)> SeedCoSpeakersAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db)
    {
        var ev = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var a = new Participant { EventId = ev.Id, Email = "a@x.dk", FullName = "Ann A", Role = ParticipantRole.Speaker, IsActive = true };
        var b = new Participant { EventId = ev.Id, Email = "b@x.dk", FullName = "Bo B", Role = ParticipantRole.Speaker, IsActive = true };
        db.Participants.AddRange(a, b);
        await db.SaveChangesAsync();

        var session = new Session { EventId = ev.Id, Title = "Shared session" };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        db.SessionSpeakers.AddRange(
            new SessionSpeaker { SessionId = session.Id, ParticipantId = a.Id },
            new SessionSpeaker { SessionId = session.Id, ParticipantId = b.Id });

        // The two per-speaker upload tasks the deadline seeder creates.
        foreach (var pid in new[] { a.Id, b.Id })
        {
            db.Tasks.Add(new ParticipantTask
            {
                EventId = ev.Id,
                AssignedParticipantId = pid,
                Title = "Upload preview presentation",
                SourceKey = SpeakerPresentationService.TaskSourceKey(pid, PresentationKind.Preview),
                State = TaskState.Open,
            });
        }
        await db.SaveChangesAsync();

        return (ev.Id, a.Id, b.Id, session.Id);
    }

    private static async Task<bool> IsDoneAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, int participantId)
    {
        var key = SpeakerPresentationService.TaskSourceKey(participantId, PresentationKind.Preview);
        return await db.Tasks.AnyAsync(t => t.SourceKey == key && t.State == TaskState.Done);
    }

    [Fact]
    public async Task One_speakers_upload_closes_the_task_for_every_speaker_on_the_session()
    {
        using var db = ScenarioFixture.NewDb();
        var (eventId, a, b, sessionId) = await SeedCoSpeakersAsync(db);

        // Speaker A completes a browser-direct upload for the shared session.
        var svc = NewService(db);
        await svc.CompleteDirectUploadAsync(eventId, a, PresentationKind.Preview, sessionId: sessionId);

        Assert.True(await IsDoneAsync(db, a), "the uploader's task must close");
        Assert.True(await IsDoneAsync(db, b),
            "the CO-SPEAKER's task must close too — the deck belongs to the session, and leaving it "
            + "open chases them for a file that is already delivered (§730).");
    }

    [Fact]
    public async Task A_speaker_on_a_DIFFERENT_session_is_untouched()
    {
        using var db = ScenarioFixture.NewDb();
        var (eventId, a, b, sessionId) = await SeedCoSpeakersAsync(db);

        // A third speaker, on their own session, must not be completed by someone else's upload.
        var c = new Participant { EventId = eventId, Email = "c@x.dk", FullName = "Cee C", Role = ParticipantRole.Speaker, IsActive = true };
        db.Participants.Add(c);
        await db.SaveChangesAsync();
        var other = new Session { EventId = eventId, Title = "Other session" };
        db.Sessions.Add(other);
        await db.SaveChangesAsync();
        var cId = c.Id;
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = other.Id, ParticipantId = cId });
        db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId,
            AssignedParticipantId = cId,
            Title = "Upload preview presentation",
            SourceKey = SpeakerPresentationService.TaskSourceKey(cId, PresentationKind.Preview),
            State = TaskState.Open,
        });
        await db.SaveChangesAsync();

        var svc = NewService(db);
        await svc.CompleteDirectUploadAsync(eventId, a, PresentationKind.Preview, sessionId: sessionId);

        Assert.False(await IsDoneAsync(db, cId),
            "a speaker on a different session must be unaffected — sharing is per SESSION, not global.");
    }

    [Fact]
    public async Task The_preview_upload_does_not_close_the_FINAL_task()
    {
        using var db = ScenarioFixture.NewDb();
        var (eventId, a, b, sessionId) = await SeedCoSpeakersAsync(db);

        db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId,
            AssignedParticipantId = a,
            Title = "Upload final presentation",
            SourceKey = SpeakerPresentationService.TaskSourceKey(a, PresentationKind.Final),
            State = TaskState.Open,
        });
        await db.SaveChangesAsync();

        var svc = NewService(db);
        await svc.CompleteDirectUploadAsync(eventId, a, PresentationKind.Preview, sessionId: sessionId);

        // 🔑 Preview and FINAL are two separate tasks, each closed by its own upload. Worth pinning
        // because the operator's phrasing was "once both are uploaded the task completes" — the
        // sharing is what is new here; the preview/final split is unchanged.
        var finalKey = SpeakerPresentationService.TaskSourceKey(a, PresentationKind.Final);
        Assert.False(await db.Tasks.AnyAsync(t => t.SourceKey == finalKey && t.State == TaskState.Done));
    }
}
