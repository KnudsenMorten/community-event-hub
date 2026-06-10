namespace CommunityHub.Core.Domain;

/// <summary>
/// One provisioned per-sponsor SharePoint upload folder we monitor for new
/// or changed files. Persisted by <c>SponsorOrderPullService</c> when it
/// pre-creates a folder + mints the anonymous edit-link, and read by the
/// upload watcher to know which folders to poll and who to email when
/// something appears in them.
///
/// One row per (event, sponsor company, folder key) -- e.g. one for the
/// sponsor's LOGO folder and one for their SPONSORWALL folder.
/// </summary>
public class SponsorUploadLocation
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>Company Manager company id (matches <see cref="ParticipantTask.SponsorCompanyId"/>).</summary>
    public string SponsorCompanyId { get; set; } = string.Empty;

    /// <summary>Display name used in notification subjects + folder paths.</summary>
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>Stable per-edition key for this folder kind (e.g. "logo", "wall"). Mirrors the task's upload subfolder so a config tweak does not orphan rows.</summary>
    public string FolderKey { get; set; } = string.Empty;

    /// <summary>Subfolder name under <c>{rootFolderPath}/{companyName}/</c> (e.g. "LOGO", "SPONSORWALL").</summary>
    public string Subfolder { get; set; } = string.Empty;

    /// <summary>Full relative path of the folder under the drive root, e.g. <c>General/Events/.../2LINKIT/LOGO</c>.</summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>Anonymous edit-link URL the sponsor clicks straight to (also embedded in the task description).</summary>
    public string EditLinkUrl { get; set; } = string.Empty;

    /// <summary>Comma-separated notification recipients ("a@x,b@y"). Empty = watcher skips.</summary>
    public string NotifyEmailsCsv { get; set; } = string.Empty;

    /// <summary>Subject template; <c>{{companyName}}</c> resolved at notification time.</summary>
    public string NotifySubject { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<SponsorUploadFile> Files { get; set; } = new();
}
