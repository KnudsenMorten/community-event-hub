namespace CommunityHub.Core.Integrations;

/// <summary>
/// One provisioned room QR code: the deep-link the QR encodes plus the URL of the
/// generated QR image stored on SharePoint.
/// </summary>
/// <param name="TargetUrl">The URL the QR encodes (the room deep-link).</param>
/// <param name="ImageUrl">The SharePoint URL of the stored QR-code image file.</param>
public sealed record RoomQr(string TargetUrl, string ImageUrl);

/// <summary>
/// The per-room QR-code provisioning seam (REQUIREMENTS § session QR). Each physical
/// room gets ONE QR linked to the room; the generated QR image is stored on
/// <b>SharePoint via URL</b> and that URL is written back onto the room's sessions
/// (<c>Session.RoomQrUrl</c>). The speaker's per-session "Download QR" button then
/// serves that stored image (drop into a PowerPoint).
///
/// Follows the established repo pattern (cf. <see cref="IBackstageSpeakerBioApi"/>):
/// a clean interface with a no-op <see cref="NullRoomQrProvider"/> default — never a
/// faked external call.
///
/// HONEST STATUS (◻ live wiring pending): the real SharePoint site / drive / root
/// folder and the SPN creds that authorise the upload are <b>operator config</b>
/// (gitignored / Key Vault), not in this repo, so the default registration is the
/// Null provider (<see cref="CanProvision"/> = false) which performs NO SharePoint
/// call. A live implementation generates the QR image (e.g. via the existing
/// <see cref="SharePointUploadClient"/> seam to store it) and returns a real
/// <see cref="RoomQr"/>; no caller changes.
/// </summary>
public interface IRoomQrProvider
{
    /// <summary>
    /// Whether this implementation can actually generate + store a QR on SharePoint.
    /// False for the null default (no wired SharePoint site / creds) — callers then
    /// skip provisioning rather than fake a URL.
    /// </summary>
    bool CanProvision { get; }

    /// <summary>
    /// Generate the QR for a room (encoding <paramref name="targetUrl"/>), store the
    /// image on SharePoint and return the stored image URL alongside the encoded
    /// target. <paramref name="eventCode"/> + <paramref name="room"/> scope the
    /// stored file path so re-provisioning a room overwrites the same file
    /// (idempotent). Only called when <see cref="CanProvision"/> is true.
    /// </summary>
    Task<RoomQr> EnsureRoomQrAsync(
        string eventCode,
        string room,
        string targetUrl,
        CancellationToken ct = default);
}

/// <summary>
/// Default no-op implementation: there is no wired SharePoint site / drive / SPN for
/// QR storage (creds/endpoints are operator config not in this repo), so this cannot
/// provision and performs no call. The hub still tracks which rooms WOULD get a QR;
/// the live wiring is ◻ (pending) and no SharePoint call is ever faked.
/// </summary>
public sealed class NullRoomQrProvider : IRoomQrProvider
{
    public bool CanProvision => false;

    public Task<RoomQr> EnsureRoomQrAsync(
        string eventCode, string room, string targetUrl, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            "No wired SharePoint site/drive/SPN for room-QR storage (creds/endpoints "
            + "are operator config not in this repo). Do not call EnsureRoomQrAsync "
            + "when CanProvision is false.");
}
