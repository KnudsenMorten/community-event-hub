using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Attendee;

/// <summary>
/// The attendee "Master Class" page (in-hub; operator 2026-06-21) — REPLACES the old
/// Zoho-Bookings deep-link. A 2-day-ticket attendee chooses their Master Class here,
/// sees their confirmed seat (with the speaker comm-page link), and can give it up.
/// The waitlist view is a sibling page (/Attendee/Waitlist). The engine
/// is the CEH-owned <see cref="MasterClassSignupService"/> (same one the emailed
/// magic-link page /MyMasterClass uses); this page resolves the attendee by the
/// signed-in participant's email in the active edition.
/// </summary>
[Authorize]
public class IndexModel : PageModel
{
    private readonly MasterClassSignupService _svc;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly MasterClassPromotionEmailService _promo;
    private readonly MasterClassEmailService _email;
    private readonly MasterClassLogisticsService _logistics;

    public IndexModel(
        MasterClassSignupService svc,
        ICurrentParticipantAccessor participant,
        MasterClassPromotionEmailService promo,
        MasterClassEmailService email,
        MasterClassLogisticsService logistics)
    {
        _svc = svc;
        _participant = participant;
        _promo = promo;
        _email = email;
        _logistics = logistics;
    }

    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    /// <summary>False when no Attendee row matches the signed-in email in this edition.</summary>
    public bool HasAttendeeRecord { get; private set; }
    /// <summary>True only for a 2-day ticket holder (Master Class access).</summary>
    public bool Eligible { get; private set; }
    public string EventName { get; private set; } = string.Empty;
    public string? Message { get; private set; }

    /// <summary>The attendee's confirmed seat, if any.</summary>
    public MasterClassSignupService.MySignup? Confirmed { get; private set; }
    /// <summary>The attendee's waitlist place / held offer, if any (shown as a hint; managed on /Attendee/Waitlist).</summary>
    public MasterClassSignupService.MySignup? Pending { get; private set; }
    /// <summary>Whether the confirmed seat has opted into the ~1-month-before reminder.</summary>
    public bool MonthReminderOptIn => Confirmed?.WantsMonthReminder ?? false;
    /// <summary>The chooseable Master Classes with capacity / availability.</summary>
    public IReadOnlyList<MasterClassSignupService.McOption> Options { get; private set; }
        = Array.Empty<MasterClassSignupService.McOption>();
    /// <summary>The comm-page slug for the confirmed Master Class (link to /MasterClass/{slug}); null if none.</summary>
    public string? CommSlug { get; private set; }

    private async Task<CommunityHub.Core.Domain.Attendee?> LoadAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return null;
        var a = await _svc.ResolveByEmailAsync(me.EventId, me.Email, ct);
        if (a is null) { HasAttendeeRecord = false; return null; }
        HasAttendeeRecord = true;
        EventName = await _svc.EventNameAsync(a.EventId, ct);
        Eligible = a.TicketStatus == TicketStatus.TwoDay;
        var mine = await _svc.GetForAttendeeAsync(a.EventId, a.Id, ct);
        Confirmed = mine.FirstOrDefault(s => s.Status == MasterClassSignupStatus.Confirmed);
        Pending = mine.FirstOrDefault(s =>
            s.Status is MasterClassSignupStatus.Waitlisted or MasterClassSignupStatus.Offered);
        Options = await _svc.ListMasterClassesAsync(a.EventId, ct);
        if (Confirmed is not null)
        {
            // Mint (idempotently) the comm-page slug so we can link the attendee to the
            // speaker-published logistics page. Best-effort: a failure just hides the link.
            try { CommSlug = await _logistics.EnsureSlugAsync(a.EventId, Confirmed.SessionId, ct); }
            catch { CommSlug = null; }
        }
        return a;
    }

    private async Task NotifyAsync(MasterClassSignupService.PromotionResult? promo, CancellationToken ct)
    {
        if (promo?.PromotedSignupId is int id)
        {
            try { await _promo.SendPromotionAsync(id, BaseUrl, ct); }
            catch { /* promotion stands even if the notify mail fails; retryable */ }
        }
    }

    public async Task<IActionResult> OnGetAsync(string? msg, CancellationToken ct)
    {
        if (_participant.Current is null) return RedirectToPage("/Login");
        Message = msg;
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostJoinAsync(int sessionId, bool autoSwitchConsent, CancellationToken ct)
    {
        var a = await LoadAsync(ct);
        if (a is null) return Page();

        var r = await _svc.SignUpAsync(a.EventId, a.Id, sessionId, autoSwitchConsent, ct);
        if (!r.Ok) return RedirectToPage(new { msg = r.Error });

        var newSignupId = await _svc.SignupIdAsync(a.EventId, a.Id, sessionId, ct);
        if (newSignupId is int sid)
        {
            try
            {
                if (r.Signup!.Status == MasterClassSignupStatus.Confirmed)
                    await _email.SendConfirmedAsync(sid, BaseUrl, ct);
                else
                    await _email.SendWaitlistedAsync(sid, BaseUrl, ct);
            }
            catch { /* signup stands even if the email fails */ }
        }

        var status = r.Signup!.Status == MasterClassSignupStatus.Confirmed
            ? "You've got a seat 🎉"
            : $"You're on the waitlist (position {r.Signup.WaitlistPosition}). We'll let you know if a seat opens.";
        return RedirectToPage(new { msg = status });
    }

    public async Task<IActionResult> OnPostGiveUpAsync(int sessionId, CancellationToken ct)
    {
        var a = await LoadAsync(ct);
        if (a is null) return Page();
        var mcTitle = (Confirmed?.SessionId == sessionId ? Confirmed?.Title : Pending?.Title) ?? "Master Class";

        var promo = await _svc.RemoveAsync(a.EventId, a.Id, sessionId, ct);
        await NotifyAsync(promo, ct);
        try { await _email.SendCancelledAsync(a.EventId, a.Email, a.FirstName, a.LastName, mcTitle, BaseUrl, a.Id, ct); }
        catch { /* removal stands even if the email fails */ }
        return RedirectToPage(new { msg = "Done — your Master Class place was updated." });
    }

    /// <summary>"Add to my calendar" — the attendee's confirmed Master Class as an .ics.</summary>
    public async Task<IActionResult> OnGetIcsAsync(CancellationToken ct)
    {
        var a = await LoadAsync(ct);
        if (a is null || Confirmed is null) return NotFound();
        var s = await _svc.GetSessionForIcsAsync(a.EventId, Confirmed.SessionId, ct);
        if (s is null) return NotFound();
        var ics = MasterClassEmailService.BuildIcs(
            Request.Host.Host, Confirmed.SessionId, s.Title, s.StartsAt, s.EndsAt, s.EditionStart);
        // Inline (no filename) so the .ics opens in the OS calendar app.
        return File(System.Text.Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8");
    }

    /// <summary>Toggle the "remind me ~1 month before" calendar opt-in on the confirmed seat.</summary>
    public async Task<IActionResult> OnPostToggleReminderAsync(bool wants, CancellationToken ct)
    {
        var a = await LoadAsync(ct);
        if (a is null) return Page();
        await _svc.SetMonthReminderOptInAsync(a.EventId, a.Id, wants, ct);
        return RedirectToPage(new { msg = wants ? "We'll remind you about a month before." : "Reminder turned off." });
    }
}
