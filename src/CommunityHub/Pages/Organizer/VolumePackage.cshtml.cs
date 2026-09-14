using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1077 — VOLUME PACKAGE BENEFITS: who qualifies, and why.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"i need to have a Volume Package Benefits organizer page to manage
/// this and see the actual status"</i>. ≥10 attendees ⇒ keynote mention + social-media announcement
/// + group photo.</para>
///
/// <para>🔑 <b>The page's real job is to make the ANSWER auditable, not just to show it.</b> A number
/// on its own invites "why is Globeteam at 9?" — so every row shows the per-check contribution
/// (orders / coupons / domains) beside the total, and those three deliberately do NOT add up to it:
/// a person found twice is counted once (the whole §1077 dedupe rule). The page says so in words,
/// because a reader who does the arithmetic and finds it "wrong" will otherwise report a bug.</para>
///
/// <para>🔒 <b>This page sends nothing.</b> Stage 1 computes and displays; the approval mail is stage
/// 2. The Recompute button re-runs the same sweep the nightly job runs — same code, so the page can
/// never show an answer the job would not have produced.</para>
/// </remarks>
[Authorize]
public class VolumePackageModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly VolumePackageQualificationService _qualify;
    private readonly VolumePackageSweep _sweep;
    private readonly VolumePackageApprovalMailService _approvals;
    private readonly VolumePackageWizardService _wizard;
    private readonly VolumePackageInviteMailService _invites;
    private readonly VolumePackagePostEventMailService _postEvent;
    private readonly FeatureGateService _gate;
    private readonly TimeProvider _clock;

    public VolumePackageModel(
        CommunityHubDbContext db, ICurrentParticipantAccessor participant,
        VolumePackageQualificationService qualify, VolumePackageSweep sweep,
        VolumePackageApprovalMailService approvals, VolumePackageWizardService wizard,
        VolumePackageInviteMailService invites, VolumePackagePostEventMailService postEvent,
        FeatureGateService gate, TimeProvider clock)
    {
        _wizard = wizard;
        _invites = invites;
        _postEvent = postEvent;
        _db = db;
        _participant = participant;
        _qualify = qualify;
        _sweep = sweep;
        _approvals = approvals;
        _gate = gate;
        _clock = clock;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    /// <summary>One row: the stored entity plus its freshly computed answer.</summary>
    public sealed record Row(
        VolumePackageCompany Company,
        int Count,
        bool Qualifies,
        int FromOrders,
        int FromCoupons,
        int FromDomains,
        (string Email, string? Name, int Tickets)? SuggestedApprover);

    public List<Row> Rows { get; private set; } = new();
    public int Threshold => VolumePackageQualificationService.Threshold;

    // ---- create / edit form -------------------------------------------------------------
    [BindProperty] public int EditId { get; set; }
    [BindProperty] public string? CustomName { get; set; }
    [BindProperty] public string? Domains { get; set; }
    [BindProperty] public string? LinkedEmails { get; set; }
    [BindProperty] public string? CouponCodes { get; set; }
    [BindProperty] public string? ErpCustomerNumbers { get; set; }

    // ---- stage 2: approve / overrule ----------------------------------------------------
    [BindProperty] public int ApproveId { get; set; }
    [BindProperty] public string? ApproverName { get; set; }
    [BindProperty] public string? ApproverEmail { get; set; }
    [BindProperty] public string? ApproverMobile { get; set; }

    /// <summary>
    /// Whether the approval-request mail is switched on. 🔑 Rendered on the page ON PURPOSE: §1018c
    /// is the standing lesson that a feature defaulted OFF and mentioned only in a settings list is
    /// a feature that never runs and nobody notices. The state belongs where the work happens.
    /// </summary>
    public bool ApprovalMailEnabled { get; private set; }

    /// <summary>The mailbox the request goes to, so the page names it rather than implying "someone".</summary>
    public string ApprovalMailRecipient => VolumePackageApprovalMailService.Recipient;

    /// <summary>
    /// §1077 stage 3 — whether the INVITATION mail (the one that reaches the company) is switched
    /// on. Shown for the same §1018c reason as the approval switch: a default-OFF feature nobody
    /// remembers is a feature that never runs and nobody notices.
    /// </summary>
    public bool InviteMailEnabled { get; private set; }

    /// <summary>§1077.9 — whether the post-event thank-you may be sent.</summary>
    public bool PostEventMailEnabled { get; private set; }

    /// <summary>The wizard link for a company, so an organizer can copy it into their own mail.</summary>
    public string LinkFor(VolumePackageCompany c) => _invites.LinkFor(c);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Create or update one entity.</summary>
    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var name = (CustomName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            Error = "Give the company a name — it is what appears in the keynote and the announcement.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        // 🔴 A company with no identifier can never qualify, and would sit on the page looking like a
        // bug for ever. Refuse it at the door rather than storing a row that cannot work.
        var domainList = VolumePackageCompany.Split(Domains)
            .Select(VolumePackageCompany.NormaliseDomain)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var linkedList = VolumePackageCompany.Split(LinkedEmails);

        if (domainList.Count == 0 && linkedList.Count == 0)
        {
            Error = "Add at least one e-mail domain (or a linked e-mail) — without one, nothing can "
                  + "ever match this company.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        var company = EditId > 0
            ? await _db.VolumePackageCompanies
                .FirstOrDefaultAsync(c => c.Id == EditId && c.EventId == me.EventId, ct)
            : null;

        if (EditId > 0 && company is null)
        {
            Error = "That company no longer exists.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        if (company is null)
        {
            company = new VolumePackageCompany { EventId = me.EventId };
            _db.VolumePackageCompanies.Add(company);
        }

        company.CustomName = name;
        company.Domains = string.Join("\n", domainList);
        company.LinkedEmails = string.Join("\n", linkedList);
        company.CouponCodes = string.Join("\n", VolumePackageCompany.Split(CouponCodes));
        company.ErpCustomerNumbers = string.Join("\n", VolumePackageCompany.Split(ErpCustomerNumbers));
        company.UpdatedAt = _clock.GetUtcNow();
        company.LastUpdatedByEmail = me.Email;

        await _db.SaveChangesAsync(ct);

        // Recompute immediately: an organizer who has just added a domain wants to see whether it
        // qualified, not to wait for tonight and wonder whether the edit took.
        await _sweep.RunAsync(me.EventId, ct);

        Message = $"Saved “{name}”.";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Re-run the nightly sweep on demand — the SAME code, so the two cannot disagree.</summary>
    public async Task<IActionResult> OnPostRecomputeAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var r = await _sweep.RunAsync(me.EventId, ct);
        Message = $"Recomputed: {r}.";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// §1077 stage 2 — APPROVE the benefits, naming who we deal with. The suggestion is a
    /// suggestion: these fields arrive pre-filled from it and the organizer may type anyone
    /// (<i>"organizer may overrule"</i>).
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Approving records a DECISION, it does not contact anybody.</b> Writing to the approver
    /// is stage 3, behind its own conversation. An organizer who approves here has said "this is the
    /// person", not "write to them now".
    /// </remarks>
    public async Task<IActionResult> OnPostApproveAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var company = await _db.VolumePackageCompanies
            .FirstOrDefaultAsync(c => c.Id == ApproveId && c.EventId == me.EventId, ct);

        if (company is null)
        {
            Error = "That company no longer exists.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        var email = (ApproverEmail ?? string.Empty).Trim();

        // 🔴 An approval with no address names nobody. It would sit on the page looking settled while
        // stage 3 has no one to write to — a decision that silently cannot be acted on.
        if (email.Length == 0 || !email.Contains('@'))
        {
            Error = "Give the approver's e-mail address — the approval records WHO we deal with, and "
                  + "without an address it settles nothing.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        company.ApproverEmail = email;
        company.ApproverName = string.IsNullOrWhiteSpace(ApproverName) ? null : ApproverName.Trim();
        company.ApproverMobile = string.IsNullOrWhiteSpace(ApproverMobile) ? null : ApproverMobile.Trim();
        company.BenefitsApprovedAt = _clock.GetUtcNow();
        company.BenefitsApprovedByEmail = me.Email;
        company.UpdatedAt = _clock.GetUtcNow();
        company.LastUpdatedByEmail = me.Email;

        await _db.SaveChangesAsync(ct);

        Message = $"Approved “{company.CustomName}” — {email} is the contact.";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// Withdraw an approval. 🔑 The DAILY SWEEP never does this (sticky approval); an organizer
    /// can, because a decision a person made is a decision a person may unmake.
    /// </summary>
    public async Task<IActionResult> OnPostWithdrawAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var company = await _db.VolumePackageCompanies
            .FirstOrDefaultAsync(c => c.Id == id && c.EventId == me.EventId, ct);

        if (company is not null)
        {
            company.BenefitsApprovedAt = null;
            company.BenefitsApprovedByEmail = null;
            // ⚠️ The approver CONTACT stays. It is who we found, and clearing it would throw away
            // the answer to a different question than the one being reopened.
            company.UpdatedAt = _clock.GetUtcNow();
            company.LastUpdatedByEmail = me.Email;
            await _db.SaveChangesAsync(ct);
            Message = $"Withdrew the approval for “{company.CustomName}”. The contact is kept.";
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// §1077 stage 2 — ask the organizer mailbox about ONE company on demand: the same mail the job
    /// sends, for a company already asked about (or when the switch was off at the time).
    /// </summary>
    /// <remarks>
    /// 🔒 <b>It obeys the same kill switch as the job.</b> A button that can mail while the feature
    /// is switched off is not a kill switch, and the operator turning it off means "this feature
    /// sends nothing" — not "nothing sends unless someone clicks".
    /// </remarks>
    public async Task<IActionResult> OnPostAskAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!await _gate.IsFeatureEnabledAsync(
                VolumePackageApprovalMailService.FeatureKey, me.EventId, ct))
        {
            Error = "The approval-request mail is switched off, so nothing was sent. Turn on "
                  + "“Volume package: ask who approves” in Settings → Features first.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        var company = await _db.VolumePackageCompanies
            .FirstOrDefaultAsync(c => c.Id == id && c.EventId == me.EventId, ct);

        var result = company is null
            ? VolumePackageApprovalMailService.ApprovalMailResult.Nothing
            : await _approvals.SendForCompanyAsync(company.Id, ct);

        Message = result.Sent
            ? $"Asked {VolumePackageApprovalMailService.Recipient} about “{company!.CustomName}”."
            : "Nothing was sent — a company is only asked about while it qualifies and is neither "
            + "approved nor declined.";

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// §1077 stage 3 — mint (or re-mint) the company's wizard link.
    /// </summary>
    /// <remarks>
    /// ⚠️ Re-issuing KILLS the previous URL. That is the behaviour you want the moment a link has
    /// gone to the wrong person, and the button says so before it does it.
    /// </remarks>
    public async Task<IActionResult> OnPostIssueLinkAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var company = await _db.VolumePackageCompanies
            .FirstOrDefaultAsync(c => c.Id == id && c.EventId == me.EventId, ct);

        if (company is null) Error = "That company no longer exists.";
        else
        {
            var token = await _wizard.IssueTokenAsync(company.Id, me.Email, ct);
            Message = token is null
                ? "The link could not be created."
                : $"New link for “{company.CustomName}”. Any previous link has stopped working.";
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Withdraw the link. 🔑 The token row is kept — "revoked on the 3rd" is a fact.</summary>
    public async Task<IActionResult> OnPostRevokeLinkAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var company = await _db.VolumePackageCompanies
            .FirstOrDefaultAsync(c => c.Id == id && c.EventId == me.EventId, ct);

        if (company is not null && await _wizard.RevokeTokenAsync(company.Id, me.Email, ct))
            Message = $"The link for “{company.CustomName}” no longer works.";
        else
            Error = "There was no link to withdraw.";

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// §1077 stage 3 — send the invitation to the approver.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>The first mail in this feature that reaches somebody outside the organizing team.</b>
    /// It is a deliberate click, never a job, and it obeys its own switch — off by default, because
    /// this is precisely what the operator's <i>"tested very detailed … approved before going into
    /// PROD"</i> was drawn around.
    /// </remarks>
    public async Task<IActionResult> OnPostInviteAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!await _gate.IsFeatureEnabledAsync(
                VolumePackageInviteMailService.FeatureKey, me.EventId, ct))
        {
            Error = "The invitation mail is switched off, so nothing was sent. Turn on “Volume "
                  + "package: invite the company” in Settings → Features first — it is the one mail "
                  + "in this feature that reaches the company itself.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        var company = await _db.VolumePackageCompanies
            .FirstOrDefaultAsync(c => c.Id == id && c.EventId == me.EventId, ct);

        if (company is null)
        {
            Error = "That company no longer exists.";
        }
        else
        {
            var result = await _invites.SendAsync(company.Id, ct);
            if (result.Sent) Message = $"Invitation sent to {result.Recipient}.";
            else Error = result.Problem;
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// §1077.9 — the post-event thank-you: pictures + the LinkedIn tagging ask.
    /// </summary>
    /// <remarks>
    /// 🔒 A deliberate click, never a job. "The event is over" is not a date the hub should infer —
    /// the gallery has to be live first, and only a person can see that it is.
    /// </remarks>
    public async Task<IActionResult> OnPostPostEventAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!await _gate.IsFeatureEnabledAsync(
                VolumePackagePostEventMailService.FeatureKey, me.EventId, ct))
        {
            Error = "The post-event thank-you is switched off. Turn on “Volume package: post-event "
                  + "thank-you” in Settings → Features first.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        var company = await _db.VolumePackageCompanies
            .FirstOrDefaultAsync(c => c.Id == id && c.EventId == me.EventId, ct);

        if (company is null) Error = "That company no longer exists.";
        else
        {
            var result = await _postEvent.SendAsync(company.Id, ct);
            if (result.Sent) Message = $"Thank-you sent to {result.Recipient}.";
            else Error = result.Problem;
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// Delete an entity. ⚠️ Only the definition — attendees and orders are untouched, and the entity
    /// simply stops being tracked. Its snapshots go with it (cascade), which is why the button says
    /// what it removes.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var company = await _db.VolumePackageCompanies
            .FirstOrDefaultAsync(c => c.Id == id && c.EventId == me.EventId, ct);

        if (company is not null)
        {
            _db.VolumePackageCompanies.Remove(company);
            await _db.SaveChangesAsync(ct);
            Message = $"Removed “{company.CustomName}”.";
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        ApprovalMailEnabled = await _gate.IsFeatureEnabledAsync(
            VolumePackageApprovalMailService.FeatureKey, eventId, ct);
        InviteMailEnabled = await _gate.IsFeatureEnabledAsync(
            VolumePackageInviteMailService.FeatureKey, eventId, ct);
        PostEventMailEnabled = await _gate.IsFeatureEnabledAsync(
            VolumePackagePostEventMailService.FeatureKey, eventId, ct);

        var companies = await _db.VolumePackageCompanies
            .Where(c => c.EventId == eventId)
            .ToListAsync(ct);

        var results = (await _qualify.ComputeAllAsync(eventId, ct))
            .ToDictionary(r => r.CompanyId);

        var rows = new List<Row>(companies.Count);
        foreach (var c in companies)
        {
            results.TryGetValue(c.Id, out var r);

            // Only suggest an approver where one is still needed — a settled row must not keep
            // offering an alternative as though the decision were open.
            var suggestion = string.IsNullOrWhiteSpace(c.ApproverEmail)
                ? await _sweep.SuggestApproverAsync(c.Id, ct)
                : null;

            rows.Add(new Row(
                c,
                r?.Count ?? 0,
                r?.Qualifies ?? false,
                r?.FromOrders ?? 0,
                r?.FromCoupons ?? 0,
                r?.FromAttendeeDomains ?? 0,
                suggestion));
        }

        // Qualified first, then the near-misses (which are the ones worth a look), then the rest.
        Rows = rows
            .OrderByDescending(x => x.Qualifies)
            .ThenByDescending(x => x.Count)
            .ThenBy(x => x.Company.CustomName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
