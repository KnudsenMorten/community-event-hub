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

    /// <summary>
    /// §598 — when a COMPLETE folder read confirmed this file is no longer in SharePoint. Null while
    /// the file is present, or while its state is unknown.
    /// </summary>
    /// <remarks>
    /// 🔒 WHY A STAMP, NOT A DELETE. Operator 2026-07-28: *"i have manually deleted these files on
    /// sharepoint, but ceh still remember them. it should check if the files actually exist in the
    /// sharepoint folder with the name, otherwiswe it should null the value (as it hasn't
    /// uploaded)."* CEH treated a stored path as proof of upload forever, so the wizard step read
    /// DONE with nothing behind it.
    ///
    /// <para>The row is STAMPED rather than deleted because it is an AUDIT TRAIL — who uploaded
    /// what, when. Deleting it would trade one lie for another. A stamped row is excluded from "what
    /// has this company delivered", so the task correctly reverts to not-done and the reminders
    /// resume, while the history survives.</para>
    ///
    /// <para>⚠️ ONLY a read that SUCCEEDED and was COMPLETE may set this. An empty or failed listing
    /// is indistinguishable from "everything was deleted", and acting on one would wipe every
    /// sponsor's artefacts at once during a transient outage — the §553/§555 rule that saved the
    /// live agenda earlier today, when a 401 looked exactly like "0 sessions".</para>
    /// </remarks>
    public DateTimeOffset? ArtefactMissingAt { get; set; }
}
