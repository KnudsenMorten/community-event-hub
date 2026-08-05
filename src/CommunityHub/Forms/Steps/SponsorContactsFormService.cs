using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// §297 — render + edit model for the inline sponsor Contacts step (Contract Signers &amp; Event
/// Coordinators, mastered in e-conomic). Shows the current ERP contacts and lets the sponsor add one
/// inline. Empty-prefix bound.
/// </summary>
public sealed class SponsorContactsModel
{
    public string? NewName { get; set; }
    public string? NewEmail { get; set; }
    public string? NewPhone { get; set; }
    public bool NewSigner { get; set; }
    public bool NewCoordinator { get; set; }

    /// <summary>True when the ERP (e-conomic) backend is reachable + writable for this company.</summary>
    public bool ErpAvailable { get; set; }
    /// <summary>Display-only: the current ERP contacts.</summary>
    public List<Contact> CurrentContacts { get; set; } = new();
    /// <summary>
    /// §472 — a contact row for display. <c>Roles</c> is the raw e-conomic string ("Role:1,2") and
    /// is kept only as a fallback; the BOOLEANS are what the step renders, because "1,2" is an ERP
    /// integration detail that means nothing to a sponsor.
    /// </summary>
    /// <param name="ContactNumber">
    /// §783.3 — the e-conomic contact number, needed to REMOVE the row. e-conomic is the master, so
    /// a contact is identified by its own number, never by name or e-mail.
    /// </param>
    public sealed record Contact(
        string Name, string? Email, string Roles, bool IsSigner, bool IsEventCoordinator,
        int ContactNumber);
}

/// <summary>
/// §297 — shared submit-service for the inline sponsor Contacts step. Previously handler-less (the
/// wizard fell back to a link that took the sponsor OUT of the wizard); now it lists the ERP contacts
/// INLINE and lets them add one without leaving. e-conomic is the master, so add is fail-soft: a
/// backend hiccup surfaces a message but never blocks the wizard. Edit/delete stays on the Company
/// Details page. Self-registers via <see cref="IWizardFormService"/>.
/// </summary>
public sealed class SponsorContactsFormService : IWizardFormService
{
    private readonly CommunityHubDbContext _db;
    private readonly EconomicContactAdminService _erp;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _cmOptions;
    private readonly ILogger<SponsorContactsFormService> _log;

    public SponsorContactsFormService(
        CommunityHubDbContext db, EconomicContactAdminService erp, CompanyManagerClient cm,
        CompanyManagerOptions cmOptions, ILogger<SponsorContactsFormService> log)
    {
        _db = db;
        _erp = erp;
        _cm = cm;
        _cmOptions = cmOptions;
        _log = log;
    }

    private Task<string?> CompanyIdAsync(int participantId, CancellationToken ct) =>
        _db.Participants.Where(p => p.Id == participantId)
            .Select(p => p.SponsorCompanyId).FirstOrDefaultAsync(ct);

    /// <summary>Resolve the e-conomic customer number for a sponsor company, or null when
    /// unavailable (integration off, no ERP link, or a lookup hiccup).</summary>
    private async Task<int?> ErpNumberAsync(string companyId, CancellationToken ct)
    {
        if (!_cmOptions.Enabled || !_erp.CanWrite || !int.TryParse(companyId, out var cid)) return null;
        try
        {
            var company = await _cm.GetCompanyAsync(cid, ct);
            if (company is not null && int.TryParse(company.ErpCustomerNumber, out var erp) && erp > 0) return erp;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Sponsor contacts step: ERP number lookup failed for {Co}.", companyId); }
        return null;
    }

    public async Task<SponsorContactsModel> LoadAsync(int eventId, int participantId, CancellationToken ct)
    {
        var model = new SponsorContactsModel();
        var companyId = await CompanyIdAsync(participantId, ct);
        if (companyId is null) return model;
        var erpNo = await ErpNumberAsync(companyId, ct);
        if (erpNo is null) return model;              // ErpAvailable stays false → the partial explains
        model.ErpAvailable = true;
        model.CurrentContacts = await CurrentAsync(erpNo.Value, ct);
        return model;
    }

    private async Task<List<SponsorContactsModel.Contact>> CurrentAsync(int erpNo, CancellationToken ct)
    {
        try
        {
            var list = await _erp.ListContactsAsync(erpNo, ct);
            return list
                .Select(c => new SponsorContactsModel.Contact(
                    c.Name, c.Email, c.RoleDisplay, c.IsSigner, c.IsEventCoordinator,
                    c.ContactNumber))
                .ToList();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Sponsor contacts step: list failed for erp {Erp}.", erpNo); return new(); }
    }

    public async Task<WizardStepOutcome> SaveAsync(
        SponsorContactsModel model, int eventId, int participantId, string email,
        ModelStateDictionary modelState, CancellationToken ct)
    {
        var companyId = await CompanyIdAsync(participantId, ct);
        if (companyId is null) return WizardStepOutcome.NotRelevant;

        var erpNo = await ErpNumberAsync(companyId, ct);
        model.ErpAvailable = erpNo is not null;
        if (erpNo is null)
        {
            // ERP not reachable — don't block the wizard; the sponsor can manage contacts later.
            return WizardStepOutcome.Advance;
        }

        var name = Trim(model.NewName);
        if (name is not null)
        {
            try
            {
                await _erp.CreateAsync(erpNo.Value, name, Trim(model.NewEmail), Trim(model.NewPhone),
                    model.NewSigner, model.NewCoordinator, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Sponsor contacts step: create failed for erp {Erp}.", erpNo);
                modelState.AddModelError(string.Empty, "Couldn't add that contact in the backend right now — please try again, or manage it on Company Details.");
                model.CurrentContacts = await CurrentAsync(erpNo.Value, ct);
                return WizardStepOutcome.Invalid;
            }
        }

        model.CurrentContacts = await CurrentAsync(erpNo.Value, ct);
        model.NewName = model.NewEmail = model.NewPhone = null;
        model.NewSigner = model.NewCoordinator = false;
        return WizardStepOutcome.Advance;
    }

    /// <summary>
    /// §783.3 — REMOVE one of this company's e-conomic contacts. Returns the removed name, or a
    /// refusal message; never throws into the caller.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-03: <i>"I am missing ability to remove contacts in the get started. I
    /// can only ADD"</i>. Add existed here; edit and delete were on Company Details only.</para>
    ///
    /// <para>🔒 <b>Scoped by the ACTOR's own company.</b> The e-conomic customer number is resolved
    /// from the signed-in participant's <c>SponsorCompanyId</c>, so a posted contact number can only
    /// ever delete from the caller's own customer record — there is no customer id on the wire.</para>
    ///
    /// <para>⚠️ <b>The LAST Event Coordinator is refused.</b> §7c makes sponsor mail
    /// coordinator-only: <c>SponsorRecipientResolver</c> selects coordinators and excludes
    /// signer-only contacts. Removing the last coordinator therefore does not degrade the sponsor's
    /// mail — it ends it, silently, with the company still looking fully configured. That is the
    /// "reaches NOBODY, and nothing says so" failure this codebase keeps meeting, so it is refused
    /// with a reason rather than allowed and regretted. Removing a signer, or a coordinator while
    /// another remains, is unrestricted.</para>
    /// </remarks>
    public async Task<string> RemoveContactAsync(
        int participantId, int contactNumber, CancellationToken ct)
    {
        var companyId = await CompanyIdAsync(participantId, ct);
        if (companyId is null) return "That contact could not be found for your company.";

        var erpNo = await ErpNumberAsync(companyId, ct);
        if (erpNo is null)
            return "The contacts backend isn't reachable right now — nothing was removed.";

        var current = await CurrentAsync(erpNo.Value, ct);
        var target = current.FirstOrDefault(c => c.ContactNumber == contactNumber);
        if (target is null) return "That contact could not be found for your company.";

        if (target.IsEventCoordinator
            && current.Count(c => c.IsEventCoordinator) == 1)
        {
            return $"{target.Name} is your only event coordinator, so they can't be removed — "
                 + "every message we send about your booth goes to the coordinators. Add another "
                 + "coordinator first, then remove this one.";
        }

        try
        {
            await _erp.DeleteAsync(erpNo.Value, contactNumber, ct);
            return $"{target.Name} was removed from your contacts.";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Sponsor contacts step: delete failed for erp {Erp} contact {Contact}.",
                erpNo, contactNumber);
            return "Couldn't remove that contact in the backend right now — please try again, or "
                 + "manage it on Company Details.";
        }
    }

    private static string? Trim(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
