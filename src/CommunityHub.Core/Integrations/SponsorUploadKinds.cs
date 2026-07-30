using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Uploads;

/// <summary>
/// §494b — the ONE definition of a sponsor upload destination, shared by the classic multipart
/// handler on Company Details and the §494 direct-to-storage endpoints.
///
/// <para><b>Why it exists.</b> Both paths must agree on folder, allowed types, size cap, the
/// versioned file name and what gets recorded afterwards. Two copies of those rules is exactly the
/// shape that produced §482 (a list and an editor that quietly described different things), so the
/// rules live here once and both callers read them.</para>
/// </summary>
public sealed record SponsorUploadSpec(
    string Folder,
    string Prefix,
    IReadOnlyList<string> Exts,
    long MaxBytes,
    IReadOnlyList<string>? Notify,
    bool IsWall);

public static class SponsorUploadKinds
{
    private const long Mb = 1024 * 1024;

    /// <summary>
    /// Resolve an upload kind to its destination, or null when the kind is unknown or its folder
    /// is not configured. Mirrors <c>CompanyDetailsModel.ResolveKind</c> exactly — same folders,
    /// prefixes, extensions and caps.
    /// </summary>
    public static SponsorUploadSpec? Resolve(string? kind, SharePointEditionConfig? sp) =>
        sp is null || string.IsNullOrWhiteSpace(sp.SiteUrl)
            ? null
            : kind switch
            {
                "some" when !string.IsNullOrWhiteSpace(sp.LogoSoMeBrandingFolderPath) =>
                    new(sp.LogoSoMeBrandingFolderPath, "SoMeBrandingLogo_", new[] { ".png" },
                        5 * Mb, sp.SponsorUploadNotify, false),
                "print" when !string.IsNullOrWhiteSpace(sp.LogoPrintFolderPath) =>
                    new(sp.LogoPrintFolderPath, "PrintLogo_", new[] { ".eps", ".ai", ".pdf" },
                        25 * Mb, sp.SponsorUploadNotify, false),
                "zoho" when !string.IsNullOrWhiteSpace(sp.LogoZohoFolderPath) =>
                    new(sp.LogoZohoFolderPath, "ZohoLogo_", new[] { ".png" },
                        5 * Mb, sp.SponsorUploadNotifyZoho, false),
                // Exhibitor wall = print artwork: vector/PDF only, up to 1 GB. This is THE case
                // direct-to-storage exists for — a gigabyte was never sane to push through a
                // request thread.
                "wall" when !string.IsNullOrWhiteSpace(sp.ExhibitorWallFolderPath) =>
                    new(sp.ExhibitorWallFolderPath, "ExhibitorWall_", new[] { ".eps", ".ai", ".pdf" },
                        1024 * Mb, sp.SponsorUploadNotify, true),
                _ => null,
            };

    /// <summary>
    /// §494b — record a completed sponsor upload: the logo pointer (for the three LOGO kinds) and
    /// the §68 upload audit row that lets every coordinator of a shared company see the current
    /// file instead of re-uploading it.
    ///
    /// <para>The exhibitor WALL is deliberately not written to the logo fields — it is print
    /// artwork, not a company logo, and recording it there would make the wizard's "Logos &amp;
    /// artwork" step read as done when no logo exists.</para>
    /// </summary>
    public static async Task RecordAsync(
        CommunityHubDbContext db, string kind, int eventId, string companyId,
        string fileName, string? webUrl, string email, DateTimeOffset now, CancellationToken ct)
    {
        if (kind is "some" or "zoho" or "print")
        {
            var info = await db.SponsorInfos
                .FirstOrDefaultAsync(s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);
            if (info is null)
            {
                info = new SponsorInfo { EventId = eventId, SponsorCompanyId = companyId };
                db.SponsorInfos.Add(info);
            }

            if (kind == "print") { info.LogoVectorPath = webUrl; info.LogoVectorFileName = fileName; }
            else                 { info.LogoRasterPath = webUrl; info.LogoRasterFileName = fileName; }
            info.LastUpdatedByEmail = email;
        }

        db.SponsorUploadAudits.Add(new SponsorUploadAudit
        {
            EventId = eventId,
            SponsorCompanyId = companyId,
            Kind = kind,
            FileName = fileName,
            Version = ParseVersion(fileName),
            WebUrl = webUrl,
            UploadedByEmail = email,
            UploadedAt = now,
        });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// §494d — notify the organizers / designer that a sponsor uploaded a file.
    ///
    /// <para><b>Why this is here and not left to the caller.</b> The classic multipart handler sent
    /// this mail; my first cut of the direct-to-storage path did not, so a direct upload would have
    /// landed silently and the designer would never have known artwork had arrived. Exactly the
    /// two-paths-drift problem this file exists to prevent — caught while writing the GUI test for
    /// it, which is the point of writing the test.</para>
    ///
    /// <para>Never throws: a notification failure must not undo an upload that already succeeded.</para>
    /// </summary>
    public static async Task NotifyAsync(
        Core.Email.IEmailSender email, SponsorUploadSpec spec, string sponsorName,
        string fileName, string? webUrl, string byEmail, ILogger? log, CancellationToken ct)
    {
        if (spec.Notify is null || spec.Notify.Count == 0) return;

        static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        var button = string.IsNullOrWhiteSpace(webUrl)
            ? ""
            : $"<p style=\"margin:18px 0 4px;\"><a href=\"{Enc(webUrl)}\" "
              + "style=\"display:inline-block;padding:14px 30px;background-color:#1565c0;"
              + "color:#ffffff !important;font-weight:700;font-size:16px;border-radius:6px;"
              + "text-decoration:none;\">Open file</a></p>";
        var html =
            $"<p>Sponsor <b>{Enc(sponsorName)}</b> uploaded a new file via Company Details.</p>"
            + $"<ul><li><b>File:</b> {Enc(fileName)}</li>"
            + $"<li><b>Uploaded by:</b> {Enc(byEmail)}</li></ul>"
            + button;
        var subject = $"Sponsor upload — {sponsorName} — {fileName} [ELDK27]";

        foreach (var to in spec.Notify)
        {
            try { await email.SendAsync(to, subject, html, ct); }
            catch (Exception ex) { log?.LogWarning(ex, "Sponsor upload notify to {To} failed.", to); }
        }
    }

    /// <summary>The trailing <c>_v{N}</c> of a versioned name, or 1 when absent.</summary>
    public static int ParseVersion(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var i = stem.LastIndexOf("_v", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && int.TryParse(stem[(i + 2)..], out var v) && v > 0 ? v : 1;
    }
}
