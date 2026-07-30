using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// Render + edit model for the sponsor Event-Coordinator wizard step (§32 / §285 Phase 2).
/// Field names match the standalone Company Details section so the wizard host's empty-prefix
/// binding fills them. <see cref="IsExhibitor"/> drives the "sponsor + exhibitor" vs "sponsor"
/// contact wording.
/// </summary>
public sealed class SponsorCoordinatorModel
{
    public string? EventCoordinatorFirstName { get; set; }
    public string? EventCoordinatorLastName { get; set; }
    public string? EventCoordinatorCompanyName { get; set; }
    public string? EventCoordinatorEmail { get; set; }
    public string? EventCoordinatorPhone { get; set; }

    /// <summary>Display-only: is this company an exhibitor (Gold+)? Never bound from POST.</summary>
    public bool IsExhibitor { get; set; }
}

/// <summary>
/// Shared submit-service for the sponsor Event-Coordinator step (§285 Phase 2). Encapsulates the
/// SAME load + validate + persist + Zoho-Backstage sync the standalone Company Details "Event
/// Coordinator" section runs (<c>CompanyDetailsModel.OnPostAsync</c> for the coordinator fields),
/// so the inline wizard step and the page stay consistent — including syncing the coordinator to
/// Zoho as the sponsor contact. The Zoho sync is FAIL-SOFT (SQL is already committed), so a Zoho
/// hiccup never blocks the wizard. Self-registers via <see cref="IWizardFormService"/>.
/// </summary>
public sealed class SponsorCoordinatorFormService : IWizardFormService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly SponsorZohoSyncService _zohoSync;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _cmOptions;
    private readonly ILogger<SponsorCoordinatorFormService> _log;

    public SponsorCoordinatorFormService(
        CommunityHubDbContext db, TimeProvider clock, SponsorZohoSyncService zohoSync,
        CompanyManagerClient cm, CompanyManagerOptions cmOptions,
        ILogger<SponsorCoordinatorFormService> log)
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

    public async Task<SponsorCoordinatorModel> LoadAsync(int eventId, int participantId, CancellationToken ct)
    {
        var companyId = await CompanyIdAsync(participantId, ct);
        var info = companyId is null ? null : await _db.SponsorInfos.AsNoTracking()
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);
        // §534 — the Email box arrived EMPTY while name, company and phone were filled in
        // (operator 2026-07-28: "bug: missing email"). SponsorInfo.EventCoordinatorEmail is
        // optional and simply had no value, so the step asked the sponsor to retype an address the
        // hub already holds — and the coordinator address is precisely what the Zoho
        // exhibitor-contact sync needs.
        //
        // Fall back to the company's FLAGGED event-coordinator contact. Display-only: the value is
        // still persisted on SponsorInfo when he saves, so this never silently overwrites a
        // deliberate choice, and a blank stays blank if there is no such contact.
        var coordinatorEmail = info?.EventCoordinatorEmail;
        if (string.IsNullOrWhiteSpace(coordinatorEmail) && companyId is not null)
        {
            coordinatorEmail = await _db.Participants.AsNoTracking()
                .Where(p => p.EventId == eventId
                            && p.SponsorCompanyId == companyId
                            && p.IsEventCoordinator
                            && p.IsActive)
                .Select(p => p.Email)
                .FirstOrDefaultAsync(ct);
        }

        return new SponsorCoordinatorModel
        {
            EventCoordinatorFirstName   = info?.EventCoordinatorFirstName,
            EventCoordinatorLastName    = info?.EventCoordinatorLastName,
            EventCoordinatorCompanyName = info?.EventCoordinatorCompanyName,
            EventCoordinatorEmail       = coordinatorEmail,
            EventCoordinatorPhone       = info?.EventCoordinatorPhone,
            IsExhibitor                 = info?.HasBooth ?? false,
        };
    }

    public async Task<WizardStepOutcome> SaveAsync(
        SponsorCoordinatorModel model, int eventId, int participantId, string email,
        ModelStateDictionary modelState, CancellationToken ct)
    {
        var companyId = await CompanyIdAsync(participantId, ct);
        if (companyId is null) return WizardStepOutcome.NotRelevant;

        // Email is optional but must be valid when supplied — identical to the standalone page.
        if (!string.IsNullOrWhiteSpace(model.EventCoordinatorEmail) && !LooksLikeEmail(model.EventCoordinatorEmail))
        {
            modelState.AddModelError(nameof(model.EventCoordinatorEmail),
                "Event coordinator email is not a valid address.");
            return WizardStepOutcome.Invalid;
        }

        var info = await GetOrCreateInfoAsync(eventId, companyId, ct);
        info.EventCoordinatorFirstName   = NormaliseOrNull(model.EventCoordinatorFirstName);
        info.EventCoordinatorLastName    = NormaliseOrNull(model.EventCoordinatorLastName);
        info.EventCoordinatorCompanyName = NormaliseOrNull(model.EventCoordinatorCompanyName);
        info.EventCoordinatorEmail       = NormaliseOrNull(model.EventCoordinatorEmail);
        info.EventCoordinatorPhone       = NormaliseOrNull(model.EventCoordinatorPhone);
        info.LastUpdatedByEmail          = email;
        model.IsExhibitor = info.HasBooth;
        await _db.SaveChangesAsync(ct);

        // Save & Sync to Zoho Backstage (FAIL-SOFT: SQL is already saved, so a Zoho outage must
        // never fail the wizard step). The coordinator is the synced sponsor contact.
        try
        {
            var sponsorName = await ResolveSponsorNameAsync(companyId, ct);
            await _zohoSync.SyncAsync(eventId, companyId, sponsorName ?? string.Empty, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sponsor coordinator inline step: Zoho sync failed for {Co}.", companyId);
        }

        return WizardStepOutcome.Advance;   // §291: saved ⇒ step's done-signal (coordinator email) reads true
    }

    // ---- helpers replicated from CompanyDetailsModel so the two stay byte-consistent ----------

    private static bool LooksLikeEmail(string? s) =>
        !string.IsNullOrWhiteSpace(s) && s.Contains('@') && s.IndexOf('.', s.IndexOf('@')) > 0;

    private static string? NormaliseOrNull(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task<SponsorInfo> GetOrCreateInfoAsync(int eventId, string companyId, CancellationToken ct)
    {
        var info = await _db.SponsorInfos.FirstOrDefaultAsync(
            s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);
        if (info is null)
        {
            info = new SponsorInfo { EventId = eventId, SponsorCompanyId = companyId, CreatedAt = _clock.GetUtcNow(), UpdatedAt = _clock.GetUtcNow() };
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
                _log.LogWarning(ex, "Sponsor coordinator inline step: Company Manager lookup failed for {Co}.", cid);
            }
        }
        return $"Company {companyId}";
    }
}
