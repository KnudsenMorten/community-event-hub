using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Sponsors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer "Sponsor deliverables" board (REQUIREMENTS §135): every sponsor company with its
/// lifecycle-completion percent and the per-stage done/overdue state
/// (contract/onboarding, logo, booth materials, booth members, assigned tasks), sorted
/// AT-RISK first (overdue) then least complete — so the companies that need chasing are at the
/// top. Read-only rollup of EXISTING data via <see cref="SponsorDeliverablesService"/> — no
/// new source of truth, no writes. COMPLEMENTS Zoho exhibitor management; it does not replace it.
///
/// <para>Auth: organizer-gated (a signed-in <see cref="ParticipantRole.Organizer"/>); a
/// non-organizer gets a friendly notice, not a 403 (matches the other organizer pages).
/// Scoped to the caller's edition.</para>
/// </summary>
[Authorize]
public class SponsorDeliverablesModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SponsorDeliverablesService _deliverables;
    private readonly TimeProvider _clock;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _cmOptions;
    private readonly ILogger<SponsorDeliverablesModel> _logger;

    public SponsorDeliverablesModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SponsorDeliverablesService deliverables,
        TimeProvider clock,
        CompanyManagerClient cm,
        CompanyManagerOptions cmOptions,
        ILogger<SponsorDeliverablesModel> logger)
    {
        _db = db;
        _participant = participant;
        _deliverables = deliverables;
        _clock = clock;
        _cm = cm;
        _cmOptions = cmOptions;
        _logger = logger;
    }

    /// <summary>True when the caller is not an organizer — render a friendly notice.</summary>
    public bool AccessDenied { get; private set; }

    /// <summary>Set when the data layer fails — an honest banner instead of a 500.</summary>
    public string? Error { get; private set; }

    /// <summary>Every sponsor company's deliverables, at-risk first.</summary>
    public IReadOnlyList<SponsorDeliverables> Companies { get; private set; } = Array.Empty<SponsorDeliverables>();

    /// <summary>The lifecycle stages, in order, for the board column headers.</summary>
    public IReadOnlyList<(string Key, string Label)> StageColumns { get; } = new[]
    {
        ("onboarding", "Onboarding"),
        ("logo", "Logo"),
        ("booth-materials", "Booth materials"),
        ("booth-members", "Booth members"),
        ("tasks", "Tasks"),
    };

    /// <summary>Count of companies fully complete (for the summary line).</summary>
    public int CompleteCount => Companies.Count(c => c.IsComplete);

    /// <summary>Count of companies with at least one overdue stage (the at-risk count).</summary>
    public int AtRiskCount => Companies.Count(c => c.AtRisk);

    /// <summary>Edition-wide average completion percent (0 when there are no companies).</summary>
    public int AveragePercent =>
        Companies.Count == 0 ? 0 : (int)Math.Round(Companies.Average(c => c.Percent));

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        try
        {
            // Build the board once to learn the company id set, resolve their display names,
            // then build again WITH names. The board build is cheap (batch-loaded, in-memory),
            // so the extra pass keeps name resolution out of the pure service.
            var bare = await _deliverables.BuildBoardAsync(me.EventId, today, companyNames: null, ct);
            var names = await ResolveCompanyNamesAsync(bare.Select(b => b.CompanyId), ct);
            Companies = await _deliverables.BuildBoardAsync(me.EventId, today, names, ct);
        }
        catch (Exception ex)
        {
            Error = "The sponsor deliverables board could not be loaded right now.";
            _logger.LogWarning(ex, "Sponsor deliverables board failed for event {EventId}.", me.EventId);
        }
        return Page();
    }

    /// <summary>
    /// Map each sponsor company id to its display name via Company Manager (public → legal
    /// name, the same chain the other sponsor pages use). A failed/disabled lookup leaves the
    /// id out of the map so the board falls back to the id rather than 500-ing.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveCompanyNamesAsync(
        IEnumerable<string> companyIds, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!_cmOptions.Enabled) return map;

        foreach (var cid in companyIds)
        {
            if (!int.TryParse(cid, out var idInt)) continue;
            try
            {
                var c = await _cm.GetCompanyAsync(idInt, ct);
                if (c is null) continue;
                map[cid] = SponsorCompanyName.Resolve(c.PublicName, c.Name, billingName: null, companyId: cid);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sponsor deliverables: company-name lookup failed for {CompanyId}.", cid);
            }
        }
        return map;
    }
}
