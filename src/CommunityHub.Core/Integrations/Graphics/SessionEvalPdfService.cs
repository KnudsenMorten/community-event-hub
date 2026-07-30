using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>One uploaded evaluation file's PROVENANCE for display (REQUIREMENTS §192c).</summary>
/// <param name="UploadedByName">Display name of the organizer who uploaded / last replaced it.</param>
/// <param name="UploadedAt">When it was uploaded / last replaced.</param>
/// <param name="FileName">The deterministic SharePoint file name.</param>
public sealed record SessionEvalFileInfo(string UploadedByName, DateTimeOffset UploadedAt, string FileName);

/// <summary>
/// One non-service session in the organizer "Session evaluations" list (REQUIREMENTS §192).
/// Each session can carry up to TWO evaluation PDFs — a <paramref name="Score"/> and an
/// <paramref name="Open"/> (open-feedback) file — each with its own provenance, or null when
/// not uploaded.
/// </summary>
/// <param name="SessionId">The session id (also the file-key + proxy-route key).</param>
/// <param name="Title">The session title.</param>
/// <param name="Room">The session's room, when assigned (a linking detail, NOT the grouping).</param>
/// <param name="SpeakerNames">The session's speakers, alphabetical.</param>
/// <param name="Score">The SCORE PDF provenance, or null when not uploaded.</param>
/// <param name="Open">The OPEN-feedback PDF provenance, or null when not uploaded.</param>
public sealed record SessionEvalPdfRow(
    int SessionId, string Title, string? Room, IReadOnlyList<string> SpeakerNames,
    SessionEvalFileInfo? Score, SessionEvalFileInfo? Open);

/// <summary>A downloaded evaluation PDF, ready to stream as a file response (REQUIREMENTS §192).</summary>
public sealed record SessionEvalPdfDownload(byte[] Content, string FileName);

/// <summary>
/// One speaker to notify after an upload (their email + name) — REQUIREMENTS §166/§192.
/// <see cref="ParticipantId"/> is the speaker's login Participant id, so the notify
/// email can carry their §169 personal auto-login magic-link (§190).
/// </summary>
public sealed record SessionSpeakerContact(string Email, string FullName, int ParticipantId);

/// <summary>
/// FINAL per-session evaluation PDFs (REQUIREMENTS §192, reworking §166): an organizer
/// uploads up to TWO PDFs per session — a <b>Score</b> PDF and an <b>Open-feedback</b> PDF —
/// each stored on SharePoint under a DETERMINISTIC, kind-tagged name
/// (<c>{CehId}-scores.pdf</c> — mandatory for all sessions — / <c>{CehId}-openfeedback.pdf</c>
/// — optional, exists for some — per §299 OPEN-30; legacy <c>session-{id}-*</c> names are
/// still served on download) via the same
/// <see cref="ISharePointFileStore"/> write seam, and each streamed back to the session's
/// speaker(s) through a HUB PROXY (<c>/session-eval/{id}/{kind}/download</c>) using the app's
/// own credentials — speakers have no SharePoint access and must NEVER receive a SharePoint
/// URL (the exact bug fixed for graphics §160).
///
/// PROVENANCE (§192c): every upload records WHO + WHEN in <see cref="SessionEvaluationFile"/>
/// (one row per (session, kind), upserted on replace). That table — not a store listing — is
/// the source of "which kinds exist" + the "uploaded by {name} on {when}" display, so the
/// organizer/speaker views work even with the store unread; the store is only touched to
/// stream the actual bytes on download.
///
/// INERT until configured: with no wired store (<see cref="ISharePointFileStore.CanStore"/> /
/// <see cref="ISharePointFileStore.CanRead"/> false) or no folder set, uploads are disabled
/// and every download returns null — nothing is faked and nothing errors. The folder lives at
/// <see cref="GraphicsSharePointOptions.SessionEvalPdfFolderPath"/>.
/// </summary>
public sealed class SessionEvalPdfService
{
    private readonly ISharePointFileStore _store;
    private readonly GraphicsSharePointOptions _options;
    private readonly CommunityHubDbContext _db;

    public SessionEvalPdfService(
        ISharePointFileStore store,
        IOptions<GraphicsSharePointOptions> options,
        CommunityHubDbContext db)
    {
        _store = store;
        _options = options.Value;
        _db = db;
    }

    private string Folder => _options.SessionEvalPdfFolderPath;
    private bool FolderSet => !string.IsNullOrWhiteSpace(Folder);

    /// <summary>True when the folder is wired for READS (the speaker download proxy can serve).</summary>
    public bool CanRead => _store.CanRead && FolderSet;

    /// <summary>True when the folder is wired for WRITES (the organizer upload is offered).</summary>
    public bool CanManage => _store.CanStore && FolderSet;

    /// <summary>All evaluation kinds, in display order (Score first).</summary>
    public static readonly IReadOnlyList<EvaluationPdfKind> AllKinds =
        new[] { EvaluationPdfKind.Score, EvaluationPdfKind.Open };

    /// <summary>The URL route slug for a kind (<c>score</c> / <c>feedback</c>) — unchanged so
    /// proxy links already sent in speaker emails keep resolving.</summary>
    public static string KindSlug(EvaluationPdfKind kind) =>
        kind == EvaluationPdfKind.Open ? "feedback" : "score";

    /// <summary>The FILE-NAME suffix for a kind (§299 OPEN-30, operator 2026-07-23):
    /// <c>scores</c> (mandatory for all sessions) / <c>openfeedback</c> (optional).</summary>
    public static string FileSuffix(EvaluationPdfKind kind) =>
        kind == EvaluationPdfKind.Open ? "openfeedback" : "scores";

    /// <summary>Parse a route slug (<c>score</c>/<c>feedback</c>) to a kind; null when unknown.</summary>
    public static EvaluationPdfKind? ParseKind(string? slug) => (slug ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "score" => EvaluationPdfKind.Score,
        "feedback" => EvaluationPdfKind.Open,
        _ => null,
    };

    /// <summary>
    /// The deterministic SharePoint file name for a session's evaluation PDF of a kind —
    /// the CEH session id as prefix (§299 OPEN-30): <c>{CehId}-scores.pdf</c> /
    /// <c>{CehId}-openfeedback.pdf</c>. This is also the naming contract the external
    /// evaluation system will use when it pushes PDFs into the DocLibrary folder itself.
    /// </summary>
    public static string FileNameFor(int sessionId, EvaluationPdfKind kind) =>
        $"{sessionId}-{FileSuffix(kind)}.pdf";

    /// <summary>The pre-OPEN-30 name (<c>session-{id}-score.pdf</c> / <c>-feedback.pdf</c>) —
    /// kept ONLY as a download fallback for files uploaded before the rename.</summary>
    public static string LegacyFileNameFor(int sessionId, EvaluationPdfKind kind) =>
        $"session-{sessionId}-{KindSlug(kind)}.pdf";

    /// <summary>The HUB PROXY url for a kind (stored/handed out, NEVER a SharePoint URL).</summary>
    public static string ProxyUrlFor(int sessionId, EvaluationPdfKind kind) =>
        $"/session-eval/{sessionId}/{KindSlug(kind)}/download";

    // ===================================================================
    //  Organizer list — every non-service session + both files' provenance
    // ===================================================================

    /// <summary>
    /// List every non-service session in the edition (title + room + speaker names) with the
    /// provenance of its Score and Open-feedback PDFs (each null when not uploaded). The list
    /// is PER SESSION (§192a) — scheduled ones first, then by title. Provenance is read from
    /// <see cref="SessionEvaluationFile"/> (the upload audit), so it does not need the store.
    /// </summary>
    public async Task<IReadOnlyList<SessionEvalPdfRow>> ListSessionsAsync(
        int eventId, CancellationToken ct = default)
    {
        var rows = await _db.Sessions
            .Where(s => s.EventId == eventId && !s.IsServiceSession)
            .Select(s => new
            {
                s.Id,
                s.Title,
                s.Room,
                s.StartsAt,
                Speakers = s.SessionSpeakers.Select(ss => ss.Participant.FullName).ToList(),
            })
            .ToListAsync(ct);

        var files = await LoadFilesAsync(eventId, null, ct);

        return rows
            .OrderBy(r => r.StartsAt == null)            // scheduled first
            .ThenBy(r => r.StartsAt)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .Select(r =>
            {
                files.TryGetValue((r.Id, EvaluationPdfKind.Score), out var score);
                files.TryGetValue((r.Id, EvaluationPdfKind.Open), out var open);
                return new SessionEvalPdfRow(
                    r.Id,
                    r.Title,
                    string.IsNullOrWhiteSpace(r.Room) ? null : r.Room,
                    r.Speakers
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    score is null ? null : new SessionEvalFileInfo(score.UploadedByName, score.UploadedAt, score.FileName),
                    open is null ? null : new SessionEvalFileInfo(open.UploadedByName, open.UploadedAt, open.FileName));
            })
            .ToList();
    }

    /// <summary>
    /// The kinds of evaluation PDF that EXIST for each of the given sessions in an edition
    /// (§192d) — drives the speaker page's per-kind buttons (Open-feedback renders only when
    /// present). Sessions with no files simply have no entry.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlySet<EvaluationPdfKind>>> GetKindsForSessionsAsync(
        int eventId, IEnumerable<int> sessionIds, CancellationToken ct = default)
    {
        var ids = sessionIds?.Distinct().ToList() ?? new List<int>();
        var result = new Dictionary<int, IReadOnlySet<EvaluationPdfKind>>();
        if (ids.Count == 0) return result;

        var rows = await _db.SessionEvaluationFiles
            .Where(f => f.EventId == eventId && ids.Contains(f.SessionId))
            .Select(f => new { f.SessionId, f.Kind })
            .ToListAsync(ct);

        foreach (var g in rows.GroupBy(r => r.SessionId))
            result[g.Key] = g.Select(x => x.Kind).ToHashSet();
        return result;
    }

    private async Task<Dictionary<(int SessionId, EvaluationPdfKind Kind), SessionEvaluationFile>> LoadFilesAsync(
        int eventId, int? sessionId, CancellationToken ct)
    {
        var q = _db.SessionEvaluationFiles.Where(f => f.EventId == eventId);
        if (sessionId is not null) q = q.Where(f => f.SessionId == sessionId);
        var rows = await q.ToListAsync(ct);
        return rows.ToDictionary(f => (f.SessionId, f.Kind));
    }

    // ===================================================================
    //  Organizer write — upload (create or REPLACE) a session's PDF + provenance
    // ===================================================================

    /// <summary>
    /// Upload (create or REPLACE) the evaluation PDF of a given <paramref name="kind"/> for a
    /// session, keyed by the deterministic <see cref="FileNameFor"/> name, and UPSERT its
    /// provenance row (who/when — §192c). Throws <see cref="InvalidOperationException"/> when
    /// the store cannot write (callers gate on <see cref="CanManage"/> first).
    /// </summary>
    public async Task UploadAsync(
        int eventId, int sessionId, EvaluationPdfKind kind, byte[] content,
        int? uploadedByParticipantId, string uploadedByName, CancellationToken ct = default)
    {
        var fileName = FileNameFor(sessionId, kind);
        await _store.UploadToFolderAsync(Folder, fileName, content, "application/pdf", ct);

        var row = await _db.SessionEvaluationFiles
            .FirstOrDefaultAsync(f => f.SessionId == sessionId && f.Kind == kind, ct);
        if (row is null)
        {
            row = new SessionEvaluationFile { EventId = eventId, SessionId = sessionId, Kind = kind };
            _db.SessionEvaluationFiles.Add(row);
        }
        row.EventId = eventId;
        row.UploadedByParticipantId = uploadedByParticipantId;
        row.UploadedByName = string.IsNullOrWhiteSpace(uploadedByName) ? "Organizer" : uploadedByName.Trim();
        row.UploadedAt = DateTimeOffset.UtcNow;
        row.FileName = fileName;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The session's speakers (email + name) to notify after an upload — scoped to the
    /// edition. Skips rows with no email. §234 5: each speaker's EFFECTIVE address is
    /// resolved the same way the other speaker mail does (the welcome, the evaluation
    /// RESULTS mail): the <see cref="SpeakerProfile.ContactEmailOverride"/> wins when set,
    /// else the participant's identity address — so the "your evaluation PDF is ready"
    /// notify reaches the speaker's PREFERRED inbox instead of ignoring the override.
    /// </summary>
    public async Task<IReadOnlyList<SessionSpeakerContact>> GetSpeakerContactsAsync(
        int eventId, int sessionId, CancellationToken ct = default)
    {
        var contacts = await _db.SessionSpeakers
            .Where(ss => ss.SessionId == sessionId && ss.Session.EventId == eventId)
            .Select(ss => new SessionSpeakerContact(
                ss.Participant.Email, ss.Participant.FullName, ss.ParticipantId))
            .ToListAsync(ct);

        var ids = contacts.Select(c => c.ParticipantId).ToList();
        var overrides = await _db.SpeakerProfiles
            .Where(sp => ids.Contains(sp.ParticipantId)
                         && sp.ContactEmailOverride != null
                         && sp.ContactEmailOverride != "")
            .ToDictionaryAsync(sp => sp.ParticipantId, sp => sp.ContactEmailOverride!, ct);

        return contacts
            .Select(c => overrides.TryGetValue(c.ParticipantId, out var ov)
                ? c with { Email = ov }
                : c)
            .Where(c => !string.IsNullOrWhiteSpace(c.Email))
            .ToList();
    }

    // ===================================================================
    //  Speaker/organizer PROXY download — gated, never a SharePoint URL
    // ===================================================================

    /// <summary>
    /// SERVER-PROXIED download of a session's evaluation PDF of a <paramref name="kind"/>
    /// (§192). ACCESS GATE: the caller is allowed when they are an ORGANIZER in this edition
    /// OR a <see cref="SessionSpeaker"/> on the session — any other participant gets null
    /// (→ 404). Streams the bytes from SharePoint with the app's creds. Returns null when not
    /// configured / not allowed / no file / the item is gone.
    /// </summary>
    public async Task<SessionEvalPdfDownload?> GetPdfForParticipantAsync(
        int eventId, int participantId, ParticipantRole role, int sessionId, EvaluationPdfKind kind,
        CancellationToken ct = default)
    {
        if (!CanRead) return null;

        // Access gate: an organizer in this edition, or a speaker on this very session.
        bool allowed = role == ParticipantRole.Organizer
            ? await _db.Sessions.AnyAsync(s => s.Id == sessionId && s.EventId == eventId, ct)
            : await _db.SessionSpeakers.AnyAsync(
                ss => ss.SessionId == sessionId
                      && ss.ParticipantId == participantId
                      && ss.Session.EventId == eventId, ct);
        if (!allowed) return null;

        var fileName = FileNameFor(sessionId, kind);
        var legacyName = LegacyFileNameFor(sessionId, kind);
        var files = await _store.ListAsync(Folder, ct);
        var match = files.FirstOrDefault(
                        f => f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    // OPEN-30 rename fallback: serve files uploaded under the old name.
                    ?? files.FirstOrDefault(
                        f => f.Name.Equals(legacyName, StringComparison.OrdinalIgnoreCase));
        if (match is null) return null;

        var bytes = await _store.DownloadAsync(match.ItemId, ct);
        if (bytes is null || bytes.Length == 0) return null;

        return new SessionEvalPdfDownload(bytes, fileName);
    }
}
