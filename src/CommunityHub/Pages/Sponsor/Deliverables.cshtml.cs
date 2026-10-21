using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Sponsors;
using CommunityHub.Sponsors;
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
    private readonly ILogger<DeliverablesModel> _logger;
    private readonly SponsorActingCompanyService _acting;

    public DeliverablesModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SponsorDeliverablesService deliverables,
        TimeProvider clock,
        ILogger<DeliverablesModel> logger,
        SponsorActingCompanyService acting)
    {
        _db = db;
        _participant = participant;
        _deliverables = deliverables;
        _clock = clock;
        _logger = logger;
        _acting = acting;
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

        // §1236 — the company this contact is ACTING for (switcher choice, re-validated), not just their primary.
        var companyId = await _acting.CurrentAsync(me.ParticipantId, me.EventId, ct);
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

    /// <summary>§1312 — the public name from CEH's own records (Company Manager is retired), or null when unknown.</summary>
    private Task<string?> ResolveCompanyNameAsync(string companyId, CancellationToken ct) =>
        CommunityHub.Core.Integrations.SponsorCompanyNameService.LocalNameAsync(_db, companyId, ct);
}
