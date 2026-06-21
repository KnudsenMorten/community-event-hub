using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Attendee;

/// <summary>
/// The attendee "Waitlist" page (in-hub; operator 2026-06-21). Shows the attendee's
/// current waitlist place or held offer for a Master Class and lets them leave it
/// (which promotes the next person). Choosing/holding a confirmed seat lives on the
/// sibling "Master Class" page (/Attendee). Engine: <see cref="MasterClassSignupService"/>,
/// attendee resolved by the signed-in participant's email in the active edition.
/// </summary>
[Authorize]
public class WaitlistModel : PageModel
{
    private readonly MasterClassSignupService _svc;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly MasterClassPromotionEmailService _promo;
    private readonly MasterClassEmailService _email;

    public WaitlistModel(
        MasterClassSignupService svc,
        ICurrentParticipantAccessor participant,
        MasterClassPromotionEmailService promo,
        MasterClassEmailService email)
    {
        _svc = svc;
        _participant = participant;
        _promo = promo;
        _email = email;
    }

    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    public bool HasAttendeeRecord { get; private set; }
    public bool Eligible { get; private set; }
    public string? Message { get; private set; }
    /// <summary>The attendee's waitlist place / held offer, if any.</summary>
    public MasterClassSignupService.MySignup? Pending { get; private set; }
    /// <summary>The attendee's confirmed seat, if any (shown as context).</summary>
    public MasterClassSignupService.MySignup? Confirmed { get; private set; }

    private async Task<CommunityHub.Core.Domain.Attendee?> LoadAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return null;
        var a = await _svc.ResolveByEmailAsync(me.EventId, me.Email, ct);
        if (a is null) { HasAttendeeRecord = false; return null; }
        HasAttendeeRecord = true;
        Eligible = a.TicketStatus == TicketStatus.TwoDay;
        var mine = await _svc.GetForAttendeeAsync(a.EventId, a.Id, ct);
        Confirmed = mine.FirstOrDefault(s => s.Status == MasterClassSignupStatus.Confirmed);
        Pending = mine.FirstOrDefault(s =>
            s.Status is MasterClassSignupStatus.Waitlisted or MasterClassSignupStatus.Offered);
        return a;
    }

    public async Task<IActionResult> OnGetAsync(string? msg, CancellationToken ct)
    {
        if (_participant.Current is null) return RedirectToPage("/Login");
        Message = msg;
        await LoadAsync(ct);
        return Page();
    }

    /// <summary>Leave the waitlist (or held offer) — frees nothing of yours but promotes the next person if you held a seat.</summary>
    public async Task<IActionResult> OnPostLeaveAsync(int sessionId, CancellationToken ct)
    {
        var a = await LoadAsync(ct);
        if (a is null) return Page();
        var title = Pending?.Title ?? "Master Class";

        var promo = await _svc.RemoveAsync(a.EventId, a.Id, sessionId, ct);
        if (promo?.PromotedSignupId is int id)
        {
            try { await _promo.SendPromotionAsync(id, BaseUrl, ct); }
            catch { /* removal stands even if the notify mail fails */ }
        }
        try { await _email.SendCancelledAsync(a.EventId, a.Email, a.FirstName, a.LastName, title, BaseUrl, a.Id, ct); }
        catch { /* removal stands even if the email fails */ }
        return RedirectToPage(new { msg = "You've left the waitlist." });
    }
}
