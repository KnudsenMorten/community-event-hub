using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Tasks;

/// <summary>
/// Builds the <c>{{placeholder}}</c> map for one sponsor company, at RENDER time.
/// </summary>
/// <remarks>
/// <para>🔒 <b>This is the render-time twin of <c>SponsorOrderPullService.SubstitutePlaceholders</c>,
/// and it resolves the same four layers in the same order</b> — per-task upload URLs (falling back
/// to the generic upload portal when SharePoint did not provision one), per-company, per-tier, then
/// the edition's facts and cross-cutting placeholder map. The pull resolves them once and bakes the
/// result into <c>Tasks.Description</c>; this resolves them every time the task is rendered, which
/// is what §684.1 means by moving resolution from seed time to render time.</para>
///
/// <para><c>{{boothNumber}}</c> comes from <c>SponsorInfo.BoothLabel</c>, which the pull already
/// persists (§41a — it exists so Zoho receives a real <c>booth_label</c> on exhibitor create).
/// Reading the stored value rather than re-deriving it from the product name means the box marking
/// on the shipment task and the booth Zoho shows cannot drift apart.</para>
///
/// <para>🔒 A placeholder this builder cannot supply renders as EMPTY, never as literal
/// <c>{{braces}}</c> — right for the reader, invisible to everyone else. <see cref="ReportMissing"/>
/// is the only thing standing between that and silence, so call it at every render site.</para>
/// </remarks>
public sealed class SponsorTaskPlaceholderBuilder
{
    private readonly CommunityHubDbContext _db;
    private readonly EventEditionConfigLoader _eventConfig;
    private readonly EventConfigOptions _eventConfigOptions;
    private readonly SponsorConfigLoader _sponsorConfig;
    private readonly SponsorConfigOptions _sponsorConfigOptions;
    private readonly ILogger<SponsorTaskPlaceholderBuilder> _log;

    public SponsorTaskPlaceholderBuilder(
        CommunityHubDbContext db,
        EventEditionConfigLoader eventConfig,
        EventConfigOptions eventConfigOptions,
        SponsorConfigLoader sponsorConfig,
        SponsorConfigOptions sponsorConfigOptions,
        ILogger<SponsorTaskPlaceholderBuilder> log)
    {
        _db = db;
        _eventConfig = eventConfig;
        _eventConfigOptions = eventConfigOptions;
        _sponsorConfig = sponsorConfig;
        _sponsorConfigOptions = sponsorConfigOptions;
        _log = log;
    }

    /// <summary>Build the map for one company.</summary>
    public async Task<IReadOnlyDictionary<string, string>> BuildAsync(
        int eventId, string? sponsorCompanyId, CancellationToken ct = default)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // (4) Edition facts + the cross-cutting placeholder map. Added FIRST so the per-company and
        // per-tier layers below OVERWRITE them — same precedence as the pull's substitution order,
        // where the earlier passes have already consumed their markers by the time this map runs.
        var facts = _eventConfig.Load(_eventConfigOptions.EventConfigPath);
        if (facts.Placeholders is { Count: > 0 } placeholders)
        {
            foreach (var (key, value) in placeholders) map[key] = value ?? string.Empty;
        }
        map["expectedAttendees"] = facts.ExpectedAttendees.ToString();
        map["editionCode"] = facts.Code ?? string.Empty;
        map["editionCodeLower"] = (facts.Code ?? string.Empty).ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(sponsorCompanyId)) return map;

        // (1) Per-company.
        var info = await _db.SponsorInfos
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.EventId == eventId && s.SponsorCompanyId == sponsorCompanyId, ct);

        map["companyName"] = info?.CompanyName ?? string.Empty;

        // {{boothNumber}} — the physical booth slot ("E-26"). The pull parses it from the booth
        // product name and PERSISTS it on SponsorInfo.BoothLabel (§41a, where it exists so Zoho gets
        // a real booth_label on exhibitor create). Same value the pull substitutes, read from the
        // row rather than re-derived, so the box marking on the shipment task and the booth shown
        // in Zoho can never disagree.
        map["boothNumber"] = info?.BoothLabel ?? string.Empty;

        // (2) Per-tier: the matching boothWallSpecs row. furnitureSpec can itself contain
        // {{couponCode}}, which resolves because the renderer walks placeholder NODES rather than
        // doing ordered string replacement — the nesting the pull had to sequence carefully is not
        // a sequencing question here at all.
        if (info is not null && info.Tier != BoothTier.None)
        {
            try
            {
                var wallSpecs = _sponsorConfig
                    .Load(_sponsorConfigOptions.SponsorConfigPath)
                    .BoothWallSpecs;

                // BoothTier.ToString() is PascalCase ("Gold") while the JSON keys are lowercase;
                // System.Text.Json replaces the dictionary instance and drops the initializer's
                // OrdinalIgnoreCase comparer, so the lookup must use a lowercased key.
                if (wallSpecs?.Tiers is { Count: > 0 } tiers
                    && tiers.TryGetValue(info.Tier.ToString().ToLowerInvariant(), out var spec))
                {
                    map["furnitureSpec"] = spec.FurnitureSpec ?? string.Empty;
                    map["wallSpecUrl"] = spec.SpecUrl ?? string.Empty;
                    map["couponCode"] = spec.Coupon ?? string.Empty;
                    map["wallSize"] = spec.WallSize ?? string.Empty;
                }
            }
            catch (FileNotFoundException)
            {
                // Sponsor config missing is already loud elsewhere; a task rendering without its
                // tier values must not be an exception on a participant's page (§682).
            }
        }

        // (0) Per-task SharePoint upload-folder URLs, keyed by the folder key the body names
        // ({{wallFolderUrl}}, {{logoFolderUrl}}). A blank/unprovisioned URL falls back to the
        // generic upload portal, exactly as the pull does — a task that links nowhere is worse than
        // one that links to the general folder.
        var fallback = map.GetValueOrDefault("uploadPortalUrl", string.Empty);
        var locations = await _db.SponsorUploadLocations
            .AsNoTracking()
            .Where(l => l.EventId == eventId && l.SponsorCompanyId == sponsorCompanyId)
            .Select(l => new { l.FolderKey, l.EditLinkUrl })
            .ToListAsync(ct);

        foreach (var location in locations)
        {
            if (string.IsNullOrWhiteSpace(location.FolderKey)) continue;
            map[location.FolderKey] = string.IsNullOrWhiteSpace(location.EditLinkUrl)
                ? fallback
                : location.EditLinkUrl;
        }

        return map;
    }

    /// <summary>
    /// Report placeholders a body asked for and this builder could not supply.
    /// </summary>
    /// <remarks>
    /// 🔒 An unresolved placeholder renders as NOTHING — never as literal <c>{{braces}}</c> — which
    /// is right for the reader and invisible to everyone else. A live task quietly missing its
    /// price, coupon code or shipping address would look entirely normal. This is the only thing
    /// standing between that and silence, so it logs at ERROR.
    /// </remarks>
    public void ReportMissing(string taskKey, IReadOnlyCollection<string> missing)
    {
        if (missing.Count == 0) return;

        _log.LogError(
            "Task '{TaskKey}' referenced {Count} placeholder(s) the hub cannot resolve: {Keys}. "
            + "They rendered as EMPTY on a participant-facing surface. Add them to the edition "
            + "config, or correct the body.",
            taskKey, missing.Count, string.Join(", ", missing));
    }
}
