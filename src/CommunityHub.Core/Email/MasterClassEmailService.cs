using System.Text;
using CommunityHub.Core.Config;
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

    private const string DefaultSupportEmail = "info@expertslive.dk";

    private readonly CommunityHubDbContext _db;
    private readonly IEmailSender _sender;
    private readonly IEmailContextAccessor _context;
    private readonly MasterClassSignupService _signups;

    // Optional template provider for the selection-invite (the operator's exact copy
    // lives in the private config template; the generic shipped default is the
    // fallback). Null in older test constructions → falls back to the inline HTML.
    private readonly EmailTemplateProvider? _templates;

    // Optional edition-config source for the support email shown in the contact
    // line. Null in older test constructions → falls back to the default.
    private readonly EventEditionConfigLoader? _eventConfigLoader;
    private readonly EventConfigOptions? _eventConfigOptions;
    private string? _supportEmail;

    public MasterClassEmailService(
        CommunityHubDbContext db, IEmailSender sender,
        IEmailContextAccessor context, MasterClassSignupService signups,
        EmailTemplateProvider? templates = null,
        EventEditionConfigLoader? eventConfigLoader = null,
        EventConfigOptions? eventConfigOptions = null)
    {
        _db = db; _sender = sender; _context = context; _signups = signups;
        _templates = templates;
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
    }

    private static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);

    /// <summary>Resolve (once, cached) the support email from edition config, else the default.</summary>
    private string SupportEmail()
    {
        if (_supportEmail is not null) return _supportEmail;
        _supportEmail = ResolveSupportEmail(_eventConfigLoader, _eventConfigOptions);
        return _supportEmail;
    }

    /// <summary>The "Questions? Email …" contact line for the inline master-class bodies.</summary>
    private string ContactLine() =>
        $"<p>Questions? Email {Enc(SupportEmail())}.</p>";

    internal static string ResolveSupportEmail(
        EventEditionConfigLoader? loader, EventConfigOptions? options)
    {
        if (loader is null) return DefaultSupportEmail;
        try
        {
            var path = options?.EventConfigPath ?? new EventConfigOptions().EventConfigPath;
            var cfg = loader.Load(path);
            if (cfg.Placeholders is not null
                && cfg.Placeholders.TryGetValue("supportEmail", out var se)
                && !string.IsNullOrWhiteSpace(se))
            {
                return se;
            }
        }
        catch { /* fail-safe to default */ }
        return DefaultSupportEmail;
    }

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
        using (_context.Set(new EmailContext(Category, eventId, null, name, FeatureKey: "masterclass-invites")))
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

        string subject;
        string html;
        if (_templates is not null)
        {
            // Render the masterclass-selection-invite template (private config copy
            // wins; generic shipped default is the fallback). The renderer encodes
            // the tokens at the seam, so no per-value Enc(...) here. The .ics/branding
            // tokens are seeded by NewTokenSet(); selectionUrl carries the secure link.
            var tokens = _templates.NewTokenSet();
            tokens["firstName"] = fn;
            tokens["eventDisplayName"] = ev;
            tokens["selectionUrl"] = url;
            using (_context.Set(new EmailContext(Category, a.EventId, null, $"{fn} {a.LastName}".Trim(), FeatureKey: "masterclass-invites")))
            {
                var rendered = _templates.Render("masterclass-selection-invite", tokens);
                subject = rendered.Subject;
                html = rendered.HtmlBody;
                await _sender.SendAsync(a.Email, subject, html, ct);
            }
        }
        else
        {
            // Fallback: legacy inline HTML (older test constructions with no provider).
            subject = $"Action required: Choose your Master Class for {ev}";
            html =
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
                ContactLine() +
                "<p>The team</p>";

            await SendAsync(a.Email, $"{fn} {a.LastName}".Trim(), a.EventId, subject, html, ct);
        }

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
        var name = $"{fn} {a.LastName}".Trim();
        // The "what the ticket currently holds" block is sender-built HTML (raw token).
        var held = string.IsNullOrWhiteSpace(inheritedMcTitle)
            ? "<p>The ticket does not currently hold a Master Class — please choose one below.</p>"
            : $"<p>The ticket currently holds the following master class: <strong>{Enc(inheritedMcTitle)}</strong></p>";

        if (_templates is not null)
        {
            var tokens = _templates.NewTokenSet();
            tokens["firstName"] = fn;
            tokens["eventDisplayName"] = ev;
            tokens["heldMasterClass"] = held;     // raw-HTML token (renderer keeps verbatim)
            tokens["selfServiceUrl"] = url;
            using (_context.Set(new EmailContext(Category, a.EventId, null, name, FeatureKey: "masterclass-invites")))
            {
                var rendered = _templates.Render("masterclass-reassignment", tokens);
                await _sender.SendAsync(a.Email, rendered.Subject, rendered.HtmlBody, ct);
            }
            return true;
        }

        // Fallback: legacy inline HTML (older test constructions with no provider).
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
            ContactLine() +
            "<p>The team</p>";

        await SendAsync(a.Email, name, a.EventId,
            $"Action required: Validate your Master Class for {ev} (ticket re-assignment)", html, ct);
        return true;
    }

    /// <summary>
    /// Confirmed-seat email. Renders the <c>masterclass-confirmed</c> TEMPLATE (private
    /// config copy wins, generic shipped default is the fallback) with the firstName /
    /// masterClassTitle / eventDisplayName / landingPageUrl / selfServiceUrl tokens —
    /// keeping the .ics download, the self-service give-up + month-before behaviour, and
    /// the <c>masterclass-invites</c> EmailContext / ring gate. Falls back to inline HTML
    /// for older test constructions with no template provider.
    /// </summary>
    public async Task SendConfirmedAsync(int signupId, string baseUrl, CancellationToken ct = default)
    {
        var c = await LoadAsync(signupId, ct);
        if (c is null) return;
        var token = await _signups.EnsureSelfServiceTokenAsync(c.S.AttendeeId, ct);
        var url = $"{baseUrl.TrimEnd('/')}/MyMasterClass?t={token}";
        var ics = $"{baseUrl.TrimEnd('/')}/MyMasterClass.ics?t={token}";
        // The attendee Master Class LANDING page (prep + Q&A + 1:1 questions, FEATURE 2),
        // reached by the same per-attendee bearer token.
        var landingUrl = $"{baseUrl.TrimEnd('/')}/MasterClassPage/{c.S.SessionId}?t={token}";
        var eventName = await _signups.EventNameAsync(c.EventId, ct);
        var name = $"{c.FirstName} {c.S.Attendee.LastName}".Trim();

        string subject;
        string html;
        if (_templates is not null)
        {
            var tokens = _templates.NewTokenSet();
            tokens["firstName"] = c.FirstName;
            tokens["masterClassTitle"] = c.Title;
            tokens["eventDisplayName"] = eventName;
            tokens["landingPageUrl"] = landingUrl;
            tokens["selfServiceUrl"] = url;
            tokens["icsUrl"] = ics;
            using (_context.Set(new EmailContext(Category, c.EventId, null, name, FeatureKey: "masterclass-invites")))
            {
                var rendered = _templates.Render("masterclass-confirmed", tokens);
                subject = rendered.Subject;
                html = rendered.HtmlBody;
                await _sender.SendAsync(c.Email, subject, html, ct);
            }
            return;
        }

        // Fallback: legacy inline HTML (older test constructions with no provider).
        html =
            $"<p>Hi {Enc(c.FirstName)},</p>" +
            $"<p>Your Master Class place is <strong>confirmed</strong>: <strong>{Enc(c.Title)}</strong>. 🎉</p>" +
            $"<p>Visit your <a href=\"{Enc(landingUrl)}\">Master Class page</a> for what to prepare, the Q&amp;A, " +
            "and to ask the speakers a private question.</p>" +
            $"<p><a href=\"{Enc(ics)}\">Add it to your calendar (.ics)</a>. On your " +
            $"<a href=\"{Enc(url)}\">self-service page</a> you can give up your seat if your plans change, " +
            "and choose to get a calendar reminder about a month before the event.</p>" +
            ContactLine() +
            "<p>See you there,<br/>The team</p>";
        await SendAsync(c.Email, name, c.EventId, $"You're confirmed: {c.Title}", html, ct);
    }

    /// <summary>Waitlist-signup email — terms included; link to leave the waitlist.</summary>
    public async Task SendWaitlistedAsync(int signupId, string baseUrl, CancellationToken ct = default)
    {
        var c = await LoadAsync(signupId, ct);
        if (c is null) return;
        var url = await SelfServiceUrlAsync(c.S.AttendeeId, baseUrl, ct);
        var name = $"{c.FirstName} {c.S.Attendee.LastName}".Trim();
        // Auto-switch consent terms: a sender-built <p> block (raw token) or empty.
        var terms = c.S.AutoSwitchConsentAt is not null
            ? "<p><strong>You accepted:</strong> if a seat opens here, your current Master Class will be " +
              "cancelled and you'll be moved to this one automatically.</p>"
            : "";

        if (_templates is not null)
        {
            var tokens = _templates.NewTokenSet();
            tokens["firstName"] = c.FirstName;
            tokens["masterClassTitle"] = c.Title;
            tokens["selfServiceUrl"] = url;
            tokens["waitlistTerms"] = terms;       // raw-HTML token (renderer keeps verbatim)
            using (_context.Set(new EmailContext(Category, c.EventId, null, name, FeatureKey: "masterclass-invites")))
            {
                var rendered = _templates.Render("masterclass-waitlisted", tokens);
                await _sender.SendAsync(c.Email, rendered.Subject, rendered.HtmlBody, ct);
            }
            return;
        }

        // Fallback: legacy inline HTML (older test constructions with no provider).
        var html =
            $"<p>Hi {Enc(c.FirstName)},</p>" +
            $"<p>You're on the <strong>waitlist</strong> for <strong>{Enc(c.Title)}</strong>. " +
            "We'll email you if a seat opens up.</p>" + terms +
            $"<p>You can <a href=\"{Enc(url)}\">leave the waitlist</a> any time.</p>" +
            ContactLine() +
            "<p>The team</p>";
        await SendAsync(c.Email, name, c.EventId,
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
        var name = $"{fn} {s.Attendee.LastName}".Trim();

        string subject;
        string html;
        if (_templates is not null)
        {
            var tokens = _templates.NewTokenSet();
            tokens["firstName"] = fn;
            tokens["masterClassTitle"] = s.Session.Title;
            using (_context.Set(new EmailContext(Category, s.EventId, null, name, FeatureKey: "masterclass-invites")))
            {
                var rendered = _templates.Render("masterclass-month-reminder", tokens);
                // The .ics attachment stays — only the HTML body became a template.
                await _sender.SendWithIcsAsync(s.Attendee.Email, rendered.Subject, rendered.HtmlBody, ics, "master-class.ics", ct);
            }
            await _signups.MarkMonthReminderSentAsync(s.Id, ct);
            return true;
        }

        // Fallback: legacy inline HTML (older test constructions with no provider).
        subject = $"Coming up: {s.Session.Title}";
        html =
            $"<p>Hi {Enc(fn)},</p>" +
            $"<p>Your Master Class <strong>{Enc(s.Session.Title)}</strong> is coming up — here's the calendar " +
            "entry you asked us to send (attached).</p>" + ContactLine() +
            "<p>See you there,<br/>The team</p>";
        using (_context.Set(new EmailContext(Category, s.EventId, null, name, FeatureKey: "masterclass-invites")))
            await _sender.SendWithIcsAsync(s.Attendee.Email, subject, html, ics, "master-class.ics", ct);

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
        var name = $"{fn} {lastName}".Trim();
        var eventName = await _signups.EventNameAsync(eventId, ct);

        if (_templates is not null)
        {
            var tokens = _templates.NewTokenSet();
            tokens["firstName"] = fn;
            tokens["masterClassTitle"] = mcTitle;
            tokens["eventDisplayName"] = eventName;
            tokens["signupUrl"] = url;
            using (_context.Set(new EmailContext(Category, eventId, null, name, FeatureKey: "masterclass-invites")))
            {
                var rendered = _templates.Render("masterclass-cancelled", tokens);
                await _sender.SendAsync(email, rendered.Subject, rendered.HtmlBody, ct);
            }
            return;
        }

        // Fallback: legacy inline HTML (older test constructions with no provider).
        var html =
            $"<p>Hi {Enc(fn)},</p>" +
            $"<p>Your place in <strong>{Enc(mcTitle)}</strong> for <strong>{Enc(eventName)}</strong> has been <strong>cancelled</strong>.</p>" +
            $"<p>You can sign up for another Master Class on your <a href=\"{Enc(url)}\">self-service page</a> " +
            "(subject to availability).</p>";
        await SendAsync(email, name, eventId,
            $"Cancelled: {mcTitle}", html, ct);
    }
}
