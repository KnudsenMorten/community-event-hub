using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Email;

/// <summary>
/// Sends a person ONE calendar-invite email when they are activated
/// (REQUIREMENTS §5 — ".ics invite attachment on activation/assignment emails so
/// the event lands in their calendar"). The attachment is an RFC 5545 VEVENT for
/// the edition itself (an all-day entry spanning the event days) so a single tap
/// drops the event into the recipient's calendar; the body also points them at
/// their personal, always-fresh feed subscription (<c>GET /cal/{token}.ics</c>),
/// which carries their own deadlines/shifts/tasks.
///
/// Routing + safety reuse the established seams — there is no new mail path:
///  - <b>To</b> = calendar address (<see cref="SpeakerProfile.CalendarEmailFor"/>:
///    speaker <see cref="SpeakerProfile.CalendarEmail"/> ?? general
///    <see cref="SpeakerProfile.ContactEmailOverride"/> ?? identity);
///  - <b>DEV redirect / PROD allowlist</b> are applied downstream by the
///    <see cref="IEmailSender"/> implementation (all mail to mok@expertslive.dk in DEV);
///  - <b>idempotent</b> via the <see cref="SentReminder"/> ledger keyed on the
///    IDENTITY address, so a re-activation pass never re-sends the invite;
///  - <b>gated</b> on <see cref="Event.CalendarSyncEnabled"/> — when the organizer
///    has disabled calendar sync for the edition, no invite is sent.
/// </summary>
public sealed class CalendarInviteEmailService
{
    /// <summary>Reminder-ledger type for the one-shot activation calendar invite.</summary>
    public const string ReminderType = "calendar-invite";

    /// <summary>The single occasion key — one activation invite per person, ever.</summary>
    public const string OccasionKey = "activation";

    private readonly CommunityHubDbContext _db;
    private readonly IEmailSender _emailSender;
    private readonly IEmailContextAccessor _context;
    private readonly TimeProvider _clock;

    // Optional template provider for the activation invite body (the generic shipped
    // default is the fallback). Null in older test constructions → inline HTML.
    private readonly EmailTemplateProvider? _templates;

    public CalendarInviteEmailService(
        CommunityHubDbContext db,
        IEmailSender emailSender,
        IEmailContextAccessor context,
        TimeProvider clock,
        EmailTemplateProvider? templates = null)
    {
        _db = db;
        _emailSender = emailSender;
        _context = context;
        _clock = clock;
        _templates = templates;
    }

    /// <summary>
    /// Send the activation calendar invite to one participant if it has not been
    /// sent yet and the edition has calendar sync enabled. Returns true when an
    /// invite was actually sent, false when skipped (sync disabled, participant
    /// not active/found, or already sent). Safe to call repeatedly.
    /// </summary>
    public async Task<bool> SendActivationInviteAsync(
        int participantId, CancellationToken ct = default)
    {
        var p = await _db.Participants
            .Include(x => x.Event)
            .FirstOrDefaultAsync(x => x.Id == participantId, ct);
        if (p is null
            || !p.IsActive
            || p.LifecycleState != ParticipantLifecycleState.Active
            || p.Event is null
            || !p.Event.CalendarSyncEnabled)
        {
            return false;
        }

        // Idempotency: one activation invite per identity address, ever.
        var already = await _db.SentReminders.AnyAsync(
            s => s.EventId == p.EventId
                 && s.RecipientEmail == p.Email
                 && s.ReminderType == ReminderType
                 && s.OccasionKey == OccasionKey,
            ct);
        if (already) return false;

        // Calendar mail prefers the calendar-specific override (wizard step 1), then
        // the general contact override, then the Sessionize/identity address.
        var emailOverrides = await _db.SpeakerProfiles
            .Where(sp => sp.ParticipantId == p.Id)
            .Select(sp => new { sp.CalendarEmail, sp.ContactEmailOverride })
            .FirstOrDefaultAsync(ct);
        var toEmail = SpeakerProfile.CalendarEmailFor(
            p.Email, emailOverrides?.CalendarEmail, emailOverrides?.ContactEmailOverride);

        var ics = BuildEventInvite(p.Event, toEmail, p.FullName);

        var firstName = string.IsNullOrWhiteSpace(p.FullName)
            ? "there"
            : p.FullName.Split(' ')[0];

        string subject;
        string htmlBody;
        // No-FeatureKey EmailContext (unchanged): this transactional invite is governed
        // only by the global outbound kill switch, not a per-feature ring.
        using (_context.Set(new EmailContext(ReminderType, p.EventId, p.Id, p.FullName)))
        {
            if (_templates is not null)
            {
                // §169: addressed to a single known participant — pass the id so any
                // hub CTA ({{hubUrl}}) becomes their personal auto-login magic-link
                // (the ambient EmailContext above already carries p.Id; this makes it
                // explicit + robust). Fail-safe to the plain hub URL when not wired.
                var tokens = _templates.NewTokenSet(p.Id);
                tokens["firstName"] = firstName;
                tokens["eventDisplayName"] = p.Event.DisplayName;
                var rendered = _templates.Render("calendar-invite", tokens);
                subject = rendered.Subject;
                htmlBody = rendered.HtmlBody;
            }
            else
            {
                // Fallback: legacy inline HTML (older test constructions with no provider).
                var encName = System.Net.WebUtility.HtmlEncode(firstName);
                var encEvent = System.Net.WebUtility.HtmlEncode(p.Event.DisplayName);
                subject = $"{p.Event.DisplayName} — added to your calendar";
                htmlBody =
                    $"<p>Hi {encName},</p>" +
                    $"<p>Welcome aboard! We've attached a calendar invitation for " +
                    $"<strong>{encEvent}</strong> so the event lands straight in your calendar — " +
                    "just open the attachment and accept.</p>" +
                    "<p>For your personal deadlines, shifts and tasks, subscribe to your private " +
                    "calendar feed from your Event Hub — it stays in sync automatically as things " +
                    "change. Sign in and look for <em>\"Add to my calendar\"</em>.</p>" +
                    "<p>See you there,<br/>The team</p>";
            }

            // The .ics attachment stays — only the HTML body became a template.
            await _emailSender.SendWithIcsAsync(
                toEmail, subject, htmlBody, ics, "event.ics", ct);
        }

        _db.SentReminders.Add(new SentReminder
        {
            EventId = p.EventId,
            RecipientEmail = p.Email,   // idempotency keys on identity
            ReminderType = ReminderType,
            OccasionKey = OccasionKey,
            SentAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Build the RFC 5545 VEVENT for the edition itself: an all-day entry from the
    /// pre-day (or start date) through the end date. Stable UID per (event,
    /// participant) so a re-issue updates the existing entry rather than duplicating.
    /// </summary>
    private static string BuildEventInvite(Event ev, string ownerEmail, string ownerName)
    {
        var startDate = ev.PreDayDate ?? ev.StartDate;
        var start = new DateTimeOffset(
            startDate.Year, startDate.Month, startDate.Day, 0, 0, 0, TimeSpan.Zero);
        // All-day DTEND is exclusive — the day after the last event day.
        var end = new DateTimeOffset(
            ev.EndDate.Year, ev.EndDate.Month, ev.EndDate.Day, 0, 0, 0, TimeSpan.Zero)
            .AddDays(1);

        var description =
            $"You're part of {ev.DisplayName}.\n\n" +
            "Open your Event Hub for your personal deadlines, shifts and tasks, and " +
            "subscribe to your private calendar feed to keep them in sync.";

        var uid = $"event-{ev.Id}-{ownerEmail}@communityhub";
        return IcsCalendarBuilder.BuildVEvent(
            uid: uid,
            summary: ev.DisplayName,
            description: description,
            location: ev.VenueName ?? string.Empty,
            startUtc: start,
            endUtc: end,
            organizerEmail: ownerEmail,
            organizerName: ownerName);
    }
}
