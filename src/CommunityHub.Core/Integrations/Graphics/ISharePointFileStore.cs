namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>One stored file's location: the path it lives at + a link to it.</summary>
/// <param name="Path">The SharePoint path the file is stored at (stable, key-derived).</param>
/// <param name="WebUrl">The hub→SharePoint link to download / preview the file.</param>
/// <param name="ItemId">The Graph driveItem id, when the live store wrote it (null otherwise).</param>
public sealed record StoredFile(string Path, string WebUrl, string? ItemId);

/// <summary>
/// One file observed inside a SharePoint folder when PULLING operator-uploaded
/// graphics (REQUIREMENTS §18 — SoMe-graphics pull). Carries just what the pull +
/// display flow needs: the Graph item id (to download / target), the file name
/// (matched by its slug against a session title) and the web URL (the display
/// link surfaced through the organizer-review → speaker-download flow).
/// </summary>
/// <param name="ItemId">The Graph driveItem id.</param>
/// <param name="Name">The file name including extension (e.g. <c>my-session.png</c>).</param>
/// <param name="WebUrl">The hub→SharePoint link to download / preview the file.</param>
public sealed record SharePointFileRef(string ItemId, string Name, string WebUrl);

/// <summary>
/// The SoMe-graphics SharePoint FILE-STORE seam (REQUIREMENTS §18): store bytes
/// under a STABLE, key-derived path, hand back a download URL, and delete by path.
///
/// Follows the established repo pattern (cf. <see cref="IBackstageSpeakerBioApi"/>):
/// a clean interface with a no-op <see cref="NullSharePointFileStore"/> default —
/// never a faked external call. The live Graph-backed implementation
/// (<see cref="GraphSharePointFileStore"/>) is wired only when the per-edition
/// site / drive / root folder are configured AND the Code-Management SPN holds the
/// Sites.Selected grant; until then the null store (<see cref="CanStore"/> = false)
/// is the default and nothing is faked.
///
/// THE STABLE-KEY CONTRACT: callers pass a key-derived <paramref name="relativePath"/>
/// (e.g. <c>Speakers/speaker-42.png</c>). Re-storing the SAME path REPLACES the bytes
/// in place (same path ⇒ same URL) so an organizer overrule never breaks the link.
/// </summary>
public interface ISharePointFileStore
{
    /// <summary>
    /// Whether this implementation can perform a real SharePoint write. False for
    /// the null default (no wired site/grant) — the engine then still computes the
    /// stable path + intended URL but stores nothing live and fakes no call.
    /// </summary>
    bool CanStore { get; }

    /// <summary>
    /// Store (create or REPLACE) a file at <paramref name="relativePath"/> under the
    /// configured root, returning its stable path + download URL. Replacing an
    /// existing path keeps the URL stable (the overrule contract). Only called when
    /// <see cref="CanStore"/> is true.
    /// </summary>
    Task<StoredFile> StoreAsync(
        string relativePath, byte[] content, string contentType, CancellationToken ct = default);

    /// <summary>Delete the file at <paramref name="relativePath"/> (idempotent — missing = no-op).</summary>
    Task DeleteAsync(string relativePath, CancellationToken ct = default);

    /// <summary>
    /// Whether this implementation can perform a real SharePoint READ (list / download)
    /// — the PULL side of the seam (REQUIREMENTS §18). False for the null default; the
    /// SoMe-graphics PULL feature is then INERT (lists nothing, downloads nothing) and
    /// fakes no call. Mirrors <see cref="CanStore"/> for the write side.
    /// </summary>
    bool CanRead { get; }

    /// <summary>
    /// List the files directly inside <paramref name="relativeFolder"/> (a
    /// drive-relative folder path the operator configured), each with its item id,
    /// name and web URL. Returns an empty list when <see cref="CanRead"/> is false or
    /// the folder is missing — never throws on a missing folder, so the PULL is inert
    /// until configured. Only called for a real read when <see cref="CanRead"/> is true.
    /// </summary>
    Task<IReadOnlyList<SharePointFileRef>> ListAsync(string relativeFolder, CancellationToken ct = default);

    /// <summary>
    /// Download the raw bytes of a drive item by its Graph id (used to materialise a
    /// pulled graphic). Returns null when <see cref="CanRead"/> is false or the item
    /// no longer exists (tolerated by callers).
    /// </summary>
    Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default);

    /// <summary>
    /// Store (create or REPLACE) a file <paramref name="fileName"/> inside a
    /// DRIVE-RELATIVE <paramref name="relativeFolder"/> (the same folder shape
    /// <see cref="ListAsync"/> reads — NOT under the graphics root). Used by the
    /// org-admin session-evaluation-QR upload (REQUIREMENTS §124). Only called when
    /// <see cref="CanStore"/> is true; the null default throws so nothing is faked.
    /// </summary>
    Task<StoredFile> UploadToFolderAsync(
        string relativeFolder, string fileName, byte[] content, string contentType,
        CancellationToken ct = default);

    /// <summary>
    /// Delete <paramref name="fileName"/> from a DRIVE-RELATIVE
    /// <paramref name="relativeFolder"/> (idempotent — missing = no-op). The
    /// write-side counterpart of <see cref="ListAsync"/> (REQUIREMENTS §124).
    /// </summary>
    Task DeleteFromFolderAsync(
        string relativeFolder, string fileName, CancellationToken ct = default);
}

/// <summary>
/// Default no-op file store: there is no wired SharePoint site / Sites.Selected
/// grant (operator config not in this repo), so this performs no call and cannot
/// store. The engine still computes the stable path / intended URL (for a dry-run
/// or test) but no Graph call is ever faked. Live wiring is 🟡 (pending) —
/// REQUIREMENTS §18.
/// </summary>
public sealed class NullSharePointFileStore : ISharePointFileStore
{
    public bool CanStore => false;

    public Task<StoredFile> StoreAsync(
        string relativePath, byte[] content, string contentType, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            "No wired SharePoint file store (site/drive/root + Sites.Selected grant "
            + "are operator config not in this repo). Do not call StoreAsync when "
            + "CanStore is false.");

    public Task DeleteAsync(string relativePath, CancellationToken ct = default) =>
        Task.CompletedTask; // delete is idempotent; a missing live store has nothing to delete

    public bool CanRead => false;

    public Task<IReadOnlyList<SharePointFileRef>> ListAsync(
        string relativeFolder, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SharePointFileRef>>(Array.Empty<SharePointFileRef>());

    public Task<byte[]?> DownloadAsync(string itemId, CancellationToken ct = default) =>
        Task.FromResult<byte[]?>(null); // nothing wired — nothing to download, nothing faked

    public Task<StoredFile> UploadToFolderAsync(
        string relativeFolder, string fileName, byte[] content, string contentType,
        CancellationToken ct = default) =>
        throw new InvalidOperationException(
            "No wired SharePoint file store (site/drive/root + Sites.Selected grant "
            + "are operator config not in this repo). Do not call UploadToFolderAsync "
            + "when CanStore is false.");

    public Task DeleteFromFolderAsync(
        string relativeFolder, string fileName, CancellationToken ct = default) =>
        Task.CompletedTask; // delete is idempotent; a missing live store has nothing to delete
}
