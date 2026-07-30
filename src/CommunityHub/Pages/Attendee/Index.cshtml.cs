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

    // §341-1: the standard §193 invite sender behind the "Send Calendar invite for Master
    // Class" button. Optional + last so existing unit tests keep compiling (the §316 pattern);
    // DI always injects it in production. Null ⇒ the button is not offered at all.
    private readonly Core.Email.CalendarInviteEmailService? _calendarInvite;
    private readonly ILogger<IndexModel>? _log;

    public IndexModel(
        MasterClassSignupService svc,
        ICurrentParticipantAccessor participant,
        MasterClassPromotionEmailService promo,
        MasterClassEmailService email,
        MasterClassLogisticsService logistics,
        Core.Email.CalendarInviteEmailService? calendarInvite = null,
        ILogger<IndexModel>? log = null)
    {
        _svc = svc;
        _participant = participant;
        _promo = promo;
        _email = email;
        _logistics = logistics;
        _calendarInvite = calendarInvite;
        _log = log;
    }

    /// <summary>
    /// §341-1 — whether to offer the manual "Send Calendar invite for Master Class" button.
    ///
    /// <para><b>Why this button exists.</b> The confirmation mail used to carry the Google /
    /// Outlook "add it online" links, which were the deliberate fallback for
    /// <c>AutoCalendarInvitesEnabled = false</c> (the migration default, and prod's state).
    /// The operator had that paragraph removed (§341-4) because the mail must not talk about
    /// calendar invites at all — so without this button an attendee would have NO way to get
    /// the Master Class into their calendar.</para>
    /// </summary>
    public bool CanSendCalendarInvite => Confirmed is not null && _calendarInvite is not null;

    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    /// <summary>False when no Attendee row matches the signed-in email in this edition.</summary>
    public bool HasAttendeeRecord { get; private set; }
    /// <summary>True only for a 2-day ticket holder (Master Class access).</summary>
    public bool Eligible { get; private set; }
    public string EventName { get; private set; } = string.Empty;
    public string? Message { get; private set; }
    /// <summary>"success" (default) or "error" — drives the _Flash kind so a FAILED
    /// signup renders as a red ⚠ error, not a green ✓ success toast (§234 UX).</summary>
    public string MessageKind { get; private set; } = "success";

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
            try { await _promo.SendPromotionAsync(id, BaseUrl, ct, promo.ReleasedTitle); }
            catch { /* promotion stands even if the notify mail fails; retryable */ }
        }
    }

    public async Task<IActionResult> OnGetAsync(string? msg, string? kind, CancellationToken ct)
    {
        if (_participant.Current is null) return RedirectToPage("/Login");
        Message = msg;
        MessageKind = string.Equals(kind, "error", StringComparison.OrdinalIgnoreCase) ? "error" : "success";
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostJoinAsync(int sessionId, bool autoSwitchConsent, CancellationToken ct)
    {
        var a = await LoadAsync(ct);
        if (a is null) return Page();

        var r = await _svc.SignUpAsync(a.EventId, a.Id, sessionId, autoSwitchConsent, ct);
        // §234 UX: a FAILED signup must render as an error toast with the engine's actual
        // message — it previously redirected with only msg, which the page showed as a
        // green ✓ success flash.
        if (!r.Ok) return RedirectToPage(new { msg = r.Error, kind = "error" });

        var newSignupId = await _svc.SignupIdAsync(a.EventId, a.Id, sessionId, ct);
        if (newSignupId is int sid)
        {
            try
            {
                if (r.Signup!.Status == MasterClassSignupStatus.Confirmed)
                    await _email.SendConfirmedAsync(sid, BaseUrl, ct);
                else
                    // Carry the attendee's queue position into the email ("You are #N on the waitlist").
                    await _email.SendWaitlistedAsync(sid, BaseUrl, r.Signup.WaitlistPosition, ct);
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

    /// <summary>Toggle the "remind me ~1 month before" calendar opt-in on the confirmed seat.</summary>
    public async Task<IActionResult> OnPostToggleReminderAsync(bool wants, CancellationToken ct)
    {
        var a = await LoadAsync(ct);
        if (a is null) return Page();
        await _svc.SetMonthReminderOptInAsync(a.EventId, a.Id, wants, ct);
        return RedirectToPage(new { msg = wants ? "We'll remind you about a month before." : "Reminder turned off." });
    }

    /// <summary>
    /// §341-1 — e-mail the attendee a calendar invitation for their confirmed Master Class day.
    ///
    /// <para>Uses the standard §193 <see cref="Core.Email.CalendarInviteEmailService"/> path (the
    /// same one the hotel/dinner buttons use, §322n), and deliberately reuses
    /// <see cref="MasterClassEmailService.MasterClassDayWindow"/> and
    /// <see cref="MasterClassEmailService.MasterClassInviteUid"/> rather than re-stating the day
    /// or the UID: the SAME uid means a re-send UPDATES the attendee's existing calendar entry
    /// instead of adding a duplicate, and the shared window means this invite can never disagree
    /// with the one the confirmation mail attaches when auto-invites are on.</para>
    /// </summary>
    public async Task<IActionResult> OnPostCalendarInviteAsync(CancellationToken ct)
    {
        var a = await LoadAsync(ct);
        if (a is null) return Page();

        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (Confirmed is null || _calendarInvite is null)
            return RedirectToPage(new { msg = "You need a confirmed Master Class seat first." , kind = "error" });

        // The window comes from the SAME service that builds the auto-attached invite, resolved
        // through the edition timezone — so the two can never disagree.
        var (startUtc, endUtc) = _email.MasterClassDayWindowUtc();

        try
        {
            var host = Request.Host.Host;
            var sent = await _calendarInvite.SendItemInviteAsync(
                me.ParticipantId,
                uid: MasterClassEmailService.MasterClassInviteUid(Confirmed.SessionId, host),
                summary: $"{EventName} — {Confirmed.Title}",
                description: "Your Master Class. Registration & breakfast open at 07:00 — "
                    + "come early so we can check everyone in; the class itself runs 09:00–16:00.",
                location: string.Empty,
                start: startUtc,
                end: endUtc,
                allDay: false,
                fileName: "master-class.ics",
                introHtml: "Here is your calendar invitation for your Master Class.",
                ct: ct);

            return RedirectToPage(sent
                ? new { msg = sent.Confirmation(), kind = "success" }
                : new { msg = "Calendar invitations are turned off for this event.", kind = "error" });
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex,
                "Attendee Master Class calendar invite failed for participant {Pid}.", me.ParticipantId);
            return RedirectToPage(new
            {
                msg = "We couldn't send the invite just now — please try again later.",
                kind = "error",
            });
        }
    }
}
