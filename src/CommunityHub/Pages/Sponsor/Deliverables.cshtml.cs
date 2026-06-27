using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Sponsors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// The signed-in sponsor's own "deliverables" checklist (REQUIREMENTS §135): a single
/// completion score ("3 of 5 done") for their company's lifecycle stages
/// (contract/onboarding, logo, booth materials, booth members, assigned tasks) with what's
/// still left + overdue, each item deep-linking to the page that fixes it. Pure rollup of
/// EXISTING data via <see cref="SponsorDeliverablesService"/> — no new source of truth, no
/// writes. COMPLEMENTS Zoho exhibitor management; it does not replace it.
///
/// <para>Only Sponsors see the content; any other role gets a friendly notice (matches the
/// other sponsor pages), not a 403. Scoped to the caller's edition + company.</para>
/// </summary>
[Authorize]
public class DeliverablesModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SponsorDeliverablesService _deliverables;
    private readonly TimeProvider _clock;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _cmOptions;
    private readonly ILogger<DeliverablesModel> _logger;

    public DeliverablesModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SponsorDeliverablesService deliverables,
        TimeProvider clock,
        CompanyManagerClient cm,
        CompanyManagerOptions cmOptions,
        ILogger<DeliverablesModel> logger)
    {
        _db = db;
        _participant = participant;
        _deliverables = deliverables;
        _clock = clock;
        _cm = cm;
        _cmOptions = cmOptions;
        _logger = logger;
    }

    /// <summary>Non-sponsor reached the page — render a friendly notice.</summary>
    public bool AccessDenied { get; private set; }
    /// <summary>This contact has no sponsor company id (nothing to show).</summary>
    public bool NoCompanyLink { get; private set; }
    /// <summary>Set when the data layer fails — an honest banner instead of a 500.</summary>
    public string? Error { get; private set; }

    public string FirstName { get; private set; } = "there";

    /// <summary>The company's deliverables rollup, or null when it could not be built.</summary>
    public SponsorDeliverables? Deliverables { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        FirstName = me.FirstName;
        if (me.Role != ParticipantRole.Sponsor) { AccessDenied = true; return Page(); }

        var companyId = await _db.Participants
            .Where(p => p.Id == me.ParticipantId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(companyId)) { NoCompanyLink = true; return Page(); }

        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        try
        {
            var name = await ResolveCompanyNameAsync(companyId, ct);
            Deliverables = await _deliverables.BuildForCompanyAsync(me.EventId, companyId, today, name, ct);
        }
        catch (Exception ex)
        {
            Error = "Your deliverables checklist could not be loaded right now.";
            _logger.LogWarning(ex, "Sponsor deliverables: build failed for company {CompanyId}.", companyId);
        }
        return Page();
    }

    /// <summary>Public name from Company Manager (public → legal), or null when unavailable.</summary>
    private async Task<string?> ResolveCompanyNameAsync(string companyId, CancellationToken ct)
    {
        if (!_cmOptions.Enabled || !int.TryParse(companyId, out var cid)) return null;
        try
        {
            var c = await _cm.GetCompanyAsync(cid, ct);
            if (c is null) return null;
            return SponsorCompanyName.Resolve(c.PublicName, c.Name, billingName: null, companyId: companyId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sponsor deliverables: company-name lookup failed for {CompanyId}.", companyId);
            return null;
        }
    }
}
