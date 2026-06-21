using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Email;

/// <summary>
/// Master Class lifecycle emails (REQUIREMENTS §6): confirmed-seat, waitlist
/// signup (terms included), and cancellation. Every email carries the self-service
/// link (give up a seat / leave a waitlist); the confirmed email also offers an
/// <c>.ics</c> download and notes the "remind me ~1 month before" option. All sends
/// go through the ring-gated <see cref="IEmailSender"/> (EmailLog-recorded).
/// Also builds the MC <c>.ics</c> reused by the self-service download + the
/// month-before reminder job.
/// </summary>
public sealed class MasterClassEmailService
{
    private const string Category = "masterclass";

    private readonly CommunityHubDbContext _db;
    private readonly IEmailSender _sender;
    private readonly IEmailContextAccessor _context;
    private readonly MasterClassSignupService _signups;

    public MasterClassEmailService(
        CommunityHubDbContext db, IEmailSender sender,
        IEmailContextAccessor context, MasterClassSignupService signups)
    {
        _db = db; _sender = sender; _context = context; _signups = signups;
    }

    private static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);

    /// <summary>RFC 5545 VEVENT for a master class — timed if the session has a start, else all-day on the edition pre-day.</summary>
    public static string BuildIcs(string host, int sessionId, string title, DateTimeOffset? start, DateTimeOffset? end, DateOnly editionStart)
    {
        string Z(DateTimeOffset d) => d.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
        var uid = $"mc-{sessionId}@{host}";
        var sb = new StringBuilder();
        sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//CommunityHub//MasterClass//EN\r\nMETHOD:PUBLISH\r\nBEGIN:VEVENT\r\n");
        sb.Append($"UID:{uid}\r\n");
        if (start is { } s)
        {
            sb.Append($"DTSTAMP:{Z(s)}\r\nDTSTART:{Z(s)}\r\n");
            sb.Append($"DTEND:{Z(end ?? s.AddHours(3))}\r\n");
        }
        else
        {
            var d = editionStart.ToString("yyyyMMdd");
            sb.Append($"DTSTAMP:{editionStart.ToDateTime(new TimeOnly(0, 0)):yyyyMMdd'T'000000'Z'}\r\n");
            sb.Append($"DTSTART;VALUE=DATE:{d}\r\n");
        }
        sb.Append($"SUMMARY:Master Class — {title}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        return sb.ToString();
    }

    private sealed record Ctx(MasterClassSignup S, string Email, string FirstName, string Title, int EventId);

    private async Task<Ctx?> LoadAsync(int signupId, CancellationToken ct)
    {
        var s = await _db.MasterClassSignups
            .Include(x => x.Attendee).Include(x => x.Session)
            .FirstOrDefaultAsync(x => x.Id == signupId, ct);
        if (s is null || string.IsNullOrWhiteSpace(s.Attendee.Email)) return null;
        var first = string.IsNullOrWhiteSpace(s.Attendee.FirstName) ? "there" : s.Attendee.FirstName;
        return new Ctx(s, s.Attendee.Email, first, s.Session.Title, s.EventId);
    }

    private async Task<string> SelfServiceUrlAsync(int attendeeId, string baseUrl, CancellationToken ct)
    {
        var token = await _signups.EnsureSelfServiceTokenAsync(attendeeId, ct);
        return $"{baseUrl.TrimEnd('/')}/MyMasterClass?t={token}";
    }

    private async Task SendAsync(string email, string name, int eventId, string subject, string html, CancellationToken ct)
    {
        using (_context.Set(new EmailContext(Category, eventId, null, name)))
            await _sender.SendAsync(email, subject, html, ct);
    }

    /// <summary>
    /// The tracked "Action required: choose your Master Class" selection invite to a
    /// 2-day-ticket attendee, with a secure self-service link. Ring-gated; stamps
    /// <see cref="Attendee.MasterClassInviteSentAt"/>. Skips if not eligible or already
    /// sent (unless <paramref name="force"/>). Returns true when an email went out.
    /// </summary>
    public async Task<bool> SendSelectionInviteAsync(
        int attendeeId, string baseUrl, bool force = false, CancellationToken ct = default)
    {
        var a = await _db.Attendees.Include(x => x.Event)
            .FirstOrDefaultAsync(x => x.Id == attendeeId, ct);
        if (a is null || a.TicketStatus != TicketStatus.TwoDay) return false;
        if (string.IsNullOrWhiteSpace(a.Email)) return false;
        if (a.MasterClassInviteSentAt is not null && !force) return false;

        var url = await SelfServiceUrlAsync(a.Id, baseUrl, ct);
        var fn = string.IsNullOrWhiteSpace(a.FirstName) ? "there" : a.FirstName;
        var ev = a.Event.DisplayName;
        var html =
            $"<p>Dear {Enc(fn)},</p>" +
            $"<p>Thank you for choosing a 2-day ticket with Master Class for <strong>{Enc(ev)}</strong>.</p>" +
            "<p><strong>Please note:</strong> your seat is not confirmed until you complete the selection below. " +
            "You'll be asked to choose a Master Class — once selected, your seat is secured.</p>" +
            $"<p style=\"margin:22px 0;\"><a href=\"{Enc(url)}\" style=\"display:inline-block;background:#1c6b35;color:#fff;text-decoration:none;font-weight:600;padding:12px 20px;border-radius:8px;\">Choose my Master Class</a></p>" +
            "<p>We recommend selecting a Master Class as soon as possible to secure your place — and if your " +
            "preferred class is already full, we suggest choosing another available one so you're guaranteed a " +
            "seat. Space is limited, so the sooner you register, the better. Simply log in with your email address.</p>" +
            "<h3>Waitlist</h3>" +
            "<p>Once a Master Class fills up, the waitlist option becomes available, allowing you to join the " +
            "waitlist for one alternative Master Class.</p>" +
            "<p><strong>Waitlist terms</strong></p><ul>" +
            "<li>You cannot join a waitlist for a Master Class while seats are still available.</li>" +
            "<li>By signing up for a waitlist, you accept that your current Master Class booking will be cancelled " +
            "and replaced by the waitlisted class if a confirmed seat becomes available.</li></ul>" +
            "<h3>Master Class self-service (Event Hub)</h3>" +
            "<p>Once you've secured a Master Class, you can log in to the Event Hub to:</p><ul>" +
            "<li>Cancel a Master Class</li><li>Sign up for waitlists</li>" +
            "<li>Ask questions to your Master Class speakers</li>" +
            "<li>Review preparation instructions for your class</li><li>Sync the event to your calendar</li></ul>" +
            "<p>The team</p>";

        await SendAsync(a.Email, $"{fn} {a.LastName}".Trim(), a.EventId,
            $"Action required: Choose your Master Class for {ev}", html, ct);

        a.MasterClassInviteSentAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Ticket-reassignment validation email (REQUIREMENTS §6): when a ticket is
    /// reassigned to a new person who has INHERITED the previous holder's Master
    /// Class, tell them what they hold + the secure self-service link + waitlist terms.
    /// Ring-gated. Returns true when sent.
    /// </summary>
    public async Task<bool> SendReassignmentValidationAsync(
        int attendeeId, string? inheritedMcTitle, string baseUrl, CancellationToken ct = default)
    {
        var a = await _db.Attendees.Include(x => x.Event)
            .FirstOrDefaultAsync(x => x.Id == attendeeId, ct);
        if (a is null || string.IsNullOrWhiteSpace(a.Email)) return false;

        var url = await SelfServiceUrlAsync(a.Id, baseUrl, ct);
        var fn = string.IsNullOrWhiteSpace(a.FirstName) ? "there" : a.FirstName;
        var ev = a.Event.DisplayName;
        var held = string.IsNullOrWhiteSpace(inheritedMcTitle)
            ? "<p>The ticket does not currently hold a Master Class — please choose one below.</p>"
            : $"<p>The ticket currently holds the following master class: <strong>{Enc(inheritedMcTitle)}</strong></p>";
        var html =
            $"<p>Dear {Enc(fn)},</p>" +
            $"<p>You have been re-assigned a 2-day ticket with Master Class for <strong>{Enc(ev)}</strong>.</p>" +
            held +
            "<p>You can choose to log in to the Event Hub to:</p><ul>" +
            "<li>Cancel a Master Class</li><li>Sign up for waitlists</li>" +
            "<li>Ask questions to your Master Class speakers</li>" +
            "<li>Review preparation instructions for your class</li><li>Sync the event to your calendar</li></ul>" +
            $"<p style=\"margin:22px 0;\"><a href=\"{Enc(url)}\" style=\"display:inline-block;background:#1c6b35;color:#fff;text-decoration:none;font-weight:600;padding:12px 20px;border-radius:8px;\">Review my Master Class</a></p>" +
            "<h3>Waitlist</h3>" +
            "<p>Once a Master Class fills up, the waitlist option becomes available, allowing you to join the " +
            "waitlist for one alternative Master Class.</p><ul>" +
            "<li>You cannot join a waitlist for a Master Class while seats are still available.</li>" +
            "<li>By signing up for a waitlist, you accept that your current Master Class booking will be cancelled " +
            "and replaced by the waitlisted class if a confirmed seat becomes available.</li></ul>" +
            "<p>The team</p>";

        await SendAsync(a.Email, $"{fn} {a.LastName}".Trim(), a.EventId,
            $"Action required: Validate your Master Class for {ev} (ticket re-assignment)", html, ct);
        return true;
    }

    /// <summary>Confirmed-seat email: self-service link, .ics download, month-before note.</summary>
    public async Task SendConfirmedAsync(int signupId, string baseUrl, CancellationToken ct = default)
    {
        var c = await LoadAsync(signupId, ct);
        if (c is null) return;
        var url = await SelfServiceUrlAsync(c.S.AttendeeId, baseUrl, ct);
        var ics = $"{baseUrl.TrimEnd('/')}/MyMasterClass.ics?t={await _signups.EnsureSelfServiceTokenAsync(c.S.AttendeeId, ct)}";
        var html =
            $"<p>Hi {Enc(c.FirstName)},</p>" +
            $"<p>Your Master Class place is <strong>confirmed</strong>: <strong>{Enc(c.Title)}</strong>. 🎉</p>" +
            $"<p><a href=\"{Enc(ics)}\">Add it to your calendar (.ics)</a>. On your " +
            $"<a href=\"{Enc(url)}\">self-service page</a> you can give up your seat if your plans change, " +
            "and choose to get a calendar reminder about a month before the event.</p>" +
            "<p>See you there,<br/>The team</p>";
        await SendAsync(c.Email, $"{c.FirstName} {c.S.Attendee.LastName}".Trim(), c.EventId,
            $"You're confirmed: {c.Title}", html, ct);
    }

    /// <summary>Waitlist-signup email — terms included; link to leave the waitlist.</summary>
    public async Task SendWaitlistedAsync(int signupId, string baseUrl, CancellationToken ct = default)
    {
        var c = await LoadAsync(signupId, ct);
        if (c is null) return;
        var url = await SelfServiceUrlAsync(c.S.AttendeeId, baseUrl, ct);
        var terms = c.S.AutoSwitchConsentAt is not null
            ? "<p><strong>You accepted:</strong> if a seat opens here, your current Master Class will be " +
              "cancelled and you'll be moved to this one automatically.</p>"
            : "";
        var html =
            $"<p>Hi {Enc(c.FirstName)},</p>" +
            $"<p>You're on the <strong>waitlist</strong> for <strong>{Enc(c.Title)}</strong>. " +
            "We'll email you if a seat opens up.</p>" + terms +
            $"<p>You can <a href=\"{Enc(url)}\">leave the waitlist</a> any time.</p>" +
            "<p>The team</p>";
        await SendAsync(c.Email, $"{c.FirstName} {c.S.Attendee.LastName}".Trim(), c.EventId,
            $"You're on the waitlist: {c.Title}", html, ct);
    }

    /// <summary>
    /// The ~1-month-before calendar reminder: a confirmed attendee who opted in gets
    /// an email carrying the master-class <c>.ics</c> attachment, and we stamp it sent.
    /// Ring-gated. Returns false if not eligible (not confirmed / not opted in / already sent).
    /// </summary>
    public async Task<bool> SendMonthReminderAsync(int signupId, string host, CancellationToken ct = default)
    {
        var s = await _db.MasterClassSignups
            .Include(x => x.Attendee).Include(x => x.Session).ThenInclude(se => se.Event)
            .FirstOrDefaultAsync(x => x.Id == signupId, ct);
        if (s is null || s.Status != MasterClassSignupStatus.Confirmed
            || !s.WantsMonthBeforeReminder || s.MonthReminderSentAt is not null) return false;
        if (string.IsNullOrWhiteSpace(s.Attendee.Email)) return false;

        var ics = BuildIcs(host, s.SessionId, s.Session.Title, s.Session.StartsAt, s.Session.EndsAt, s.Session.Event.StartDate);
        var fn = string.IsNullOrWhiteSpace(s.Attendee.FirstName) ? "there" : s.Attendee.FirstName;
        var html =
            $"<p>Hi {Enc(fn)},</p>" +
            $"<p>Your Master Class <strong>{Enc(s.Session.Title)}</strong> is coming up — here's the calendar " +
            "entry you asked us to send (attached).</p><p>See you there,<br/>The team</p>";
        using (_context.Set(new EmailContext(Category, s.EventId, null, $"{fn} {s.Attendee.LastName}".Trim())))
            await _sender.SendWithIcsAsync(s.Attendee.Email, $"Coming up: {s.Session.Title}", html, ics, "master-class.ics", ct);

        await _signups.MarkMonthReminderSentAsync(s.Id, ct);
        return true;
    }

    /// <summary>Cancellation email (the signup row is already gone, so details are passed in).</summary>
    public async Task SendCancelledAsync(
        int eventId, string email, string firstName, string lastName, string mcTitle,
        string baseUrl, int attendeeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        var url = await SelfServiceUrlAsync(attendeeId, baseUrl, ct);
        var fn = string.IsNullOrWhiteSpace(firstName) ? "there" : firstName;
        var html =
            $"<p>Hi {Enc(fn)},</p>" +
            $"<p>Your place in the Master Class <strong>{Enc(mcTitle)}</strong> has been <strong>cancelled</strong>.</p>" +
            $"<p>You can sign up for another Master Class on your <a href=\"{Enc(url)}\">self-service page</a> " +
            "(subject to availability).</p>" +
            "<p>The team</p>";
        await SendAsync(email, $"{fn} {lastName}".Trim(), eventId,
            $"Cancelled: {mcTitle}", html, ct);
    }
}
