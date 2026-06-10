namespace CommunityHub.Core.Domain;

/// <summary>
/// One file observed by the upload watcher in a <see cref="SponsorUploadLocation"/>
/// folder. The watcher upserts these rows on every poll: a new row triggers
/// a "file uploaded" email, a changed <see cref="ETag"/> (sponsor replaced
/// the file) triggers a "file updated" email. Deletions are tolerated -- a
/// missing file is not removed from this table on its own (so a one-poll
/// blip does not re-fire emails when the file reappears unchanged).
/// </summary>
public class SponsorUploadFile
{
    public int Id { get; set; }

    public int SponsorUploadLocationId { get; set; }
    public SponsorUploadLocation Location { get; set; } = null!;

    /// <summary>Display name (e.g. "logo.eps"). Not necessarily unique across versions.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Graph driveItem id -- the stable identity within the drive.</summary>
    public string GraphItemId { get; set; } = string.Empty;

    /// <summary>Graph ETag -- changes when the file is replaced. Drives the "updated" notification.</summary>
    public string? ETag { get; set; }

    public DateTimeOffset? LastModifiedUtc { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastNotifiedAt { get; set; }
}
