using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommunityHub.Pages.Sponsor;

[Authorize]
public class InfoModel : PageModel
{
    public const int MaxDesc = 1000;
    public const int MaxShort = 80;
    public const int MaxSocial = 600;
    private const long MaxLogoBytes = 8 * 1024 * 1024;
    private static readonly string[] VectorExt = { ".eps", ".ai", ".svg", ".pdf" };
    private static readonly string[] RasterExt = { ".jpg", ".jpeg", ".png" };

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;
    private readonly IWebHostEnvironment _env;
    private readonly IEmailSender _emailSender;
    private readonly EmailOptions _emailOptions;
    private readonly ILogger<InfoModel> _logger;

    public InfoModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock,
        IWebHostEnvironment env,
        IEmailSender emailSender,
        IOptions<EmailOptions> emailOptions,
        ILogger<InfoModel> logger)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
        _env = env;
        _emailSender = emailSender;
        _emailOptions = emailOptions.Value;
        _logger = logger;
    }

    [BindProperty] public string? CompanyDescription { get; set; }
    [BindProperty] public string? CompanyDescriptionShort { get; set; }
    [BindProperty] public string? SocialMediaIntro { get; set; }
    [BindProperty] public IFormFile? LogoVectorUpload { get; set; }
    [BindProperty] public IFormFile? LogoRasterUpload { get; set; }

    public bool AccessDenied { get; private set; }
    public string? CompanyId { get; private set; }
    /// <summary>Resolved per request from the signed-in Participant row.</summary>
    private string? SponsorCompanyId { get; set; }
    public string? CurrentLogoVectorUrl { get; private set; }
    public string? CurrentLogoVectorFile { get; private set; }
    public string? CurrentLogoRasterUrl { get; private set; }
    public string? CurrentLogoRasterFile { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        SponsorCompanyId = await _db.Participants
            .Where(p => p.Id == me.ParticipantId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);
        if (me.Role != ParticipantRole.Sponsor || string.IsNullOrWhiteSpace(SponsorCompanyId))
        {
            AccessDenied = true; return Page();
        }
        CompanyId = SponsorCompanyId;

        var info = await _db.SponsorInfos.FirstOrDefaultAsync(
            s => s.EventId == me.EventId && s.SponsorCompanyId == SponsorCompanyId, ct);
        if (info is not null)
        {
            CompanyDescription = info.CompanyDescription;
            CompanyDescriptionShort = info.CompanyDescriptionShort;
            SocialMediaIntro = info.SocialMediaIntro;
            CurrentLogoVectorUrl = info.LogoVectorPath is not null ? $"/{info.LogoVectorPath}" : null;
            CurrentLogoVectorFile = info.LogoVectorFileName;
            CurrentLogoRasterUrl = info.LogoRasterPath is not null ? $"/{info.LogoRasterPath}" : null;
            CurrentLogoRasterFile = info.LogoRasterFileName;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        SponsorCompanyId = await _db.Participants
            .Where(p => p.Id == me.ParticipantId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);
        if (me.Role != ParticipantRole.Sponsor || string.IsNullOrWhiteSpace(SponsorCompanyId))
        {
            AccessDenied = true; return Page();
        }
        CompanyId = SponsorCompanyId;

        // --- Validate text lengths ------------------------------------------
        if ((CompanyDescription ?? "").Length > MaxDesc)
        { Error = $"Description must be {MaxDesc} chars max."; return Page(); }
        if ((CompanyDescriptionShort ?? "").Length > MaxShort)
        { Error = $"Short description must be {MaxShort} chars max."; return Page(); }
        if ((SocialMediaIntro ?? "").Length > MaxSocial)
        { Error = $"Social-media intro must be {MaxSocial} chars max."; return Page(); }

        // --- Validate uploads -----------------------------------------------
        if (LogoVectorUpload is not null && LogoVectorUpload.Length > 0)
        {
            if (LogoVectorUpload.Length > MaxLogoBytes) { Error = "Vector logo too large (8MB max)."; return Page(); }
            var ext = Path.GetExtension(LogoVectorUpload.FileName).ToLowerInvariant();
            if (!VectorExt.Contains(ext))
            { Error = $"Vector logo must be one of: {string.Join(", ", VectorExt)}."; return Page(); }
        }
        if (LogoRasterUpload is not null && LogoRasterUpload.Length > 0)
        {
            if (LogoRasterUpload.Length > MaxLogoBytes) { Error = "Raster logo too large (8MB max)."; return Page(); }
            var ext = Path.GetExtension(LogoRasterUpload.FileName).ToLowerInvariant();
            if (!RasterExt.Contains(ext))
            { Error = $"Raster logo must be one of: {string.Join(", ", RasterExt)} (JPG preferred)."; return Page(); }
        }

        // --- Upsert SponsorInfo --------------------------------------------
        var info = await _db.SponsorInfos.FirstOrDefaultAsync(
            s => s.EventId == me.EventId && s.SponsorCompanyId == SponsorCompanyId, ct);
        if (info is null)
        {
            info = new SponsorInfo
            {
                EventId = me.EventId,
                SponsorCompanyId = SponsorCompanyId!,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.SponsorInfos.Add(info);
        }
        else
        {
            info.UpdatedAt = _clock.GetUtcNow();
        }

        info.CompanyDescription = string.IsNullOrWhiteSpace(CompanyDescription) ? null : CompanyDescription.Trim();
        info.CompanyDescriptionShort = string.IsNullOrWhiteSpace(CompanyDescriptionShort) ? null : CompanyDescriptionShort.Trim();
        info.SocialMediaIntro = string.IsNullOrWhiteSpace(SocialMediaIntro) ? null : SocialMediaIntro.Trim();
        info.LastUpdatedByEmail = me.Email;

        // --- Save uploaded files (persisted under wwwroot/uploads/sponsors/<co>/) -
        var coSafe = SanitizeForPath(SponsorCompanyId!);
        var uploadsDir = Path.Combine(_env.WebRootPath, "uploads", "sponsors", coSafe);
        Directory.CreateDirectory(uploadsDir);

        if (LogoVectorUpload is not null && LogoVectorUpload.Length > 0)
        {
            var ext = Path.GetExtension(LogoVectorUpload.FileName).ToLowerInvariant();
            var fileName = $"logo-vector{ext}";
            var filePath = Path.Combine(uploadsDir, fileName);
            await using (var fs = System.IO.File.Create(filePath))
            {
                await LogoVectorUpload.CopyToAsync(fs, ct);
            }
            info.LogoVectorPath = $"uploads/sponsors/{coSafe}/{fileName}";
            info.LogoVectorFileName = LogoVectorUpload.FileName;
        }
        if (LogoRasterUpload is not null && LogoRasterUpload.Length > 0)
        {
            var ext = Path.GetExtension(LogoRasterUpload.FileName).ToLowerInvariant();
            var fileName = $"logo-raster{ext}";
            var filePath = Path.Combine(uploadsDir, fileName);
            await using (var fs = System.IO.File.Create(filePath))
            {
                await LogoRasterUpload.CopyToAsync(fs, ct);
            }
            info.LogoRasterPath = $"uploads/sponsors/{coSafe}/{fileName}";
            info.LogoRasterFileName = LogoRasterUpload.FileName;
        }

        await _db.SaveChangesAsync(ct);

        // --- Auto-mark matching sponsor tasks Done -------------------------
        await MarkTasksDoneAsync(me.EventId, SponsorCompanyId!, info, ct);

        // --- Notify organizer (best-effort, non-blocking) ------------------
        try { await NotifyOrganizerAsync(me, info, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify organizer of sponsor info update for {Co}", SponsorCompanyId);
        }

        // Reset display state from the (now saved) entity
        CurrentLogoVectorUrl = info.LogoVectorPath is not null ? $"/{info.LogoVectorPath}" : null;
        CurrentLogoVectorFile = info.LogoVectorFileName;
        CurrentLogoRasterUrl = info.LogoRasterPath is not null ? $"/{info.LogoRasterPath}" : null;
        CurrentLogoRasterFile = info.LogoRasterFileName;
        Message = "Saved. The organizer team has been notified.";
        return Page();
    }

    private async Task MarkTasksDoneAsync(
        int eventId, string companyId, SponsorInfo info, CancellationToken ct)
    {
        // Title fragments that indicate a "we collected this in the form" task.
        var doneRules = new List<(Func<string, bool> TitleMatch, Func<SponsorInfo, bool> Condition)>
        {
            (t => t.Contains("Upload logo in vector", StringComparison.OrdinalIgnoreCase),
                i => !string.IsNullOrWhiteSpace(i.LogoVectorPath)),
            (t => t.Contains("Upload logo in JPG", StringComparison.OrdinalIgnoreCase)
               || t.Contains("Upload logo in JPG or PNG", StringComparison.OrdinalIgnoreCase),
                i => !string.IsNullOrWhiteSpace(i.LogoRasterPath)),
            (t => t.Contains("social media posts", StringComparison.OrdinalIgnoreCase)
               || t.Contains("company description", StringComparison.OrdinalIgnoreCase),
                i => !string.IsNullOrWhiteSpace(i.CompanyDescription)
                  && !string.IsNullOrWhiteSpace(i.CompanyDescriptionShort)
                  && !string.IsNullOrWhiteSpace(i.SocialMediaIntro)),
        };

        var tasks = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.SponsorCompanyId == companyId
                        && t.State != TaskState.Done)
            .ToListAsync(ct);
        bool changed = false;
        foreach (var t in tasks)
        {
            foreach (var (titleMatch, cond) in doneRules)
            {
                if (titleMatch(t.Title) && cond(info))
                {
                    t.State = TaskState.Done;
                    changed = true;
                    break;
                }
            }
        }
        if (changed) await _db.SaveChangesAsync(ct);
    }

    private async Task NotifyOrganizerAsync(
        CurrentParticipant me, SponsorInfo info, CancellationToken ct)
    {
        var origin = $"{Request.Scheme}://{Request.Host}";
        var orgReview = $"{origin}/Organizer/Sponsors";
        var html =
            $"<p>Sponsor company <strong>{System.Net.WebUtility.HtmlEncode(SponsorCompanyId)}</strong> " +
            $"just submitted/updated their sponsor info in the Event Hub.</p>" +
            $"<p>Submitted by: {System.Net.WebUtility.HtmlEncode(me.FullName)} &lt;{me.Email}&gt;</p>" +
            $"<ul>" +
            $"<li>Vector logo: {(string.IsNullOrEmpty(info.LogoVectorFileName) ? "(not provided)" : System.Net.WebUtility.HtmlEncode(info.LogoVectorFileName))}</li>" +
            $"<li>Raster logo: {(string.IsNullOrEmpty(info.LogoRasterFileName) ? "(not provided)" : System.Net.WebUtility.HtmlEncode(info.LogoRasterFileName))}</li>" +
            $"<li>Company description: {(string.IsNullOrEmpty(info.CompanyDescription) ? "(empty)" : info.CompanyDescription!.Length + " chars")}</li>" +
            $"<li>Short description: {(string.IsNullOrEmpty(info.CompanyDescriptionShort) ? "(empty)" : info.CompanyDescriptionShort!.Length + " chars")}</li>" +
            $"<li>Social-media intro: {(string.IsNullOrEmpty(info.SocialMediaIntro) ? "(empty)" : info.SocialMediaIntro!.Length + " chars")}</li>" +
            $"</ul>" +
            $"<p><a href='{orgReview}'>Open organizer Sponsors page</a></p>" +
            $"<p>Cheers,<br/>ELDK Event Hub</p>";

        var subject = $"[ELDK Event Hub] Sponsor info submitted: {SponsorCompanyId}";
        await _emailSender.SendAsync(_emailOptions.FromAddress, subject, html, ct);
    }

    private static string SanitizeForPath(string s)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', '.', ' ' }).ToHashSet();
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        return sb.ToString();
    }
}
