using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages;

/// <summary>
/// Attendee Master Class self-service (REQUIREMENTS §6). Reached EITHER by the
/// emailed magic-link <c>/MyMasterClass?t=&lt;token&gt;</c> (a per-attendee bearer
/// token — no password) OR by a normally signed-in attendee (resolved by their
/// email in the active edition). A 2-day-ticket attendee can join a Master Class /
/// its waitlist, see their status, and give up a confirmed seat (which promotes the
/// next waitlisted attendee + sends them the ring-gated promotion email).
/// </summary>
[AllowAnonymous]
public class MyMasterClassModel : PageModel
{
    private readonly MasterClassSignupService _svc;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly MasterClassPromotionEmailService _promo;
    private readonly MasterClassEmailService _email;

    public MyMasterClassModel(
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

    public string Token { get; private set; } = string.Empty;
    public bool InvalidLink { get; private set; }
    public bool Eligible { get; private set; }
    public string AttendeeName { get; private set; } = string.Empty;
    public string EventName { get; private set; } = string.Empty;
    public string? Message { get; private set; }
    public string? ErrorMessage { get; private set; }

    // --- §140 inline flash placement -------------------------------------------------
    // A confirmation/error after a POST-redirect (PRG) is shown NEXT TO the action that
    // produced it instead of as a faint top banner. Each handler tags its redirect with
    // the session it concerns (FlashSessionId) and/or a named scope (FlashScope, e.g.
    // "reminder"); the page renders the message inline at that spot. FlashIsError styles it.
    /// <summary>Named placement hint for a flash with no session (e.g. "reminder").</summary>
    public string? FlashScope { get; private set; }
    /// <summary>The Master Class the flash concerns, so it renders inside that card.</summary>
    public int? FlashSessionId { get; private set; }
    /// <summary>True when the flash is an error (red), false for a success (green).</summary>
    public bool FlashIsError { get; private set; }
    /// <summary>Whether there is any flash message to show at all.</summary>
    public bool HasFlash => !string.IsNullOrEmpty(Message);
    /// <summary>Flash belongs by the ~1-month reminder toggle (in the confirmed card).</summary>
    public bool FlashAtReminder => FlashScope == "reminder" && Confirmed is not null;
    /// <summary>Flash belongs at the top of the confirmed-seat card (e.g. a switch landed here).</summary>
    public bool FlashAtConfirmed =>
        !FlashAtReminder && Confirmed is not null && FlashSessionId == Confirmed.SessionId;
    /// <summary>
    /// True when the flash WILL be rendered inline somewhere on the page (so the top
    /// fallback can stay hidden). Inline spots: the reminder toggle, the confirmed card,
    /// a section-1 waitlist card, or a section-2 option card.
    /// </summary>
    public bool FlashPlacedInline =>
        HasFlash && (FlashAtReminder || FlashAtConfirmed
            || (FlashSessionId is int sid
                && (Waitlists.Any(w => w.SessionId == sid)
                    || Options.Any(o => o.SessionId == sid && Confirmed?.SessionId != sid))));

    /// <summary>The attendee's confirmed seat, if any (§139 section 1).</summary>
    public MasterClassSignupService.MySignup? Confirmed { get; private set; }
    /// <summary>The attendee's waitlist place(s) / any held offer (§139 section 1).</summary>
    public IReadOnlyList<MasterClassSignupService.MySignup> Waitlists { get; private set; }
        = Array.Empty<MasterClassSignupService.MySignup>();
    /// <summary>First waitlist/offer, kept for the legacy offer handlers.</summary>
    public MasterClassSignupService.MySignup? Pending => Waitlists.Count > 0 ? Waitlists[0] : null;
    /// <summary>Whether the confirmed seat has opted into the ~1-month-before reminder.</summary>
    public bool MonthReminderOptIn => Confirmed?.WantsMonthReminder ?? false;
    /// <summary>ALL master classes with live availability (§139 section 2).</summary>
    public IReadOnlyList<MasterClassSignupService.McOption> Options { get; private set; }
        = Array.Empty<MasterClassSignupService.McOption>();

    private async Task<CommunityHub.Core.Domain.Attendee?> LoadAsync(string? t, CancellationToken ct)
    {
        Token = t ?? string.Empty;
        // Resolve by the emailed token first; otherwise a normally signed-in attendee.
        var a = await _svc.ResolveByTokenAsync(Token, ct);
        if (a is null && _participant.Current is { } me)
            a = await _svc.ResolveByEmailAsync(me.EventId, me.Email, ct);
        if (a is null) { InvalidLink = true; return null; }
        AttendeeName = $"{a.FirstName} {a.LastName}".Trim();
        EventName = await _svc.EventNameAsync(a.EventId, ct);
        Eligible = a.TicketStatus == TicketStatus.TwoDay;
        var mine = await _svc.GetForAttendeeAsync(a.EventId, a.Id, ct);
        Confirmed = mine.FirstOrDefault(s => s.Status == MasterClassSignupStatus.Confirmed);
        Waitlists = mine.Where(s => s.Status is MasterClassSignupStatus.Waitlisted or MasterClassSignupStatus.Offered)
            .OrderBy(s => s.WaitlistPosition ?? int.MaxValue).ToList();
        Options = await _svc.ListMasterClassesAsync(a.EventId, ct);
        return a;
    }

    private async Task NotifyAsync(MasterClassSignupService.PromotionResult? promo, CancellationToken ct)
    {
        if (promo?.PromotedSignupId is int id)
        {
            try { await _promo.SendPromotionAsync(id, $"{Request.Scheme}://{Request.Host}", ct); }
            catch { /* promotion stands even if the notify mail fails; retryable */ }
        }
    }

    public async Task<IActionResult> OnGetAsync(
        string? t, string? msg, string? scope, int? sid, bool err, CancellationToken ct)
    {
        Message = msg;
        FlashScope = scope;
        FlashSessionId = sid;
        FlashIsError = err;
        await LoadAsync(t, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostJoinAsync(string? t, int sessionId, bool autoSwitchConsent, CancellationToken ct)
    {
        var a = await LoadAsync(t, ct);
        if (a is null) return Page();

        var r = await _svc.SignUpAsync(a.EventId, a.Id, sessionId, autoSwitchConsent, ct);
        if (!r.Ok) return RedirectToPage(new { t = Token, msg = r.Error, sid = sessionId, err = true });

        // Confirmation / waitlist-with-terms email.
        var newSignupId = await _svc.SignupIdAsync(a.EventId, a.Id, sessionId, ct);
        if (newSignupId is int sid)
        {
            try
            {
                if (r.Signup!.Status == MasterClassSignupStatus.Confirmed)
                    await _email.SendConfirmedAsync(sid, BaseUrl, ct);
                else
                    // Carry the attendee's queue position (from GetForAttendeeAsync via the
                    // SignUp result) into the email so they see "You are #N on the waitlist".
                    await _email.SendWaitlistedAsync(sid, BaseUrl, r.Signup.WaitlistPosition, ct);
            }
            catch { /* signup stands even if the email fails */ }
        }

        var status = r.Signup!.Status == MasterClassSignupStatus.Confirmed
            ? "You've got a seat 🎉"
            : $"You're on the waitlist (position {r.Signup.WaitlistPosition}). We'll let you know if a seat opens.";
        return RedirectToPage(new { t = Token, msg = status, sid = sessionId });
    }

    public async Task<IActionResult> OnPostGiveUpAsync(string? t, int sessionId, CancellationToken ct)
    {
        var a = await LoadAsync(t, ct);
        if (a is null) return Page();
        var mcTitle = (Confirmed?.SessionId == sessionId ? Confirmed?.Title : Pending?.Title) ?? "Master Class";

        var promo = await _svc.RemoveAsync(a.EventId, a.Id, sessionId, ct);
        await NotifyAsync(promo, ct);                       // notify whoever got the freed seat
        try { await _email.SendCancelledAsync(a.EventId, a.Email, a.FirstName, a.LastName, mcTitle, BaseUrl, a.Id, ct); }
        catch { /* removal stands even if the email fails */ }
        // The row is now gone; the freed class still shows in section 2, so anchor the
        // confirmation to its card (sid) — right where the give-up button was.
        return RedirectToPage(new { t = Token, msg = "Done — your Master Class place was updated.", sid = sessionId });
    }

    /// <summary>
    /// §139: ATOMICALLY switch the attendee's confirmed seat to <paramref name="sessionId"/>.
    /// The pre-submit warning (cshtml <c>confirm()</c>) is the UX guard; this is the safety
    /// net — if the class filled during the session the service refuses and the attendee
    /// keeps their current seat (<see cref="MasterClassSignupService.NowFullError"/>).
    /// </summary>
    public async Task<IActionResult> OnPostSwitchAsync(string? t, int sessionId, CancellationToken ct)
    {
        var a = await LoadAsync(t, ct);
        if (a is null) return Page();

        var hadSeat = Confirmed is not null;
        var oldTitle = Confirmed?.Title;
        var (ok, err, freed) = await _svc.SwitchAsync(a.EventId, a.Id, sessionId, ct);
        if (!ok) return RedirectToPage(new { t = Token, msg = err, sid = sessionId, err = true });

        await NotifyAsync(freed, ct);   // notify whoever got the seat we released
        var newTitle = Options.FirstOrDefault(o => o.SessionId == sessionId)?.Title ?? "your new Master Class";
        try
        {
            var newSignupId = await _svc.SignupIdAsync(a.EventId, a.Id, sessionId, ct);
            if (newSignupId is int sid) await _email.SendConfirmedAsync(sid, BaseUrl, ct);
            if (hadSeat && !string.IsNullOrEmpty(oldTitle))
                await _email.SendCancelledAsync(a.EventId, a.Email, a.FirstName, a.LastName, oldTitle, BaseUrl, a.Id, ct);
        }
        catch { /* the switch stands even if a mail fails */ }

        // The target is now the confirmed seat (section 1) — anchor the success there.
        return RedirectToPage(new { t = Token, msg = $"Switched — you've got a seat in {newTitle}. 🎉", sid = sessionId });
    }

    /// <summary>"Add to my calendar" — the attendee's confirmed Master Class as an .ics.</summary>
    public async Task<IActionResult> OnGetIcsAsync(string? t, CancellationToken ct)
    {
        var a = await LoadAsync(t, ct);
        if (a is null || Confirmed is null) return NotFound();
        var s = await _svc.GetSessionForIcsAsync(a.EventId, Confirmed.SessionId, ct);
        if (s is null) return NotFound();
        var ics = MasterClassEmailService.BuildIcs(
            Request.Host.Host, Confirmed.SessionId, s.Title, s.StartsAt, s.EndsAt, s.EditionStart);
        // Inline (no filename) so it opens in the calendar app rather than downloading.
        return File(System.Text.Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8");
    }

    /// <summary>Toggle the "remind me ~1 month before" calendar opt-in on the confirmed seat.</summary>
    public async Task<IActionResult> OnPostToggleReminderAsync(string? t, bool wants, CancellationToken ct)
    {
        var a = await LoadAsync(t, ct);
        if (a is null) return Page();
        await _svc.SetMonthReminderOptInAsync(a.EventId, a.Id, wants, ct);
        // Anchor next to the reminder toggle in the confirmed card.
        return RedirectToPage(new { t = Token, scope = "reminder",
            msg = wants ? "We'll remind you about a month before." : "Reminder turned off." });
    }

    public async Task<IActionResult> OnPostAcceptAsync(string? t, CancellationToken ct)
    {
        var a = await LoadAsync(t, ct);
        if (a is null) return Page();
        var (ok, err, freed) = await _svc.AcceptOfferAsync(a.EventId, a.Id, ct);
        await NotifyAsync(freed, ct);   // notify whoever got the seat you released
        return RedirectToPage(new { t = Token, msg = ok ? "Switched — you've taken the new Master Class." : err });
    }

    public async Task<IActionResult> OnPostDeclineAsync(string? t, CancellationToken ct)
    {
        var a = await LoadAsync(t, ct);
        if (a is null) return Page();
        var promo = await _svc.DeclineOfferAsync(a.EventId, a.Id, ct);
        await NotifyAsync(promo, ct);
        return RedirectToPage(new { t = Token, msg = "You kept your current Master Class; the offer was passed on." });
    }
}
