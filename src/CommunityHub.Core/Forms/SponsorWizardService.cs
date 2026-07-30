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
        // Key MUST be "company" (NOT "details") — "details" is the SPEAKER step's global handler
        // key and wizard handlers share one key→handler map across all roles; a "details" collision
        // let the sponsor company partial hijack the speaker details step in Release.
        steps.Add(new("company", "company", detailsDone));

        // §292 registered the sponsor's speaking session (title + abstract + speakers) as a WIZARD
        // STEP, gated on HasSponsorSession.
        //
        // §495 (operator 2026-07-28) — it is NOT a wizard step any more. Same reasoning as §474's
        // booth members: Get Started asks for what we need from a sponsor NOW, and a session title,
        // abstract and speaker line-up are rarely settled at onboarding. Leaving it in the flow
        // either invites a placeholder abstract that someone must chase down later, or parks the
        // sponsor short of 100% on something they cannot answer yet.
        //
        // Nothing is lost: "Submit session description" already exists as a sponsor TASK, its
        // SourceKey head is `sponsor` (not a WizardOwnedPrefix), so §400/§410 already surface it on
        // the deadlines step — dated, chased by the reminder cadence, and completable any time.
        // The /Forms/Wizard?step=session PAGE stays routable so the task's deep link still works.

        // (the sponsor's speaking session is a task, not a step — see §495 above)

        // 🔒 §597 — THE "EVENT COORDINATOR" STEP IS REMOVED. DO NOT RE-ADD IT.
        //
        // Operator 2026-07-28: *"step event coordinator is not relevant"*, after
        // *"i'm confused, as we also have this step"*.
        //
        // It was a SINGLE-contact form (first/last name, company, e-mail, phone) for data that the
        // "contacts" step below already manages as a LIST, with Signer / Event-coordinator
        // checkboxes and an add form. Two steps therefore disagreed about whether a company has ONE
        // coordinator or MANY — and his model is many: *"it should list anyone with event
        // coordinaotr status and give option to add / delete"*, and *"emails like reminders go out
        // to all people linked to the sponsor with the event coordinator status"*.
        //
        // Removing the step does NOT remove the data: `SponsorInfo.EventCoordinator*` is still
        // written (Company Details page, ERP/Company Manager sync) and is still what feeds Zoho as
        // the single sponsor + exhibitor contact.
        //
        // ⚠️ §596.1 — WHY THAT DISTINCTION IS LOAD-BEARING. Zoho holds exactly ONE contact per
        // sponsor/exhibitor record and its E-MAIL MAY BE CHANGED AT MOST 3 TIMES, EVER. Exceeding
        // that makes BOTH objects unupdatable, fixable only by deleting them — which loses their
        // leads. So the Zoho contact must NOT be re-derived from "whoever is first in the
        // coordinator list" on every pass: adding or deleting a coordinator would churn that
        // e-mail and burn the cap. It stays where it is, changed deliberately, never as a
        // side effect of editing the list. See §597.2.

        // 3. Your contacts (Contract Signers & Event Coordinators) — tracked from e-conomic
        //    (the master) so the step reflects reality: done = the company has at least one
        //    ERP contact. Fail-soft: if e-conomic / Company Manager is briefly unavailable the
        //    state is left undeterminable (null) — shown as a guided link, never counted nor
        //    used as the "Continue" target — rather than wrongly marking it incomplete.
        steps.Add(new("contacts", "contacts", await ContactsDoneAsync(companyId, ct)));

        // 4. Logos & artwork — §297: done only when ALL THREE logos (SoMe + Print + Zoho) are
        //    uploaded. SoMe + Zoho both persist to LogoRasterPath on SponsorInfo, so the per-kind
        //    upload audit is the only place all three are distinguishable.
        var logoKinds = await _db.SponsorUploadAudits.AsNoTracking()
            .Where(a => a.EventId == eventId && a.SponsorCompanyId == companyId
                        && (a.Kind == "some" || a.Kind == "print" || a.Kind == "zoho"))
            .Select(a => a.Kind).Distinct().ToListAsync(ct);
        var logoDone = logoKinds.Contains("some") && logoKinds.Contains("print") && logoKinds.Contains("zoho");
        steps.Add(new("logos", "logos", logoDone));

        // 5 + 6. Booth steps — exhibitors only.
        //
        // §474 — "booth-members" is NOT a wizard step. A sponsor onboards ~6 months out and cannot
        // know then who will staff the booth, so asking inside Get Started either forced an invented
        // answer or parked them at 60% on a question nobody could answer yet.
        //
        // Nothing is lost by removing it: `sponsor:{companyId}:register-booth-members` is an existing
        // task that backs its own deliverables stage, is AUTO-CLOSED by SponsorOrderPullService once
        // members are on file, and — because its SourceKey head is `sponsor`, not a
        // WizardOwnedPrefix — is already surfaced by the §400/§410 deadlines step. So it stays
        // dated, chased and completable from My Tasks; it just stops gating the wizard.
        if (info?.HasBooth == true)
        {
            var hasMaterials = await _db.SponsorBoothMaterials
                .AnyAsync(m => m.EventId == eventId && m.SponsorCompanyId == companyId, ct);
            steps.Add(new("booth-materials", "booth-materials", hasMaterials));

            // §229 — booth check-in: when the team expects to arrive on pre-day. Done once
            // ANY answer is saved (a time slot or the not-participating opt-out).
            steps.Add(new("booth-checkin", "booth-checkin",
                !string.IsNullOrWhiteSpace(info.BoothCheckInSlot)));
        }

        // 8. Party — §228: ONE group reservation for the whole company. Done once ANY
        // linked contact has answered for the group (yes or no), so nobody else on the
        // team is asked to sign up again. The step deep-links to /Party (see the page).
        var partyAnswered = await _db.PartyRsvps
            .AnyAsync(r => r.EventId == eventId && r.ParticipantId != null
                && _db.Participants.Any(p => p.Id == r.ParticipantId
                    && p.EventId == eventId && p.SponsorCompanyId == companyId), ct);
        steps.Add(new("party", "party", partyAnswered));

        // 9. §400 — closing summary of everything dated that lives outside this wizard (booth
        //    material deadlines, logos, session assets). Always Done: it asks nothing, so it must
        //    never keep a finished sponsor below 100%.
        //
        // §410 — but ONLY when there is something outside the wizard to show. Sponsors normally do
        // have it (the `sponsor…` task set is 110 dated rows on prod), so unlike the attendee this
        // step will nearly always appear — the check exists so a company with nothing assigned yet
        // does not get an empty step. Scoped to the COMPANY, matching how sponsor tasks are keyed.
        var hasOutside = await _db.Tasks.AsNoTracking()
            .Where(t => t.EventId == eventId && t.SponsorCompanyId == companyId)
            .Select(t => t.SourceKey)
            .ToListAsync(ct);
        if (hasOutside.Any(CommunityHub.Core.Participants.OutsideWizardTasks.IsOutsideWizard))
        {
            steps.Add(new("deadlines", "deadlines", true));
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
