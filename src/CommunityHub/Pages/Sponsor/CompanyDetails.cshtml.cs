using System.Text.RegularExpressions;
using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// Sponsor "Company Details" — the static, always-available place a sponsor /
/// exhibitor maintains the vital company info that drives Zoho Backstage exposure
/// and ELDK social-media promotion. Data lives in CEH SQL (<see cref="SponsorInfo"/>),
/// scoped to (EventId, SponsorCompanyId).
///
/// STAGE 2 (this change): adds the versioned SharePoint logo / exhibitor-wall
/// uploads + per-upload notify emails. §768.14 — the FOLDERS now come from the
/// document-library registry, not this page; only the notify recipients are still
/// edition config. Each upload is stored as
/// <c>{sponsor}-{kind}-{version}.{ext}</c> (see <c>SponsorUploadNaming</c>) with an
/// auto-incrementing version so the latest file is obvious. STAGE 3 will add the
/// live "Save &amp; Sync to Zoho" + booth members + booth materials.
/// </summary>
// Exhibitor-wall artwork can be up to 1 GB (operator 2026-06-28). Raise BOTH the request-body
// ceiling and the multipart limit on this page — otherwise the framework rejects anything over
// the ~28.6 MB Kestrel/Form default with HTTP 413 during binding, BEFORE the handler's size check.
[Authorize]
[RequestSizeLimit(1_073_741_824)]
[RequestFormLimits(MultipartBodyLengthLimit = 1_073_741_824)]
public class CompanyDetailsModel : PageModel
{
    // §802.3 — the numbers now come from ONE place, because they are ZOHO's limits and were measured
    // against the live API (§801.2). They lived here, in the wizard step, and hard-coded in this
    // page's character counter; the sync needed a fourth copy, which is where it stopped being
    // reasonable. These aliases stay so the .cshtml keeps compiling.
    public const int MaxOverview = CommunityHub.Core.Integrations.ZohoExhibitorLimits.Overview;
    public const int MaxShort    = CommunityHub.Core.Integrations.ZohoExhibitorLimits.ShortDescription;
    public const int MaxSocial   = CommunityHub.Core.Integrations.ZohoExhibitorLimits.SocialBrandingText;
    public const int MaxUrl      = CommunityHub.Core.Integrations.ZohoExhibitorLimits.Url;

    private const long Mb = 1024 * 1024;

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _cmOptions;
    private readonly EventEditionConfigLoader _cfg;
    private readonly EventConfigOptions _cfgOptions;
    private readonly SharePointUploadClient _sp;
    private readonly IEmailSender _email;
    private readonly SponsorZohoSyncService _zohoSync;
    private readonly CommunityHub.Core.Integrations.Erp.EconomicContactAdminService _erpContacts;
    private readonly CommunityHub.Core.Integrations.DocLibrary.IDocLibraryPathResolver _paths;
    private readonly ILogger<CompanyDetailsModel> _log;

    /// <summary>§482b — pulls an ERP contact change back into the hub straight away. OPTIONAL so a
    /// host without Company Manager keeps working; the edit still lands in the ERP either way.</summary>
    private readonly CommunityHub.Core.Integrations.SponsorContactSyncService? _contactSync;

    public CompanyDetailsModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock,
        CompanyManagerClient cm,
        CompanyManagerOptions cmOptions,
        EventEditionConfigLoader cfg,
        EventConfigOptions cfgOptions,
        SharePointUploadClient sp,
        IEmailSender email,
        SponsorZohoSyncService zohoSync,
        CommunityHub.Core.Integrations.Erp.EconomicContactAdminService erpContacts,
        CommunityHub.Core.Integrations.DocLibrary.IDocLibraryPathResolver paths,
        ILogger<CompanyDetailsModel> log,
        CommunityHub.Core.Integrations.SponsorContactSyncService? contactSync = null)
    {
        _paths = paths;
        _contactSync = contactSync;
        _db = db;
        _participant = participant;
        _clock = clock;
        _cm = cm;
        _cmOptions = cmOptions;
        _cfg = cfg;
        _cfgOptions = cfgOptions;
        _sp = sp;
        _email = email;
        _zohoSync = zohoSync;
        _erpContacts = erpContacts;
        _log = log;
    }

    /// <summary>Non-sponsor reached the page (server-side gate, not CSS).</summary>
    public bool AccessDenied { get; private set; }
    /// <summary>This contact has no sponsor company id (nothing to edit).</summary>
    public bool NoCompanyLink { get; private set; }

    /// <summary>Public company name from Company Manager (read-only here).</summary>
    public string? PublicName { get; private set; }
    /// <summary>True when this company bought booth products (shown as "Exhibitor").</summary>
    public bool IsExhibitor { get; private set; }
    public string TypeLabel => IsExhibitor ? "Exhibitor" : "Sponsor";

    public string? Message { get; private set; }
    public string? Error { get; private set; }

    /// <summary>When this company's details row was last saved (UTC), for the
    /// "Last saved …" line next to Save (§51). Null until the row exists.</summary>
    public DateTimeOffset? LastSavedAt { get; private set; }

    // Which upload buttons are available (SharePoint configured + folder set).
    public bool CanUploadSoMe { get; private set; }
    public bool CanUploadPrint { get; private set; }
    public bool CanUploadWall { get; private set; }

    /// <summary>
    /// §68 — the latest upload per logo/wall kind ("some"/"print"/"wall"),
    /// so the page can show the already-uploaded file (name + version + when + by
    /// whom) under each upload control on load. Empty until something is uploaded.
    /// </summary>
    public IReadOnlyDictionary<string, SponsorUploadAudit> LatestUploads { get; private set; }
        = new Dictionary<string, SponsorUploadAudit>();

    /// <summary>§64 — when the booth-members section was last saved (max member
    /// UpdatedAt/CreatedAt), for the "Last saved …" line by its Save &amp; Sync button.</summary>
    public DateTimeOffset? BoothMembersLastSavedAt { get; private set; }

    /// <summary>§64 — when the booth-materials section was last saved (max material
    /// CreatedAt), for the "Last saved …" line by its Save &amp; Sync button.</summary>
    public DateTimeOffset? BoothMaterialsLastSavedAt { get; private set; }

    /// <summary>This exhibitor's booth members (CEH-maintained; exhibitor-only).</summary>
    public List<SponsorBoothMember> BoothMembers { get; private set; } = new();

    /// <summary>Booth video URLs (exhibitor-only, max 6).</summary>
    public List<SponsorBoothMaterial> BoothVideos { get; private set; } = new();
    /// <summary>Booth collateral files (exhibitor-only, max 6).</summary>
    public List<SponsorBoothMaterial> BoothCollateral { get; private set; } = new();
    public const int MaxVideos = 6;
    public const int MaxCollateral = 6;
    public bool CanUploadCollateral { get; private set; }

    /// <summary>§229 — the saved booth check-in slot (pre-day expected arrival), null = unanswered.</summary>
    public string? CurrentBoothCheckInSlot { get; private set; }
    /// <summary>§298 — how many booth members will check in (feeds the pre-day lunch order).</summary>
    public int? CurrentBoothCheckInMemberCount { get; private set; }
    public DateTimeOffset? BoothCheckInSetAt { get; private set; }
    public string? BoothCheckInSetByEmail { get; private set; }

    /// <summary>The sponsor's e-conomic customer number (from the webshop company), if any.</summary>
    public int? ErpCustomerNumber { get; private set; }
    /// <summary>True when this sponsor's e-conomic contacts can be managed here (live ERP).</summary>
    public bool CanManageErpContacts { get; private set; }
    /// <summary>The sponsor's live e-conomic contacts (ERP is the master; reflects ERP adds/deletes).</summary>
    public IReadOnlyList<CommunityHub.Core.Integrations.Erp.EconomicContactAdminService.ContactView> ErpContacts { get; private set; }
        = Array.Empty<CommunityHub.Core.Integrations.Erp.EconomicContactAdminService.ContactView>();

    // --- Editable fields (bound) ---------------------------------------------
    [BindProperty] public string? WebsiteUrl { get; set; }
    [BindProperty] public string? LinkedInUrl { get; set; }
    [BindProperty] public string? TwitterUrl { get; set; }
    [BindProperty] public string? CompanyOverview { get; set; }
    [BindProperty] public string? SocialMediaBrandingText { get; set; }
    [BindProperty] public string? CompanyShortDescription { get; set; }

    // Event Coordinator (primary contact → Zoho contact).
    [BindProperty] public string? EventCoordinatorFirstName { get; set; }
    [BindProperty] public string? EventCoordinatorLastName { get; set; }
    [BindProperty] public string? EventCoordinatorCompanyName { get; set; }
    [BindProperty] public string? EventCoordinatorEmail { get; set; }
    [BindProperty] public string? EventCoordinatorPhone { get; set; }

    // The file from whichever upload form was submitted.
    [BindProperty] public IFormFile? UploadFile { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Sponsor) { AccessDenied = true; return Page(); }

        await LoadAsync(me, prefill: true, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Sponsor) { AccessDenied = true; return Page(); }

        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (companyId is null) { NoCompanyLink = true; await LoadAsync(me, prefill: false, ct); return Page(); }

        var error = ValidateUrl("Website URL", WebsiteUrl)
                    ?? ValidateUrl("LinkedIn URL", LinkedInUrl)
                    ?? ValidateUrl("Twitter URL", TwitterUrl)
                    ?? ValidateLen("Company Overview", CompanyOverview, MaxOverview)
                    ?? ValidateLen("Social Media Branding Text", SocialMediaBrandingText, MaxSocial)
                    ?? ValidateLen("Company Short Description", CompanyShortDescription, MaxShort)
                    ?? (string.IsNullOrWhiteSpace(EventCoordinatorEmail) || LooksLikeEmail(EventCoordinatorEmail)
                        ? null : "Event coordinator email is not a valid address.");
        if (error is not null)
        {
            Error = error;
            await LoadAsync(me, prefill: false, ct);   // keep posted values
            return Page();
        }

        var info = await GetOrCreateInfoAsync(me.EventId, companyId!, ct);
        info.WebsiteUrl              = NormaliseOrNull(WebsiteUrl);
        info.LinkedInUrl             = NormaliseOrNull(LinkedInUrl);
        info.TwitterUrl             = NormaliseOrNull(TwitterUrl);
        info.CompanyDescription      = NormaliseOrNull(CompanyOverview);
        info.SocialMediaIntro        = NormaliseOrNull(SocialMediaBrandingText);
        if (info.HasBooth)
            info.CompanyDescriptionShort = NormaliseOrNull(CompanyShortDescription);
        info.EventCoordinatorFirstName   = NormaliseOrNull(EventCoordinatorFirstName);
        info.EventCoordinatorLastName    = NormaliseOrNull(EventCoordinatorLastName);
        info.EventCoordinatorCompanyName = NormaliseOrNull(EventCoordinatorCompanyName);
        info.EventCoordinatorEmail       = NormaliseOrNull(EventCoordinatorEmail);
        info.EventCoordinatorPhone       = NormaliseOrNull(EventCoordinatorPhone);
        info.LastUpdatedByEmail      = me.Email;
        await _db.SaveChangesAsync(ct);

        // Save & Sync to Zoho Backstage (fail-soft: SQL is already saved). Resolves
        // + caches the Zoho sponsor/exhibitor id by company name, then writes by id.
        var sponsorName = await ResolveSponsorNameAsync(companyId!, ct);
        var sync = await _zohoSync.SyncAsync(me.EventId, companyId!, sponsorName ?? string.Empty, ct);
        Message = BuildSyncMessage(sync);

        await LoadAsync(me, prefill: true, ct);
        return Page();
    }

    /// <summary>
    /// §229 — save the booth check-in slot ("when do you expect to arrive at your booth
    /// on 9 Feb 2027"): one of <see cref="BoothCheckInSlots.All"/>. Stored on the company's
    /// <see cref="SponsorInfo"/> row (who + when audited); no Zoho sync — hub-only data.
    /// </summary>
    public async Task<IActionResult> OnPostBoothCheckInAsync(string? slot, int? memberCount, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Sponsor) { AccessDenied = true; return Page(); }

        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (companyId is null) { NoCompanyLink = true; await LoadAsync(me, prefill: true, ct); return Page(); }

        if (!BoothCheckInSlots.IsValid(slot))
        {
            Error = "Please pick when you expect to arrive at your booth (or the not-participating option).";
            await LoadAsync(me, prefill: true, ct);
            return Page();
        }

        var info = await GetOrCreateInfoAsync(me.EventId, companyId!, ct);
        info.BoothCheckInSlot = slot;
        // §298: booth-member count feeds the pre-day lunch order; cleared on the opt-out, clamped 1..50.
        info.BoothCheckInMemberCount = slot == BoothCheckInSlots.NotParticipating
            ? null
            : (memberCount is int n && n > 0 ? Math.Min(n, 50) : null);
        info.BoothCheckInSetAt = _clock.GetUtcNow();
        info.BoothCheckInSetByEmail = me.Email;
        await _db.SaveChangesAsync(ct);

        Message = slot == BoothCheckInSlots.NotParticipating
            ? "Saved — we've noted that you don't expect to participate on pre-day."
            : $"Saved — we'll expect you at your booth between {BoothCheckInSlots.Label(slot)} on 9 Feb 2027.";
        await LoadAsync(me, prefill: true, ct);
        return Page();
    }

    /// <summary>
    /// §41 auto-save: persist ONLY the plain string fields (links, descriptions,
    /// event-coordinator fields) to <see cref="SponsorInfo"/> WITHOUT triggering the
    /// Zoho Backstage sync. Wired to blur / ~1s-debounce on the page so a sponsor never
    /// loses edits, while the Zoho 3×-email cap stays protected (the explicit
    /// "Save &amp; Sync to Zoho" button is the only thing that syncs). Antiforgery is
    /// enforced (Razor Pages default). Returns a tiny JSON status for the inline
    /// "Saving…/Saved ✓" indicator; it never redirects.
    /// </summary>
    public async Task<IActionResult> OnPostAutoSaveAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return new JsonResult(new { ok = false, error = "not-signed-in" }) { StatusCode = 401 };
        if (me.Role != ParticipantRole.Sponsor) return new JsonResult(new { ok = false, error = "forbidden" }) { StatusCode = 403 };

        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (companyId is null) return new JsonResult(new { ok = false, error = "no-company" }) { StatusCode = 400 };

        var error = ValidateUrl("Website URL", WebsiteUrl)
                    ?? ValidateUrl("LinkedIn URL", LinkedInUrl)
                    ?? ValidateUrl("Twitter URL", TwitterUrl)
                    ?? ValidateLen("Company Overview", CompanyOverview, MaxOverview)
                    ?? ValidateLen("Social Media Branding Text", SocialMediaBrandingText, MaxSocial)
                    ?? ValidateLen("Company Short Description", CompanyShortDescription, MaxShort)
                    ?? (string.IsNullOrWhiteSpace(EventCoordinatorEmail) || LooksLikeEmail(EventCoordinatorEmail)
                        ? null : "Event coordinator email is not a valid address.");
        if (error is not null)
            return new JsonResult(new { ok = false, error }) { StatusCode = 400 };

        var info = await GetOrCreateInfoAsync(me.EventId, companyId!, ct);
        info.WebsiteUrl              = NormaliseOrNull(WebsiteUrl);
        info.LinkedInUrl             = NormaliseOrNull(LinkedInUrl);
        info.TwitterUrl              = NormaliseOrNull(TwitterUrl);
        info.CompanyDescription      = NormaliseOrNull(CompanyOverview);
        info.SocialMediaIntro        = NormaliseOrNull(SocialMediaBrandingText);
        if (info.HasBooth)
            info.CompanyDescriptionShort = NormaliseOrNull(CompanyShortDescription);
        info.EventCoordinatorFirstName   = NormaliseOrNull(EventCoordinatorFirstName);
        info.EventCoordinatorLastName    = NormaliseOrNull(EventCoordinatorLastName);
        info.EventCoordinatorCompanyName = NormaliseOrNull(EventCoordinatorCompanyName);
        info.EventCoordinatorEmail       = NormaliseOrNull(EventCoordinatorEmail);
        info.EventCoordinatorPhone       = NormaliseOrNull(EventCoordinatorPhone);
        info.LastUpdatedByEmail      = me.Email;
        // GetOrCreateInfoAsync stamps UpdatedAt only on the existing-row path; ensure a
        // freshly-created row also carries a saved-at so the "Last saved" line appears.
        info.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        return new JsonResult(new { ok = true, savedAt = info.UpdatedAt?.ToUniversalTime().ToString("dd MMM yyyy HH:mm") + " UTC" });
    }

    private static string BuildSyncMessage(SponsorZohoSyncService.SyncResult r)
    {
        if (!r.Enabled)
            return "Company details saved. (Zoho Backstage sync is not enabled for this environment.)";
        if (r.Error is not null)
            return $"Company details saved. {r.Error}";
        if (r.IsExhibitor)
            return r.SponsorSynced && r.ExhibitorSynced
                ? "Company details saved and synced to your Zoho Backstage sponsor + exhibitor records."
                : "Company details saved. Zoho sync was partial — please try Save & Sync again.";
        return r.SponsorSynced
            ? "Company details saved and synced to your Zoho Backstage sponsor record."
            : "Company details saved. Zoho sync was partial — please try Save & Sync again.";
    }

    /// <summary>
    /// Upload a logo / exhibitor-wall file to its SharePoint folder with an
    /// auto-incrementing version suffix, then notify the configured recipients.
    /// <paramref name="kind"/> ∈ { some, print, zoho, wall }.
    /// </summary>
    public async Task<IActionResult> OnPostUploadAsync(string kind, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Sponsor) { AccessDenied = true; return Page(); }

        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (companyId is null) { NoCompanyLink = true; await LoadAsync(me, prefill: true, ct); return Page(); }

        var sp = _cfg.Load(_cfgOptions.EventConfigPath).SharePoint;
        var spec = ResolveKind(kind, sp);
        if (spec is null)
        {
            Error = "That upload isn't available.";
            await LoadAsync(me, prefill: true, ct);
            return Page();
        }

        var info = await _db.SponsorInfos.AsNoTracking().FirstOrDefaultAsync(
            s => s.EventId == me.EventId && s.SponsorCompanyId == companyId, ct);
        var isExhibitor = info?.HasBooth ?? false;
        if (spec.IsWall && !isExhibitor)
        {
            Error = "The exhibitor wall upload is for exhibitors only.";
            await LoadAsync(me, prefill: true, ct);
            return Page();
        }

        // Validate the posted file.
        if (UploadFile is null || UploadFile.Length == 0)
        {
            Error = "Please choose a file to upload.";
            await LoadAsync(me, prefill: true, ct);
            return Page();
        }
        if (UploadFile.Length > spec.MaxBytes)
        {
            Error = $"File is too large (max {spec.MaxBytes / Mb} MB).";
            await LoadAsync(me, prefill: true, ct);
            return Page();
        }
        var ext = Path.GetExtension(UploadFile.FileName).ToLowerInvariant();
        if (!spec.Accepts(ext))
        {
            Error = $"Unsupported file type. Allowed: {spec.AllowedText}.";
            await LoadAsync(me, prefill: true, ct);
            return Page();
        }

        var sponsorName = await ResolveSponsorNameAsync(companyId!, ct);
        try
        {
            // §455 (operator 2026-07-27: "those are also very large files - should we change those
            // as well so we dont kill infra"). Exhibitor-wall artwork is capped at 1 GB, and this
            // buffered the whole file into a MemoryStream then ToArray()'d it — the same double
            // copy that made the speaker slide upload fragile. Now streamed straight into Graph's
            // chunked session: peak memory is one chunk, whatever the artwork weighs.
            var fileName = await CommunityHub.Uploads.SponsorUploadKinds.NextVersionedNameAsync(
                _sp, sp!.SiteUrl, sp.DriveName, spec.Folder, kind, sponsorName, ext, _log, ct);
            await using var wallUpload = UploadFile.OpenReadStream();
            var (_, webUrl, _) = await _sp.UploadFileStreamAsync(
                sp.SiteUrl, sp.DriveName, spec.Folder, fileName, wallUpload, UploadFile.Length,
                string.IsNullOrWhiteSpace(UploadFile.ContentType) ? "application/octet-stream" : UploadFile.ContentType,
                ct);

            // Persist the uploaded LOGO's location so the sponsor "Get started"
            // wizard's "Logos & artwork" step (SponsorWizardService, which checks
            // LogoRasterPath / LogoVectorPath) can detect completion. The Web logo
            // is a raster PNG -> LogoRasterPath; the print logo is vector
            // (EPS/AI/PDF) -> LogoVectorPath. The exhibitor-wall upload is not a
            // company logo, so it is intentionally NOT recorded on the logo fields.
            if (kind is "some" or "print")
            {
                var logoInfo = await GetOrCreateInfoAsync(me.EventId, companyId!, ct);
                if (kind == "print")
                {
                    logoInfo.LogoVectorPath = webUrl;
                    logoInfo.LogoVectorFileName = fileName;
                }
                else
                {
                    logoInfo.LogoRasterPath = webUrl;
                    logoInfo.LogoRasterFileName = fileName;
                }
                logoInfo.LastUpdatedByEmail = me.Email;
                await _db.SaveChangesAsync(ct);
            }

            // §68 — record the upload (who/when/what) so every coordinator of this
            // shared company sees the current file on load and doesn't re-upload.
            _db.SponsorUploadAudits.Add(new SponsorUploadAudit
            {
                EventId = me.EventId,
                SponsorCompanyId = companyId!,
                Kind = kind,
                FileName = fileName,
                Version = ParseVersion(fileName),
                WebUrl = webUrl,
                UploadedByEmail = me.Email,
                UploadedAt = _clock.GetUtcNow(),
            });
            await _db.SaveChangesAsync(ct);

            await NotifyUploadAsync(spec, sponsorName ?? string.Empty, fileName, webUrl, me.Email, ct);
            Message = $"Uploaded {fileName}. The organizers have been notified.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "CompanyDetails: {Kind} upload failed for company {Co}.", kind, companyId);
            Error = "The upload could not be completed. Please try again, or contact the organizers.";
        }

        await LoadAsync(me, prefill: true, ct);
        return Page();
    }

    // ---- upload kinds --------------------------------------------------------

    /// <summary>
    /// §768 — delegates to <see cref="SponsorUploadKinds.Resolve"/>, the ONE definition of what an
    /// upload kind means.
    /// </summary>
    /// <remarks>
    /// 🔒 This method used to be a SECOND, hand-maintained copy of those rules — same four kinds,
    /// same folders, prefixes, extensions, size caps and notify lists — and
    /// <c>SponsorUploadKinds</c>'s own doc comment admitted it: <i>"Mirrors
    /// CompanyDetailsModel.ResolveKind exactly"</i>. A comment is not a mechanism. Two copies of a
    /// rule drift the moment one is edited, and the §768 filename change (a new versioned naming
    /// convention) is exactly the kind of edit that would have been applied to one and not the
    /// other — leaving a sponsor's logo landing under two different names depending on which page
    /// they used.
    /// <para>§768.14 — the local <c>UploadSpec</c> shim is gone too: it re-shaped the shared spec
    /// into a near-identical record, which is a copy of the DATA where there was already a copy of
    /// the rules. The extra <c>_sp.IsConfigured</c> check stays — it asks whether the upload client
    /// is wired, which is this page's concern rather than the kind registry's.</para>
    /// </remarks>
    private CommunityHub.Uploads.SponsorUploadSpec? ResolveKind(string kind, SharePointEditionConfig? sp) =>
        _sp.IsConfigured
            ? CommunityHub.Uploads.SponsorUploadKinds.Resolve(kind, _paths, sp)
            : null;

    /// <summary>
    /// The version a stored name carries. §768.14 — the local <c>_v{N}</c> regex is gone; naming is
    /// one file's job now (<see cref="SponsorUploadNaming"/>), and this stays tolerant of the
    /// retired form only because historical audit rows are DISPLAYED here.
    /// </summary>
    private static int ParseVersion(string fileName) =>
        CommunityHub.Uploads.SponsorUploadNaming.ParseVersion(fileName);

    /// <summary>
    /// §768.14 — delegates to <see cref="SponsorUploadKinds.NotifyAsync"/>.
    /// </summary>
    /// <remarks>
    /// This page carried its own byte-identical copy of the notification mail. §494d records what a
    /// second copy costs here specifically: the direct-to-storage path silently sent NO designer
    /// notification because it was written separately, so artwork arrived and nobody was told.
    /// </remarks>
    private Task NotifyUploadAsync(
        CommunityHub.Uploads.SponsorUploadSpec spec, string sponsorName, string fileName, string? webUrl,
        string byEmail, CancellationToken ct) =>
        CommunityHub.Uploads.SponsorUploadKinds.NotifyAsync(
            _email, spec, sponsorName, fileName, webUrl, byEmail, _log, ct);

    // ---- Sponsor self-service e-conomic contacts (ERP master) ----------------

    /// <summary>
    /// §482b (operator: <i>"you need to trigger a sync if you make chg"</i> and <i>"maybe you can
    /// extend so it syncs to both ceh and cm from erp"</i>) — push an ERP contact change through
    /// BOTH downstream hops immediately, instead of leaving the sponsor to wonder why their edit
    /// changed nothing visible until a scheduled job ran hours later.
    ///
    /// <para>ERP is the master. Hop 1 reconciles ERP → Company Manager (scoped to THIS customer, so
    /// a page POST never triggers an estate-wide sweep). Hop 2 pulls Company Manager → the hub, so
    /// the participant rows and the "Linked Event Coordinators" list agree with what was just
    /// saved.</para>
    ///
    /// <para>FAIL-SOFT on purpose: the ERP write has already succeeded and is the system of record.
    /// A sync hiccup must not turn a saved change into an error message — it only means the hub
    /// catches up on the next scheduled run, so it is logged, not surfaced.</para>
    /// </summary>
    private void PushContactChangeDownstream(int eventId, string companyId, int erpCustomerNumber)
    {
        // §493b (operator: "make it into queue so it complete behind. no problem.") — QUEUED, not
        // awaited. Run inline this was 2–4 sequential external calls (e-conomic, then Company
        // Manager, then the hub pull) BEFORE the page responded: exactly the §443 shape that made
        // the admin interface feel broken. The sponsor's save now returns immediately.
        //
        // Its OWN DI scope: the request scope — and its DbContext — is disposed the moment the
        // response is written, so continuing to use it would throw ObjectDisposedException under
        // load. This is the one detail that makes fire-and-forget safe here.
        var scopeFactory = HttpContext.RequestServices.GetRequiredService<IServiceScopeFactory>();
        var logger = _log;

        _ = Task.Run(async () =>
        {
            // NOT the request's CancellationToken: that is cancelled as soon as the response
            // completes, which would abort the work we just deliberately moved off the request.
            var ct = CancellationToken.None;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;

                var erpToCm = sp.GetService<CommunityHub.Core.Integrations.Erp.ErpWebshopContactSyncService>();
                if (erpToCm is not null)
                {
                    await erpToCm.SyncAsync(onlyCustomerNumber: erpCustomerNumber, ct: ct);
                }

                var cmToHub = sp.GetService<CommunityHub.Core.Integrations.SponsorContactSyncService>();
                if (cmToHub is not null && int.TryParse(companyId, out var cmCompanyId))
                {
                    await cmToHub.SyncCompanyAsync(eventId, cmCompanyId, ct);
                }
            }
            catch (Exception ex)
            {
                // Swallowed on purpose. The ERP write — the system of record — already succeeded,
                // and an unobserved exception on a background task must never take the host down.
                // The scheduled ErpSyncCustomerContactJob sweep is the backstop: worst case the hub
                // catches up on its next run instead of within seconds.
                logger.LogWarning(ex,
                    "Background contact sync failed for company {Co} (ERP customer {Erp}); "
                    + "the scheduled reconcile will catch it up.", companyId, erpCustomerNumber);
            }
        });
    }

    public async Task<IActionResult> OnPostAddErpContactAsync(
        string name, string? email, string? phone, bool signer, bool coordinator, CancellationToken ct)
    {
        var (me, companyId, redirect) = await GuardSponsorAsync(ct);
        if (redirect is not null) return redirect;
        var erpNo = await ResolveErpCustomerNumberAsync(companyId!, ct);
        if (erpNo is null || !_erpContacts.CanWrite) { Error = "backend isn't available for your company."; await LoadAsync(me!, prefill: true, ct); return Page(); }
        if (string.IsNullOrWhiteSpace(name)) { Error = "Name is required."; await LoadAsync(me!, prefill: true, ct); return Page(); }

        try
        {
            await _erpContacts.CreateAsync(erpNo.Value, name, email, phone, signer, coordinator, ct);
            PushContactChangeDownstream(me!.EventId, companyId!, erpNo.Value);
            Message = "Contact added.";
        }
        catch (Exception ex) { _log.LogWarning(ex, "AddErpContact failed."); Error = "Could not add the contact in backend. Please try again."; }
        await LoadAsync(me!, prefill: true, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateErpContactAsync(
        int contactNumber, string name, string? email, string? phone,
        bool signer, bool coordinator, string? notes, CancellationToken ct)
    {
        var (me, companyId, redirect) = await GuardSponsorAsync(ct);
        if (redirect is not null) return redirect;
        var erpNo = await ResolveErpCustomerNumberAsync(companyId!, ct);
        if (erpNo is null || !_erpContacts.CanWrite) { Error = "backend isn't available for your company."; await LoadAsync(me!, prefill: true, ct); return Page(); }

        try
        {
            await _erpContacts.UpdateAsync(erpNo.Value, contactNumber, name ?? string.Empty, email, phone, signer, coordinator, notes, ct);
            PushContactChangeDownstream(me!.EventId, companyId!, erpNo.Value);
            Message = "Contact updated.";
        }
        catch (Exception ex) { _log.LogWarning(ex, "UpdateErpContact failed."); Error = "Could not update the contact in backend. Please try again."; }
        await LoadAsync(me!, prefill: true, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteErpContactAsync(int contactNumber, CancellationToken ct)
    {
        var (me, companyId, redirect) = await GuardSponsorAsync(ct);
        if (redirect is not null) return redirect;
        var erpNo = await ResolveErpCustomerNumberAsync(companyId!, ct);
        if (erpNo is null || !_erpContacts.CanWrite) { Error = "backend isn't available for your company."; await LoadAsync(me!, prefill: true, ct); return Page(); }

        try
        {
            await _erpContacts.DeleteAsync(erpNo.Value, contactNumber, ct);
            PushContactChangeDownstream(me!.EventId, companyId!, erpNo.Value);
            Message = "Contact removed.";
        }
        catch (Exception ex) { _log.LogWarning(ex, "DeleteErpContact failed."); Error = "Could not remove the contact in backend. Please try again."; }
        await LoadAsync(me!, prefill: true, ct);
        return Page();
    }

    private async Task LoadAsync(CurrentParticipant me, bool prefill, CancellationToken ct)
    {
        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (companyId is null) { NoCompanyLink = true; return; }

        var info = await _db.SponsorInfos.AsNoTracking().FirstOrDefaultAsync(
            s => s.EventId == me.EventId && s.SponsorCompanyId == companyId, ct);

        IsExhibitor = info?.HasBooth ?? false;
        // §51 — surface the row's last-saved instant for the "Last saved …" line.
        LastSavedAt = info?.UpdatedAt;

        // §229 — booth check-in (pre-day expected arrival) state for the section + wizard.
        CurrentBoothCheckInSlot = info?.BoothCheckInSlot;
        CurrentBoothCheckInMemberCount = info?.BoothCheckInMemberCount;
        BoothCheckInSetAt = info?.BoothCheckInSetAt;
        BoothCheckInSetByEmail = info?.BoothCheckInSetByEmail;

        if (prefill && info is not null)
        {
            WebsiteUrl              = info.WebsiteUrl;
            LinkedInUrl             = info.LinkedInUrl;
            TwitterUrl              = info.TwitterUrl;
            CompanyOverview         = info.CompanyDescription;
            SocialMediaBrandingText = info.SocialMediaIntro;
            CompanyShortDescription = info.CompanyDescriptionShort;
            EventCoordinatorFirstName   = info.EventCoordinatorFirstName;
            EventCoordinatorLastName    = info.EventCoordinatorLastName;
            EventCoordinatorCompanyName = info.EventCoordinatorCompanyName;
            EventCoordinatorEmail       = info.EventCoordinatorEmail;
            EventCoordinatorPhone       = info.EventCoordinatorPhone;
        }

        PublicName = await ResolveSponsorNameAsync(companyId, ct, allowFallback: false);

        // Which upload buttons to show. §768.14 — asked of ResolveKind, so a control appears
        // exactly when the POST handler would accept it. Reading the config keys directly (as this
        // did) meant the button's condition and the handler's condition were two separate rules
        // over two separate sources: a sponsor could be shown an upload that then refused the file.
        var sp = _cfg.Load(_cfgOptions.EventConfigPath).SharePoint;
        CanUploadSoMe  = ResolveKind("some", sp) is not null;
        CanUploadPrint = ResolveKind("print", sp) is not null;
        CanUploadWall  = IsExhibitor && ResolveKind("wall", sp) is not null;

        // §68 — surface the latest upload per logo/wall kind so each upload control
        // can show the already-uploaded file (name + version + when + by whom).
        var audits = await _db.SponsorUploadAudits.AsNoTracking()
            .Where(a => a.EventId == me.EventId && a.SponsorCompanyId == companyId)
            .ToListAsync(ct);
        var latest = audits
            .GroupBy(a => a.Kind)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.UploadedAt).First());

        // §68 FALLBACK: a logo can exist on the SponsorInfo row (LogoRasterPath /
        // LogoVectorPath, e.g. uploaded before the per-upload audit was recorded) with NO
        // audit row. Without this, the "current file" line would be blank on load even
        // though a logo is on file. Synthesize a display entry from the stored logo so the
        // raster control ("some") and the vector control ("print") still show the
        // already-uploaded file (name + when) on load, like the booth-materials controls.
        if (info is not null)
        {
            var fallbacks = new[]
            {
                ("some",  info.LogoRasterPath, info.LogoRasterFileName),
                ("print", info.LogoVectorPath, info.LogoVectorFileName),
            };
            foreach (var (kind, path, fileName) in fallbacks)
            {
                if (latest.ContainsKey(kind) || string.IsNullOrWhiteSpace(fileName)) continue;
                latest[kind] = new SponsorUploadAudit
                {
                    EventId = me.EventId,
                    SponsorCompanyId = companyId!,
                    Kind = kind,
                    FileName = fileName!,
                    Version = ParseVersion(fileName!),
                    WebUrl = path,
                    UploadedByEmail = info.LastUpdatedByEmail ?? string.Empty,
                    UploadedAt = info.UpdatedAt ?? info.CreatedAt,
                };
            }
        }

        LatestUploads = latest;

        if (IsExhibitor)
        {
            BoothMembers = await _db.SponsorBoothMembers
                .Where(m => m.EventId == me.EventId && m.SponsorCompanyId == companyId && m.DeletedAt == null)
                .OrderBy(m => m.LastName).ThenBy(m => m.FirstName)
                .ToListAsync(ct);
            // §64 — last-saved for the booth-members section (latest add/edit).
            var lastMember = BoothMembers
                .Select(m => m.UpdatedAt ?? m.CreatedAt)
                .DefaultIfEmpty()
                .Max();
            BoothMembersLastSavedAt = lastMember == default ? (DateTimeOffset?)null : lastMember;

            var mats = await _db.SponsorBoothMaterials
                .Where(m => m.EventId == me.EventId && m.SponsorCompanyId == companyId)
                .OrderBy(m => m.CreatedAt)
                .ToListAsync(ct);
            BoothVideos = mats.Where(m => m.Kind == BoothMaterialKind.Video).ToList();
            BoothCollateral = mats.Where(m => m.Kind == BoothMaterialKind.Collateral).ToList();
            CanUploadCollateral =
                sp is not null && !string.IsNullOrWhiteSpace(sp.SiteUrl) && _sp.IsConfigured
                && _paths.TryResolve(
                       CommunityHub.Core.Integrations.DocLibrary.DocLibraryPaths.SponsorBoothCollateral,
                       out _);
            // §64 — last-saved for the booth-materials section (latest video/file add).
            BoothMaterialsLastSavedAt = mats.Count > 0 ? mats.Max(m => m.CreatedAt) : (DateTimeOffset?)null;
        }

        // Sponsor's e-conomic contacts (ERP is the master). Read LIVE so any add /
        // delete / role change made in e-conomic is reflected here immediately, and
        // the sponsor can manage them — writes go straight back to e-conomic.
        ErpCustomerNumber = await ResolveErpCustomerNumberAsync(companyId, ct);
        if (_erpContacts.CanWrite && ErpCustomerNumber is int erpNo)
        {
            CanManageErpContacts = true;
            try { ErpContacts = await _erpContacts.ListContactsAsync(erpNo, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "CompanyDetails: e-conomic contacts load failed for {Co}.", companyId); }
        }
    }

    /// <summary>Resolve this sponsor's e-conomic customer number from the webshop company.</summary>
    private async Task<int?> ResolveErpCustomerNumberAsync(string companyId, CancellationToken ct)
    {
        if (!_cmOptions.Enabled || !int.TryParse(companyId, out var cid)) return null;
        try
        {
            var company = await _cm.GetCompanyAsync(cid, ct);
            return company is not null && int.TryParse(company.ErpCustomerNumber, out var n) && n > 0 ? n : null;
        }
        catch (Exception ex) { _log.LogWarning(ex, "CompanyDetails: ERP number lookup failed for {Co}.", companyId); return null; }
    }

    // ---- Booth materials (exhibitor-only): videos (URLs) + collateral (files) --

    public async Task<IActionResult> OnPostAddVideoAsync(string? videoUrl, CancellationToken ct)
    {
        var (me, companyId, redirect) = await GuardSponsorAsync(ct);
        if (redirect is not null) return redirect;

        var url = (videoUrl ?? string.Empty).Trim();
        var count = await _db.SponsorBoothMaterials.CountAsync(
            m => m.EventId == me!.EventId && m.SponsorCompanyId == companyId && m.Kind == BoothMaterialKind.Video, ct);
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            Error = "Enter a full video URL starting with https://";
        else if (url.Length > 1000)
            Error = "That URL is too long.";
        else if (count >= MaxVideos)
            Error = $"You can add up to {MaxVideos} videos.";
        else
        {
            _db.SponsorBoothMaterials.Add(new SponsorBoothMaterial
            {
                EventId = me!.EventId, SponsorCompanyId = companyId!, Kind = BoothMaterialKind.Video, Url = url,
                CreatedByEmail = me.Email,
            });
            await _db.SaveChangesAsync(ct);
            Message = "Video added.";
        }
        await LoadAsync(me!, prefill: true, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostUploadCollateralAsync(CancellationToken ct)
    {
        var (me, companyId, redirect) = await GuardSponsorAsync(ct);
        if (redirect is not null) return redirect;

        var sp = _cfg.Load(_cfgOptions.EventConfigPath).SharePoint;
        if (sp is null || string.IsNullOrWhiteSpace(sp.SiteUrl) || !_sp.IsConfigured
            || !_paths.TryResolve(
                   CommunityHub.Core.Integrations.DocLibrary.DocLibraryPaths.SponsorBoothCollateral,
                   out var collateralFolder))
        { Error = "Collateral upload isn't available."; await LoadAsync(me!, prefill: true, ct); return Page(); }

        var count = await _db.SponsorBoothMaterials.CountAsync(
            m => m.EventId == me!.EventId && m.SponsorCompanyId == companyId && m.Kind == BoothMaterialKind.Collateral, ct);
        if (count >= MaxCollateral) { Error = $"You can add up to {MaxCollateral} collateral files."; await LoadAsync(me!, prefill: true, ct); return Page(); }
        if (UploadFile is null || UploadFile.Length == 0) { Error = "Please choose a file to upload."; await LoadAsync(me!, prefill: true, ct); return Page(); }
        if (UploadFile.Length > 25 * Mb) { Error = "File is too large (max 25 MB)."; await LoadAsync(me!, prefill: true, ct); return Page(); }
        var ext = Path.GetExtension(UploadFile.FileName).ToLowerInvariant();
        if (!new[] { ".jpg", ".jpeg", ".png", ".pdf" }.Contains(ext))
        { Error = "Unsupported file type. Allowed: JPG, JPEG, PNG, PDF."; await LoadAsync(me!, prefill: true, ct); return Page(); }

        var sponsorName = await ResolveSponsorNameAsync(companyId!, ct);
        try
        {
            // §455/§476 — STREAM it. This was still buffering the whole file into a MemoryStream
            // and then calling ToArray(), holding a 25 MB brochure in RAM TWICE on a shared
            // instance before a single byte reached SharePoint. Booth collateral is precisely the
            // large-file case §455 was built for, and this was the last sponsor upload path still
            // buffering — the same shape that produced the speaker upload failure.
            var baseName = Path.GetFileNameWithoutExtension(UploadFile.FileName);
            var fileName = $"{SanitizeNameComponent(sponsorName)}_{SanitizeNameComponent(baseName)}{ext}";
            await using var upload = UploadFile.OpenReadStream();
            var (_, webUrl, _) = await _sp.UploadFileStreamAsync(
                sp.SiteUrl, sp.DriveName, collateralFolder, fileName,
                upload, UploadFile.Length,
                string.IsNullOrWhiteSpace(UploadFile.ContentType) ? "application/octet-stream" : UploadFile.ContentType, ct);
            _db.SponsorBoothMaterials.Add(new SponsorBoothMaterial
            {
                EventId = me!.EventId, SponsorCompanyId = companyId!, Kind = BoothMaterialKind.Collateral,
                Url = webUrl ?? string.Empty, FileName = UploadFile.FileName, CreatedByEmail = me.Email,
            });
            await _db.SaveChangesAsync(ct);
            Message = $"Uploaded {UploadFile.FileName}.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "CompanyDetails: collateral upload failed for {Co}.", companyId);
            Error = "The upload could not be completed. Please try again.";
        }
        await LoadAsync(me!, prefill: true, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteMaterialAsync(int materialId, CancellationToken ct)
    {
        var (me, companyId, redirect) = await GuardSponsorAsync(ct);
        if (redirect is not null) return redirect;

        var m = await _db.SponsorBoothMaterials.FirstOrDefaultAsync(
            x => x.Id == materialId && x.EventId == me!.EventId && x.SponsorCompanyId == companyId, ct);
        if (m is not null)
        {
            _db.SponsorBoothMaterials.Remove(m);
            await _db.SaveChangesAsync(ct);
            Message = "Removed.";
        }
        await LoadAsync(me!, prefill: true, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostNotifyMaterialsAsync(CancellationToken ct)
    {
        var (me, companyId, redirect) = await GuardSponsorAsync(ct);
        if (redirect is not null) return redirect;

        var sp = _cfg.Load(_cfgOptions.EventConfigPath).SharePoint;
        var name = await ResolveSponsorNameAsync(companyId!, ct);
        var videos = await _db.SponsorBoothMaterials
            .Where(m => m.EventId == me!.EventId && m.SponsorCompanyId == companyId)
            .OrderBy(m => m.CreatedAt).ToListAsync(ct);
        var recipients = sp?.SponsorUploadNotify ?? new List<string>();
        if (recipients.Count > 0)
        {
            string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
            var vids = videos.Where(v => v.Kind == BoothMaterialKind.Video).Select(v => $"<li><a href=\"{Enc(v.Url)}\">{Enc(v.Url)}</a></li>");
            var cols = videos.Where(v => v.Kind == BoothMaterialKind.Collateral).Select(v => $"<li>{Enc(v.FileName)} — <a href=\"{Enc(v.Url)}\">open</a></li>");
            var html = $"<p>Exhibitor <b>{Enc(name)}</b> updated their booth materials in the Event Hub.</p>"
                + "<p><b>Videos:</b></p><ul>" + (string.Join("", vids) is { Length: > 0 } vh ? vh : "<li>(none)</li>") + "</ul>"
                + "<p><b>Collateral:</b></p><ul>" + (string.Join("", cols) is { Length: > 0 } ch ? ch : "<li>(none)</li>") + "</ul>"
                + "<p>(Zoho Backstage has no materials API — please place these into the exhibitor profile.)</p>";
            foreach (var to in recipients)
            {
                try { await _email.SendAsync(to, $"Booth materials updated — {name} [ELDK27]", html, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "Booth materials notify {To} failed.", to); }
            }
            Message = "Booth materials saved and submitted to the Event System (Zoho) — you don't need to do anything else.";
        }
        else
        {
            Message = "Booth materials saved.";
        }
        await LoadAsync(me!, prefill: true, ct);
        return Page();
    }

    // ---- Booth members (exhibitor-only) -------------------------------------

    public async Task<IActionResult> OnPostAddMemberAsync(
        string? firstName, string? lastName, string? email, BoothMemberRole role, CancellationToken ct)
    {
        var (me, companyId, redirect) = await GuardSponsorAsync(ct);
        if (redirect is not null) return redirect;

        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName) || string.IsNullOrWhiteSpace(email))
            Error = "First name, last name and email are required for a booth member.";
        else if (!LooksLikeEmail(email))
            Error = "Please enter a valid email address.";
        else
        {
            var em = email.Trim();
            // Match any row with this email (incl. a tombstoned one) so re-adding someone you
            // removed REVIVES the original row rather than erroring or duplicating.
            var existing = await _db.SponsorBoothMembers.FirstOrDefaultAsync(
                m => m.EventId == me!.EventId && m.SponsorCompanyId == companyId && m.Email == em, ct);
            if (existing is { DeletedAt: null })
                Error = "A booth member with that email already exists.";
            else
            {
                if (existing is not null)
                {
                    existing.DeletedAt = null;   // revive
                    existing.FirstName = firstName.Trim(); existing.LastName = lastName.Trim();
                    existing.Role = role; existing.UpdatedAt = _clock.GetUtcNow();
                    existing.SyncedToZoho = false;
                }
                else
                {
                    _db.SponsorBoothMembers.Add(new SponsorBoothMember
                    {
                        EventId = me!.EventId, SponsorCompanyId = companyId!,
                        FirstName = firstName.Trim(), LastName = lastName.Trim(), Email = em, Role = role,
                    });
                }
                await _db.SaveChangesAsync(ct);
                // §66 — auto-sync on add (no manual Sync button). Only the new/revived
                // member is unsynced, so this pushes just them (no redundant Zoho write).
                var add = await SyncMembersInternalAsync(me!, companyId!, ct);
                Message = "Booth member added. " + SummariseSync(add);
            }
        }
        await LoadAsync(me!, prefill: true, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateMemberAsync(
        int memberId, string? firstName, string? lastName, string? email, BoothMemberRole role, CancellationToken ct)
    {
        var ajax = WantsJson();
        var (me, companyId, redirect) = await GuardSponsorAsync(ct);
        if (redirect is not null)
            return ajax ? new JsonResult(new { ok = false, error = "not-available" }) { StatusCode = 403 } : redirect;

        var m = await _db.SponsorBoothMembers.FirstOrDefaultAsync(
            x => x.Id == memberId && x.EventId == me!.EventId && x.SponsorCompanyId == companyId && x.DeletedAt == null, ct);

        string? err = null;
        if (m is null) err = "That booth member was not found.";
        else if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName) || string.IsNullOrWhiteSpace(email))
            err = "First name, last name and email are required.";
        else if (!LooksLikeEmail(email))
            err = "Please enter a valid email address.";

        if (err is not null)
        {
            if (ajax) return new JsonResult(new { ok = false, error = err }) { StatusCode = 400 };
            Error = err;
            await LoadAsync(me!, prefill: true, ct);
            return Page();
        }

        // Only re-sync when something actually changed (respects the no-redundant-Zoho-write
        // / email-is-key 3× cap rules): an unchanged row never touches Zoho.
        var changed = m!.FirstName != firstName!.Trim()
                      || m.LastName != lastName!.Trim()
                      || !string.Equals(m.Email, email!.Trim(), StringComparison.OrdinalIgnoreCase)
                      || m.Role != role;
        if (changed)
        {
            m.FirstName = firstName.Trim(); m.LastName = lastName.Trim();
            m.Email = email.Trim(); m.Role = role; m.UpdatedAt = _clock.GetUtcNow();
            m.SyncedToZoho = false;   // re-sync needed
            await _db.SaveChangesAsync(ct);
        }

        var sync = changed ? await SyncMembersInternalAsync(me!, companyId!, ct) : (SponsorZohoSyncService.BoothSyncResult?)null;

        if (ajax)
        {
            var savedAt = (m.UpdatedAt ?? m.CreatedAt).ToUniversalTime().ToString("dd MMM yyyy HH:mm") + " UTC";
            return new JsonResult(new
            {
                ok = true,
                savedAt,
                sync = sync is null ? "No changes." : SummariseSync(sync),
            });
        }

        Message = changed ? "Booth member updated. " + SummariseSync(sync!) : "No changes to save.";
        await LoadAsync(me!, prefill: true, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteMemberAsync(int memberId, CancellationToken ct)
    {
        var (me, companyId, redirect) = await GuardSponsorAsync(ct);
        if (redirect is not null) return redirect;

        var m = await _db.SponsorBoothMembers.FirstOrDefaultAsync(
            x => x.Id == memberId && x.EventId == me!.EventId && x.SponsorCompanyId == companyId && x.DeletedAt == null, ct);
        if (m is not null)
        {
            var email = m.Email;

            // Member delete ONLY — never the exhibitor/sponsor RECORD (REQUIREMENTS §56).
            // Best-effort delete in Zoho FIRST (Zoho now supports member delete). A Zoho
            // failure must NOT break the hub delete, so this is fully fail-soft.
            var zoho = await _zohoSync.DeleteBoothMemberAsync(me!.EventId, companyId!, email, ct);

            // Tombstone (soft-delete) regardless: the tombstone is the hub's record-keeping
            // and ALSO blocks the add-only re-pull, so a removal can never be resurrected
            // even if the Zoho call failed. The sync skips re-pulling a tombstoned email.
            m.DeletedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);

            Message = zoho switch
            {
                SponsorZohoSyncService.BoothMemberDeleteResult.Deleted =>
                    "Booth member removed from the Event Hub and from Zoho Backstage.",
                SponsorZohoSyncService.BoothMemberDeleteResult.NotFoundInZoho =>
                    "Booth member removed from the Event Hub. (They were not present in Zoho Backstage.)",
                SponsorZohoSyncService.BoothMemberDeleteResult.SyncDisabled =>
                    "Booth member removed from the Event Hub.",
                _ => // Error
                    "Booth member removed from the Event Hub, but removing them in Zoho Backstage "
                    + "failed — please also remove them in Zoho directly. They will not re-appear here.",
            };
        }
        await LoadAsync(me!, prefill: true, ct);
        return Page();
    }

    /// <summary>True when the request is an AJAX (fetch) post — used by the §66 booth-member
    /// auto-save so it returns JSON instead of re-rendering the whole page.</summary>
    private bool WantsJson() =>
        string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

    /// <summary>§66 — run the Zoho booth-member sync inline (auto-sync). The sync itself is
    /// add-only by email and skips members already flagged synced, so re-running it after a
    /// single change never re-pushes unchanged members (no redundant Zoho write).</summary>
    private async Task<SponsorZohoSyncService.BoothSyncResult> SyncMembersInternalAsync(
        CurrentParticipant me, string companyId, CancellationToken ct)
    {
        var name = await ResolveSponsorNameAsync(companyId, ct);
        return await _zohoSync.SyncBoothMembersAsync(me.EventId, companyId, name ?? string.Empty, ct);
    }

    private static string SummariseSync(SponsorZohoSyncService.BoothSyncResult r) =>
        !r.Enabled ? "Zoho Backstage sync is not enabled for this environment."
        : r.Error is not null ? r.Error
        : $"Synced to Zoho — {r.AddedToZoho} added, {r.PulledFromZoho} pulled.";

    /// <summary>Shared guard for the booth-member handlers: returns (me, companyId) or a redirect/page.</summary>
    private async Task<(CurrentParticipant? me, string? companyId, IActionResult? redirect)> GuardSponsorAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return (null, null, RedirectToPage("/Login"));
        if (me.Role != ParticipantRole.Sponsor) { AccessDenied = true; return (me, null, Page()); }
        var companyId = await GetCompanyIdAsync(me.ParticipantId, ct);
        if (companyId is null) { NoCompanyLink = true; await LoadAsync(me, prefill: true, ct); return (me, null, Page()); }
        return (me, companyId, null);
    }

    private static bool LooksLikeEmail(string? s) =>
        !string.IsNullOrWhiteSpace(s) && s.Contains('@') && s.IndexOf('.', s.IndexOf('@')) > 0;

    private async Task<SponsorInfo> GetOrCreateInfoAsync(int eventId, string companyId, CancellationToken ct)
    {
        var info = await _db.SponsorInfos.FirstOrDefaultAsync(
            s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);
        if (info is null)
        {
            info = new SponsorInfo { EventId = eventId, SponsorCompanyId = companyId, CreatedAt = _clock.GetUtcNow(), UpdatedAt = _clock.GetUtcNow() };
            _db.SponsorInfos.Add(info);
        }
        else
        {
            info.UpdatedAt = _clock.GetUtcNow();
        }
        return info;
    }

    /// <summary>Public name from Company Manager. With allowFallback, returns "Company {id}" if unresolved.</summary>
    private async Task<string?> ResolveSponsorNameAsync(string companyId, CancellationToken ct, bool allowFallback = true)
    {
        if (_cmOptions.Enabled && int.TryParse(companyId, out var cid))
        {
            try
            {
                var company = await _cm.GetCompanyAsync(cid, ct);
                if (company is not null)
                {
                    var name = !string.IsNullOrWhiteSpace(company.PublicName) ? company.PublicName : company.Name;
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "CompanyDetails: Company Manager lookup failed for {Co}.", cid);
            }
        }
        return allowFallback ? $"Company {companyId}" : null;
    }

    private static string? ValidateUrl(string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;     // optional
        var v = value.Trim();
        if (v.Length > MaxUrl) return $"{label} is too long (max {MaxUrl} chars).";
        if (!v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return $"{label} must be a full URL starting with https:// (e.g. https://www.example.com).";
        return Uri.TryCreate(v, UriKind.Absolute, out _) ? null : $"{label} is not a valid URL.";
    }

    // §802.1 — the save is REFUSED and the message carries the numbers. Operator 2026-08-04: *"tell
    // the exhibitor the issue including number of chars and block them from saving until fixed"*.
    // 🔒 Server-side, deliberately: the textarea's `maxlength` is a convenience, not a rule — it does
    // not survive every paste path and does not exist at all for anything that posts without the page.
    private static string? ValidateLen(string label, string? value, int max) =>
        CommunityHub.Core.Integrations.ZohoExhibitorLimits.IsTooLong(value, max)
            ? CommunityHub.Core.Integrations.ZohoExhibitorLimits.TooLongMessage(label, value, max)
            : null;

    private static string? NormaliseOrNull(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>SharePoint-safe filename component (strips illegal chars, collapses whitespace).</summary>
    private static string SanitizeNameComponent(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Sponsor";
        var cleaned = new string(name.Where(c => "\"*:<>?/\\|".IndexOf(c) < 0).ToArray());
        cleaned = string.Join(" ", cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? "Sponsor" : cleaned;
    }

    private async Task<string?> GetCompanyIdAsync(int participantId, CancellationToken ct) =>
        await _db.Participants
            .Where(p => p.Id == participantId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);
}
