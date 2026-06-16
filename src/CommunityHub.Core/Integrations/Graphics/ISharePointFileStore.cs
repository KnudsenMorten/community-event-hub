namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>One stored file's location: the path it lives at + a link to it.</summary>
/// <param name="Path">The SharePoint path the file is stored at (stable, key-derived).</param>
/// <param name="WebUrl">The hub→SharePoint link to download / preview the file.</param>
/// <param name="ItemId">The Graph driveItem id, when the live store wrote it (null otherwise).</param>
public sealed record StoredFile(string Path, string WebUrl, string? ItemId);

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
}
