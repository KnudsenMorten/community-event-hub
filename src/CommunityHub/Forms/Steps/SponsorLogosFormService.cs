using System.Text.RegularExpressions;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// Render + edit model for the sponsor Logos wizard step (§32 / §285 Phase 2). Carries the two
/// posted logo files (Web / Print) + the currently-saved logo names for display. Bound with an
/// EMPTY prefix by the wizard host, so the IFormFile names match the partial.
/// </summary>
/// <remarks>
/// §6.7 / §768.14 — there were THREE logos. The separate "Logo for the Event System (Zoho) lead
/// system" upload is gone: one Web PNG now serves both the promotion graphics and the external
/// event system, so a sponsor uploads their logo once instead of twice into two folders that had to
/// be kept in step by hand.
/// </remarks>
public sealed class SponsorLogosModel
{
    public IFormFile? SoMeLogo { get; set; }    // PNG for web + social-media branding
    public IFormFile? PrintLogo { get; set; }   // vector (.eps/.ai/.pdf) for print

    // Display-only (never bound): the currently-saved file for each logo, so a coordinator can see
    // what's already uploaded (file inputs can't be pre-filled). They come from the per-kind upload
    // audit rather than SponsorInfo, which stores only raster-vs-vector.
    public string? CurrentSoMeFileName { get; set; }
    public string? CurrentPrintFileName { get; set; }
}

/// <summary>
/// Shared submit-service for the sponsor Logos step (§285 Phase 2). Runs the SAME upload flow the
/// standalone Company Details "Logos &amp; artwork" section uses (<c>CompanyDetailsModel.OnPostUploadAsync</c>):
/// resolve the per-kind spec from the edition SharePoint config, validate size/type, upload a
/// VERSIONED file to SharePoint, persist the logo path on <see cref="SponsorInfo"/> (so the wizard
/// step's completion — LogoRasterPath/LogoVectorPath — flips to done, §291), and write the
/// <see cref="SponsorUploadAudit"/> record so every coordinator sees the current file. Uploads are
/// optional-per-save (fill any, then Next). Self-registers via <see cref="IWizardFormService"/>.
/// (Note: the organizer email notification stays on the standalone page path for now; the audit +
/// SharePoint file still record every wizard upload.)
/// </summary>
public sealed class SponsorLogosFormService : IWizardFormService
{
    private const long Mb = 1024 * 1024;

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly EventEditionConfigLoader _cfg;
    private readonly EventConfigOptions _cfgOptions;
    private readonly SharePointUploadClient _sp;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _cmOptions;
    private readonly CommunityHub.Core.Integrations.DocLibrary.IDocLibraryPathResolver _paths;
    private readonly ILogger<SponsorLogosFormService> _log;

    public SponsorLogosFormService(
        CommunityHubDbContext db, TimeProvider clock, EventEditionConfigLoader cfg,
        EventConfigOptions cfgOptions, SharePointUploadClient sp, CompanyManagerClient cm,
        CompanyManagerOptions cmOptions,
        CommunityHub.Core.Integrations.DocLibrary.IDocLibraryPathResolver paths,
        ILogger<SponsorLogosFormService> log)
    {
        _paths = paths;
        _db = db;
        _clock = clock;
        _cfg = cfg;
        _cfgOptions = cfgOptions;
        _sp = sp;
        _cm = cm;
        _cmOptions = cmOptions;
        _log = log;
    }

    private Task<string?> CompanyIdAsync(int participantId, CancellationToken ct) =>
        _db.Participants.Where(p => p.Id == participantId)
            .Select(p => p.SponsorCompanyId).FirstOrDefaultAsync(ct);

    public async Task<SponsorLogosModel> LoadAsync(int eventId, int participantId, CancellationToken ct)
    {
        var companyId = await CompanyIdAsync(participantId, ct);
        var model = new SponsorLogosModel();
        if (companyId is null) return model;

        // Latest uploaded file per kind (some/print) from the audit — the only place the two are
        // distinguishable, since SponsorInfo records only raster-vs-vector.
        var latest = await LatestByKindAsync(_db, eventId, companyId, ct);
        model.CurrentSoMeFileName = latest.GetValueOrDefault("some");
        model.CurrentPrintFileName = latest.GetValueOrDefault("print");
        return model;
    }

    /// <summary>Latest uploaded file name per logo kind (some/print) for a company.</summary>
    /// <remarks>
    /// §768.14 — historical <c>zoho</c> rows are deliberately NOT read back. The kind is retired, so
    /// showing one would offer a sponsor a "current file" for an upload that no longer exists.
    /// </remarks>
    internal static async Task<Dictionary<string, string>> LatestByKindAsync(
        CommunityHubDbContext db, int eventId, string companyId, CancellationToken ct) =>
        (await db.SponsorUploadAudits.AsNoTracking()
            .Where(a => a.EventId == eventId && a.SponsorCompanyId == companyId
                        && (a.Kind == "some" || a.Kind == "print"))
            .GroupBy(a => a.Kind)
            .Select(g => new { Kind = g.Key, FileName = g.OrderByDescending(x => x.UploadedAt).Select(x => x.FileName).First() })
            .ToListAsync(ct))
        .ToDictionary(x => x.Kind, x => x.FileName);

    public async Task<WizardStepOutcome> SaveAsync(
        SponsorLogosModel model, int eventId, int participantId, string email,
        ModelStateDictionary modelState, CancellationToken ct)
    {
        var companyId = await CompanyIdAsync(participantId, ct);
        if (companyId is null) return WizardStepOutcome.NotRelevant;

        var sp = _cfg.Load(_cfgOptions.EventConfigPath).SharePoint;
        var sponsorName = await ResolveSponsorNameAsync(companyId, ct);

        // Upload whichever of the two logos were provided this save; each is independent.
        var uploaded = 0;
        uploaded += await TryUploadAsync(model.SoMeLogo, "some", eventId, companyId, email, sponsorName, sp, modelState, ct);
        uploaded += await TryUploadAsync(model.PrintLogo, "print", eventId, companyId, email, sponsorName, sp, modelState, ct);

        // A validation error on ANY file re-renders the step; otherwise advance (uploads are
        // optional — a sponsor may add a logo later).
        return modelState.IsValid ? WizardStepOutcome.Advance : WizardStepOutcome.Invalid;
    }

    /// <summary>Upload one posted logo file; returns 1 on success, 0 when none/invalid. Adds a
    /// field error to <paramref name="modelState"/> on a validation/upload failure.</summary>
    private async Task<int> TryUploadAsync(
        IFormFile? file, string kind, int eventId, string companyId, string email, string? sponsorName,
        SharePointEditionConfig? sp, ModelStateDictionary modelState, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return 0;   // not provided this save

        var fieldKey = kind == "print"
            ? nameof(SponsorLogosModel.PrintLogo)
            : nameof(SponsorLogosModel.SoMeLogo);

        var spec = ResolveKind(kind, sp);
        if (spec is null)
        {
            modelState.AddModelError(fieldKey, "Logo uploads aren't available right now — please try again later or contact the organizers.");
            return 0;
        }
        if (file.Length > spec.MaxBytes)
        {
            modelState.AddModelError(fieldKey, $"File is too large (max {spec.MaxBytes / Mb} MB).");
            return 0;
        }
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!spec.Accepts(ext))
        {
            modelState.AddModelError(fieldKey, $"Unsupported file type. Allowed: {spec.AllowedText}.");
            return 0;
        }

        try
        {
            // §455 — STREAM rather than buffer. A print logo is a VECTOR file allowed up to 25 MB,
            // and the old path held it in memory twice (MemoryStream + ToArray) before anything
            // reached SharePoint. Same shape as the speaker-upload failure that motivated §455.
            var fileName = await CommunityHub.Uploads.SponsorUploadKinds.NextVersionedNameAsync(
                _sp, sp!.SiteUrl, sp.DriveName, spec.Folder, kind, sponsorName, ext, _log, ct);
            await using var upload = file.OpenReadStream();
            var (_, webUrl, _) = await _sp.UploadFileStreamAsync(
                sp.SiteUrl, sp.DriveName, spec.Folder, fileName, upload, file.Length,
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType, ct);

            // Persist the logo location so the "logos" step completes (§291). The Web logo is a
            // raster PNG → LogoRasterPath; the print logo is vector → LogoVectorPath.
            var info = await GetOrCreateInfoAsync(eventId, companyId, ct);
            if (kind == "print") { info.LogoVectorPath = webUrl; info.LogoVectorFileName = fileName; }
            else                 { info.LogoRasterPath = webUrl; info.LogoRasterFileName = fileName; }
            info.LastUpdatedByEmail = email;
            await _db.SaveChangesAsync(ct);

            _db.SponsorUploadAudits.Add(new SponsorUploadAudit
            {
                EventId = eventId, SponsorCompanyId = companyId, Kind = kind, FileName = fileName,
                Version = ParseVersion(fileName), WebUrl = webUrl, UploadedByEmail = email,
                UploadedAt = _clock.GetUtcNow(),
            });
            await _db.SaveChangesAsync(ct);
            return 1;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sponsor logos inline step: {Kind} upload failed for company {Co}.", kind, companyId);
            modelState.AddModelError(fieldKey, "The upload could not be completed. Please try again, or contact the organizers.");
            return 0;
        }
    }

    // ---- the shared rules, no longer replicated ------------------------------------------------

    /// <summary>
    /// §768.14 — delegates to <see cref="SponsorUploadKinds.Resolve"/>.
    /// </summary>
    /// <remarks>
    /// 🔒 This was the FOURTH copy of the upload rules, and its own section header described the
    /// mechanism keeping it correct: <i>"helpers replicated from CompanyDetailsModel so the two stay
    /// byte-consistent"</i>. Nothing enforced that. §768.13 collapsed the Company Details copy and
    /// left this one, so for one day the wizard and the page really did disagree — this file still
    /// wrote <c>ZohoLogo_</c>, <c>SoMeBrandingLogo_</c> and the old <c>_v{N}</c> names. A sponsor's
    /// logo would land under a different name depending on whether they used the wizard or the page,
    /// and the graphics matcher only ever looked for one of them.
    /// </remarks>
    private CommunityHub.Uploads.SponsorUploadSpec? ResolveKind(string kind, SharePointEditionConfig? sp) =>
        _sp.IsConfigured
            ? CommunityHub.Uploads.SponsorUploadKinds.Resolve(kind, _paths, sp)
            : null;

    private static int ParseVersion(string fileName) =>
        CommunityHub.Uploads.SponsorUploadNaming.ParseVersion(fileName);

    private async Task<SponsorInfo> GetOrCreateInfoAsync(int eventId, string companyId, CancellationToken ct)
    {
        var info = await _db.SponsorInfos.FirstOrDefaultAsync(
            s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);
        if (info is null)
        {
            info = new SponsorInfo { EventId = eventId, SponsorCompanyId = companyId, CreatedAt = _clock.GetUtcNow(), UpdatedAt = _clock.GetUtcNow() };
            _db.SponsorInfos.Add(info);
        }
        else { info.UpdatedAt = _clock.GetUtcNow(); }
        return info;
    }

    private async Task<string?> ResolveSponsorNameAsync(string companyId, CancellationToken ct)
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
            catch (Exception ex) { _log.LogWarning(ex, "Sponsor logos inline step: Company Manager lookup failed for {Co}.", cid); }
        }
        return $"Company {companyId}";
    }
}
