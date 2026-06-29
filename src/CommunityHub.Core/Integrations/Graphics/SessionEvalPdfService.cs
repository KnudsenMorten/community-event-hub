using System.Text.RegularExpressions;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>One non-service session in the organizer "Final evaluation PDFs" list (REQUIREMENTS §166).</summary>
/// <param name="SessionId">The session id (also the deterministic file key, <c>session-{id}.pdf</c>).</param>
/// <param name="Title">The session title.</param>
/// <param name="SpeakerNames">The session's speakers, alphabetical (for display next to the row).</param>
/// <param name="HasPdf">True when a final evaluation PDF has already been uploaded for this session.</param>
public sealed record SessionEvalPdfRow(
    int SessionId, string Title, IReadOnlyList<string> SpeakerNames, bool HasPdf);

/// <summary>A downloaded final-evaluation PDF, ready to stream as a file response (REQUIREMENTS §166).</summary>
public sealed record SessionEvalPdfDownload(byte[] Content, string FileName);

/// <summary>One speaker to notify after an upload (their email + name) — REQUIREMENTS §166.</summary>
public sealed record SessionSpeakerContact(string Email, string FullName);

/// <summary>
/// FINAL per-session evaluation PDFs (REQUIREMENTS §166): an organizer uploads the final
/// evaluation PDF for a session, it is stored on SharePoint with a DETERMINISTIC name
/// (<c>session-{id}.pdf</c>) via the same <see cref="ISharePointFileStore"/> write seam the
/// QR upload (§124) uses, and it is streamed back to the session's speaker(s) through a HUB
/// PROXY (<c>/session-eval/{id}/download</c>) using the app's own credentials — speakers have
/// no SharePoint access and must NEVER receive a SharePoint URL (the exact bug fixed for
/// graphics §160). Mirrors <see cref="SessionEvalsQrService"/> (read/write seam) and
/// <see cref="GraphicsService.GetSpeakerGraphicFileAsync"/> (proxy + access gate).
///
/// INERT until configured: with no wired store (<see cref="ISharePointFileStore.CanStore"/> /
/// <see cref="ISharePointFileStore.CanRead"/> false) or no folder set, every read returns
/// empty and every download returns null — nothing is faked and nothing errors. The folder
/// lives at <see cref="GraphicsSharePointOptions.SessionEvalPdfFolderPath"/>.
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

    /// <summary>The deterministic SharePoint file name for a session's final evaluation PDF.</summary>
    public static string FileNameFor(int sessionId) => $"session-{sessionId}.pdf";

    /// <summary>The HUB PROXY url stored on <see cref="Session.EvaluationFormUrl"/> (NEVER a SharePoint URL).</summary>
    public static string ProxyUrlFor(int sessionId) => $"/session-eval/{sessionId}/download";

    private static readonly Regex FileNamePattern =
        new(@"^session-(\d+)\.pdf$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ===================================================================
    //  Organizer list — every non-service session + its upload status
    // ===================================================================

    /// <summary>
    /// List every non-service session in the edition (title + speaker names) with a
    /// PDF-uploaded status, scheduled ones first then by title. The folder is listed at most
    /// ONCE; when the store can't read, every row's <see cref="SessionEvalPdfRow.HasPdf"/> is
    /// false (the status simply degrades, the list still renders).
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
                s.StartsAt,
                Speakers = s.SessionSpeakers.Select(ss => ss.Participant.FullName).ToList(),
            })
            .ToListAsync(ct);

        // Which session ids already have a "session-{id}.pdf" in the folder (one list call).
        var uploaded = await ListUploadedSessionIdsAsync(ct);

        return rows
            .OrderBy(r => r.StartsAt == null)            // scheduled first
            .ThenBy(r => r.StartsAt)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .Select(r => new SessionEvalPdfRow(
                r.Id,
                r.Title,
                r.Speakers
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                uploaded.Contains(r.Id)))
            .ToList();
    }

    /// <summary>
    /// The set of session ids that already have a final evaluation PDF in the folder
    /// (parsed from the <c>session-{id}.pdf</c> file names). Empty + inert when not configured.
    /// </summary>
    public async Task<HashSet<int>> ListUploadedSessionIdsAsync(CancellationToken ct = default)
    {
        var set = new HashSet<int>();
        if (!CanRead) return set;

        var files = await _store.ListAsync(Folder, ct);
        foreach (var f in files)
        {
            var id = ParseSessionId(f.Name);
            if (id is not null) set.Add(id.Value);
        }
        return set;
    }

    private static int? ParseSessionId(string fileName)
    {
        var m = FileNamePattern.Match(fileName ?? string.Empty);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    // ===================================================================
    //  Organizer write — upload (create or REPLACE) the session's PDF
    // ===================================================================

    /// <summary>
    /// Upload (create or REPLACE) the final evaluation PDF for a session, keyed by the
    /// deterministic <see cref="FileNameFor"/> name. Throws
    /// <see cref="InvalidOperationException"/> when the store cannot write (callers gate on
    /// <see cref="CanManage"/> first).
    /// </summary>
    public Task<StoredFile> UploadAsync(int sessionId, byte[] content, CancellationToken ct = default) =>
        _store.UploadToFolderAsync(Folder, FileNameFor(sessionId), content, "application/pdf", ct);

    /// <summary>
    /// The session's speakers (email + name) to notify after an upload — scoped to the
    /// edition. Skips rows with no email.
    /// </summary>
    public async Task<IReadOnlyList<SessionSpeakerContact>> GetSpeakerContactsAsync(
        int eventId, int sessionId, CancellationToken ct = default)
    {
        var contacts = await _db.SessionSpeakers
            .Where(ss => ss.SessionId == sessionId && ss.Session.EventId == eventId)
            .Select(ss => new SessionSpeakerContact(ss.Participant.Email, ss.Participant.FullName))
            .ToListAsync(ct);

        return contacts
            .Where(c => !string.IsNullOrWhiteSpace(c.Email))
            .ToList();
    }

    // ===================================================================
    //  Speaker/organizer PROXY download — gated, never a SharePoint URL
    // ===================================================================

    /// <summary>
    /// SERVER-PROXIED download of a session's final evaluation PDF (§166). ACCESS GATE: the
    /// caller is allowed when they are an ORGANIZER in this edition OR a
    /// <see cref="SessionSpeaker"/> on the session — any other participant gets null (→ 404).
    /// Streams the bytes from SharePoint with the app's creds so a speaker (no SharePoint
    /// permission) actually gets the file. Returns null when not configured / not allowed /
    /// no file / the item is gone.
    /// </summary>
    public async Task<SessionEvalPdfDownload?> GetPdfForParticipantAsync(
        int eventId, int participantId, ParticipantRole role, int sessionId,
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

        var fileName = FileNameFor(sessionId);
        var files = await _store.ListAsync(Folder, ct);
        var match = files.FirstOrDefault(
            f => f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (match is null) return null;

        var bytes = await _store.DownloadAsync(match.ItemId, ct);
        if (bytes is null || bytes.Length == 0) return null;

        return new SessionEvalPdfDownload(bytes, fileName);
    }
}
