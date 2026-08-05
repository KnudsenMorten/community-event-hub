using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>One session-evaluation QR file in the SharePoint folder (REQUIREMENTS §124).</summary>
/// <param name="FileName">The raw file name including extension (e.g. <c>Room-16-Floor-1-Device13.png</c>).</param>
/// <param name="RoomDisplay">The room parsed from the file name, dashes turned to spaces (e.g. "Room 16").</param>
/// <param name="ItemId">The Graph driveItem id, used to download the bytes.</param>
/// <param name="WebUrl">The SharePoint preview/download URL (may be empty).</param>
/// <param name="SessionId">
/// 🔑 §749.2 — the CEH session this file belongs to, parsed from a <c>session-{id}-…</c> name.
/// NULL for a legacy room-named file, which no longer matches anything: §748 replaced the per-ROOM
/// model with one QR per SESSION (operator: <i>"1 per session"</i>), and the operator's follow-up was
/// explicit — <i>"current is linked to room name but now we use sessionname"</i>.
/// </param>
public sealed record SessionEvalQrFile(
    string FileName, string RoomDisplay, string ItemId, string WebUrl, int? SessionId = null);

/// <summary>A session (id + its assigned room) to match against the room-QR files.</summary>
public sealed record SessionRoomRef(int SessionId, string? Room);

/// <summary>The bytes of a downloaded QR, ready to stream as a file response.</summary>
public sealed record DownloadedQr(byte[] Content, string FileName, string ContentType);

/// <summary>
/// Reads the per-ROOM session-evaluation QR codes an operator keeps in a SharePoint
/// folder (REQUIREMENTS §124) and matches each to a session by ROOM NAME, so a
/// speaker can DOWNLOAD the QR for the room their session is in. Reuses the §110/§68
/// <see cref="ISharePointFileStore"/> read seam (<see cref="ISharePointFileStore.ListAsync"/>
/// / <see cref="ISharePointFileStore.DownloadAsync"/>) plus its write seam for the
/// org-admin upload/replace. The folder lives at
/// <see cref="GraphicsSharePointOptions.SessionEvalsQrFolderPath"/>.
///
/// INERT until configured: with no wired store (<see cref="ISharePointFileStore.CanRead"/>
/// false) or no folder set, every read returns empty and every download returns null —
/// nothing is faked and nothing errors. Files are named with the room in the name,
/// e.g. <c>Room-16-Floor-1-Device13.png</c> (room = "Room 16"); matching is by a
/// normalized room key so spaces / dashes / casing / a trailing "(Keynote)" don't
/// break the match.
/// </summary>
public sealed class SessionEvalsQrService
{
    private readonly ISharePointFileStore _store;
    private readonly GraphicsSharePointOptions _options;

    private readonly DocLibrary.IDocLibraryPathResolver _paths;

    public SessionEvalsQrService(
        ISharePointFileStore store, IOptions<GraphicsSharePointOptions> options,
        DocLibrary.IDocLibraryPathResolver paths)
    {
        _store = store;
        _options = options.Value;
        _paths = paths;
    }

    /// <summary>
    /// §768 — resolved from the registry. This was <c>SessionEvalsQrFolderPath</c>, which still
    /// named <c>Speakers/SessionEvals-QR</c> after the library was reorganised to
    /// <c>Speakers/SessionEvaluations/QR</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ The stale value did not fail loudly, because this is a READ: a listing of a folder that no
    /// longer exists comes back EMPTY, and every caller reads that as "no QR codes have been
    /// uploaded yet". <c>TryResolve</c> preserves the inert-when-unconfigured behaviour rather than
    /// throwing at an organizer.
    /// </remarks>
    private string Folder =>
        _paths.TryResolve(DocLibrary.DocLibraryPaths.SessionEvaluationQr, out var p)
            ? p
            : string.Empty;
    private bool FolderSet => !string.IsNullOrWhiteSpace(Folder);

    /// <summary>True when the QR folder is wired for READS (speaker download links are offered).</summary>
    public bool CanRead => _store.CanRead && FolderSet;

    /// <summary>True when the QR folder is wired for WRITES (org-admin upload/replace is offered).</summary>
    public bool CanManage => _store.CanStore && FolderSet;

    // ===================================================================
    //  READ — list + match + download
    // ===================================================================

    /// <summary>
    /// List every QR file in the folder (org-admin view). Empty + inert when not
    /// configured. Ordered by room display for a stable list.
    /// </summary>
    public async Task<IReadOnlyList<SessionEvalQrFile>> ListAllAsync(CancellationToken ct = default)
    {
        if (!CanRead) return Array.Empty<SessionEvalQrFile>();

        var files = await _store.ListAsync(Folder, ct);
        return files
            .Select(f => new SessionEvalQrFile(
                f.Name, RoomDisplayFromFileName(f.Name), f.ItemId, f.WebUrl, SessionIdFromFileName(f.Name)))
            .OrderBy(f => f.RoomDisplay, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// §749.2 — the CEH session id out of a generated QR file name
    /// (<c>session-{id}-{title-slug}-qr.png</c>, produced by
    /// <c>SessionQrCodeService.FileName</c>). NULL for anything else.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Deliberately strict.</b> It matches the generator's own prefix and nothing looser — a
    /// fuzzy rule that also accepted, say, a bare <c>17.png</c> would eventually bind a printed code
    /// to the wrong session, and a QR pointing at someone else's talk is worse than one that does not
    /// resolve at all. The id must be the FIRST segment after <c>session-</c> and must be digits.
    /// </remarks>
    public static int? SessionIdFromFileName(string? fileName)
    {
        var name = (fileName ?? string.Empty).Trim();
        const string prefix = "session-";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = name[prefix.Length..];
        var end = 0;
        while (end < rest.Length && char.IsAsciiDigit(rest[end])) end++;
        if (end == 0) return null;

        // The digits must END the segment — "session-12x-…" is not session 12.
        if (end < rest.Length && rest[end] is not ('-' or '.')) return null;

        return int.TryParse(rest[..end], out var id) ? id : null;
    }

    /// <summary>
    /// Match each given session to ITS OWN QR file, listing the folder at most ONCE. Returns a map
    /// sessionId → matched file; sessions with no matching file are simply absent. Empty + inert
    /// when not configured.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>§749.2 — matching is by SESSION now, not by room.</b> Operator 2026-07-31:
    /// <i>"current is linked to room name but now we use sessionname"</i>. §748 had already made the
    /// QR a property of the session rather than the room (<i>"1 per session"</i>), so a room-keyed
    /// lookup was the last piece still describing the old model.</para>
    ///
    /// <para>🔒 <b>There is deliberately NO room-name fallback.</b> It is tempting — legacy files are
    /// still sitting in the folder — but a fallback would hand a speaker the QR for whoever else used
    /// their room, and a code that opens the WRONG session's feedback form is far worse than one that
    /// does not resolve. Legacy room-named files now match nothing, which is the honest outcome.</para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, SessionEvalQrFile>> MatchSessionsAsync(
        IEnumerable<SessionRoomRef> sessions, CancellationToken ct = default)
    {
        var result = new Dictionary<int, SessionEvalQrFile>();
        if (!CanRead) return result;

        var files = await ListAllAsync(ct);
        if (files.Count == 0) return result;

        var bySession = new Dictionary<int, SessionEvalQrFile>();
        foreach (var f in files.Where(f => f.SessionId is not null))
        {
            // First wins, and the list is name-ordered, so the pick is stable across calls rather
            // than depending on whatever order the store happened to return.
            bySession.TryAdd(f.SessionId!.Value, f);
        }

        foreach (var s in sessions)
        {
            if (bySession.TryGetValue(s.SessionId, out var match)) result[s.SessionId] = match;
        }

        return result;
    }

    /// <summary>
    /// Download the QR file for ONE session, or null when not configured / no such file / the item
    /// is gone. The caller must already have confirmed the session belongs to this speaker.
    /// </summary>
    public async Task<DownloadedQr?> DownloadForSessionAsync(int sessionId, CancellationToken ct = default)
    {
        if (!CanRead) return null;

        var files = await ListAllAsync(ct);
        var match = files.FirstOrDefault(f => f.SessionId == sessionId);
        if (match is null) return null;

        var bytes = await _store.DownloadAsync(match.ItemId, ct);
        if (bytes is null) return null;

        return new DownloadedQr(bytes, match.FileName, ContentTypeForFile(match.FileName));
    }

    /// <summary>
    /// Download the QR file matching <paramref name="room"/> (the bytes), or null when
    /// not configured / no room / no match / the item is gone. Used by the speaker
    /// "Download room QR" handler after the caller has confirmed the room belongs to one
    /// of the speaker's OWN sessions.
    /// </summary>
    public async Task<DownloadedQr?> DownloadForRoomAsync(string? room, CancellationToken ct = default)
    {
        if (!CanRead) return null;

        var files = await ListAllAsync(ct);
        if (files.Count == 0) return null;

        var indexed = files.Select(f => (file: f, norm: NormalizeRoom(f.RoomDisplay))).ToList();
        var match = MatchRoom(room, indexed);
        if (match is null) return null;

        var bytes = await _store.DownloadAsync(match.ItemId, ct);
        if (bytes is null) return null;

        return new DownloadedQr(bytes, match.FileName, ContentTypeForFile(match.FileName));
    }

    // ===================================================================
    //  WRITE — org-admin upload / replace / delete
    // ===================================================================

    /// <summary>
    /// Upload (create or REPLACE) a QR file in the folder, keyed by its file name.
    /// Throws <see cref="InvalidOperationException"/> when the store cannot write
    /// (callers gate on <see cref="CanManage"/> first).
    /// </summary>
    public Task<StoredFile> UploadAsync(
        string fileName, byte[] content, string contentType, CancellationToken ct = default) =>
        _store.UploadToFolderAsync(Folder, fileName, content, contentType, ct);

    /// <summary>Delete a QR file from the folder by name (idempotent).</summary>
    public Task DeleteAsync(string fileName, CancellationToken ct = default) =>
        _store.DeleteFromFolderAsync(Folder, fileName, ct);

    // ===================================================================
    //  Matching helpers (room name ⇄ file name)
    // ===================================================================

    private static SessionEvalQrFile? MatchRoom(
        string? sessionRoom, IReadOnlyList<(SessionEvalQrFile file, string norm)> files)
    {
        var sr = NormalizeRoom(sessionRoom);
        if (sr.Length == 0) return null;

        // 1) exact normalized room match (the common case).
        foreach (var (file, norm) in files)
        {
            if (norm.Length > 0 && norm == sr) return file;
        }

        // 2) tolerant containment either direction — covers a file room that carries an
        // extra descriptor ("Hall A1 Keynote" vs a session "Hall A1"), or a session room
        // that is just the number/short code ("16" → "Room 16"). Guard on length ≥ 2 so a
        // 1-char stub can't swallow every session.
        foreach (var (file, norm) in files)
        {
            if (norm.Length >= 2 && sr.Length >= 2 && (norm.Contains(sr) || sr.Contains(norm)))
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>
    /// The ROOM portion of a QR file name: drop the extension, then keep the leading
    /// dash-separated segments up to (but not including) the first "Floor" / "Device"
    /// marker, joined with spaces. <c>Room-16-Floor-1-Device13.png</c> → "Room 16";
    /// <c>Hall-A1-Keynote-Floor-2-Device3.png</c> → "Hall A1 Keynote".
    /// </summary>
    public static string RoomDisplayFromFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;

        var dot = fileName.LastIndexOf('.');
        var stem = dot > 0 ? fileName[..dot] : fileName;

        var parts = stem.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var keep = new List<string>();
        foreach (var p in parts)
        {
            if (p.Equals("Floor", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("Device", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            keep.Add(p);
        }

        // No recognizable marker? Fall back to the whole stem (dashes → spaces).
        return keep.Count > 0 ? string.Join(' ', keep) : stem.Replace('-', ' ').Trim();
    }

    /// <summary>Lower-cased letters+digits only — the comparable room key for matching.</summary>
    public static string NormalizeRoom(string? room)
    {
        if (string.IsNullOrWhiteSpace(room)) return string.Empty;
        var sb = new System.Text.StringBuilder(room.Length);
        foreach (var ch in room)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static string ContentTypeForFile(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        var ext = dot >= 0 ? fileName[(dot + 1)..].ToLowerInvariant() : string.Empty;
        return ext switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "svg" => "image/svg+xml",
            "pdf" => "application/pdf",
            _ => "application/octet-stream",
        };
    }
}
