namespace CommunityHub.Core.Domain;

/// <summary>
/// Audit of one sponsor self-service upload made on the Company Details page —
/// the logo / exhibitor-wall uploads that write straight to SharePoint via
/// <c>SharePointUploadClient</c> (NOT the watcher-monitored anonymous-edit-link
/// folders, which use <see cref="SponsorUploadLocation"/> / <see cref="SponsorUploadFile"/>).
///
/// REQUIREMENTS §68: a company is shared by many event-coordinators, so on page
/// LOAD we must show the file that was ALREADY uploaded (name + version + when +
/// by WHOM) for every upload control, otherwise the whole team re-uploads. The
/// versioned SharePoint path persists (e.g. <see cref="SponsorInfo.LogoRasterPath"/>),
/// but it carries no uploaded-when / uploaded-by, and the three logo kinds share
/// only two logo slots — so this table records every upload with its uploader and
/// timestamp, keyed by upload <see cref="Kind"/>, and the page reads the LATEST
/// row per kind to render the "current file" line.
///
/// Scoped to (EventId, SponsorCompanyId, Kind). One row PER upload (history is
/// kept); the newest <see cref="UploadedAt"/> is the current file.
/// </summary>
public class SponsorUploadAudit
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>WooCommerce / Company Manager company id (same key as SponsorInfo).</summary>
    public string SponsorCompanyId { get; set; } = string.Empty;

    /// <summary>Upload control this row belongs to: "some", "print", "zoho", "wall".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The versioned file name written to SharePoint (e.g. <c>SoMeBrandingLogo_2LINKIT_v3.png</c>).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>The version number parsed from the file name (the <c>_vN</c> suffix), or 1.</summary>
    public int Version { get; set; } = 1;

    /// <summary>SharePoint web URL of the uploaded file (for the "open" link). Null if unknown.</summary>
    public string? WebUrl { get; set; }

    /// <summary>Email of the signed-in coordinator who performed the upload.</summary>
    public string UploadedByEmail { get; set; } = string.Empty;

    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}
