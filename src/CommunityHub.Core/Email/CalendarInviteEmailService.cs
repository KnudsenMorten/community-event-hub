using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Email;

/// <summary>
/// The ONE mechanism for putting something into a participant's calendar: it
/// e-mails them a real meeting INVITATION (an RFC 5545 <c>METHOD:REQUEST</c>
/// VEVENT attached to the mail) so a single tap drops the item into their
/// calendar — the same approach as the Appreciation Dinner / hotel invites.
///
/// REQUIREMENTS §193: this replaces every "Download .ics" / "Calendar sync"
/// button. Instead of streaming a file, the hub calls
/// <see cref="SendItemInviteAsync"/> for the task due date, session time, etc.
/// and the recipient receives a calendar invitation in their inbox. There is no
/// .ics download and no subscribable feed any more.
///
/// Routing + safety reuse the established seams — there is no new mail path:
///  - <b>To</b> = calendar address (<see cref="SpeakerProfile.CalendarEmailFor"/>:
///    speaker <see cref="SpeakerProfile.CalendarEmail"/> ?? general
///    <see cref="SpeakerProfile.ContactEmailOverride"/> ?? identity) — so the
///    invite honours the participant's chosen calendar / override e-mail;
///  - <b>DEV redirect / PROD allowlist</b> are applied downstream by the
///    <see cref="IEmailSender"/> implementation;
///  - <b>gated</b> on <see cref="Event.CalendarSyncEnabled"/> — when the organizer
///    has disabled calendar features for the edition, no invite is sent.
/// </summary>
/// <summary>
/// §432 — the outcome of one calendar-invite send, carrying the address it actually went to.
///
/// <para>Operator 2026-07-27: <i>"can you show which email account invitation was sent to as an
/// extra service"</i>. Every one of the eleven send sites reported the same fixed sentence,
/// <i>"check your inbox"</i> — and "your inbox" is precisely the thing in question: calendar mail
/// prefers the calendar override, then the speaker contact override, then the identity address
/// (<see cref="SpeakerProfile.CalendarEmailFor"/>). Someone with an alternate address set (§422)
/// was told to check an inbox we had not necessarily used, with no way to tell which.</para>
///
/// <para>The resolution happens INSIDE the send, so the address is returned from there rather than
/// re-derived by each caller — two copies of that precedence chain would be free to drift, and the
/// message would then confidently name the wrong mailbox.</para>
///
/// <para>Implicitly converts to <see cref="bool"/> so the existing <c>result ? … : …</c> call sites
/// keep their meaning unchanged; only the ones that want the address need to look at it.</para>
/// </summary>
/// <param name="Sent">True when an invite was actually sent.</param>
/// <param name="ToEmail">The address it was sent to; null when nothing was sent.</param>
public readonly record struct CalendarInviteResult(bool Sent, string? ToEmail)
{
    public static implicit operator bool(CalendarInviteResult r) => r.Sent;

    /// <summary>Nothing was sent (participant missing, or calendar sync disabled for the edition).</summary>
    public static CalendarInviteResult NotSent => new(false, null);

    /// <summary>
    /// The user-facing confirmation, naming the mailbox. One builder for all eleven sites so the
    /// wording cannot drift apart again.
    /// </summary>
    /// <param name="noun">"Invite" or "Reminder" — what the button the person pressed calls it.</param>
    public string Confirmation(string noun = "Invite") =>
        string.IsNullOrWhiteSpace(ToEmail)
            ? $"{noun} sent — check your inbox for the calendar invitation."
            : $"{noun} sent to {ToEmail} — check that inbox for the calendar invitation.";
}

public sealed class CalendarInviteEmailService
{
    /// <summary>EmailContext type tag for calendar-invite sends.</summary>
    public const string ReminderType = "calendar-invite";

    private readonly CommunityHubDbContext _db;
    private readonly IEmailSender _emailSender;
    private readonly IEmailContextAccessor _context;
    private readonly TimeProvider _clock;

    // §234 6: the hub's from-address is the invite ORGANIZER (mail clients only process
    // a METHOD:REQUEST as a real invitation when the organizer is the SENDER, not the
    // recipient). Optional so legacy test constructions keep compiling → defaults.
    private readonly EmailOptions _emailOptions;

    public CalendarInviteEmailService(
        CommunityHubDbContext db,
        IEmailSender emailSender,
        IEmailContextAccessor context,
        TimeProvider clock,
        IOptions<EmailOptions>? emailOptions = null)
    {
        _db = db;
        _emailSender = emailSender;
        _context = context;
        _clock = clock;
        _emailOptions = emailOptions?.Value ?? new EmailOptions();
    }

    /// <summary>
    /// E-mail one participant a calendar INVITATION for a single item (a task due
    /// date, a session, a key date, a party, a master class, …). Returns true when
    /// an invite was actually sent, false when skipped (participant not found, the
    /// edition has calendar sync disabled). Safe to call repeatedly — the invite
    /// carries a stable <paramref name="uid"/> so a re-send UPDATES the existing
    /// calendar entry rather than duplicating it.
    /// </summary>
    /// <param name="participantId">The signed-in participant to invite.</param>
    /// <param name="uid">Stable per-item UID (e.g. <c>task-42</c>) — re-sends update.</param>
    /// <param name="summary">Calendar entry title.</param>
    /// <param name="description">Calendar entry body / notes.</param>
    /// <param name="location">Optional location.</param>
    /// <param name="start">Start (UTC). For <paramref name="allDay"/> the date is used.</param>
    /// <param name="end">End (UTC, exclusive for all-day).</param>
    /// <param name="allDay">True for an all-day entry (e.g. a deadline reminder).</param>
    /// <param name="fileName">Attachment file name, e.g. <c>reminder.ics</c>.</param>
    /// <param name="introHtml">Optional lead sentence for the e-mail body.</param>
    /// <param name="mailKey">
    /// §705.15a — WHICH calendar mail this is, for the Settings registry: <c>calendar-activation</c>,
    /// <c>calendar-dinner</c> or <c>hotel-calendar-selfsend</c>. Defaults to the legacy generic
    /// <c>calendar-invite</c> so an un-migrated caller keeps working.
    /// </summary>
    /// <remarks>
    /// 🔒 All of these are USER-INITIATED and therefore ring-EXEMPT, so this parameter does NOT change
    /// who receives anything. Its only job is to stop ONE row on the Settings page standing for three
    /// unrelated mails — the same generic-naming defect as <c>hotel-invite</c> (§705.15) and
    /// <c>graphics-release</c> (§705.14a).
    /// </remarks>
    public async Task<CalendarInviteResult> SendItemInviteAsync(
        int participantId,
        string uid,
        string summary,
        string description,
        string? location,
        DateTimeOffset start,
        DateTimeOffset end,
        bool allDay,
        string fileName,
        string? introHtml = null,
        CancellationToken ct = default,
        string mailKey = "calendar-invite")
    {
        var p = await _db.Participants
            .Include(x => x.Event)
            .FirstOrDefaultAsync(x => x.Id == participantId, ct);
        if (p is null || p.Event is null || !p.Event.CalendarSyncEnabled)
        {
            return CalendarInviteResult.NotSent;
        }

        // Calendar mail prefers the calendar-specific override (wizard step 1), then
        // the general contact override, then the Sessionize/identity address.
        var emailOverrides = await _db.SpeakerProfiles
            .Where(sp => sp.ParticipantId == p.Id)
            .OrderBy(sp => sp.Id)
            .Select(sp => new { sp.CalendarEmail, sp.ContactEmailOverride })
            .FirstOrDefaultAsync(ct);
        var toEmail = SpeakerProfile.CalendarEmailFor(
            p.Email, emailOverrides?.CalendarEmail, emailOverrides?.ContactEmailOverride);

        // §234 6: a REAL invitation — ORGANIZER = the hub's from-address (the sender),
        // ATTENDEE = the recipient. The old code put the RECIPIENT as organizer, which
        // calendar clients refuse to process as an invite (you can't be invited by
        // yourself), leaving the .ics an inert attachment.
        var ics = IcsCalendarBuilder.BuildVEvent(
            uid: uid,
            summary: summary,
            description: description,
            location: location ?? string.Empty,
            startUtc: start,
            endUtc: end,
            organizerEmail: _emailOptions.FromAddress,
            organizerName: _emailOptions.FromDisplayName,
            allDay: allDay,
            attendeeEmail: toEmail,
            attendeeName: p.FullName);

        var firstName = string.IsNullOrWhiteSpace(p.FullName)
            ? "there"
            : p.FullName.Split(' ')[0];
        var encName = System.Net.WebUtility.HtmlEncode(firstName);
        var encSummary = System.Net.WebUtility.HtmlEncode(summary);
        var lead = string.IsNullOrWhiteSpace(introHtml)
            ? $"Here is a calendar invitation for <strong>{encSummary}</strong>."
            : introHtml;
        // §378 (operator 2026-07-26: "the text been sent as calendar invite includes this old text -
        // remove it 'Your calendar invitation is attached — open it to add this to your calendar.'
        // … this is the old ics wording"). What we send is a METHOD:REQUEST VEVENT, which Outlook,
        // Gmail and Apple Mail render as a NATIVE invitation with Accept/Decline — there is no file
        // to "open", so the sentence described a behaviour the recipient never sees. The lead
        // sentence already says what the mail is; nothing replaces the removed line.
        var htmlBody =
            $"<p>Hi {encName},</p>" +
            $"<p>{lead}</p>" +
            $"<p>See you there,<br/>{_emailOptions.EventCode} organizer team</p>";

        // §326ca — RING-EXEMPT, and now stated EXPLICITLY instead of by omission.
        //
        // The audit asked for a ring gate on every send; this is the deliberate exception,
        // and the reason got stronger since it was written: §322n dropped the automatic
        // invite on RSVP=Yes and §326g replaced it with an explicit "📅 Email me a calendar
        // invite" BUTTON. The only way to reach this code is a person pressing a button
        // asking for THEIR OWN invite. Ring-gating that would mean a ring-3 participant
        // clicks the button and silently receives nothing — the same "locked out by a
        // rollout ring" failure the PIN sign-in mail is exempted from (§326av).
        //
        // Marking it RingExempt rather than leaving FeatureKey null IS the fix: every send
        // is now either ring-gated or explicitly exempt with a reason. "No key" must never
        // again be able to mean "nobody classified this".
        // §705.15a — TemplateName names WHICH calendar mail this is (activation / dinner / hotel
        // self-send) so the Settings page stops showing one row for three. RingExempt is unchanged:
        // all three are user-initiated, so the identity affects the page, never the audience.
        using (_context.Set(new EmailContext(
            ReminderType, p.EventId, p.Id, p.FullName,
            TemplateName: mailKey, RingExempt: true)))
        {
            await _emailSender.SendWithIcsAsync(toEmail, summary, htmlBody, ics, fileName, ct);
        }

        // §432: the resolved address travels back with the outcome, so the confirmation can name
        // the mailbox we actually used rather than the one the person assumes.
        return new CalendarInviteResult(true, toEmail);
    }
}
