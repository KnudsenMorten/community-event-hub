using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
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
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }
        var n = await _svc.SeedDefaultMasterClassesAsync(me.EventId, ct);
        return RedirectToPage(new { msg = $"Created {n} master class(es). Set capacity on each below." });
    }

    // §326bl (operator 2026-07-25): OnPostSendInvitesAsync + its card were REMOVED.
    // AttendeeBackstageSyncJob already sends the selection invite automatically to every
    // eligible 2-day attendee (stamping Attendee.MasterClassInviteSentAt so nobody gets two),
    // so the manual bulk button duplicated the job and put a mass-send one click away.
    // MasterClassEmailService.SendSelectionInviteAsync is untouched — the job still calls it.

    public async Task<IActionResult> OnPostCapacityAsync(
        int session, int? capacity, string? confirmPhrase, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        // §326ba: refuse 0/negative outright, and SAY SO. The service ignores it too
        // (defence in depth), but a silent no-op is how a typo becomes a mystery later.
        if (capacity is <= 0)
        {
            return RedirectToPage(new { session, msg =
                "Capacity must be 1 or more — nothing was changed. Leave the field EMPTY if "
                + "the class genuinely has no limit; 0 is treated as a mistake, not as unlimited." });
        }

        // §339 — CLEARING the capacity makes the class UNLIMITED, which promotes and E-MAILS every
        // waitlisted attendee at once. Until now that sat behind a JavaScript confirm() ONLY: with
        // JS off, or on a direct POST, there was no gate at all. It is also the mildest-LOOKING
        // action on the page — emptying a text box — while being one of the largest in effect.
        //
        // Now the same TYPED confirmation §334 put on the other mass-send paths, enforced on the
        // SERVER where it cannot be bypassed. Gated on null specifically: raising a NUMBER also
        // promotes people, but it promotes at most as many as the number allows and is an obviously
        // deliberate act; clearing the field is unbounded and looks like nothing.
        if (capacity is null
            && !TypedConfirmation.Matches(confirmPhrase, TypedConfirmation.ConfirmPhrase))
        {
            return RedirectToPage(new { session, msg =
                "Nothing was changed. Leaving capacity EMPTY makes this class UNLIMITED and "
                + "immediately promotes AND e-mails every waitlisted attendee. To confirm, type "
                + $"{TypedConfirmation.ConfirmPhrase} in the confirm box next to the capacity field." });
        }

        // Raising the cap opens seats that the waitlist takes first (§93/§94); notify
        // every attendee the engine promoted/moved as a result.
        var promotions = await _svc.SetCapacityAsync(me.EventId, session, capacity, ct);
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        foreach (var p in promotions)
        {
            if (p.PromotedSignupId is int id)
            {
                try { await _promo.SendPromotionAsync(id, baseUrl, ct, p.ReleasedTitle); } catch { /* retryable */ }
            }
        }
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
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var promo = await _svc.RemoveAsync(me.EventId, attendeeId, session, ct);

        // §389 (operator 2026-07-26: "i guess the remove from my organizer side didn't work").
        // This used to report "Entry removed." UNCONDITIONALLY — including when RemoveAsync found
        // no row and did nothing. A no-op that congratulates you is worse than an error: the
        // organizer walks away believing the person is gone, and only finds out later when that
        // person is still on the list (or, as here, gets promoted into a seat). Say what happened.
        if (promo is null)
        {
            return RedirectToPage(new { session, msg = "Nothing to remove — that attendee had no entry for this Master Class." });
        }

        if (promo.PromotedSignupId is int promotedId)
        {
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            // §386: pass the released title so the promoted attendee gets ONE mail covering both
            // "you moved up" and "your old seat went" — this site was missed in the first sweep.
            try { await _promo.SendPromotionAsync(promotedId, baseUrl, ct, promo.ReleasedTitle); }
            catch { /* retryable */ }

            return RedirectToPage(new { session, msg = "Entry removed — the next person on the wait list was promoted and e-mailed." });
        }

        return RedirectToPage(new { session, msg = "Entry removed." });
    }
}
