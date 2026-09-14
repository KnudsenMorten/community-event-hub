using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
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
/// <param name="Folder">Resolved from the document-library registry — never from a config literal.</param>
/// <param name="Kind">The kind token, so the caller can build the file name via
/// <see cref="SponsorUploadNaming"/> rather than assembling one itself.</param>
/// <param name="Exts">Permitted extensions; empty ⇒ any format.</param>
public sealed record SponsorUploadSpec(
    string Folder,
    string Kind,
    IReadOnlyList<string> Exts,
    long MaxBytes,
    IReadOnlyList<string>? Notify,
    bool IsWall)
{
    /// <summary>True when <paramref name="ext"/> may be uploaded for this kind.</summary>
    /// <remarks>An EMPTY <see cref="Exts"/> means "any format" — the exhibitor wall. Asking here
    /// rather than at four call sites keeps that empty-means-everything rule from being read as
    /// "nothing is allowed" by whichever caller is written next.</remarks>
    public bool Accepts(string? ext) =>
        Exts.Count == 0
        || (ext is not null && Exts.Contains(ext, StringComparer.OrdinalIgnoreCase));

    /// <summary>Human-readable list of what is allowed, for a validation message.</summary>
    public string AllowedText => Exts.Count == 0 ? "any file type" : string.Join(", ", Exts);
}

public static class SponsorUploadKinds
{
    private const long Mb = 1024 * 1024;

    /// <summary>
    /// THE definition of what an upload kind means: destination folder, permitted extensions, size
    /// cap, who is notified, and whether it goes direct-to-storage. Null when the kind is unknown or
    /// its folder does not resolve.
    /// </summary>
    /// <remarks>
    /// <para>🔒 §768 — this used to say <i>"Mirrors CompanyDetailsModel.ResolveKind exactly"</i>, and
    /// it did: the sponsor Company Details page carried a second, hand-maintained copy of these
    /// rules. §768.13 collapsed that copy and §768.14 collapsed a THIRD in the Get Started wizard,
    /// whose own comment described itself as <i>"helpers replicated from CompanyDetailsModel so the
    /// two stay byte-consistent"</i>. <b>A comment asserting that two copies agree is not a mechanism
    /// for keeping them in agreement</b> — the next edit to one of them is where they part company,
    /// and the symptom would be a sponsor's file landing under a different name depending on which
    /// page they uploaded from.</para>
    ///
    /// <para>🔒 §768.14 — the folder now comes from <see cref="IDocLibraryPathResolver"/>, not from
    /// the edition config. <paramref name="sp"/> is still required, but only for the things that are
    /// genuinely not paths: the site the upload seam authenticates against, and the notification
    /// recipients.</para>
    ///
    /// <para>The <c>"zoho"</c> kind is GONE (§6.7): one Web logo now serves both the promotion
    /// graphics and the external event system. It resolves to null rather than to a plausible
    /// folder, so a stale form field fails loudly instead of writing somewhere nothing reads.</para>
    /// </remarks>
    public static SponsorUploadSpec? Resolve(
        string? kind, IDocLibraryPathResolver paths, SharePointEditionConfig? sp)
    {
        if (sp is null || string.IsNullOrWhiteSpace(sp.SiteUrl) || paths is null) return null;

        var (key, exts, maxBytes, notify, isWall) = kind switch
        {
            "some" => (DocLibraryPaths.SponsorLogoWeb, new[] { ".png" },
                       5 * Mb, sp.SponsorUploadNotify, false),
            "print" => (DocLibraryPaths.SponsorLogoPrint, new[] { ".eps", ".ai", ".pdf" },
                        25 * Mb, sp.SponsorUploadNotify, false),
            // The exhibitor wall takes ANY format (work order §111) and up to 1 GB. This is THE case
            // direct-to-storage exists for — a gigabyte was never sane to push through a request
            // thread.
            // §1072 — the WALL has its own audience: the organizer mailbox PLUS whoever produces the
            // wall. 🔒 Falls back to the shared list when the edition has not set it, so this is
            // additive and an unconfigured edition behaves exactly as before.
            "wall" => (DocLibraryPaths.SponsorExhibitorWall, Array.Empty<string>(),
                       1024 * Mb,
                       sp.SponsorWallUploadNotify is { Count: > 0 }
                           ? sp.SponsorWallUploadNotify
                           : sp.SponsorUploadNotify,
                       true),
            _ => (null!, null!, 0L, null!, false),
        };

        if (key is null) return null;
        if (!paths.TryResolve(key, out var folder)) return null;

        return new SponsorUploadSpec(folder, kind!, exts, maxBytes, notify, isWall);
    }

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
        if (kind is "some" or "print")
        {
            // 🔒 §1034 — upsert: bypass the sponsor filter or a hidden row becomes a duplicate.
            var info = await db.SponsorInfos.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);
            if (info is null)
            {
                info = new SponsorInfo { EventId = eventId, SponsorCompanyId = companyId, IsSponsor = true };
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
        string fileName, string? webUrl, string byEmail, ILogger? log, CancellationToken ct,
        // 🔴 §1072 — REQUIRED IN PRACTICE, optional only so no existing caller breaks. See below.
        Core.Email.IEmailContextAccessor? ctx = null)
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

        // 🔴 §1072 — RING-EXEMPT, OR THIS MAIL IS SILENTLY EATEN.
        //
        // ⚠️ THE ACTUAL CAUSE of *"nerdio have upload logo … but no mail"*. Everything upstream
        // worked: the file reached SharePoint, the audit row was written, this method ran and
        // composed the mail. Then `BrevoEmailSender` dropped every recipient as an **unknown
        // recipient**, because `mok@expertslive.dk` and `sb@homeworkers.dk` are ORGANIZER MAILBOXES,
        // not participant rows, and the per-recipient ring gate fails closed on an address it
        // cannot resolve to a ring. Correctly — that is what stops a typo'd address being mailed.
        //
        // 🔴 THE SAME DEFECT, A THIRD TIME IN ONE DAY: §1060(i)'s auto-approve notice to info@, and
        // §1061's audit/health reporting of the same drops. ⇒ The rule, written where the next
        // person will hit it: **a mail to a FIXED operator mailbox must declare itself ops mail.**
        // An address that is not a participant has no ring, and "no ring" means "dropped".
        //
        // 🔑 It is invisible from here: this method's own try/catch sees SUCCESS — the send did not
        // throw, it was refused two layers down. Nothing in this file could have reported it.
        using var _ = ctx?.Set(new Core.Email.EmailContext("sponsor-upload-notify", RingExempt: true));

        foreach (var to in spec.Notify)
        {
            try { await email.SendAsync(to, subject, html, ct); }
            catch (Exception ex) { log?.LogWarning(ex, "Sponsor upload notify to {To} failed.", to); }
        }
    }

    /// <summary>The version a stored name carries, or 1. See <see cref="SponsorUploadNaming.ParseVersion"/>.</summary>
    public static int ParseVersion(string fileName) => SponsorUploadNaming.ParseVersion(fileName);

    /// <summary>
    /// The next free file name for a sponsor upload in <paramref name="folder"/> — THE one
    /// implementation, shared by all four upload paths.
    /// </summary>
    /// <remarks>
    /// <para>🔒 §768.14 — this existed in FOUR hand-copied forms (Company Details, the direct-upload
    /// endpoints, the sponsor artefact uploader and the Get Started wizard). They agreed by
    /// inspection only, and each one built the name itself, which is how the writers and
    /// <c>ResolveNewestSponsorLogo</c> came to disagree about where the version number lives.</para>
    ///
    /// <para>⚠️ A listing failure must NOT block the upload: it falls back to version 1. Overwriting
    /// a same-named file is recoverable; refusing a sponsor's artwork at a deadline is not. The
    /// listing is also the reason this cannot be a pure function — the next version is a fact about
    /// the folder, not about the request.</para>
    /// </remarks>
    public static async Task<string> NextVersionedNameAsync(
        Core.Integrations.SharePointUploadClient sp,
        string siteUrl, string driveName, string folder,
        string kind, string? sponsorName, string ext,
        ILogger? log, CancellationToken ct)
    {
        var next = 1;
        try
        {
            foreach (var file in await sp.ListFolderFilesAsync(siteUrl, driveName, folder, ct))
            {
                if (SponsorUploadNaming.Matches(file.Name, kind, sponsorName, out var v) && v >= next)
                    next = v + 1;
            }
        }
        catch (Exception ex)
        {
            log?.LogWarning(
                ex, "Sponsor upload: could not list '{Folder}' to version the {Kind} name; using v1.",
                folder, kind);
        }

        return SponsorUploadNaming.Build(kind, sponsorName, next, ext);
    }
}
