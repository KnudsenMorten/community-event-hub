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
/// Render + edit model for the sponsor Logos wizard step (§32 / §285 Phase 2). Carries the three
/// posted logo files (SoMe / Print / Zoho) + the currently-saved logo paths for display. Bound
/// with an EMPTY prefix by the wizard host, so the IFormFile names match the partial.
/// </summary>
public sealed class SponsorLogosModel
{
    public IFormFile? SoMeLogo { get; set; }    // PNG for social-media branding
    public IFormFile? PrintLogo { get; set; }   // vector (.eps/.ai/.pdf) for print
    public IFormFile? ZohoLogo { get; set; }    // PNG for the Zoho lead system

    // Display-only (never bound): the currently-saved file for EACH of the three logos, so a
    // coordinator can see what's already uploaded (file inputs can't be pre-filled). SoMe + Zoho
    // both persist to LogoRasterPath on SponsorInfo, so these come from the per-kind upload audit.
    public string? CurrentSoMeFileName { get; set; }
    public string? CurrentPrintFileName { get; set; }
    public string? CurrentZohoFileName { get; set; }
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
    private readonly ILogger<SponsorLogosFormService> _log;

    public SponsorLogosFormService(
        CommunityHubDbContext db, TimeProvider clock, EventEditionConfigLoader cfg,
        EventConfigOptions cfgOptions, SharePointUploadClient sp, CompanyManagerClient cm,
        CompanyManagerOptions cmOptions, ILogger<SponsorLogosFormService> log)
    {
        _db = db;
        _clock = clock;
        _cfg = cfg;
        _cfgOptions = cfgOptions;
        _sp = sp;
        _cm = cm;
        _cmOptions = cmOptions;
        _log = log;
    }

    private sealed record UploadSpec(string Folder, string Prefix, string[] Exts, long MaxBytes);

    private Task<string?> CompanyIdAsync(int participantId, CancellationToken ct) =>
        _db.Participants.Where(p => p.Id == participantId)
            .Select(p => p.SponsorCompanyId).FirstOrDefaultAsync(ct);

    public async Task<SponsorLogosModel> LoadAsync(int eventId, int participantId, CancellationToken ct)
    {
        var companyId = await CompanyIdAsync(participantId, ct);
        var model = new SponsorLogosModel();
        if (companyId is null) return model;

        // Latest uploaded file per kind (some/print/zoho) from the audit — the only place all three
        // are distinguishable (SoMe + Zoho share LogoRasterPath on SponsorInfo).
        var latest = await LatestByKindAsync(_db, eventId, companyId, ct);
        model.CurrentSoMeFileName = latest.GetValueOrDefault("some");
        model.CurrentPrintFileName = latest.GetValueOrDefault("print");
        model.CurrentZohoFileName = latest.GetValueOrDefault("zoho");
        return model;
    }

    /// <summary>Latest uploaded file name per logo kind (some/print/zoho) for a company.</summary>
    internal static async Task<Dictionary<string, string>> LatestByKindAsync(
        CommunityHubDbContext db, int eventId, string companyId, CancellationToken ct) =>
        (await db.SponsorUploadAudits.AsNoTracking()
            .Where(a => a.EventId == eventId && a.SponsorCompanyId == companyId
                        && (a.Kind == "some" || a.Kind == "print" || a.Kind == "zoho"))
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

        // Upload whichever of the three logos were provided this save; each is independent.
        var uploaded = 0;
        uploaded += await TryUploadAsync(model.SoMeLogo, "some", eventId, companyId, email, sponsorName, sp, modelState, ct);
        uploaded += await TryUploadAsync(model.PrintLogo, "print", eventId, companyId, email, sponsorName, sp, modelState, ct);
        uploaded += await TryUploadAsync(model.ZohoLogo, "zoho", eventId, companyId, email, sponsorName, sp, modelState, ct);

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

        var fieldKey = kind switch { "print" => nameof(SponsorLogosModel.PrintLogo), "zoho" => nameof(SponsorLogosModel.ZohoLogo), _ => nameof(SponsorLogosModel.SoMeLogo) };

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
        if (!spec.Exts.Contains(ext))
        {
            modelState.AddModelError(fieldKey, $"Unsupported file type. Allowed: {string.Join(", ", spec.Exts)}.");
            return 0;
        }

        try
        {
            // §455 — STREAM rather than buffer. A print logo is a VECTOR file allowed up to 25 MB,
            // and the old path held it in memory twice (MemoryStream + ToArray) before anything
            // reached SharePoint. Same shape as the speaker-upload failure that motivated §455.
            var fileName = await NextVersionedNameAsync(
                sp!.SiteUrl, sp.DriveName, spec.Folder, spec.Prefix, SanitizeNameComponent(sponsorName), ext, ct);
            await using var upload = file.OpenReadStream();
            var (_, webUrl, _) = await _sp.UploadFileStreamAsync(
                sp.SiteUrl, sp.DriveName, spec.Folder, fileName, upload, file.Length,
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType, ct);

            // Persist the logo location so the "logos" step completes (§291). SoMe + Zoho are
            // raster PNGs → LogoRasterPath; the print logo is vector → LogoVectorPath.
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

    // ---- helpers replicated from CompanyDetailsModel so the two stay byte-consistent ----------

    private UploadSpec? ResolveKind(string kind, SharePointEditionConfig? sp)
    {
        if (sp is null || string.IsNullOrWhiteSpace(sp.SiteUrl) || !_sp.IsConfigured) return null;
        return kind switch
        {
            "some" when !string.IsNullOrWhiteSpace(sp.LogoSoMeBrandingFolderPath) =>
                new(sp.LogoSoMeBrandingFolderPath, "SoMeBrandingLogo_", new[] { ".png" }, 5 * Mb),
            "print" when !string.IsNullOrWhiteSpace(sp.LogoPrintFolderPath) =>
                new(sp.LogoPrintFolderPath, "PrintLogo_", new[] { ".eps", ".ai", ".pdf" }, 25 * Mb),
            "zoho" when !string.IsNullOrWhiteSpace(sp.LogoZohoFolderPath) =>
                new(sp.LogoZohoFolderPath, "ZohoLogo_", new[] { ".png" }, 5 * Mb),
            _ => null,
        };
    }

    private async Task<string> NextVersionedNameAsync(
        string siteUrl, string driveName, string folder, string prefix, string sponsor, string ext, CancellationToken ct)
    {
        var stem = $"{prefix}{sponsor}_v";
        var next = 1;
        try
        {
            var files = await _sp.ListFolderFilesAsync(siteUrl, driveName, folder, ct);
            var rx = new Regex("^" + Regex.Escape(stem) + @"(\d+)\b", RegexOptions.IgnoreCase);
            var max = files.Select(f => rx.Match(f.Name)).Where(m => m.Success)
                .Select(m => int.TryParse(m.Groups[1].Value, out var n) ? n : 0).DefaultIfEmpty(0).Max();
            next = max + 1;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sponsor logos inline step: could not list {Folder} to version; defaulting to v1.", folder);
        }
        return $"{stem}{next}{ext}";
    }

    private static int ParseVersion(string fileName)
    {
        var m = Regex.Match(fileName, @"_v(\d+)\b", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > 0 ? n : 1;
    }

    private static string SanitizeNameComponent(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Sponsor";
        var cleaned = new string(name.Where(c => "\"*:<>?/\\|".IndexOf(c) < 0).ToArray());
        cleaned = string.Join(" ", cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? "Sponsor" : cleaned;
    }

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
