using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>Which logo slot a wizard upload fills.</summary>
public enum VolumePackageLogoKind
{
    Web = 0,
    Print = 1,
}

/// <summary>
/// §1077 stage 3 — THE COMPANY'S OWN WIZARD: what they approve, who coordinates it, their logo and
/// their LinkedIn page. Reached by a token link, with no hub account.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11, the four steps: <i>"1. Participation … 2. Event coordinator … 3. Logo
/// … 4. LinkedIn URL"</i>, plus an explicit <i>"we do not want to participate"</i> that
/// <b>cancels the wizard AND its reminders</b>.</para>
///
/// <para>🔴 <b>The token WRITES, so the §1040 read-only leg is gone and the rest must carry it.</b>
/// Every method here resolves the token to exactly ONE <see cref="VolumePackageCompany"/> and writes
/// only that row. There is no company id in any signature that a caller could substitute, no listing
/// of other companies, and nothing that reads another person's data. ⇒ A forwarded link is bounded
/// by construction: it can misrepresent this company's own wishes and nothing else.</para>
///
/// <para>🔒 <b>Nothing here sends mail.</b> The invitation that carries the link is the caller's
/// business (<c>VolumePackageInviteMailService</c>, behind its own switch); the wizard itself is
/// silent, so a company filling it in never triggers a message to anybody.</para>
/// </remarks>
public sealed class VolumePackageWizardService
{
    /// <summary>
    /// The default expiry, matching the §1040 monitor links: the operator's <i>"active until 15 feb
    /// 2027 (after event)"</i>. ⚠️ After the edition on purpose — the group photo and its follow-up
    /// are settled in the days AFTER the event, not before it.
    /// </summary>
    public static readonly DateTimeOffset DefaultExpiry = AttendeeMonitor.DefaultExpiry;

    private readonly CommunityHubDbContext _db;
    private readonly ISharePointFileStore _store;
    private readonly IDocLibraryPathResolver _paths;
    private readonly TimeProvider _clock;

    public VolumePackageWizardService(
        CommunityHubDbContext db, ISharePointFileStore store, IDocLibraryPathResolver paths,
        TimeProvider? clock = null)
    {
        _db = db;
        _store = store;
        _paths = paths;
        _clock = clock ?? TimeProvider.System;
    }

    // =====================================================================
    //  The token
    // =====================================================================

    /// <summary>
    /// Issue (or RE-issue) the link for one company. Re-issuing mints a NEW token, which silently
    /// kills the previous URL — that is the point when a link has gone to the wrong person.
    /// </summary>
    public async Task<string?> IssueTokenAsync(
        int companyId, string? byEmail = null, CancellationToken ct = default)
    {
        var company = await _db.VolumePackageCompanies.FirstOrDefaultAsync(c => c.Id == companyId, ct);
        if (company is null) return null;

        var now = _clock.GetUtcNow();
        company.WizardToken = AttendeeMonitor.NewToken();
        company.WizardTokenIssuedAt = now;
        company.WizardTokenRevokedAt = null;
        company.WizardTokenExpiresAt = DefaultExpiry;
        company.UpdatedAt = now;
        company.LastUpdatedByEmail = byEmail;

        await _db.SaveChangesAsync(ct);
        return company.WizardToken;
    }

    /// <summary>
    /// Withdraw the link. 🔑 The token is KEPT, not blanked: "this link was revoked on the 3rd" is a
    /// different fact from "there has never been a link", and only one of them answers the question
    /// an organizer asks after a mistake.
    /// </summary>
    public async Task<bool> RevokeTokenAsync(
        int companyId, string? byEmail = null, CancellationToken ct = default)
    {
        var company = await _db.VolumePackageCompanies.FirstOrDefaultAsync(c => c.Id == companyId, ct);
        if (company?.WizardToken is null) return false;

        var now = _clock.GetUtcNow();
        company.WizardTokenRevokedAt = now;
        company.UpdatedAt = now;
        company.LastUpdatedByEmail = byEmail;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Token → company, and <b>the only gate on the wizard</b>.
    /// </summary>
    /// <remarks>
    /// 🔒 Null for unknown, revoked and expired alike, so the page answers <b>404</b> to all three.
    /// ⚠️ Deliberately not "this link has expired": telling a stranger which of the three it is
    /// confirms that a token is real, and the URL's secrecy is the thing being protected.
    /// </remarks>
    public async Task<VolumePackageCompany?> ResolveAsync(
        string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var company = await _db.VolumePackageCompanies
            .FirstOrDefaultAsync(c => c.WizardToken == token, ct);

        return company is not null && company.WizardTokenIsActive(_clock.GetUtcNow())
            ? company
            : null;
    }

    /// <summary>Record that the link was opened — so an organizer can see it is in use.</summary>
    public async Task NoteOpenedAsync(VolumePackageCompany company, CancellationToken ct = default)
    {
        company.WizardOpenCount++;
        company.WizardLastOpenedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    // =====================================================================
    //  The four steps
    // =====================================================================

    /// <summary>Step 1 — which benefits the company approves.</summary>
    public async Task SaveParticipationAsync(
        VolumePackageCompany company, bool keynote, bool social, bool groupPhoto,
        CancellationToken ct = default)
    {
        company.ApprovedKeynoteMention = keynote;
        company.ApprovedSocialMediaAnnouncement = social;
        company.ApprovedGroupPhoto = groupPhoto;
        // 🔑 Ticking anything after a decline UN-declines: a company that changed its mind should not
        // have to ask an organizer to undo a click, and the reminders resume with the participation.
        if (keynote || social || groupPhoto) company.DeclinedAt = null;
        company.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The explicit <i>"we do not want to participate"</i>. 🔴 It clears the approvals as well as
    /// setting the decline — a row that says "declined" while three benefits sit ticked is a
    /// contradiction someone will later resolve in the wrong direction.
    /// </summary>
    public async Task DeclineAsync(VolumePackageCompany company, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        company.DeclinedAt = now;
        company.ApprovedKeynoteMention = false;
        company.ApprovedSocialMediaAnnouncement = false;
        company.ApprovedGroupPhoto = false;
        company.WizardCompletedAt = now;   // it IS finished — with a no
        company.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Step 2 — the company's own group-photo coordinator.</summary>
    public async Task SaveCoordinatorAsync(
        VolumePackageCompany company, string? name, string? email, string? mobile,
        CancellationToken ct = default)
    {
        company.GroupPhotoContactName = Trimmed(name);
        company.GroupPhotoContactEmail = Trimmed(email);
        company.GroupPhotoContactMobile = Trimmed(mobile);
        company.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Step 4 — the LinkedIn page the announcement should tag.</summary>
    public async Task SaveLinkedInAsync(
        VolumePackageCompany company, string? url, CancellationToken ct = default)
    {
        company.LinkedInUrl = Trimmed(url);
        company.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Mark the wizard finished (step 4's "Done").</summary>
    public async Task CompleteAsync(VolumePackageCompany company, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        company.WizardCompletedAt = now;
        company.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    // =====================================================================
    //  Step 3 — the logo
    // =====================================================================

    /// <summary>True when logo uploads can actually be stored (folder registered + store wired).</summary>
    public bool CanUploadLogo =>
        _store.CanStore
        && _paths.TryResolve(DocLibraryPaths.GroupPhotoLogoWeb, out var w) && w.Length > 0
        && _paths.TryResolve(DocLibraryPaths.GroupPhotoLogoPrint, out var p) && p.Length > 0;

    /// <summary>
    /// 🔒 Images only, by ALLOWLIST — the opposite choice from the media library (§1078), and for
    /// the opposite reason: this upload comes from an anonymous token holder, not from our own crew,
    /// and the file is destined for a keynote slide. "A logo" is a small, known set; anything else
    /// arriving here is a mistake or an attempt.
    /// </summary>
    private static readonly HashSet<string> AllowedLogoExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".eps", ".ai", ".pdf" };

    /// <summary>⚠️ 25 MB: generous for a print-quality logo, far below anything that hurts.</summary>
    public const long MaxLogoBytes = 25L * 1024 * 1024;

    /// <summary>Why a logo was refused, in words the uploader can act on. Null = accepted.</summary>
    public static string? LogoRejectionReason(string? fileName, long lengthBytes)
    {
        var ext = Path.GetExtension(MediaLibraryService.SafeName(fileName));
        if (string.IsNullOrWhiteSpace(ext) || !AllowedLogoExtensions.Contains(ext))
            return "That file type is not a logo we can use. Send PNG, JPG, SVG, EPS, AI or PDF.";
        if (lengthBytes > MaxLogoBytes)
            return "That file is larger than 25 MB. A print-quality logo is normally far smaller.";
        return null;
    }

    /// <summary>
    /// Store one logo and remember where it went.
    /// </summary>
    /// <remarks>
    /// 🔑 The stored name is derived from the COMPANY ID, never from what the uploader called the
    /// file: a re-upload then replaces the previous one, instead of leaving the design team with
    /// <c>logo.png</c>, <c>logo(1).png</c> and <c>logo-final-v2.png</c> and no way to tell which is
    /// current. It also means the uploader's file name never becomes a path.
    /// </remarks>
    public async Task<bool> SaveLogoAsync(
        VolumePackageCompany company, VolumePackageLogoKind kind,
        string fileName, Stream content, long contentLength, string contentType,
        CancellationToken ct = default)
    {
        if (!CanUploadLogo) return false;
        if (LogoRejectionReason(fileName, contentLength) is not null) return false;

        var key = kind == VolumePackageLogoKind.Print
            ? DocLibraryPaths.GroupPhotoLogoPrint
            : DocLibraryPaths.GroupPhotoLogoWeb;
        if (!_paths.TryResolve(key, out var folder) || folder.Length == 0) return false;

        var ext = Path.GetExtension(MediaLibraryService.SafeName(fileName)).ToLowerInvariant();
        var stored = $"vp-{company.Id}-{(kind == VolumePackageLogoKind.Print ? "print" : "web")}{ext}";

        var result = await _store.UploadStreamToFolderAsync(
            folder, stored, content, contentLength, contentType, ct);

        if (kind == VolumePackageLogoKind.Print) company.LogoPrintPath = result.Path;
        else company.LogoWebPath = result.Path;

        company.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static string? Trimmed(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
}
