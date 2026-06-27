using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>One session-evaluation QR file in the SharePoint folder (REQUIREMENTS §124).</summary>
/// <param name="FileName">The raw file name including extension (e.g. <c>Room-16-Floor-1-Device13.png</c>).</param>
/// <param name="RoomDisplay">The room parsed from the file name, dashes turned to spaces (e.g. "Room 16").</param>
/// <param name="ItemId">The Graph driveItem id, used to download the bytes.</param>
/// <param name="WebUrl">The SharePoint preview/download URL (may be empty).</param>
public sealed record SessionEvalQrFile(string FileName, string RoomDisplay, string ItemId, string WebUrl);

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

    public SessionEvalsQrService(
        ISharePointFileStore store, IOptions<GraphicsSharePointOptions> options)
    {
        _store = store;
        _options = options.Value;
    }

    private string Folder => _options.SessionEvalsQrFolderPath;
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
            .Select(f => new SessionEvalQrFile(f.Name, RoomDisplayFromFileName(f.Name), f.ItemId, f.WebUrl))
            .OrderBy(f => f.RoomDisplay, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Match each given session to the QR file for its ROOM (by normalized room name),
    /// listing the folder at most ONCE. Returns a map sessionId → matched file for the
    /// sessions that matched; sessions with no room / no matching file are simply
    /// absent. Empty + inert when not configured.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, SessionEvalQrFile>> MatchSessionsAsync(
        IEnumerable<SessionRoomRef> sessions, CancellationToken ct = default)
    {
        var result = new Dictionary<int, SessionEvalQrFile>();
        if (!CanRead) return result;

        var files = await ListAllAsync(ct);
        if (files.Count == 0) return result;

        // Pre-compute the normalized room key per file once.
        var indexed = files.Select(f => (file: f, norm: NormalizeRoom(f.RoomDisplay))).ToList();

        foreach (var s in sessions)
        {
            if (result.ContainsKey(s.SessionId)) continue;
            var match = MatchRoom(s.Room, indexed);
            if (match is not null) result[s.SessionId] = match;
        }

        return result;
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
