using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Organizer;
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

    private readonly CommunityHub.Core.Settings.FeatureGateService _gate;
    private readonly CommunityHub.Core.Settings.RingResolver _rings;
    // §724 — the MAIL's own ring is now what decides the audience, so the monitor reads it from
    // the same service the transport does. Optional + last so existing construction still compiles.
    private readonly EmailTemplateRingService? _templateRings;

    public WelcomeModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SponsorRecipientResolver recipients,
        SponsorWelcomeEmailService welcome,
        CommunityHub.Core.Settings.FeatureGateService gate,
        CommunityHub.Core.Settings.RingResolver rings,
        EmailTemplateRingService? templateRings = null)
    {
        _db = db;
        _participant = participant;
        _recipients = recipients;
        _welcome = welcome;
        _gate = gate;
        _rings = rings;
        _templateRings = templateRings;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public bool IsError { get; private set; }
    public List<CompanyRow> Rows { get; private set; } = new();

    /// <summary>True when the edition's <c>welcome-email</c> switch is OFF — the reconcile
    /// job skips entirely, so NOTHING on this page will send until it is turned on.</summary>
    public bool WelcomeEmailDisabled { get; private set; }

    /// <summary>The ring <c>welcome-email</c> is released to — the ceiling on who can be
    /// welcomed at all right now.</summary>
    public CommunityHub.Core.Settings.Ring ReleasedRing { get; private set; }

    /// <summary>
    /// One sponsor company's coordinator + welcome status.
    ///
    /// <para>§326cb — the page used to show only counts, so a company that had NOT been
    /// welcomed looked identical whether it was blocked, out of ring, or simply not due
    /// yet. <paramref name="BlockedReason"/> and <paramref name="OutOfRing"/> name the two
    /// real reasons, using the SAME gates the sender applies.</para>
    /// </summary>
    public record CompanyRow(
        string CompanyId,
        int Coordinators,
        int SignerOnly,
        int WelcomeSent,
        string? BlockedReason = null,
        int OutOfRing = 0)
    {
        /// <summary>Coordinators still waiting: not welcomed, not blocked, in ring.</summary>
        public int Pending => Math.Max(0, Coordinators - WelcomeSent - OutOfRing);

        /// <summary>Plain-language status for the grid — why this company is where it is.</summary>
        public string Status =>
            Coordinators == 0 ? "No coordinator — add one"
            : BlockedReason is not null ? "Blocked"
            : WelcomeSent >= Coordinators ? "All welcomed"
            : OutOfRing > 0 && Pending == 0 ? $"Waiting for ring ({OutOfRing})"
            : $"Sending — {Pending} due next run";
    }

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

    public async Task<IActionResult> OnPostResetAllAsync(string? confirmPhrase, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        // §334: this clears the sent-ledger for EVERY sponsor company, and the 15-minute
        // reconcile job then mails a fresh welcome to all of them. One mis-click is a mass
        // send — the exact "unforeseen scenario" class the safety review was about.
        if (!TypedConfirmation.Matches(confirmPhrase, TypedConfirmation.ConfirmPhrase))
        {
            Message = TypedConfirmation.Rejection(
                TypedConfirmation.ConfirmPhrase,
                "clear the welcome flag for every sponsor company");
            await LoadRowsAsync(me.EventId, ct);
            return Page();
        }

        var deleted = await _welcome.ResetForAllSponsorsAsync(me.EventId, ct);
        Message = $"Cleared {deleted} welcome flag(s) across all sponsor companies. Resends will now go out.";
        await LoadRowsAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadRowsAsync(int eventId, CancellationToken ct)
    {
        // §326cb: the welcome is AUTOMATIC (SponsorWelcomeReconcileJob, every 15 min). This
        // page is a monitor, so it must report the same gates the job applies — otherwise a
        // company that will never be welcomed looks the same as one that is simply due.
        WelcomeEmailDisabled = !await _gate.IsFeatureEnabledAsync("welcome-email", eventId, ct);
        ReleasedRing = await _gate.GetReleasedRingAsync("welcome-email", eventId, ct);

        var sponsors = await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.Role == ParticipantRole.Sponsor
                        && p.SponsorCompanyId != null
                        && p.SponsorCompanyId != "")
            .Select(p => new
            {
                p.Id,
                p.SponsorCompanyId,
                p.Email,
                p.IsEventCoordinator,
                p.IsSigner,
                p.IsActive,
            })
            .ToListAsync(ct);

        // GATE 1 — the provisioning guard: a booth company (Gold+) is blocked until its
        // SharePoint upload folders exist, so exhibitors never land on dead upload links.
        var boothCompanies = (await _db.SponsorInfos
                .Where(s => s.EventId == eventId && s.SponsorPackage >= SponsorPackage.Gold)
                .Select(s => s.SponsorCompanyId)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var provisioned = (await _db.SponsorUploadLocations
                .Where(l => l.EventId == eventId && l.EditLinkUrl != null && l.EditLinkUrl != "")
                .Select(l => l.SponsorCompanyId)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // GATE 2 — the per-recipient ring, evaluated with the SAME rule WelcomeEmailService
        // uses, so this column can never disagree with what the job will actually do.
        //
        // 🔒 §724 — that rule CHANGED, so this had to change with it. The feature ring no longer
        // gates a welcome; the audience is the MAIL's own (mail × role) ring. Left as it was, this
        // monitor would have kept reporting "out of ring" from a number nothing consults any more —
        // a page confidently wrong about the one thing it exists to answer, which is the §326bx
        // defect this page was built to end.
        var outOfRing = new HashSet<int>();
        var sponsorWelcomeKey =
            Core.Email.WelcomeVariants.TemplateKeyFor(ParticipantRole.Sponsor) ?? "welcome";
        CommunityHub.Core.Settings.Ring? sponsorMailRing = _templateRings is not null
            ? await _templateRings.GetEffectiveRingAsync(
                eventId, sponsorWelcomeKey, ct, ParticipantRole.Sponsor)
            : null;

        foreach (var c in sponsors.Where(p => p.IsEventCoordinator && p.IsActive))
        {
            if (sponsorMailRing is not { } mailRing) break;   // ring service unwired (tests) ⇒ no claim
            var theirs = await _rings.GetEffectiveRingAsync(c.Id, ct);
            if (!CommunityHub.Core.Settings.Rings.IsActiveForRing(theirs, mailRing)) outOfRing.Add(c.Id);
        }

        // Welcome ledger for the edition (one row per welcomed recipient).
        //
        // §340-G-1: read by ADDRESS **and** by occasion key. §326bf made the send idempotent on
        // `welcome:{participantId}`, so a contact whose address changed since their welcome has a
        // row under the OLD address — and an address-only read would report "Welcome sent" as 0 for
        // them AND count them as out-of-ring, i.e. it would tell the operator to send a welcome
        // that has already gone out. A count that is wrong in the direction of "do it again" is
        // worse than no count at all.
        var welcomeLedger = await _db.SentReminders
            .Where(s => s.EventId == eventId && s.ReminderType == "welcome")
            .Select(s => new { s.RecipientEmail, s.OccasionKey })
            .ToListAsync(ct);
        var welcomedEmails = welcomeLedger.Select(s => s.RecipientEmail)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var welcomedKeys = welcomeLedger.Select(s => s.OccasionKey).ToHashSet(StringComparer.Ordinal);

        // Takes the two fields rather than a Participant: the list above is an anonymous
        // projection, and widening it to full entities just to satisfy a helper signature would
        // pull whole rows across for a set-membership test.
        bool Welcomed(int participantId, string email) =>
            welcomedEmails.Contains(email) || welcomedKeys.Contains($"welcome:{participantId}");

        Rows = sponsors
            .GroupBy(p => p.SponsorCompanyId!)
            .Select(g => new CompanyRow(
                CompanyId: g.Key,
                // Coordinator = the audience (both-roles counts as a coordinator).
                Coordinators: g.Count(p => p.IsEventCoordinator && p.IsActive),
                // Signer-only = excluded from sponsor mail (informational column).
                SignerOnly: g.Count(p => p.IsSigner && !p.IsEventCoordinator),
                WelcomeSent: g.Count(p => p.IsEventCoordinator && p.IsActive
                                          && Welcomed(p.Id, p.Email)),
                // §326cd: the old text said "run the sponsor pull first", which was wrong
                // advice — the pull runs itself every 30 minutes and PROVISIONS the folder
                // (SponsorOrderPullService → EnsureFolderWithEditLinkAsync). So this state is
                // normally transient. It only STICKS when provisioning cannot succeed, which
                // is the thing worth telling the operator.
                BlockedReason: boothCompanies.Contains(g.Key) && !provisioned.Contains(g.Key)
                    ? "Waiting for its SharePoint upload folder. The sponsor pull provisions this "
                      + "automatically within ~30 minutes — no action needed. If it stays here, "
                      + "SharePoint provisioning is failing or not configured (check the pull job log)."
                    : null,
                OutOfRing: g.Count(p => p.IsEventCoordinator && p.IsActive
                                        && !Welcomed(p.Id, p.Email)
                                        && outOfRing.Contains(p.Id))))
            .OrderBy(r => r.CompanyId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
