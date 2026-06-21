using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer Master Class management (REQUIREMENTS §6): the list of master classes
/// with seat capacity + live seated/waitlist counts, set-capacity, and per-MC
/// roster (confirmed seats + the ordered waitlist). Each roster row exposes the
/// attendee's self-service link (the magic-link they use to manage their place)
/// so organizers can share it until the automated email ships. Organizer-gated.
/// </summary>
[Authorize]
public class MasterClassesModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly MasterClassSignupService _svc;
    private readonly CommunityHub.Core.Email.MasterClassPromotionEmailService _promo;
    private readonly CommunityHub.Core.Email.MasterClassEmailService _email;

    public MasterClassesModel(
        ICurrentParticipantAccessor participant,
        MasterClassSignupService svc,
        CommunityHub.Core.Email.MasterClassPromotionEmailService promo,
        CommunityHub.Core.Email.MasterClassEmailService email)
    {
        _participant = participant;
        _svc = svc;
        _promo = promo;
        _email = email;
    }

    public int InviteEligible { get; private set; }
    public int InviteSent { get; private set; }
    public int InviteNotSent { get; private set; }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public IReadOnlyList<MasterClassSignupService.McOption> MasterClasses { get; private set; }
        = Array.Empty<MasterClassSignupService.McOption>();

    public int? SelectedSession { get; private set; }
    public string? SelectedTitle { get; private set; }
    public sealed record RosterLine(int AttendeeId, string Name, string Email, DateTimeOffset When, string SelfServiceUrl);
    public IReadOnlyList<RosterLine> Seated { get; private set; } = Array.Empty<RosterLine>();
    public IReadOnlyList<RosterLine> Offered { get; private set; } = Array.Empty<RosterLine>();
    public IReadOnlyList<RosterLine> Waitlist { get; private set; } = Array.Empty<RosterLine>();


    private async Task<RosterLine> ToLineAsync(
        MasterClassSignupService.RosterRow r, CancellationToken ct)
    {
        var token = await _svc.EnsureSelfServiceTokenAsync(r.AttendeeId, ct);
        var url = $"{Request.Scheme}://{Request.Host}/MyMasterClass?t={token}";
        return new RosterLine(r.AttendeeId, r.Name, r.Email, r.SignedUpAt, url);
    }

    public async Task<IActionResult> OnGetAsync(int? session, string? msg, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Message = msg;
        MasterClasses = await _svc.ListMasterClassesAsync(me.EventId, ct);
        (InviteEligible, InviteSent, InviteNotSent) = await _svc.InviteStatsAsync(me.EventId, ct);

        if (session is int sid)
        {
            SelectedSession = sid;
            SelectedTitle = MasterClasses.FirstOrDefault(m => m.SessionId == sid)?.Title;
            var (seated, offered, wait) = await _svc.GetRosterAsync(me.EventId, sid, ct);
            async Task<List<RosterLine>> Lines(IReadOnlyList<MasterClassSignupService.RosterRow> rows)
            {
                var ls = new List<RosterLine>();
                foreach (var r in rows) ls.Add(await ToLineAsync(r, ct));
                return ls;
            }
            Seated = await Lines(seated);
            Offered = await Lines(offered);
            Waitlist = await Lines(wait);
        }
        return Page();
    }

    /// <summary>Create the 8 standard ELDK27 master classes (idempotent).</summary>
    public async Task<IActionResult> OnPostSeedAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        var n = await _svc.SeedDefaultMasterClassesAsync(me.EventId, ct);
        return RedirectToPage(new { msg = $"Created {n} master class(es). Set capacity on each below." });
    }

    /// <summary>Send the "choose your Master Class" invite to every 2-day attendee not yet invited.</summary>
    public async Task<IActionResult> OnPostSendInvitesAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var ids = await _svc.EligibleNotInvitedIdsAsync(me.EventId, ct);
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var sent = 0;
        foreach (var id in ids)
        {
            try { if (await _email.SendSelectionInviteAsync(id, baseUrl, ct: ct)) sent++; } catch { /* retry later */ }
        }
        return RedirectToPage(new { msg = $"Sent {sent} Master Class selection invite(s)." });
    }

    public async Task<IActionResult> OnPostCapacityAsync(int session, int? capacity, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await _svc.SetCapacityAsync(me.EventId, session, capacity, ct);
        return RedirectToPage(new { session, msg = "Capacity updated." });
    }

    /// <summary>
    /// Organizer removes one attendee's signup (a waitlist entry OR a confirmed
    /// seat). Removing a confirmed seat promotes the next waitlisted attendee and
    /// sends them the ring-gated promotion email (same as a self-service give-up).
    /// </summary>
    public async Task<IActionResult> OnPostRemoveAsync(int session, int attendeeId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var promo = await _svc.RemoveAsync(me.EventId, attendeeId, session, ct);
        if (promo?.PromotedSignupId is int promotedId)
        {
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            try { await _promo.SendPromotionAsync(promotedId, baseUrl, ct); } catch { /* retryable */ }
        }
        return RedirectToPage(new { session, msg = "Entry removed." });
    }
}
