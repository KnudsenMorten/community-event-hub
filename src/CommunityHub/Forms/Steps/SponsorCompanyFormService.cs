using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// Render + edit model for the sponsor Company-details wizard step (§32 / §285 Phase 2). Field
/// names match the standalone Company Details section so the wizard host's empty-prefix binding
/// fills them. <see cref="IsExhibitor"/> gates the exhibitor-only short description + drives copy.
/// </summary>
public sealed class SponsorCompanyModel
{
    public string? WebsiteUrl { get; set; }
    public string? LinkedInUrl { get; set; }
    public string? TwitterUrl { get; set; }
    public string? CompanyOverview { get; set; }
    public string? SocialMediaBrandingText { get; set; }
    public string? CompanyShortDescription { get; set; }

    /// <summary>Display-only: exhibitor (Gold+) — shows the short-description field. Never bound.</summary>
    public bool IsExhibitor { get; set; }
}

/// <summary>
/// Shared submit-service for the sponsor Company-details step (§285 Phase 2). Runs the SAME
/// validation + persist + Zoho-Backstage sync as the standalone Company Details section
/// (<c>CompanyDetailsModel.OnPostAsync</c> for the company fields), so the inline step and the
/// page stay consistent. URL + length limits mirror the page constants; the Zoho sync is
/// FAIL-SOFT. Self-registers via <see cref="IWizardFormService"/>.
/// </summary>
public sealed class SponsorCompanyFormService : IWizardFormService
{
    // §802.3 — Zoho's MEASURED limits (§801.2), from the one shared place. This step and
    // CompanyDetailsModel each carried their own copy of these numbers; "mirror them" was a comment,
    // not a mechanism, and the sync would have made a fourth copy.
    private const int MaxOverview = CommunityHub.Core.Integrations.ZohoExhibitorLimits.Overview;
    private const int MaxShort    = CommunityHub.Core.Integrations.ZohoExhibitorLimits.ShortDescription;
    private const int MaxSocial   = CommunityHub.Core.Integrations.ZohoExhibitorLimits.SocialBrandingText;
    private const int MaxUrl      = CommunityHub.Core.Integrations.ZohoExhibitorLimits.Url;

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly SponsorZohoSyncService _zohoSync;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _cmOptions;
    private readonly ILogger<SponsorCompanyFormService> _log;

    public SponsorCompanyFormService(
        CommunityHubDbContext db, TimeProvider clock, SponsorZohoSyncService zohoSync,
        CompanyManagerClient cm, CompanyManagerOptions cmOptions,
        ILogger<SponsorCompanyFormService> log)
    {
        _db = db;
        _clock = clock;
        _zohoSync = zohoSync;
        _cm = cm;
        _cmOptions = cmOptions;
        _log = log;
    }

    private Task<string?> CompanyIdAsync(int participantId, CancellationToken ct) =>
        _db.Participants.Where(p => p.Id == participantId)
            .Select(p => p.SponsorCompanyId).FirstOrDefaultAsync(ct);

    public async Task<SponsorCompanyModel> LoadAsync(int eventId, int participantId, CancellationToken ct)
    {
        var companyId = await CompanyIdAsync(participantId, ct);
        var info = companyId is null ? null : await _db.SponsorInfos.AsNoTracking()
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);
        return new SponsorCompanyModel
        {
            WebsiteUrl              = info?.WebsiteUrl,
            LinkedInUrl             = info?.LinkedInUrl,
            TwitterUrl              = info?.TwitterUrl,
            CompanyOverview         = info?.CompanyDescription,
            SocialMediaBrandingText = info?.SocialMediaIntro,
            CompanyShortDescription = info?.CompanyDescriptionShort,
            IsExhibitor             = info?.HasBooth ?? false,
        };
    }

    public async Task<WizardStepOutcome> SaveAsync(
        SponsorCompanyModel model, int eventId, int participantId, string email,
        ModelStateDictionary modelState, CancellationToken ct)
    {
        var companyId = await CompanyIdAsync(participantId, ct);
        if (companyId is null) return WizardStepOutcome.NotRelevant;

        var error = ValidateUrl("Website URL", model.WebsiteUrl)
                    ?? ValidateUrl("LinkedIn URL", model.LinkedInUrl)
                    ?? ValidateUrl("Twitter URL", model.TwitterUrl)
                    ?? ValidateLen("Company Overview", model.CompanyOverview, MaxOverview)
                    ?? ValidateLen("Social Media Branding Text", model.SocialMediaBrandingText, MaxSocial)
                    ?? ValidateLen("Company Short Description", model.CompanyShortDescription, MaxShort);
        if (error is not null)
        {
            modelState.AddModelError(string.Empty, error);
            return WizardStepOutcome.Invalid;
        }

        var info = await GetOrCreateInfoAsync(eventId, companyId, ct);
        info.WebsiteUrl         = NormaliseOrNull(model.WebsiteUrl);
        info.LinkedInUrl        = NormaliseOrNull(model.LinkedInUrl);
        info.TwitterUrl         = NormaliseOrNull(model.TwitterUrl);
        info.CompanyDescription = NormaliseOrNull(model.CompanyOverview);
        info.SocialMediaIntro   = NormaliseOrNull(model.SocialMediaBrandingText);
        if (info.HasBooth)   // the short description is exhibitor-only (mirrors the page)
            info.CompanyDescriptionShort = NormaliseOrNull(model.CompanyShortDescription);
        info.LastUpdatedByEmail = email;
        model.IsExhibitor = info.HasBooth;
        await _db.SaveChangesAsync(ct);

        try
        {
            var sponsorName = await ResolveSponsorNameAsync(companyId, ct);
            await _zohoSync.SyncAsync(eventId, companyId, sponsorName ?? string.Empty, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sponsor company inline step: Zoho sync failed for {Co}.", companyId);
        }

        return WizardStepOutcome.Advance;   // §291: saved ⇒ step done-signal (website/description) reads true
    }

    // ---- helpers replicated from CompanyDetailsModel so the two stay byte-consistent ----------

    private static string? ValidateUrl(string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;   // optional
        var v = value.Trim();
        if (v.Length > MaxUrl) return $"{label} is too long (max {MaxUrl} chars).";
        if (!v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return $"{label} must be a full URL starting with https:// (e.g. https://www.example.com).";
        return Uri.TryCreate(v, UriKind.Absolute, out _) ? null : $"{label} is not a valid URL.";
    }

    // §802.1 — same refusal, same words, same numbers as the page: the sponsor is told how many
    // characters they have and how many to remove, and the save is blocked until they do.
    private static string? ValidateLen(string label, string? value, int max) =>
        CommunityHub.Core.Integrations.ZohoExhibitorLimits.IsTooLong(value, max)
            ? CommunityHub.Core.Integrations.ZohoExhibitorLimits.TooLongMessage(label, value, max)
            : null;

    private static string? NormaliseOrNull(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task<SponsorInfo> GetOrCreateInfoAsync(int eventId, string companyId, CancellationToken ct)
    {
        // 🔒 §1034 — UPSERT, so it must see rows the sponsor filter hides: finding nothing here
        // would Add a SECOND row for a company that already has one. Reached only by a signed-in
        // SPONSOR filling in their own company, so a row created here IS a sponsor.
        var info = await _db.SponsorInfos.IgnoreQueryFilters().FirstOrDefaultAsync(
            s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);
        if (info is null)
        {
            info = new SponsorInfo { EventId = eventId, SponsorCompanyId = companyId, IsSponsor = true, CreatedAt = _clock.GetUtcNow(), UpdatedAt = _clock.GetUtcNow() };
            _db.SponsorInfos.Add(info);
        }
        else
        {
            info.UpdatedAt = _clock.GetUtcNow();
        }
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
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Sponsor company inline step: Company Manager lookup failed for {Co}.", cid);
            }
        }
        return $"Company {companyId}";
    }
}
