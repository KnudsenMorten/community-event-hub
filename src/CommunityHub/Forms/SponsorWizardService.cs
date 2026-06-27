using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Forms;

/// <summary>
/// One step in the sponsor "Get started" wizard (REQUIREMENTS §32) — a section of the
/// Company Details page. <see cref="Done"/> is nullable: true/false for steps whose
/// completion is tracked in the hub, null for guided steps whose state lives in an
/// external system (e.g. ERP contacts) — those are shown as links, not counted.
/// </summary>
/// <param name="Key">Stable key → resx label/description + the Company Details anchor.</param>
/// <param name="Anchor">The fragment on /Sponsor/CompanyDetails this step jumps to.</param>
/// <param name="Done">Tracked completion, or null when not hub-tracked.</param>
public sealed record SponsorWizardStep(string Key, string Anchor, bool? Done);

/// <summary>The sponsor's Company Details progress (§32), mirroring the speaker wizard.</summary>
public sealed record SponsorWizardView(IReadOnlyList<SponsorWizardStep> Steps)
{
    /// <summary>
    /// Every step is numbered + counted (the list shows 1..<see cref="TotalSteps"/>), so the
    /// "Continue — step X of Y" line and the numbered list always share the SAME base. A step
    /// whose completion can't be determined right now (<c>Done == null</c>, e.g. e-conomic
    /// briefly unavailable) is shown but never counted as done and never the "Continue" target.
    /// </summary>
    public int TotalSteps => Steps.Count;
    public int DoneCount => Steps.Count(s => s.Done == true);
    public bool AllDone => TotalSteps > 0 && DoneCount >= TotalSteps;
    public int Percent => TotalSteps == 0 ? 0 : (int)Math.Round(100.0 * DoneCount / TotalSteps);

    /// <summary>The next not-completed step (the "Continue" target): the first step that is
    /// NOT done. A step with an undeterminable state (null) is skipped so "Continue" never
    /// gets stuck on a step we can't confirm.</summary>
    public SponsorWizardStep? NextStep => Steps.FirstOrDefault(s => s.Done == false);

    /// <summary>1-based position of <see cref="NextStep"/> in the displayed list (so the
    /// number in "step X of Y" matches the list item the user sees), or 0 when all done.</summary>
    public int NextStepNumber
    {
        get
        {
            for (var i = 0; i < Steps.Count; i++)
                if (Steps[i].Done == false) return i + 1;
            return 0;
        }
    }
}

/// <summary>
/// Builds the sponsor "Get started" wizard (§32, design A) from the Company Details
/// page SECTIONS, in order, entitlement-aware (booth steps only for exhibitors).
/// Completion is read from hub data only (SponsorInfo + booth members/materials) so
/// it is fast and side-effect-free; the ERP-contacts step is a guided link (its state
/// lives in e-conomic). Each step deep-links into /Sponsor/CompanyDetails.
/// </summary>
public sealed class SponsorWizardService
{
    private readonly CommunityHubDbContext _db;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _cmOptions;
    private readonly EconomicContactAdminService _erpContacts;
    private readonly ILogger<SponsorWizardService> _log;

    public SponsorWizardService(
        CommunityHubDbContext db, CompanyManagerClient cm, CompanyManagerOptions cmOptions,
        EconomicContactAdminService erpContacts, ILogger<SponsorWizardService> log)
    {
        _db = db;
        _cm = cm;
        _cmOptions = cmOptions;
        _erpContacts = erpContacts;
        _log = log;
    }

    public async Task<SponsorWizardView?> BuildAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var companyId = await _db.Participants
            .Where(p => p.Id == participantId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(companyId)) return null;

        var info = await _db.SponsorInfos.AsNoTracking()
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);

        var steps = new List<SponsorWizardStep>();

        // 1. Company Details — basic info filled (website or company description).
        var detailsDone = info is not null &&
            (!string.IsNullOrWhiteSpace(info.WebsiteUrl) || !string.IsNullOrWhiteSpace(info.CompanyDescription));
        steps.Add(new("details", "company", detailsDone));

        // 2. Event Coordinator — a coordinator email on file.
        steps.Add(new("coordinator", "coordinator",
            info is not null && !string.IsNullOrWhiteSpace(info.EventCoordinatorEmail)));

        // 3. Your contacts (Contract Signers & Event Coordinators) — tracked from e-conomic
        //    (the master) so the step reflects reality: done = the company has at least one
        //    ERP contact. Fail-soft: if e-conomic / Company Manager is briefly unavailable the
        //    state is left undeterminable (null) — shown as a guided link, never counted nor
        //    used as the "Continue" target — rather than wrongly marking it incomplete.
        steps.Add(new("contacts", "contacts", await ContactsDoneAsync(companyId, ct)));

        // 4. Logos & artwork — at least one logo uploaded.
        var logoDone = info is not null &&
            (!string.IsNullOrWhiteSpace(info.LogoRasterPath) || !string.IsNullOrWhiteSpace(info.LogoVectorPath));
        steps.Add(new("logos", "logos", logoDone));

        // 5 + 6. Booth steps — exhibitors only.
        if (info?.HasBooth == true)
        {
            var hasMembers = await _db.SponsorBoothMembers
                .AnyAsync(m => m.EventId == eventId && m.SponsorCompanyId == companyId && m.DeletedAt == null, ct);
            steps.Add(new("booth-members", "booth-members", hasMembers));

            var hasMaterials = await _db.SponsorBoothMaterials
                .AnyAsync(m => m.EventId == eventId && m.SponsorCompanyId == companyId, ct);
            steps.Add(new("booth-materials", "booth-materials", hasMaterials));
        }

        return new SponsorWizardView(steps);
    }

    /// <summary>
    /// True when the company has ≥1 e-conomic contact, false when none, null when it can't be
    /// determined (integration disabled/unavailable or no ERP customer link). Mirrors the
    /// Company Details contacts read; fail-soft so the wizard never throws on an ERP hiccup.
    /// </summary>
    private async Task<bool?> ContactsDoneAsync(string companyId, CancellationToken ct)
    {
        if (!_cmOptions.Enabled || !_erpContacts.CanWrite || !int.TryParse(companyId, out var cid))
            return null;
        try
        {
            var company = await _cm.GetCompanyAsync(cid, ct);
            if (company is null || !int.TryParse(company.ErpCustomerNumber, out var erpNo) || erpNo <= 0)
                return null;
            var contacts = await _erpContacts.ListContactsAsync(erpNo, ct);
            return contacts.Count > 0;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sponsor wizard: e-conomic contacts check failed for {Co}.", companyId);
            return null;
        }
    }
}
