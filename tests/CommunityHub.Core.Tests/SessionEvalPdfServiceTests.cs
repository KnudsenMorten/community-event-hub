using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// FINAL per-session evaluation PDFs (REQUIREMENTS §192, reworking §166): an organizer
/// uploads up to TWO PDFs per session — a SCORE PDF and an OPEN-feedback PDF — each landing
/// in a SharePoint folder under a DETERMINISTIC, kind-tagged name
/// (<c>session-{id}-score.pdf</c> / <c>session-{id}-feedback.pdf</c>) and streamed back to
/// the session's speaker(s) through a HUB PROXY — never a SharePoint URL. Uses a FAKE store
/// (no external call). Proves: the per-kind proxy-url contract, the two-kind upload/list
/// round-trip + PROVENANCE (who/when), the "which kinds exist" lookup, the inert
/// (not-configured) path, and — the security-critical part — the proxy ACCESS GATE
/// (organizer + own-session speaker allowed; a foreign speaker / other role denied) for BOTH
/// kinds. NO real data.
/// </summary>
public sealed class SessionEvalPdfServiceTests
{
    // §768: resolved from the DocLibrary registry, not an app setting. The old literal was a
    // `SessionEvals-PDF` folder that does not exist in the reorganised library — results live at
    // `Speakers/SessionEvaluations/Result`.
    private static readonly string Folder = TestDocLibrary.PathFor(
        CommunityHub.Core.Integrations.DocLibrary.DocLibraryPaths.SessionEvaluationResults);

    private const int EventId = 7;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"evalpdf-{Guid.NewGuid():N}")
            .Options);

    /// <param name="folder">Pass <c>""</c> to model an UNCONFIGURED library — the inert case.</param>
    private static SessionEvalPdfService NewService(
        CommunityHubDbContext db, FakePdfStore store, string? folder = null) =>
        new(store, Options.Create(new GraphicsSharePointOptions
        {
            Enabled = true,
            SiteUrl = "https://contoso.sharepoint.example.test/sites/eldk",
        }), db,
        TestDocLibrary.Resolver(folder == string.Empty ? string.Empty : TestDocLibrary.Root));

    /// <summary>Seed an organizer, two speakers, and a session with only the FIRST speaker.</summary>
    private static async Task<(int sessionId, int organizerId, int ownSpeakerId, int otherSpeakerId)>
        SeedAsync(CommunityHubDbContext db)
    {
        var org = new Participant { EventId = EventId, Email = "org@example.test", FullName = "Org Person", Role = ParticipantRole.Organizer };
        var own = new Participant { EventId = EventId, Email = "own@example.test", FullName = "Own Speaker", Role = ParticipantRole.Speaker };
        var other = new Participant { EventId = EventId, Email = "other@example.test", FullName = "Other Speaker", Role = ParticipantRole.Speaker };
        db.Participants.AddRange(org, own, other);
        await db.SaveChangesAsync();

        var session = new Session { EventId = EventId, Title = "Securing Your Cloud", Type = SessionType.TechnicalSession };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = session.Id, ParticipantId = own.Id });
        await db.SaveChangesAsync();

        return (session.Id, org.Id, own.Id, other.Id);
    }

    // ---- the per-kind proxy-url / file-name contract ----------------------

    [Fact]
    public void ProxyUrl_and_file_name_are_per_kind_deterministic_and_never_a_sharepoint_url()
    {
        Assert.Equal("/session-eval/42/score/download", SessionEvalPdfService.ProxyUrlFor(42, EvaluationPdfKind.Score));
        Assert.Equal("/session-eval/42/feedback/download", SessionEvalPdfService.ProxyUrlFor(42, EvaluationPdfKind.Open));
        // §299 OPEN-30: CehId-prefixed names (scores mandatory / openfeedback optional)...
        Assert.Equal("42-scores.pdf", SessionEvalPdfService.FileNameFor(42, EvaluationPdfKind.Score));
        Assert.Equal("42-openfeedback.pdf", SessionEvalPdfService.FileNameFor(42, EvaluationPdfKind.Open));
        // ...with the pre-rename names still resolvable as a download fallback.
        Assert.Equal("session-42-score.pdf", SessionEvalPdfService.LegacyFileNameFor(42, EvaluationPdfKind.Score));
        Assert.Equal("session-42-feedback.pdf", SessionEvalPdfService.LegacyFileNameFor(42, EvaluationPdfKind.Open));
        Assert.DoesNotContain("sharepoint", SessionEvalPdfService.ProxyUrlFor(42, EvaluationPdfKind.Open), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("score", EvaluationPdfKind.Score)]
    [InlineData("SCORE", EvaluationPdfKind.Score)]
    [InlineData("feedback", EvaluationPdfKind.Open)]
    public void ParseKind_maps_the_route_slug(string slug, EvaluationPdfKind expected)
        => Assert.Equal(expected, SessionEvalPdfService.ParseKind(slug));

    [Theory]
    [InlineData("open")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseKind_returns_null_for_an_unknown_slug(string? slug)
        => Assert.Null(SessionEvalPdfService.ParseKind(slug));

    // ---- two-kind upload + list + PROVENANCE round-trip -------------------

    [Fact]
    public async Task The_two_kinds_upload_independently_and_record_provenance()
    {
        using var db = NewDb();
        var (sessionId, organizerId, _, _) = await SeedAsync(db);
        var store = new FakePdfStore(canRead: true, canStore: true);
        var svc = NewService(db, store);

        // Before any upload: listed PER SESSION, but neither file present.
        var before = Assert.Single(await svc.ListSessionsAsync(EventId));
        Assert.Equal(sessionId, before.SessionId);
        Assert.Null(before.Score);
        Assert.Null(before.Open);
        Assert.Equal(new[] { "Own Speaker" }, before.SpeakerNames);

        // Upload ONLY the score → score present + provenance, open still null.
        await svc.UploadAsync(EventId, sessionId, EvaluationPdfKind.Score, new byte[] { 1, 2, 3 }, organizerId, "Org Person");
        var afterScore = Assert.Single(await svc.ListSessionsAsync(EventId));
        Assert.NotNull(afterScore.Score);
        Assert.Equal("Org Person", afterScore.Score!.UploadedByName);
        Assert.Equal(SessionEvalPdfService.FileNameFor(sessionId, EvaluationPdfKind.Score), afterScore.Score.FileName);
        Assert.Null(afterScore.Open);

        // Upload the open-feedback INDEPENDENTLY → both present now.
        await svc.UploadAsync(EventId, sessionId, EvaluationPdfKind.Open, new byte[] { 9 }, organizerId, "Org Person");
        var afterBoth = Assert.Single(await svc.ListSessionsAsync(EventId));
        Assert.NotNull(afterBoth.Score);
        Assert.NotNull(afterBoth.Open);

        // Both deterministic files landed in the folder.
        var names = store[Folder].Select(f => f.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[]
        {
            SessionEvalPdfService.FileNameFor(sessionId, EvaluationPdfKind.Open),
            SessionEvalPdfService.FileNameFor(sessionId, EvaluationPdfKind.Score),
        }.OrderBy(n => n).ToArray(), names);
    }

    /// <summary>
    /// 🔒 §749.3 — DECIDED (operator 2026-07-31): <i>"engine should win, overwrite the manual
    /// upload"</i>. Pinned so nobody restores the manual file as the winner on the grounds that
    /// overwriting somebody's work looks like a bug.
    /// </summary>
    /// <remarks>
    /// <para>🔑 The overwrite must be <b>attributable, not silent</b>: the provenance row is what the
    /// organiser and speaker views render, so re-stamping it to the automatic name is the only thing
    /// that lets someone see the engine took over. Leaving the organiser's name on an engine-produced
    /// file would be worse than the overwrite itself.</para>
    ///
    /// <para>🔒 <b>Open-feedback is NOT touched</b> by an automatic score publish. The engine
    /// publishes the <c>scores</c> artefact only, and asserting that here is what stops a future
    /// "publish everything" change quietly eating the one slot that stays the organiser's.</para>
    /// </remarks>
    [Fact]
    public async Task An_automatic_publish_OVERWRITES_a_manual_score_upload_and_says_who()
    {
        using var db = NewDb();
        var (sessionId, organizerId, _, _) = await SeedAsync(db);
        var store = new FakePdfStore(canRead: true, canStore: true);
        var svc = NewService(db, store);

        // An organiser gets something to the speaker by hand first.
        await svc.UploadAsync(EventId, sessionId, EvaluationPdfKind.Score,
            new byte[] { 1, 1, 1 }, organizerId, "Org Person");
        await svc.UploadAsync(EventId, sessionId, EvaluationPdfKind.Open,
            new byte[] { 7, 7 }, organizerId, "Org Person");

        // Then the engine publishes — the same deterministic name, no participant behind it.
        await svc.UploadAsync(EventId, sessionId, EvaluationPdfKind.Score,
            new byte[] { 2, 2, 2, 2 }, uploadedByParticipantId: null,
            uploadedByName: "Session Evaluation (automatic)");

        var after = Assert.Single(await svc.ListSessionsAsync(EventId));

        // The engine's file won, and the row SAYS so rather than still crediting the organiser.
        Assert.Equal("Session Evaluation (automatic)", after.Score!.UploadedByName);

        // One file per kind — a replace, never a second copy accumulating beside it.
        var scoreName = SessionEvalPdfService.FileNameFor(sessionId, EvaluationPdfKind.Score);
        Assert.Single(store[Folder].Where(f => f.Name == scoreName));
        Assert.Equal(2, store[Folder].Count);

        // 🔒 Open-feedback is untouched: still there, still the organiser's.
        Assert.NotNull(after.Open);
        Assert.Equal("Org Person", after.Open!.UploadedByName);
    }

    [Fact]
    public async Task Replace_upserts_the_same_provenance_row_in_place()
    {
        using var db = NewDb();
        var (sessionId, organizerId, _, _) = await SeedAsync(db);
        var svc = NewService(db, new FakePdfStore(canRead: true, canStore: true));

        await svc.UploadAsync(EventId, sessionId, EvaluationPdfKind.Score, new byte[] { 1 }, organizerId, "First Org");
        await svc.UploadAsync(EventId, sessionId, EvaluationPdfKind.Score, new byte[] { 2 }, organizerId, "Second Org");

        // One row (upsert, not a duplicate); newest provenance wins.
        var row = Assert.Single(db.SessionEvaluationFiles.Where(f => f.SessionId == sessionId && f.Kind == EvaluationPdfKind.Score));
        Assert.Equal("Second Org", row.UploadedByName);
    }

    // ---- which kinds exist (speaker page driver, §192d) -------------------

    [Fact]
    public async Task GetKinds_reports_score_then_both_as_files_are_added()
    {
        using var db = NewDb();
        var (sessionId, organizerId, _, _) = await SeedAsync(db);
        var svc = NewService(db, new FakePdfStore(canRead: true, canStore: true));

        Assert.Empty(await svc.GetKindsForSessionsAsync(EventId, new[] { sessionId }));

        await svc.UploadAsync(EventId, sessionId, EvaluationPdfKind.Score, new byte[] { 1 }, organizerId, "Org Person");
        var scoreOnly = await svc.GetKindsForSessionsAsync(EventId, new[] { sessionId });
        Assert.True(scoreOnly[sessionId].Contains(EvaluationPdfKind.Score));
        Assert.False(scoreOnly[sessionId].Contains(EvaluationPdfKind.Open));

        await svc.UploadAsync(EventId, sessionId, EvaluationPdfKind.Open, new byte[] { 2 }, organizerId, "Org Person");
        var both = await svc.GetKindsForSessionsAsync(EventId, new[] { sessionId });
        Assert.True(both[sessionId].Contains(EvaluationPdfKind.Score));
        Assert.True(both[sessionId].Contains(EvaluationPdfKind.Open));
    }

    // ---- the ACCESS GATE for BOTH kinds (the security-critical part) ------

    [Theory]
    [InlineData(EvaluationPdfKind.Score)]
    [InlineData(EvaluationPdfKind.Open)]
    public async Task Proxy_allows_an_organizer_and_the_own_session_speaker_but_denies_a_foreign_speaker(EvaluationPdfKind kind)
    {
        using var db = NewDb();
        var (sessionId, organizerId, ownSpeakerId, otherSpeakerId) = await SeedAsync(db);
        var store = new FakePdfStore(canRead: true, canStore: true);
        var svc = NewService(db, store);
        await svc.UploadAsync(EventId, sessionId, kind, new byte[] { 9, 9, 9 }, organizerId, "Org Person");

        // Organizer in this edition → allowed.
        var asOrg = await svc.GetPdfForParticipantAsync(EventId, organizerId, ParticipantRole.Organizer, sessionId, kind);
        Assert.NotNull(asOrg);
        Assert.Equal(SessionEvalPdfService.FileNameFor(sessionId, kind), asOrg!.FileName);
        Assert.NotEmpty(asOrg.Content);

        // The speaker ON this session → allowed.
        Assert.NotNull(await svc.GetPdfForParticipantAsync(EventId, ownSpeakerId, ParticipantRole.Speaker, sessionId, kind));

        // A DIFFERENT speaker, not on this session → denied (→ 404).
        Assert.Null(await svc.GetPdfForParticipantAsync(EventId, otherSpeakerId, ParticipantRole.Speaker, sessionId, kind));
    }

    [Fact]
    public async Task Proxy_denies_when_the_session_belongs_to_another_edition()
    {
        using var db = NewDb();
        var (sessionId, organizerId, _, _) = await SeedAsync(db);
        var svc = NewService(db, new FakePdfStore(canRead: true, canStore: true));
        await svc.UploadAsync(EventId, sessionId, EvaluationPdfKind.Score, new byte[] { 1 }, organizerId, "Org Person");

        // Same organizer id, WRONG event id → denied.
        Assert.Null(await svc.GetPdfForParticipantAsync(EventId + 1, organizerId, ParticipantRole.Organizer, sessionId, EvaluationPdfKind.Score));
    }

    [Fact]
    public async Task Proxy_returns_null_for_a_kind_that_was_not_uploaded()
    {
        using var db = NewDb();
        var (sessionId, organizerId, _, _) = await SeedAsync(db);
        var svc = NewService(db, new FakePdfStore(canRead: true, canStore: true));

        // Only the score is uploaded → the open-feedback download 404s.
        await svc.UploadAsync(EventId, sessionId, EvaluationPdfKind.Score, new byte[] { 1 }, organizerId, "Org Person");
        Assert.NotNull(await svc.GetPdfForParticipantAsync(EventId, organizerId, ParticipantRole.Organizer, sessionId, EvaluationPdfKind.Score));
        Assert.Null(await svc.GetPdfForParticipantAsync(EventId, organizerId, ParticipantRole.Organizer, sessionId, EvaluationPdfKind.Open));
    }

    // ---- inert when not configured ----------------------------------------

    [Fact]
    public async Task Is_inert_when_the_store_cannot_read_or_the_folder_is_blank()
    {
        using var db = NewDb();
        var (sessionId, organizerId, _, _) = await SeedAsync(db);

        // Store can't read/write → still lists sessions (per-session), nothing streamed, nothing faked.
        var noStore = NewService(db, new FakePdfStore(canRead: false, canStore: false));
        Assert.False(noStore.CanRead);
        Assert.False(noStore.CanManage);
        var row = Assert.Single(await noStore.ListSessionsAsync(EventId));
        Assert.Null(row.Score);
        Assert.Null(row.Open);
        Assert.Null(await noStore.GetPdfForParticipantAsync(EventId, organizerId, ParticipantRole.Organizer, sessionId, EvaluationPdfKind.Score));

        // Folder blank → also inert even with a capable store.
        var blank = NewService(db, new FakePdfStore(canRead: true, canStore: true), folder: "");
        Assert.False(blank.CanRead);
        Assert.False(blank.CanManage);
    }

    // ---- speaker contacts to notify ---------------------------------------

    [Fact]
    public async Task Speaker_contacts_returns_the_sessions_speakers_with_an_email()
    {
        using var db = NewDb();
        var (sessionId, _, _, _) = await SeedAsync(db);
        var svc = NewService(db, new FakePdfStore(canRead: true, canStore: true));

        var contacts = await svc.GetSpeakerContactsAsync(EventId, sessionId);
        var only = Assert.Single(contacts);
        Assert.Equal("own@example.test", only.Email);
        Assert.Equal("Own Speaker", only.FullName);
    }

    [Fact]
    public async Task Speaker_contacts_honor_the_contact_email_override()
    {
        // §234 5: the "your evaluation PDF is ready" notify must reach the speaker's
        // PREFERRED inbox — the SpeakerProfile.ContactEmailOverride wins over the
        // identity address, matching the welcome / evaluation-results routing.
        using var db = NewDb();
        var (sessionId, _, ownSpeakerId, _) = await SeedAsync(db);
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = EventId, ParticipantId = ownSpeakerId,
            ContactEmailOverride = "preferred@example.test",
        });
        await db.SaveChangesAsync();
        var svc = NewService(db, new FakePdfStore(canRead: true, canStore: true));

        var only = Assert.Single(await svc.GetSpeakerContactsAsync(EventId, sessionId));
        Assert.Equal("preferred@example.test", only.Email);
        Assert.Equal(ownSpeakerId, only.ParticipantId);   // §169 magic-link id unchanged
    }

    // ---- helpers -----------------------------------------------------------

    private static SharePointFileRef File(string name) =>
        new("item-" + name, name, "https://store.example.test/" + name);

    /// <summary>In-memory fake store: list/download/upload/delete against one folder.</summary>
    private sealed class FakePdfStore : ISharePointFileStore
    {
        private readonly Dictionary<string, List<SharePointFileRef>> _byFolder = new(StringComparer.OrdinalIgnoreCase);

        public FakePdfStore(bool canRead, bool canStore) { CanRead = canRead; CanStore = canStore; }

        public List<SharePointFileRef> this[string folder]
        {
            get
            {
                if (!_byFolder.TryGetValue(folder, out var list)) { list = new(); _byFolder[folder] = list; }
                return list;
            }
        }

        public bool CanRead { get; }
        public bool CanStore { get; }

        public Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SharePointFileRef>>(
                CanRead && _byFolder.TryGetValue(relativeFolder, out var list) ? list.ToList() : new List<SharePointFileRef>());

        public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(CanRead ? new byte[] { 1, 2, 3 } : null);

        public Task<StoredFile> UploadToFolderAsync(
            string relativeFolder, string fileName, byte[] content, string contentType, CancellationToken ct = default)
        {
            if (!CanStore) throw new InvalidOperationException("cannot write");
            this[relativeFolder].RemoveAll(f => f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            this[relativeFolder].Add(File(fileName));
            return Task.FromResult(new StoredFile($"{relativeFolder}/{fileName}", "https://store.example.test/" + fileName, "item-" + fileName));
        }

        public Task DeleteFromFolderAsync(string relativeFolder, string fileName, CancellationToken ct = default)
        {
            this[relativeFolder].RemoveAll(f => f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }

        // Write-to-root side unused by §192.
        public Task<StoredFile> StoreAsync(string relativePath, byte[] content, string contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("root store not used by §192");
        public Task DeleteAsync(string relativePath, CancellationToken ct = default) => Task.CompletedTask;
    }
}
