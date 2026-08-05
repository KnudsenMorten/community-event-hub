using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// §297 — render + edit model for the inline sponsor Booth-materials step. Adds up to three video
/// URLs inline and shows the current materials. Collateral FILE uploads stay on the standalone
/// Company Details page (they need the SharePoint upload flow); this step keeps the sponsor in the
/// wizard for the common case (videos) and shows what's already there. Empty-prefix bound.
/// </summary>
public sealed class SponsorBoothMaterialsModel
{
    // §476 — SIX rows, matching the service's own MaxVideos = 6. The step previously offered three
    // while the service accepted six, so the UI was capping below its own contract.
    public string? Video1Url { get; set; }
    public string? Video2Url { get; set; }
    public string? Video3Url { get; set; }
    public string? Video4Url { get; set; }
    public string? Video5Url { get; set; }
    public string? Video6Url { get; set; }

    /// <summary>§476 — collateral FILE upload, inline. Previously the step told the sponsor to go to
    /// the Company Details page, i.e. leave the wizard to finish a wizard step (§473's complaint).</summary>
    public Microsoft.AspNetCore.Http.IFormFile? CollateralFile { get; set; }

    /// <summary>True when the collateral folder is configured, so the view can show the field
    /// rather than offer an upload that cannot work.</summary>
    public bool CanUploadCollateral { get; set; }

    /// <summary>§476 — ids the sponsor ticked for removal (posted from the current-materials list).</summary>
    public List<int>? RemoveIds { get; set; }

    /// <summary>The materials already saved (videos + collateral), each removable by id.</summary>
    public List<Material> CurrentMaterials { get; set; } = new();
    public sealed record Material(int Id, string Kind, string Display);
}

/// <summary>
/// §297 — shared submit-service for the inline sponsor Booth-materials step. Previously handler-less
/// (the wizard fell back to a link that left the wizard); now it renders the current materials INLINE
/// and lets the sponsor add video URLs without leaving. Persists to <see cref="SponsorBoothMaterial"/>.
/// Self-registers via <see cref="IWizardFormService"/>.
/// </summary>
public sealed class SponsorBoothMaterialsFormService : IWizardFormService
{
    private const int MaxVideos = 6;        // matches the standalone Company Details cap
    private const int MaxCollateral = 6;    // §476 — same cap the operator asked for
    private const long Mb = 1024 * 1024;
    private static readonly string[] CollateralExts = { ".jpg", ".jpeg", ".png", ".pdf" };

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly Core.Config.EventEditionConfigLoader? _cfg;
    private readonly Core.Config.EventConfigOptions? _cfgOptions;
    private readonly Core.Integrations.SharePointUploadClient? _sp;
    private readonly Core.Integrations.DocLibrary.IDocLibraryPathResolver? _paths;

    /// <summary>The SharePoint dependencies are OPTIONAL (same shape as
    /// <see cref="SponsorSessionFormService"/>): unconfigured hosts and tests keep working, and the
    /// step simply does not offer a collateral upload it cannot honour.</summary>
    public SponsorBoothMaterialsFormService(
        CommunityHubDbContext db,
        TimeProvider clock,
        Core.Config.EventEditionConfigLoader? cfg = null,
        Core.Config.EventConfigOptions? cfgOptions = null,
        Core.Integrations.SharePointUploadClient? sp = null,
        Core.Integrations.DocLibrary.IDocLibraryPathResolver? paths = null)
    {
        _db = db;
        _clock = clock;
        _cfg = cfg;
        _cfgOptions = cfgOptions;
        _sp = sp;
        _paths = paths;
    }

    /// <summary>The configured SharePoint block, or null when this host has no config wired.</summary>
    private Core.Config.SharePointEditionConfig? SharePointConfig() =>
        _cfg is null || _cfgOptions is null ? null : _cfg.Load(_cfgOptions.EventConfigPath).SharePoint;

    /// <summary>
    /// §768.14 — the collateral folder, from the document-library registry rather than an edition
    /// config key. Null when no resolver is wired (tests) or the key does not resolve.
    /// </summary>
    private string? CollateralFolder() =>
        _paths is not null
        && _paths.TryResolve(Core.Integrations.DocLibrary.DocLibraryPaths.SponsorBoothCollateral, out var f)
            ? f
            : null;

    /// <summary>§476 — collateral upload is possible only with a client AND a resolvable folder.</summary>
    private bool CanUploadCollateral(Core.Config.SharePointEditionConfig? sp) =>
        sp is not null && _sp is not null
        && !string.IsNullOrWhiteSpace(sp.SiteUrl)
        && CollateralFolder() is not null
        && _sp.IsConfigured;

    private Task<string?> CompanyIdAsync(int participantId, CancellationToken ct) =>
        _db.Participants.Where(p => p.Id == participantId)
            .Select(p => p.SponsorCompanyId).FirstOrDefaultAsync(ct);

    public async Task<SponsorBoothMaterialsModel> LoadAsync(int eventId, int participantId, CancellationToken ct)
    {
        var model = new SponsorBoothMaterialsModel();
        var companyId = await CompanyIdAsync(participantId, ct);
        if (companyId is null) return model;
        model.CurrentMaterials = await CurrentAsync(eventId, companyId, ct);
        model.CanUploadCollateral = CanUploadCollateral(SharePointConfig());
        return model;
    }

    private async Task<List<SponsorBoothMaterialsModel.Material>> CurrentAsync(int eventId, string companyId, CancellationToken ct) =>
        (await _db.SponsorBoothMaterials.AsNoTracking()
            .Where(m => m.EventId == eventId && m.SponsorCompanyId == companyId)
            .OrderBy(m => m.Kind).ThenBy(m => m.Id)
            .Select(m => new { m.Id, m.Kind, m.Url, m.FileName })
            .ToListAsync(ct))
        .Select(m => new SponsorBoothMaterialsModel.Material(
            m.Id,
            m.Kind.ToString(),
            m.Kind == BoothMaterialKind.Video ? m.Url : (m.FileName ?? m.Url)))
        .ToList();

    /// <summary>
    /// §476 — remove the materials the sponsor ticked. A wrong video URL previously could only be
    /// ADDED past (the list was read-only), so a typo lived on the booth screen forever. Done with
    /// tick boxes processed on save rather than per-row delete buttons, because the wizard host owns
    /// the ONE form on the page and a nested form would not post.
    ///
    /// <para>Scoped by event + company in the WHERE clause, so a hand-made post cannot delete
    /// another company's rows by id.</para>
    /// </summary>
    private async Task<int> RemoveSelectedAsync(
        SponsorBoothMaterialsModel model, int eventId, string companyId, CancellationToken ct)
    {
        var ids = model.RemoveIds?.Where(i => i > 0).Distinct().ToList();
        if (ids is null || ids.Count == 0) return 0;

        var rows = await _db.SponsorBoothMaterials
            .Where(m => m.EventId == eventId && m.SponsorCompanyId == companyId && ids.Contains(m.Id))
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;

        _db.SponsorBoothMaterials.RemoveRange(rows);
        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task<WizardStepOutcome> SaveAsync(
        SponsorBoothMaterialsModel model, int eventId, int participantId, string email,
        ModelStateDictionary modelState, CancellationToken ct)
    {
        var companyId = await CompanyIdAsync(participantId, ct);
        if (companyId is null) return WizardStepOutcome.NotRelevant;

        // §476 — removals FIRST, so a sponsor can delete a wrong video and add its replacement in
        // the same save without tripping the MaxVideos cap on rows that are on their way out.
        await RemoveSelectedAsync(model, eventId, companyId, ct);

        var videoCount = await _db.SponsorBoothMaterials.CountAsync(
            m => m.EventId == eventId && m.SponsorCompanyId == companyId && m.Kind == BoothMaterialKind.Video, ct);

        var urls = new[]
        {
            Trim(model.Video1Url), Trim(model.Video2Url), Trim(model.Video3Url),
            Trim(model.Video4Url), Trim(model.Video5Url), Trim(model.Video6Url),
        };
        var added = 0;
        for (var i = 0; i < urls.Length; i++)
        {
            var url = urls[i];
            if (url is null) continue;
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.Length > 1000)
            {
                modelState.AddModelError(string.Empty, $"Video {i + 1}: enter a full URL starting with https:// (or leave it blank).");
                continue;
            }
            if (videoCount + added >= MaxVideos)
            {
                modelState.AddModelError(string.Empty, $"You can add up to {MaxVideos} videos.");
                break;
            }
            _db.SponsorBoothMaterials.Add(new SponsorBoothMaterial
            {
                EventId = eventId, SponsorCompanyId = companyId, Kind = BoothMaterialKind.Video, Url = url,
                CreatedByEmail = email,
            });
            added++;
        }

        if (added > 0) await _db.SaveChangesAsync(ct);

        // §476 — collateral FILE upload, inline. Runs AFTER the videos so a bad URL and a bad file
        // are reported together rather than one hiding the other.
        await TryUploadCollateralAsync(model, eventId, companyId, email, modelState, ct);

        model.CurrentMaterials = await CurrentAsync(eventId, companyId, ct);
        model.CanUploadCollateral = CanUploadCollateral(SharePointConfig());

        if (!modelState.IsValid) return WizardStepOutcome.Invalid;

        // §475: booth materials are OPTIONAL. The §297 "at least one" gate used to block Save & next
        // with an empty booth, which trapped a sponsor who simply has not chosen their booth video
        // yet — months out, most have not. A malformed URL is still refused above (that is a real
        // error); having nothing to add is not, so an empty step now advances.
        // The step's DONE state is unchanged (SponsorWizardService still needs a material to tick
        // it), so this affects whether the sponsor can PROCEED, not whether the work is recorded.

        model.Video1Url = model.Video2Url = model.Video3Url =
            model.Video4Url = model.Video5Url = model.Video6Url = null;
        return WizardStepOutcome.Advance;
    }

    private static string? Trim(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>
    /// §476 — upload one collateral file to SharePoint and record it. Mirrors the standalone
    /// Company Details handler (same folder, same cap, same allowed types) so the wizard and the
    /// page cannot drift apart, and STREAMS the file (§455) because collateral is the large-PDF
    /// case. Adds a model error and returns on any refusal; never throws.
    /// </summary>
    private async Task TryUploadCollateralAsync(
        SponsorBoothMaterialsModel model, int eventId, string companyId, string email,
        ModelStateDictionary modelState, CancellationToken ct)
    {
        var file = model.CollateralFile;
        if (file is null || file.Length == 0) return;   // nothing offered this save

        var field = nameof(SponsorBoothMaterialsModel.CollateralFile);
        var sp = SharePointConfig();
        if (!CanUploadCollateral(sp))
        {
            // Honest refusal rather than a silent drop — the sponsor would otherwise believe the
            // brochure is on file and nobody would notice until the booth was being set up.
            modelState.AddModelError(field,
                "File uploads aren't available right now — your videos were saved. "
                + "Please try again later or send the file to the organizers.");
            return;
        }

        var count = await _db.SponsorBoothMaterials.CountAsync(
            m => m.EventId == eventId && m.SponsorCompanyId == companyId
                 && m.Kind == BoothMaterialKind.Collateral, ct);
        if (count >= MaxCollateral)
        {
            modelState.AddModelError(field, $"You can add up to {MaxCollateral} collateral files.");
            return;
        }
        if (file.Length > 25 * Mb)
        {
            modelState.AddModelError(field, "File is too large (max 25 MB).");
            return;
        }
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!CollateralExts.Contains(ext))
        {
            modelState.AddModelError(field,
                $"Unsupported file type. Allowed: {string.Join(", ", CollateralExts)}.");
            return;
        }

        try
        {
            var baseName = Path.GetFileNameWithoutExtension(file.FileName);
            var fileName = $"{Sanitize(companyId)}_{Sanitize(baseName)}{ext}";
            await using var upload = file.OpenReadStream();
            var (_, webUrl, _) = await _sp!.UploadFileStreamAsync(
                sp!.SiteUrl, sp.DriveName, CollateralFolder()!, fileName,
                upload, file.Length,
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                ct);

            _db.SponsorBoothMaterials.Add(new SponsorBoothMaterial
            {
                EventId = eventId, SponsorCompanyId = companyId,
                Kind = BoothMaterialKind.Collateral,
                Url = webUrl ?? string.Empty, FileName = file.FileName, CreatedByEmail = email,
            });
            await _db.SaveChangesAsync(ct);
            model.CollateralFile = null;
        }
        catch (Exception)
        {
            // The videos above are already committed; failing the whole step would lose them.
            modelState.AddModelError(field,
                "The file could not be uploaded. Everything else was saved — please try the file again.");
        }
    }

    /// <summary>File-name-safe component (letters, digits, dash, underscore).</summary>
    private static string Sanitize(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? "file"
            : new string(s.Trim().Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray());
}
