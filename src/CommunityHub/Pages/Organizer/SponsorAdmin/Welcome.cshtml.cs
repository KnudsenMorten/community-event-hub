using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer.SponsorAdmin;

/// <summary>
/// Organizer surface to (re)send the sponsor welcome/intro email and reset the
/// welcome flag so a resend actually fires (REQUIREMENTS §7c). The audience is
/// the universal sponsor-email rule: every send goes ONLY to a company's
/// EVENT-COORDINATOR contacts (signer-only excluded, both-roles included), via
/// the shared <see cref="SponsorRecipientResolver"/> /
/// <see cref="SponsorWelcomeEmailService"/>. The welcome send is idempotent
/// (one per contact, tracked in <c>SentReminder</c> ReminderType="welcome"), so
/// "Reset" deletes the matching ledger rows and "Resend" then re-sends.
///
/// Auth matches the other SponsorAdmin pages: signed-in Organizer only; everyone
/// else gets a friendly AccessDenied notice (not a 401). Delivery is still gated
/// by <c>BrevoEmailSender</c>'s redirect/allowlist.
/// </summary>
[Authorize]
public class WelcomeModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SponsorRecipientResolver _recipients;
    private readonly SponsorWelcomeEmailService _welcome;

    public WelcomeModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SponsorRecipientResolver recipients,
        SponsorWelcomeEmailService welcome)
    {
        _db = db;
        _participant = participant;
        _recipients = recipients;
        _welcome = welcome;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public bool IsError { get; private set; }
    public List<CompanyRow> Rows { get; private set; } = new();

    /// <summary>One sponsor company's coordinator + welcome status.</summary>
    public record CompanyRow(
        string CompanyId,
        int Coordinators,
        int SignerOnly,
        int WelcomeSent);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadRowsAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostResendAsync(string companyId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var r = await _welcome.SendForCompanyAsync(me.EventId, companyId, ct);
        if (r.Blocked)
        {
            Message = $"Company {companyId}: {r.Reason}";
            IsError = true;
        }
        else
        {
            Message = r.CoordinatorsResolved == 0
                ? $"Company {companyId} has no event-coordinator contacts, so nothing was sent. Add a coordinator (or set the coordinator flag) first."
                : $"Company {companyId}: sent {r.Sent} welcome email(s) to {r.CoordinatorsResolved} coordinator(s)."
                  + (r.Skipped > 0 ? $" {r.Skipped} already-welcomed (Reset first to force a resend)." : string.Empty);
            IsError = r.CoordinatorsResolved == 0;
        }
        await LoadRowsAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostResetAsync(string companyId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var deleted = await _welcome.ResetForCompanyAsync(me.EventId, companyId, ct);
        Message = $"Company {companyId}: cleared {deleted} welcome flag(s). A resend will now go out.";
        await LoadRowsAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostResendAllAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var results = await _welcome.SendForAllSponsorsAsync(me.EventId, ct);
        var sent = results.Sum(r => r.Sent);
        var coordinators = results.Sum(r => r.CoordinatorsResolved);
        var blocked = results.Count(r => r.Blocked);
        Message = $"Sent {sent} welcome email(s) across {results.Count} sponsor companies "
                  + $"({coordinators} coordinator(s) resolved). Reset a company first to re-send to already-welcomed coordinators."
                  + (blocked > 0
                      ? $" {blocked} booth company(ies) skipped — SharePoint upload folders not provisioned yet (run the sponsor pull first)."
                      : string.Empty);
        await LoadRowsAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostResetAllAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var deleted = await _welcome.ResetForAllSponsorsAsync(me.EventId, ct);
        Message = $"Cleared {deleted} welcome flag(s) across all sponsor companies. Resends will now go out.";
        await LoadRowsAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadRowsAsync(int eventId, CancellationToken ct)
    {
        var sponsors = await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.Role == ParticipantRole.Sponsor
                        && p.SponsorCompanyId != null
                        && p.SponsorCompanyId != "")
            .Select(p => new
            {
                p.SponsorCompanyId,
                p.Email,
                p.IsEventCoordinator,
                p.IsSigner,
                p.IsActive,
            })
            .ToListAsync(ct);

        // Welcome ledger for the edition (one row per welcomed recipient).
        var welcomedEmails = (await _db.SentReminders
            .Where(s => s.EventId == eventId && s.ReminderType == "welcome")
            .Select(s => s.RecipientEmail)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Rows = sponsors
            .GroupBy(p => p.SponsorCompanyId!)
            .Select(g => new CompanyRow(
                CompanyId: g.Key,
                // Coordinator = the audience (both-roles counts as a coordinator).
                Coordinators: g.Count(p => p.IsEventCoordinator && p.IsActive),
                // Signer-only = excluded from sponsor mail (informational column).
                SignerOnly: g.Count(p => p.IsSigner && !p.IsEventCoordinator),
                WelcomeSent: g.Count(p => p.IsEventCoordinator && p.IsActive
                                          && welcomedEmails.Contains(p.Email))))
            .OrderBy(r => r.CompanyId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
