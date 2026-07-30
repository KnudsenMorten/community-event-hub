using System.Text;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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

    // §210/§210b: the confirmed-seat calendar invite represents the FULL Master Class day
    // (registration/breakfast → close), not just one session's slot, so ~1500 attendees
    // calendar the whole pre-day and are nudged to arrive EARLY. §210b: the INVITE now
    // blocks 08:00–16:00 (registration & breakfast open at 07:00, the class itself runs
    // 09:00–16:00) — blocking 08:00 in the calendar nudges people to arrive in good time.
    // These are the operator's stated defaults (9 Feb 2027); the wall-clock time is placed
    // in the edition's timezone (dates.timezone) so no UTC offset is hardcoded.
    private static readonly DateOnly DefaultMasterClassDay = new(2027, 2, 9);
    private static readonly TimeOnly MasterClassStart = new(8, 0);
    private static readonly TimeOnly MasterClassEnd = new(16, 0);

    /// <summary>
    /// §341-1 — the SAME day/window the confirmation's calendar invite uses, exposed so the
    /// attendee hub's "Send Calendar invite for Master Class" button can reuse it instead of
    /// re-stating it. One definition: a second copy would drift, and an invite whose window
    /// disagrees with the one the system considers correct is worse than no button (the §335
    /// "re-state the condition, never approximate it" rule).
    /// </summary>
    public static (DateOnly Day, TimeOnly Start, TimeOnly End) MasterClassDayWindow =>
        (DefaultMasterClassDay, MasterClassStart, MasterClassEnd);

    /// <summary>
    /// §341-1 — the stable calendar UID for an attendee's Master Class day, shared with the
    /// confirmation mail's invite so a manual re-send UPDATES the existing calendar entry
    /// rather than creating a duplicate (the §322n rule).
    /// </summary>
    public static string MasterClassInviteUid(int sessionId, string host) =>
        $"mc-{sessionId}@{host}";

    /// <summary>
    /// §341-1 — the Master Class day window as UTC instants, resolved through THIS instance's
    /// edition timezone (<c>dates.timezone</c>).
    ///
    /// <para>An instance method on purpose: the timezone comes from edition config, which this
    /// service already loads. The attendee hub's manual-invite button calls this instead of
    /// resolving the zone itself, so the manual invite and the auto-attached invite can never
    /// land on different instants.</para>
    /// </summary>
    public (DateTimeOffset StartUtc, DateTimeOffset EndUtc) MasterClassDayWindowUtc()
    {
        var (day, start, end) = MasterClassDayWindow;
        var tz = ResolveTimeZoneSafe(EventTimezone());
        var localStart = new DateTime(day.Year, day.Month, day.Day, start.Hour, start.Minute, 0,
            DateTimeKind.Unspecified);
        var localEnd = new DateTime(day.Year, day.Month, day.Day, end.Hour, end.Minute, 0,
            DateTimeKind.Unspecified);
        return (
            new DateTimeOffset(localStart, tz.GetUtcOffset(localStart)).ToUniversalTime(),
            new DateTimeOffset(localEnd, tz.GetUtcOffset(localEnd)).ToUniversalTime());
    }

    /// <summary>Resolve a zone id, falling back to the edition default then UTC — never throws.</summary>
    private static TimeZoneInfo ResolveTimeZoneSafe(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(DefaultEventTimezone); }
            catch { return TimeZoneInfo.Utc; }
        }
    }

    // Last-resort timezone when edition config carries none — the ELDK edition zone.
    private const string DefaultEventTimezone = "Europe/Copenhagen";

    /// <summary>§210/§210b: the prominent "registration & breakfast from 07:00 / come
    /// early / class runs 09:00–16:00" callout used by the inline confirmed-email fallback
    /// (the templates carry their own copy). Mobile-first highlighted box (100% width,
    /// padding-based, no fixed heights), readable in light/dark email themes (§191).</summary>
    private const string EarlyArrivalBlockHtml =
        "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:0 0 20px;\">" +
        "<tr><td style=\"background-color:#fff8e1;border-left:4px solid #f5a623;padding:16px 18px;border-radius:6px;\">" +
        "<p style=\"margin:0;font-size:16px;color:#1a1a2e;\">" +
        "<strong>🚪 Registration &amp; breakfast from 07:00 &mdash; come early</strong> so we can check everyone in; " +
        "your Master Class runs <strong>09:00&ndash;16:00</strong> (the calendar invite is 08:00&ndash;16:00 to help " +
        "you arrive in good time).</p></td></tr></table>";

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

    // §234 3: delivered-vs-dropped seam — the reassignment-validation pending marker
    // is cleared only when the transport actually DELIVERED (a ring-dropped send keeps
    // the marker so the next sync run retries). Null (legacy/test wiring) ⇒ old
    // behaviour (a non-throwing send counts as sent).
    private readonly IEmailDeliveryOutcome? _outcome;

    // §234 6: the hub's from-address, used as the ORGANIZER of the confirmed-seat /
    // month-reminder calendar invites (a METHOD:REQUEST is only processed as a real
    // invitation when the organizer is the sender). Optional → shipped defaults.
    private readonly EmailOptions _emailOptions;

    public MasterClassEmailService(
        CommunityHubDbContext db, IEmailSender sender,
        IEmailContextAccessor context, MasterClassSignupService signups,
        EmailTemplateProvider? templates = null,
        EventEditionConfigLoader? eventConfigLoader = null,
        EventConfigOptions? eventConfigOptions = null,
        IEmailDeliveryOutcome? outcome = null,
        IOptions<EmailOptions>? emailOptions = null)
    {
        _db = db; _sender = sender; _context = context; _signups = signups;
        _templates = templates;
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
        _outcome = outcome;
        _emailOptions = emailOptions?.Value ?? new EmailOptions();
    }

    private static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);

    /// <summary>Resolve (once, cached) the support email from edition config, else the default.</summary>
    private string SupportEmail()
    {
        if (_supportEmail is not null) return _supportEmail;
        _supportEmail = ResolveSupportEmail(_eventConfigLoader, _eventConfigOptions);
        return _supportEmail;
    }

    /// <summary>
    /// §707.35b — the edition's AUTHORITATIVE 2-day ticket class id(s), read once and cached.
    /// </summary>
    /// <remarks>
    /// 🔒 Operator 2026-07-30: *"ticket class has a unique number, dont use the displayname of the
    /// ticket class"*. `masterClassTickets.twoDayClassIds` in the edition config; for ELDK27 that is
    /// the 2-day class, while the 1-day class is deliberately absent (anything unlisted is non-2-day).
    ///
    /// <para>Empty when no config is wired (older constructions and most tests) ⇒
    /// <see cref="MasterClassTicketPolicy"/> falls back to the name markers, which is its documented
    /// behaviour and keeps every existing caller working unchanged.</para>
    /// </remarks>
    private IReadOnlyList<string> TwoDayClassIds()
    {
        if (_twoDayClassIds is not null) return _twoDayClassIds;
        try
        {
            _twoDayClassIds = _eventConfigLoader is null
                ? Array.Empty<string>()
                : _eventConfigLoader.Load(
                        (_eventConfigOptions ?? new EventConfigOptions()).EventConfigPath)
                    .MasterClassTwoDayClassIds;
        }
        catch
        {
            // Fail-safe: a config read must never break a send. No ids ⇒ the name rule decides.
            _twoDayClassIds = Array.Empty<string>();
        }
        return _twoDayClassIds;
    }

    private IReadOnlyList<string>? _twoDayClassIds;

    /// <summary>
    /// §707.35b — the ONE place that answers "does this mirrored ticket grant a Master Class?".
    /// Id-authoritative, name markers only as the fallback for rows with no id.
    /// </summary>
    private bool IsTwoDay(string? ticketClassId, string? ticketClassName) =>
        MasterClassTicketPolicy.IncludesMasterClass(ticketClassId, ticketClassName, TwoDayClassIds());

    /// <summary>The "Questions? Email …" contact line for the inline master-class bodies.</summary>
    private string ContactLine() =>
        $"<p>Questions? Email {Enc(SupportEmail())}.</p>";

    /// <summary>
    /// §169: the recipient attendee's login <see cref="Participant"/> id, so a Master
    /// Class email's hub CTA can carry their PERSONAL auto-login magic-link
    /// (<see cref="EmailTemplateProvider.NewTokenSet"/> with a participant id rewrites
    /// the <c>{{hubUrl}}</c> token to <c>{HubUrl}/go/{token}</c>). The 2-day-ticket
    /// holders are provisioned a login-capable, Attendee-role Participant — matched on
    /// Email + EventId — by <see cref="AttendeeWelcomeProvisioningService"/>; the
    /// Attendee-role match is preferred, but any same-email Participant for the edition
    /// is accepted as a fallback.
    ///
    /// <para>Best-effort + FAIL-SAFE: no Participant yet (e.g. the email fired before
    /// provisioning) or any error ⇒ <c>null</c> ⇒ the §169 seam keeps the PLAIN hub
    /// URL and the attendee self-service <c>selectionUrl</c> is untouched. Never throws.</para>
    /// </summary>
    internal static async Task<int?> ResolveAttendeeParticipantIdAsync(
        CommunityHubDbContext db, string? email, int eventId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        try
        {
            var norm = email.Trim().ToLowerInvariant();
            var matches = await db.Participants
                .Where(p => p.EventId == eventId && p.Email.ToLower() == norm)
                .Select(p => new { p.Id, p.Role })
                .ToListAsync(ct);
            if (matches.Count == 0) return null;
            // Prefer the provisioned Attendee-role login participant; else any match.
            var pick = matches.FirstOrDefault(m => m.Role == ParticipantRole.Attendee)
                       ?? matches[0];
            return pick.Id;
        }
        catch
        {
            // Fail-safe: never let participant resolution break a Master Class send.
            return null;
        }
    }

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

    /// <summary>The edition timezone id (<c>dates.timezone</c>, an IANA id such as
    /// <c>Europe/Copenhagen</c>) for the §210 Master Class day invite, else the default.</summary>
    private string EventTimezone() => ResolveEventTimezone(_eventConfigLoader, _eventConfigOptions);

    internal static string ResolveEventTimezone(
        EventEditionConfigLoader? loader, EventConfigOptions? options)
    {
        if (loader is null) return DefaultEventTimezone;
        try
        {
            var path = options?.EventConfigPath ?? new EventConfigOptions().EventConfigPath;
            var cfg = loader.Load(path);
            if (cfg.Dates is not null && !string.IsNullOrWhiteSpace(cfg.Dates.Timezone))
            {
                return cfg.Dates.Timezone;
            }
        }
        catch { /* fail-safe to default */ }
        return DefaultEventTimezone;
    }

    /// <summary>
    /// RFC 5545 VEVENT for a master class — timed if the session has a start, else all-day
    /// on the edition pre-day. §234 6: when <paramref name="organizerEmail"/> AND
    /// <paramref name="attendeeEmail"/> are supplied the VEVENT is a REAL invitation
    /// (<c>METHOD:REQUEST</c> + ORGANIZER=sender + ATTENDEE=recipient — what makes mail
    /// clients auto-add / offer Accept); without them it stays the legacy
    /// <c>METHOD:PUBLISH</c> attachment. The UID is stable per session, so a re-send
    /// UPDATES the recipient's existing entry instead of duplicating it.
    /// </summary>
    public static string BuildIcs(
        string host, int sessionId, string title, DateTimeOffset? start, DateTimeOffset? end,
        DateOnly editionStart,
        string? organizerEmail = null, string? organizerName = null,
        string? attendeeEmail = null, string? attendeeName = null)
    {
        string Z(DateTimeOffset d) => d.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
        var invite = !string.IsNullOrWhiteSpace(organizerEmail) && !string.IsNullOrWhiteSpace(attendeeEmail);
        var uid = MasterClassInviteUid(sessionId, host);   // 341-1: one definition (shared with the attendee hub button)
        var sb = new StringBuilder();
        sb.Append($"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//CommunityHub//MasterClass//EN\r\nMETHOD:{(invite ? "REQUEST" : "PUBLISH")}\r\nBEGIN:VEVENT\r\n");
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
        if (invite) AppendInviteRoles(sb, organizerEmail!, organizerName, attendeeEmail!, attendeeName);
        sb.Append($"SUMMARY:Master Class — {title}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        return sb.ToString();
    }

    /// <summary>§234 6: the ORGANIZER (sender) + ATTENDEE (recipient) lines that turn a
    /// VEVENT into a processable invitation. RSVP=FALSE — replies to the from-mailbox
    /// are not processed, so they are not solicited.</summary>
    private static void AppendInviteRoles(
        StringBuilder sb, string organizerEmail, string? organizerName,
        string attendeeEmail, string? attendeeName)
    {
        static string Esc(string s) => s
            .Replace("\\", "\\\\").Replace(",", "\\,").Replace(";", "\\;")
            .Replace("\r\n", "\\n").Replace("\n", "\\n");
        sb.Append($"ORGANIZER;CN={Esc(string.IsNullOrWhiteSpace(organizerName) ? organizerEmail : organizerName!)}:mailto:{organizerEmail}\r\n");
        sb.Append("ATTENDEE;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=FALSE;");
        sb.Append($"CN={Esc(string.IsNullOrWhiteSpace(attendeeName) ? attendeeEmail : attendeeName!)}:mailto:{attendeeEmail}\r\n");
    }

    /// <summary>
    /// §210/§210b RFC 5545 VEVENT for the WHOLE Master Class day — a fixed local wall-clock
    /// window (§210b default 08:00–16:00 on the pre-day) so the confirmed-seat invite drives
    /// attendees to arrive EARLY rather than just at their session's slot. The times are
    /// emitted in the edition's local zone via a <c>TZID</c> + a <c>VTIMEZONE</c> whose
    /// offset is DERIVED from <paramref name="timezoneId"/> (the existing
    /// <see cref="EventLocalTime"/> resolver) — no hardcoded UTC offset, matching the
    /// rest of the calendar code. §234 6: with <paramref name="organizerEmail"/> +
    /// <paramref name="attendeeEmail"/> supplied it is a REAL <c>METHOD:REQUEST</c>
    /// invitation (ORGANIZER=sender, ATTENDEE=recipient); without them it stays the
    /// legacy <c>METHOD:PUBLISH</c> attachment. Stable UID per session ⇒ re-sends update.
    /// </summary>
    public static string BuildMasterClassDayIcs(
        string host, int sessionId, string title, string? timezoneId,
        DateOnly day, TimeOnly start, TimeOnly end,
        string? organizerEmail = null, string? organizerName = null,
        string? attendeeEmail = null, string? attendeeName = null)
    {
        var tz = EventLocalTime.Resolve(timezoneId);
        var tzid = string.IsNullOrWhiteSpace(timezoneId) ? EventLocalTime.UtcLabel : timezoneId.Trim();
        var localStart = new DateTime(day.Year, day.Month, day.Day, start.Hour, start.Minute, 0, DateTimeKind.Unspecified);
        var localEnd = new DateTime(day.Year, day.Month, day.Day, end.Hour, end.Minute, 0, DateTimeKind.Unspecified);
        var offset = FmtOffset(tz.GetUtcOffset(localStart));
        var uid = MasterClassInviteUid(sessionId, host);   // 341-1: one definition (shared with the attendee hub button)
        var invite = !string.IsNullOrWhiteSpace(organizerEmail) && !string.IsNullOrWhiteSpace(attendeeEmail);

        static string Local(DateTime d) => d.ToString("yyyyMMdd'T'HHmmss");

        var sb = new StringBuilder();
        sb.Append($"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//CommunityHub//MasterClass//EN\r\nMETHOD:{(invite ? "REQUEST" : "PUBLISH")}\r\n");
        // A single STANDARD component carrying the offset in effect for the Master Class
        // day (derived from the resolved zone) so the 08:00–16:00 wall time resolves in
        // the recipient's calendar without assuming UTC.
        sb.Append("BEGIN:VTIMEZONE\r\n");
        sb.Append($"TZID:{tzid}\r\n");
        sb.Append("BEGIN:STANDARD\r\n");
        sb.Append($"DTSTART:{Local(localStart)}\r\n");
        sb.Append($"TZOFFSETFROM:{offset}\r\n");
        sb.Append($"TZOFFSETTO:{offset}\r\n");
        sb.Append("END:STANDARD\r\nEND:VTIMEZONE\r\n");
        sb.Append("BEGIN:VEVENT\r\n");
        sb.Append($"UID:{uid}\r\n");
        sb.Append($"DTSTAMP:{DateTimeOffset.UtcNow.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}\r\n");
        sb.Append($"DTSTART;TZID={tzid}:{Local(localStart)}\r\n");
        sb.Append($"DTEND;TZID={tzid}:{Local(localEnd)}\r\n");
        if (invite) AppendInviteRoles(sb, organizerEmail!, organizerName, attendeeEmail!, attendeeName);
        sb.Append($"SUMMARY:Master Class — {title}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// §252 pass-2 orphan (a): the SAME §210b Master-Class day window the .ics carries
    /// (08:00–16:00 local on the pre-day), converted to UTC for the
    /// <see cref="CalendarLinkBuilder"/> "open the calendar entry" web links, so the
    /// Google/Outlook links and the attached invitation state one identical window.
    /// </summary>
    internal static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) MasterClassDayWindowUtc(
        string? timezoneId)
    {
        var tz = EventLocalTime.Resolve(timezoneId);
        var day = DefaultMasterClassDay;
        var localStart = new DateTime(day.Year, day.Month, day.Day,
            MasterClassStart.Hour, MasterClassStart.Minute, 0, DateTimeKind.Unspecified);
        var localEnd = new DateTime(day.Year, day.Month, day.Day,
            MasterClassEnd.Hour, MasterClassEnd.Minute, 0, DateTimeKind.Unspecified);
        return (new DateTimeOffset(localStart, tz.GetUtcOffset(localStart)),
                new DateTimeOffset(localEnd, tz.GetUtcOffset(localEnd)));
    }

    /// <summary>RFC 5545 UTC-offset rendering (e.g. <c>+0100</c>), derived from the zone.</summary>
    private static string FmtOffset(TimeSpan o)
    {
        var sign = o < TimeSpan.Zero ? "-" : "+";
        var a = o.Duration();
        return $"{sign}{a.Hours:00}{a.Minutes:00}";
    }

    private sealed record Ctx(MasterClassSignup S, string Email, string FirstName, string Title, int EventId);

    private async Task<Ctx?> LoadAsync(int signupId, CancellationToken ct)
    {
        var s = await _db.MasterClassSignups
            .Include(x => x.Attendee).Include(x => x.Session).ThenInclude(se => se.Event)
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

    // §252 F5/F6: every Master Class lifecycle send rides the SAME welcome-email
    // ring as the §241 selection invite + §243 ticket-cancellation (one ring, one
    // raise at go-live — a split ring could deliver invites but drop confirms), and
    // carries the recipient's resolved Participant id so the §234 participant-keyed
    // ring gate can engage (no AttendeeWelcome tag — the §217 cap is only for the
    // mass welcome).
    /// <param name="templateName">
    /// §707.2 — the MAIL IDENTITY, so this send resolves its own (mail × role) ring instead of falling
    /// back to the <c>welcome-email</c> FEATURE ring. This helper is shared by the legacy no-template
    /// fallbacks of THREE different mails, so the identity has to be passed in: it is a property of the
    /// mail, not of how the body happened to be rendered. <c>null</c> only for a mail that is
    /// <paramref name="ringExempt"/> anyway, where no ring can apply.
    /// </param>
    /// <param name="ringExempt">
    /// Mirrors the templated branch's exemption so the two branches cannot gate differently (§707.3).
    /// </param>
    private async Task SendAsync(
        string email, string name, int eventId, int? participantId,
        string subject, string html, string? templateName, CancellationToken ct,
        bool ringExempt = false)
    {
        using (_context.Set(new EmailContext(
            Category, eventId, participantId, name,
            TemplateName: templateName, FeatureKey: "welcome-email", RingExempt: ringExempt)))
            await _sender.SendAsync(email, subject, html, ct);
    }

    /// <summary>
    /// The 2-day-ticket attendee's FIRST email — the de-facto 2-day welcome (§215). It
    /// opens with a WELCOME intro and PARTY info (16:00–18:30, 9 Feb 2027, expo/food area,
    /// Bella Center, RSVP in the hub), its PRIMARY CTA lands the attendee on the Get-Started
    /// wizard (<c>{{hubUrl}}/Forms/GetStarted</c>, carried through the §169 magic-link so they
    /// arrive signed-in), and it KEEPS the Master-Class selection deep-link as a secondary
    /// link. Tracked + ring-gated; stamps <see cref="Attendee.MasterClassInviteSentAt"/>.
    /// Skips if not eligible or already sent (unless <paramref name="force"/>). Returns true
    /// when an email went out.
    ///
    /// <para>§241: because this IS the 2-day attendee WELCOME, the send is tagged
    /// <see cref="EmailContext.AttendeeWelcome"/> + FeatureKey <c>welcome-email</c> (the
    /// §217 <c>Email:AttendeeWelcomeMaxReleaseRing</c> cap + the welcome feature ring both
    /// apply, keyed on the recipient's provisioned Participant), and the
    /// <see cref="Attendee.MasterClassInviteSentAt"/> stamp is written only when the
    /// transport actually DELIVERED (<see cref="IEmailDeliveryOutcome"/> seam) — a
    /// ring-dropped invite stays eligible so the sync auto-retries it once rings widen.
    /// Null seam (legacy/test wiring) ⇒ the old always-stamp behaviour.</para>
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
        var name = $"{fn} {a.LastName}".Trim();
        // §169 + §241: the recipient's provisioned login Participant id — it binds the
        // template's {{hubUrl}} CTA to their personal magic-link AND (as the ambient
        // EmailContext.ParticipantId) lets the sender ring-gate on the PERSON even when
        // the delivery address alone would not resolve to a participant.
        var pid = await ResolveAttendeeParticipantIdAsync(_db, a.Email, a.EventId, ct);

        // 🔒 §707.14 — NEVER PROMISE A ONE-TAP SIGN-IN TO A LOGIN THAT IS SWITCHED OFF.
        //
        // This mail's primary CTA is a MAGIC LINK, and `EmailMagicLinkService.ResolveAsync` refuses
        // an inactive participant ("Account is inactive") — so sending it to a deactivated login
        // delivers a button that silently drops the person on the PIN page. Found live 2026-07-30:
        // participant 82 was organizer-deactivated on 07-28 and was still sent this invite on
        // 07-30 06:25. The operator reported it as "the magic link is not working"; the link was
        // fine, the ACCOUNT was off.
        //
        // Holding the mail is the honest behaviour AND self-healing: `MasterClassInviteSentAt` is
        // left unstamped, so the moment the login is re-activated (or the §707.12 ticket
        // activation runs) the next sync sends it for real. `force` does NOT override this —
        // an organizer re-sending by hand would hit exactly the same dead link.
        if (pid is int loginId)
        {
            var loginUsable = await _db.Participants
                .AnyAsync(p => p.Id == loginId && p.IsActive, ct);
            if (!loginUsable) return false;
        }

        // §241/§217: the selection invite is the de-facto 2-day WELCOME (§215) — tag it
        // as an attendee welcome so the dedicated AttendeeWelcomeMaxReleaseRing ceiling
        // + the welcome-email feature ring apply, exactly like the 1-day welcome.
        // §707.2: TemplateName is set on the SHARED context, so both the templated branch and the
        // legacy fallback below carry the same mail identity.
        var context = new EmailContext(
            Category, a.EventId, pid, name,
            TemplateName: "masterclass-selection-invite",
            FeatureKey: "welcome-email", AttendeeWelcome: true);

        string subject;
        string html;
        if (_templates is not null)
        {
            // Render the masterclass-selection-invite template (private config copy
            // wins; generic shipped default is the fallback). The renderer encodes
            // the tokens at the seam, so no per-value Enc(...) here. The .ics/branding
            // tokens are seeded by NewTokenSet(); selectionUrl carries the secure link.
            // §169: the participant id makes the generic {{hubUrl}} CTA the recipient's
            // personal auto-login magic-link (the selectionUrl deep-link is unchanged).
            var tokens = _templates.NewTokenSet(pid);
            tokens["firstName"] = fn;
            tokens["eventDisplayName"] = ev;
            tokens["selectionUrl"] = url;
            using (_context.Set(context))
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
            // §215: a full 2-day welcome — welcome intro + Party info + a Get-Started
            // primary CTA, keeping the Master-Class selection deep-link as a secondary
            // link. (The template path carries the §169 magic-link Get-Started CTA; this
            // no-template fallback uses the plain hub Get-Started route.)
            var getStartedUrl = $"{baseUrl.TrimEnd('/')}/Forms/Wizard";
            subject = $"Action required: Choose your Master Class for {ev}";
            html =
                $"<p>Welcome to <strong>{Enc(ev)}</strong>, {Enc(fn)}!</p>" +
                "<p>Thank you for choosing a 2-day ticket with Master Class — we can't wait to see you. " +
                "You have your own Event Hub to get everything ready, with two quick things to set up: " +
                "pick your Master Class and let us know about the Party.</p>" +
                "<p>🎉 <strong>You're invited to the Party</strong> — 16:00–18:30 on 9 February 2027, in the " +
                "expo / food area at Bella Center. You can RSVP in the hub.</p>" +
                $"<p style=\"margin:22px 0;\"><a href=\"{Enc(getStartedUrl)}\" style=\"display:inline-block;background-color:#1565c0;color:#ffffff !important;text-decoration:none;font-weight:700;padding:14px 30px;border-radius:8px;\">Get started in the hub</a></p>" +
                "<p><strong>Please note:</strong> your seat is not confirmed until you complete the selection. " +
                "You'll be asked to choose a Master Class — once selected, your seat is secured. " +
                $"You can jump straight to the chooser: <a href=\"{Enc(url)}\">Choose my Master Class</a>.</p>" +
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

            using (_context.Set(context))
                await _sender.SendAsync(a.Email, subject, html, ct);
        }

        // §241: consult the delivered-vs-dropped seam — a ring-dropped / kill-switched
        // send is NOT an invite. Do not stamp, so the attendee stays in
        // EligibleNotInvitedIdsAsync and the sync jobs retry automatically once the
        // operator widens the ring / attendee-welcome cap. No seam wired (legacy/test
        // constructions) ⇒ the old behaviour: a non-throwing send counts as sent.
        if (_outcome is not null && !_outcome.LastSendDelivered) return false;

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

        // §169 + §252 F6: the recipient's provisioned login Participant id — it binds
        // the template's {{hubUrl}} CTA to their personal magic-link AND (as the
        // ambient EmailContext.ParticipantId) lets the sender ring-gate on the PERSON.
        var pid = await ResolveAttendeeParticipantIdAsync(_db, a.Email, a.EventId, ct);

        if (_templates is not null)
        {
            var tokens = _templates.NewTokenSet(pid);
            tokens["firstName"] = fn;
            tokens["eventDisplayName"] = ev;
            tokens["heldMasterClass"] = held;     // raw-HTML token (renderer keeps verbatim)
            tokens["selfServiceUrl"] = url;
            using (_context.Set(new EmailContext(
                Category, a.EventId, pid, name,
                TemplateName: "masterclass-reassignment", FeatureKey: "welcome-email")))
            {
                var rendered = _templates.Render("masterclass-reassignment", tokens);
                await _sender.SendAsync(a.Email, rendered.Subject, rendered.HtmlBody, ct);
            }
            return await CompleteReassignmentValidationAsync(a, ct);
        }

        // Fallback: legacy inline HTML (older test constructions with no provider).
        var html =
            $"<p>Dear {Enc(fn)},</p>" +
            $"<p>You have been re-assigned a <strong>2-day ticket with Master Class</strong> for <strong>{Enc(ev)}</strong>.</p>" +
            held +
            "<p>You can choose to log in to the Event Hub to:</p><ul>" +
            "<li>Cancel a Master Class</li><li>Sign up for waitlists</li>" +
            "<li>Ask questions to your Master Class speakers</li>" +
            "<li>Review preparation instructions for your class</li><li>Sync the event to your calendar</li></ul>" +
            $"<p style=\"margin:22px 0;\"><a href=\"{Enc(url)}\" style=\"display:inline-block;background-color:#1565c0;color:#ffffff !important;text-decoration:none;font-weight:700;padding:14px 30px;border-radius:8px;\">Review my Master Class</a></p>" +
            "<h3>Waitlist</h3>" +
            "<p>Once a Master Class fills up, the waitlist option becomes available, allowing you to join the " +
            "waitlist for one alternative Master Class.</p><ul>" +
            "<li>You cannot join a waitlist for a Master Class while seats are still available.</li>" +
            "<li>By signing up for a waitlist, you accept that your current Master Class booking will be cancelled " +
            "and replaced by the waitlisted class if a confirmed seat becomes available.</li></ul>" +
            ContactLine() +
            "<p>The team</p>";

        await SendAsync(a.Email, name, a.EventId, pid,
            $"Action required: Validate your Master Class for {ev} (ticket re-assignment)", html,
            "masterclass-reassignment", ct);
        return await CompleteReassignmentValidationAsync(a, ct);
    }

    /// <summary>
    /// §234 3 — after a reassignment-validation send that did not throw, consult the
    /// delivered-vs-dropped seam: only an ACTUALLY DELIVERED send clears the attendee's
    /// <see cref="Attendee.ReassignmentValidationPendingSince"/> retry marker (and counts
    /// as sent). A ring-dropped / kill-switched send keeps the marker so the next sync
    /// run retries — the same not-delivered-is-not-sent rule the welcome/reminder
    /// ledgers follow. With no seam wired (legacy/test constructions) the old behaviour
    /// stands: a non-throwing send counts as sent and clears the marker.
    /// </summary>
    private async Task<bool> CompleteReassignmentValidationAsync(Attendee a, CancellationToken ct)
    {
        var delivered = _outcome is null || _outcome.LastSendDelivered;
        if (delivered && a.ReassignmentValidationPendingSince is not null)
        {
            a.ReassignmentValidationPendingSince = null;
            await _db.SaveChangesAsync(ct);
        }
        return delivered;
    }

    /// <summary>One attendee still owed the reassignment-validation email (§234 3), with
    /// the inherited-MC title RECOMPUTED from their current confirmed signup.</summary>
    public sealed record PendingReassignmentValidation(int AttendeeId, string? InheritedMcTitle);

    /// <summary>
    /// §234 3 — persist the "owes a reassignment-validation email" intent for the given
    /// attendees BEFORE any send is attempted (crash-safe: a run that dies between the
    /// sync and the sends leaves the markers for the next run). Idempotent: an already
    /// pending attendee keeps their original timestamp.
    /// </summary>
    public async Task MarkReassignmentValidationsPendingAsync(
        int eventId, IReadOnlyCollection<int> attendeeIds, CancellationToken ct = default)
    {
        if (attendeeIds.Count == 0) return;
        var rows = await _db.Attendees
            .Where(a => a.EventId == eventId && attendeeIds.Contains(a.Id))
            .ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var a in rows)
            a.ReassignmentValidationPendingSince ??= now;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// §234 3 — every attendee in the edition still owed the reassignment-validation
    /// email: marked pending (this run or an earlier one whose send failed / was
    /// ring-dropped) and still ACTIVE in the mirror (a since-cancelled ticket keeps its
    /// marker dormant; it resumes if the ticket reactivates). The inherited-MC title is
    /// recomputed from the CURRENT confirmed signup — exactly what the sync computed at
    /// detection time — so a retried email never states stale facts.
    /// </summary>
    public async Task<IReadOnlyList<PendingReassignmentValidation>> GetPendingReassignmentValidationsAsync(
        int eventId, CancellationToken ct = default)
    {
        var pending = await _db.Attendees.AsNoTracking()
            .Where(a => a.EventId == eventId
                        && a.ReassignmentValidationPendingSince != null
                        && a.MirrorState == MirrorState.Active
                        // No address ⇒ unsendable: keep the marker dormant (the mirror
                        // will fill the email on a later pull) instead of retry-noise.
                        && a.Email != null && a.Email != "")
            .Select(a => a.Id)
            .ToListAsync(ct);
        if (pending.Count == 0) return Array.Empty<PendingReassignmentValidation>();

        var titles = await _db.MasterClassSignups.AsNoTracking()
            .Where(s => s.EventId == eventId && pending.Contains(s.AttendeeId)
                        && s.Status == MasterClassSignupStatus.Confirmed)
            .Select(s => new { s.AttendeeId, s.Session.Title })
            .ToListAsync(ct);
        var titleByAttendee = titles
            .GroupBy(t => t.AttendeeId)
            .ToDictionary(g => g.Key, g => g.First().Title);

        return pending
            .Select(id => new PendingReassignmentValidation(
                id, titleByAttendee.TryGetValue(id, out var t) ? t : null))
            .ToList();
    }

    /// <summary>
    /// Confirmed-seat email. Renders the <c>masterclass-confirmed</c> TEMPLATE (private
    /// config copy wins, generic shipped default is the fallback) with the firstName /
    /// masterClassTitle / eventDisplayName / landingPageUrl / selfServiceUrl tokens —
    /// keeping the .ics download, the self-service give-up + month-before behaviour, and
    /// the <c>welcome-email</c> EmailContext / ring gate (§252 F5 — the whole MC funnel
    /// rides one ring). Falls back to inline HTML
    /// for older test constructions with no template provider.
    /// </summary>
    public async Task SendConfirmedAsync(int signupId, string baseUrl, CancellationToken ct = default)
    {
        var c = await LoadAsync(signupId, ct);
        if (c is null) return;
        // §257: the automatic Master-Class calendar invite is off by default. When off,
        // send the confirmation WITHOUT the attached METHOD:REQUEST invite; the Google/
        // Outlook "add to calendar" links stay in the body so the attendee can add it
        // themselves. Attach the invite only when the organizer enabled auto invites.
        var autoInvite = await _db.Events
            .Where(e => e.Id == c.EventId)
            .Select(e => e.AutoCalendarInvitesEnabled)
            .FirstOrDefaultAsync(ct);
        var token = await _signups.EnsureSelfServiceTokenAsync(c.S.AttendeeId, ct);
        var url = $"{baseUrl.TrimEnd('/')}/MyMasterClass?t={token}";
        // §193: instead of a "Download .ics" link, ATTACH the calendar invitation to the
        // confirmation e-mail so the seat lands straight in the attendee's calendar.
        var icsHost = Uri.TryCreate(baseUrl, UriKind.Absolute, out var bu) ? bu.Host : "communityhub";
        // §210/§210b: calendar the FULL Master Class day (08:00–16:00 local on the pre-day)
        // — not just the session slot — so attendees plan to arrive early (registration &
        // breakfast open at 07:00; the class itself runs 09:00–16:00). The timezone is
        // taken from edition config so the wall time lands in the venue's zone.
        var name = $"{c.FirstName} {c.S.Attendee.LastName}".Trim();
        // §234 6: a REAL calendar invitation — ORGANIZER = the hub's from-address,
        // ATTENDEE = the confirmed attendee — so the seat lands in their calendar with
        // invite processing (auto-add / Accept) instead of an inert attachment.
        var ics = BuildMasterClassDayIcs(
            icsHost, c.S.SessionId, c.Title, EventTimezone(),
            DefaultMasterClassDay, MasterClassStart, MasterClassEnd,
            organizerEmail: _emailOptions.FromAddress, organizerName: _emailOptions.FromDisplayName,
            attendeeEmail: c.Email, attendeeName: name);
        // The attendee Master Class LANDING page (prep + Q&A + 1:1 questions, FEATURE 2),
        // reached by the same per-attendee bearer token.
        var landingUrl = $"{baseUrl.TrimEnd('/')}/MasterClassPage/{c.S.SessionId}?t={token}";
        var eventName = await _signups.EventNameAsync(c.EventId, ct);

        // §252 pass-2 orphan (a): "open the calendar entry" web links (Google + Outlook
        // compose) alongside the attached .ics — a managed browser tends to SAVE an .ics
        // instead of handing it to the calendar app, and the operator asked for
        // open-not-download. Same day window as the invite (08:00–16:00 local, §210b).
        var (dayStartUtc, dayEndUtc) = MasterClassDayWindowUtc(EventTimezone());
        var googleUrl = CalendarLinkBuilder.GoogleUrl(
            $"Master Class — {c.Title}", dayStartUtc, dayEndUtc,
            details: $"{eventName}: your confirmed Master Class seat.");
        var outlookUrl = CalendarLinkBuilder.OutlookUrl(
            $"Master Class — {c.Title}", dayStartUtc, dayEndUtc,
            details: $"{eventName}: your confirmed Master Class seat.");

        // §169 + §252 F6: the recipient's provisioned login Participant id — magic-link
        // CTA + participant-keyed ring gate (never fail-closed dropped as "unknown").
        var pid = await ResolveAttendeeParticipantIdAsync(_db, c.Email, c.EventId, ct);

        string subject;
        string html;
        if (_templates is not null)
        {
            var tokens = _templates.NewTokenSet(pid);
            tokens["firstName"] = c.FirstName;
            tokens["masterClassTitle"] = c.Title;
            tokens["eventDisplayName"] = eventName;
            tokens["landingPageUrl"] = landingUrl;
            // §418 (operator 2026-07-27: "the button should redirect to the q&a page (which is the
            // topic) … it goes wrongly to master class selection"). The button read "Open my Master
            // Class page" but linked to the SELECTION step: §341-6 pointed it at /Attendee, then
            // §351-7/§365 repointed every /Attendee link to the wizard — and this one was swept
            // along with them even though its destination was never the selection screen.
            //
            // The ID only, so the template can build {{hubUrl}}/MasterClassPage/{{id}} — the
            // MAGIC-LINK origin plus the deep path, which signs the attendee in AND lands them on
            // their own class. `landingPageUrl` above reaches the same page but carries the
            // per-attendee ?t= bearer token instead, which would break the "every button is a magic
            // link, no exceptions" rule.
            tokens["masterClassSessionId"] = c.S.SessionId.ToString();
            tokens["selfServiceUrl"] = url;
            tokens["googleCalendarUrl"] = googleUrl;
            tokens["outlookCalendarUrl"] = outlookUrl;
            using (_context.Set(new EmailContext(
                Category, c.EventId, pid, name,
                TemplateName: "masterclass-confirmed", FeatureKey: "welcome-email")))
            {
                var rendered = _templates.Render("masterclass-confirmed", tokens);
                subject = rendered.Subject;
                html = rendered.HtmlBody;
                if (autoInvite)
                    await _sender.SendWithIcsAsync(c.Email, subject, html, ics, "master-class.ics", ct);
                else
                    await _sender.SendAsync(c.Email, subject, html, ct);
            }
            return;
        }

        // Fallback: legacy inline HTML (older test constructions with no provider).
        // §257: the calendar sentence + subject depend on whether the auto invite is on.
        // §378: the "attached — open it" half is GONE. A METHOD:REQUEST invitation renders natively
        // as Accept/Decline; there is no attachment for the recipient to open. The add-online links
        // stay, because those are real and useful in both branches.
        var calendarSentence = autoInvite
            ? "<p><strong>Add this to your calendar:</strong> " +
              $"<a href=\"{Enc(googleUrl)}\">Google Calendar</a> or " +
              $"<a href=\"{Enc(outlookUrl)}\">Outlook</a>. On your "
            : "<p><strong>Add this to your calendar:</strong> " +
              $"<a href=\"{Enc(googleUrl)}\">Google Calendar</a> or " +
              $"<a href=\"{Enc(outlookUrl)}\">Outlook</a>. On your ";
        html =
            $"<p>Hi {Enc(c.FirstName)},</p>" +
            $"<p>Your Master Class place is <strong>confirmed</strong>: <strong>{Enc(c.Title)}</strong>. 🎉</p>" +
            EarlyArrivalBlockHtml +
            $"<p>Visit your <a href=\"{Enc(landingUrl)}\">Master Class page</a> for what to prepare, the Q&amp;A, " +
            "and to ask the speakers a private question.</p>" +
            calendarSentence +
            $"<a href=\"{Enc(url)}\">self-service page</a> you can give up your seat if your plans change, " +
            "and choose to get a calendar reminder about a month before the event.</p>" +
            ContactLine() +
            "<p>See you there,<br/>The team</p>";
        var confirmSubject = autoInvite
            ? $"You're confirmed: {c.Title} — see the calendar invite for important details"
            : $"You're confirmed: {c.Title}";
        using (_context.Set(new EmailContext(
            Category, c.EventId, pid, name,
            TemplateName: "masterclass-confirmed", FeatureKey: "welcome-email")))
        {
            if (autoInvite)
                await _sender.SendWithIcsAsync(c.Email, confirmSubject, html, ics, "master-class.ics", ct);
            else
                await _sender.SendAsync(c.Email, confirmSubject, html, ct);
        }
    }

    /// <summary>
    /// Waitlist-signup email — terms included; link to leave the waitlist. When the
    /// attendee's <paramref name="waitlistPosition"/> is known (the value surfaced by
    /// <see cref="MasterClassSignupService.GetForAttendeeAsync"/>), the email states their
    /// place in the queue ("You are #N on the waitlist") so they see their real status.
    /// </summary>
    public async Task SendWaitlistedAsync(
        int signupId, string baseUrl, int? waitlistPosition = null, CancellationToken ct = default)
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
        // The attendee's place in the queue: a sender-built <p> block (raw token) or empty.
        var positionBlock = waitlistPosition is int pos && pos > 0
            ? $"<p>You are <strong>#{pos}</strong> on the waitlist — we admit waitlisted attendees in order.</p>"
            : "";

        // §169 + §252 F6: the recipient's provisioned login Participant id — magic-link
        // CTA + participant-keyed ring gate (never fail-closed dropped as "unknown").
        var pid = await ResolveAttendeeParticipantIdAsync(_db, c.Email, c.EventId, ct);

        if (_templates is not null)
        {
            var tokens = _templates.NewTokenSet(pid);
            tokens["firstName"] = c.FirstName;
            tokens["masterClassTitle"] = c.Title;
            tokens["selfServiceUrl"] = url;
            tokens["waitlistTerms"] = terms;             // raw-HTML token (renderer keeps verbatim)
            tokens["waitlistPositionBlock"] = positionBlock; // raw-HTML token (Block suffix)
            using (_context.Set(new EmailContext(
                Category, c.EventId, pid, name,
                TemplateName: "masterclass-waitlisted", FeatureKey: "welcome-email")))
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
            "We'll email you if a seat opens up.</p>" + positionBlock + terms +
            $"<p>You can <a href=\"{Enc(url)}\">leave the waitlist</a> any time.</p>" +
            ContactLine() +
            "<p>The team</p>";
        await SendAsync(c.Email, name, c.EventId, pid,
            $"You're on the waitlist: {c.Title}", html, "masterclass-waitlisted", ct);
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

        var fn = string.IsNullOrWhiteSpace(s.Attendee.FirstName) ? "there" : s.Attendee.FirstName;
        var name = $"{fn} {s.Attendee.LastName}".Trim();
        // §234 6: the month-before reminder carries a REAL invitation too (same stable
        // UID as the confirmed-seat invite ⇒ it UPDATES the existing calendar entry).
        var ics = BuildIcs(host, s.SessionId, s.Session.Title, s.Session.StartsAt, s.Session.EndsAt,
            s.Session.Event.StartDate,
            organizerEmail: _emailOptions.FromAddress, organizerName: _emailOptions.FromDisplayName,
            attendeeEmail: s.Attendee.Email, attendeeName: name);

        // §169 + §252 F6: the recipient's provisioned login Participant id — magic-link
        // CTA + participant-keyed ring gate (never fail-closed dropped as "unknown").
        var pid = await ResolveAttendeeParticipantIdAsync(_db, s.Attendee.Email, s.EventId, ct);

        string subject;
        string html;
        if (_templates is not null)
        {
            var tokens = _templates.NewTokenSet(pid);
            tokens["firstName"] = fn;
            tokens["masterClassTitle"] = s.Session.Title;
            // 🔒 §707.2 TRAP 1 — DELIBERATELY NOT GIVEN A TemplateName, AND IT MUST STAY THAT WAY.
            // `masterclass-month-reminder` was REMOVED from EmailTemplateCatalog by §244 (operator:
            // "drop this reminder 1 month before, it's too confusing"); the file survives only so
            // historic sends can be re-rendered, and MasterClassMonthReminderJob is unscheduled.
            // Since §705.2 a REGISTERED key with no ring row fails closed — but an UNREGISTERED one
            // falls back to the feature ring, which is the current, working behaviour. Wiring this
            // key would therefore not fail loudly; it would just make the send resolve nothing and
            // go silent. Leave it unwired unless the mail is genuinely revived and re-registered.
            using (_context.Set(new EmailContext(Category, s.EventId, pid, name, FeatureKey: "welcome-email")))
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
        using (_context.Set(new EmailContext(Category, s.EventId, pid, name, FeatureKey: "welcome-email")))
            await _sender.SendWithIcsAsync(s.Attendee.Email, subject, html, ics, "master-class.ics", ct);

        await _signups.MarkMonthReminderSentAsync(s.Id, ct);
        return true;
    }

    /// <summary>§243: reminder-ledger type + template name for the 2-day TICKET-cancelled
    /// notification (distinct from <c>masterclass-cancelled</c>, which is a SEAT
    /// cancellation for a still-active ticket holder).</summary>
    public const string TicketCancelledTemplate = "masterclass-cancelled-ticket";

    /// <summary>§243: how far back the sweep looks for un-notified cancellations. Bounds
    /// BOTH the retry window for failed / ring-dropped sends AND the backfill on first
    /// deploy (a weeks-old cancellation is never suddenly mailed about).</summary>
    public const int TicketCancelledSweepWindowDays = 7;

    /// <summary>
    /// §243 (operator 2026-07-07) — notify every 2-DAY holder whose TICKET was soft-cancelled
    /// (<see cref="MirrorState.Cancelled"/>, §128) that their ticket was cancelled and their
    /// Master-Class seat released. SWEEP + LEDGER model (the same self-healing shape as the
    /// §241 selection-invite sweep, with <see cref="SentReminder"/> as the once-per-occasion
    /// dedup instead of an attendee column):
    /// <list type="bullet">
    /// <item>the occasion keys on <see cref="Attendee.CancelledAt"/>, so ONE email per
    /// cancellation — a REAPPEARED ticket clears the stamp (§128) and a LATER cancellation
    /// stamps a new instant ⇒ a fresh occasion ⇒ it mails again;</item>
    /// <item>the ledger row is written only on an actually-DELIVERED send
    /// (<see cref="IEmailDeliveryOutcome"/> seam) — a failed / ring-dropped send is retried
    /// by the next sync run within the <see cref="TicketCancelledSweepWindowDays"/> window;</item>
    /// <item>ring-gated per recipient like any attendee mail: EmailContext FeatureKey
    /// <c>welcome-email</c> + the recipient's provisioned Participant id (NO AttendeeWelcome
    /// tag — this is not a welcome, so the §217 ceiling does not apply).</item>
    /// </list>
    /// Returns how many notifications were actually delivered. 1-day cancellations are out of
    /// scope (they get no mail at all — §242).
    /// </summary>
    public async Task<int> SendPendingTicketCancellationsAsync(
        int eventId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var windowStart = now.AddDays(-TicketCancelledSweepWindowDays);

        // 🔒 §707.34b / §707.35b — 2-day-ness is decided from the TICKET CLASS, NOT TicketStatus.
        //
        // 🔑 The class is read ID-FIRST (`TicketClassId`), name markers only as the fallback for rows
        // with no id. Operator 2026-07-30: *"ticket class has a unique number, dont use the
        // displayname of the ticket class"* — the name is an editable label (and WAS edited, §447),
        // so keying a mail audience on it lets a rename in Zoho silently change who gets mailed.
        //
        // THE BUG THIS REPLACES: the filter used to be
        // `MirrorState == Cancelled && TicketStatus == TicketStatus.TwoDay`, and those two are
        // MUTUALLY EXCLUSIVE by construction. `AttendeeTicketSyncService.FromBackstage` maps any
        // ticket Zoho reports as not-attending to `TicketStatus.None` unconditionally — and the SAME
        // `status_string = "not_attending"` is what sets `CancelledUpstream`, which is what makes the
        // row Cancelled. One upstream field drove both conditions in opposite directions, so a 2-day
        // attendee cancelled in Zoho was NEVER notified. Silently: no error, no ledger row, nothing
        // to notice. Verified on PROD 2026-07-30 (row 7312: MirrorState 1, TicketStatus 0, no mail).
        //
        // ⚠️ It looked correct because it is TIMING-DEPENDENT: it fires when a row reaches Cancelled
        // while the attendee feed still says *attending* (an order-level cancel Zoho has not yet
        // propagated), which leaves TicketStatus at TwoDay. It worked just often enough to hide.
        //
        // The ticket CLASS survives the cancellation (verified on PROD row 7312: still
        // "2-day (Pre-day + Main Event)"), so it answers the same question using a field the
        // cancellation does not destroy. §242 still holds — the 1-day class is not in
        // `twoDayClassIds`, so 1-day cancellations remain out of scope — and an unknown class
        // FAILS CLOSED (no mail).
        var candidates = await _db.Attendees.AsNoTracking()
            .Where(a => a.EventId == eventId
                        && a.MirrorState == MirrorState.Cancelled
                        && a.CancelledAt != null && a.CancelledAt >= windowStart
                        && a.Email != null && a.Email != "")
            .Select(a => new { a.Id, a.Email, a.CancelledAt, a.TicketClassId, a.TicketClassName })
            .ToListAsync(ct);

        // Evaluated in memory: the id-set membership and the space-insensitive marker match are not
        // EF-translatable. The candidate set is one edition's cancellations inside a 7-day window,
        // so it is small by construction.
        var cancelled = candidates
            .Where(a => IsTwoDay(a.TicketClassId, a.TicketClassName))
            .ToList();
        if (cancelled.Count == 0) return 0;

        var sent = 0;
        foreach (var c in cancelled)
        {
            // One email per PERSON per cancellation DAY: keyed on the identity address +
            // the CancelledAt DATE (an email holding two tickets cancelled in the same
            // reconcile still gets ONE notification, not two).
            //
            // §326bc: this used to key on UtcTicks. CancelledAt is re-stamped on every
            // cancel and cleared on reappearance, so a flapping feed minted a BRAND-NEW
            // occasion key every cycle — the sent-ledger could never match and the whole
            // cancelled cohort was re-mailed on each flap. The unique index cannot help
            // when the key itself keeps changing. A date-granular key absorbs the flap
            // while still letting a genuine LATER cancellation (after a re-purchase)
            // notify again, which a fully static key would have suppressed forever.
            var occasion = $"cancelled:{c.CancelledAt!.Value.UtcDateTime:yyyy-MM-dd}";
            var already = await _db.SentReminders.AnyAsync(
                s => s.EventId == eventId
                     && s.RecipientEmail == c.Email
                     && s.ReminderType == TicketCancelledTemplate
                     && s.OccasionKey == occasion, ct);
            if (already) continue;

            try
            {
                if (!await SendTicketCancelledAsync(c.Id, ct)) continue;
            }
            catch
            {
                continue;   // not ledgered ⇒ retried next run (within the window)
            }

            _db.SentReminders.Add(new SentReminder
            {
                EventId = eventId,
                RecipientEmail = c.Email,
                ReminderType = TicketCancelledTemplate,
                OccasionKey = occasion,
                SentAt = now,
            });
            await _db.SaveChangesAsync(ct);
            sent++;
        }
        return sent;
    }

    /// <summary>
    /// §243 — render + send the <c>masterclass-cancelled-ticket</c> notification to ONE
    /// soft-cancelled 2-day holder. Returns true only when the transport actually
    /// DELIVERED (seam-aware, like <see cref="SendSelectionInviteAsync"/>); the caller
    /// owes the ledger row. Fail-safe skips: not found / not 2-day / not cancelled / no email.
    /// <para>§707.34b / §707.35b — "2-day" is read from the ticket CLASS (<see cref="Attendee
    /// .TicketClassId"/> first, name markers as fallback), which survives cancellation — NOT from
    /// <see cref="Attendee.TicketStatus"/>, which the sync zeroes on the very same pass that cancels
    /// the row.</para>
    /// </summary>
    private async Task<bool> SendTicketCancelledAsync(int attendeeId, CancellationToken ct)
    {
        var a = await _db.Attendees.AsNoTracking().Include(x => x.Event)
            .FirstOrDefaultAsync(x => x.Id == attendeeId, ct);
        // 🔒 §707.34b / §707.35b — 2-day-ness from the ticket CLASS (id first), matching the sweep.
        // This guard carried the SAME `TicketStatus == TwoDay` contradiction, so fixing only the
        // sweep would have changed nothing: it would have found the rows and then refused every one
        // of them here.
        if (a is null
            || !IsTwoDay(a.TicketClassId, a.TicketClassName)
            || a.MirrorState != MirrorState.Cancelled
            || string.IsNullOrWhiteSpace(a.Email)) return false;

        var fn = string.IsNullOrWhiteSpace(a.FirstName) ? "there" : a.FirstName;
        var ev = a.Event.DisplayName;
        var name = $"{fn} {a.LastName}".Trim();
        // Ring-gate on the PERSON (their provisioned login Participant), like every
        // attendee mail; the login itself is already locked out (§216), which does not
        // block ring resolution.
        var pid = await ResolveAttendeeParticipantIdAsync(_db, a.Email, a.EventId, ct);
        // §707.2: shared by both branches below, so each carries the mail identity.
        var context = new EmailContext(
            Category, a.EventId, pid, name,
            TemplateName: TicketCancelledTemplate, FeatureKey: "welcome-email");

        if (_templates is not null)
        {
            var tokens = _templates.NewTokenSet(pid);
            tokens["firstName"] = fn;
            tokens["eventDisplayName"] = ev;

            // 🔒 §707.24 — DO NOT CLAIM A LOCKOUT THAT DID NOT HAPPEN. This mail used to assert
            // "your Event Hub sign-in has been closed" unconditionally. §216 only closes the login
            // when the address holds NO remaining active ticket, so someone cancelling one of two
            // tickets kept full access and was told otherwise — the operator hit exactly that on
            // 2026-07-30. Say what is true, per recipient. (Block suffix = raw-HTML token.)
            var stillEntitled = await _db.Attendees.AnyAsync(x =>
                x.EventId == a.EventId
                && x.MirrorState == MirrorState.Active
                && x.Email == a.Email, ct);
            tokens["signInBlockBlock"] = stillEntitled
                ? "<p style=\"margin:0 0 16px;\">You still hold another active ticket, so your "
                  + "<strong>Event Hub sign-in stays open</strong>.</p>"
                : "<p style=\"margin:0 0 16px;\">Your <strong>Event Hub sign-in has been closed</strong>.</p>";

            using (_context.Set(context))
            {
                var rendered = _templates.Render(TicketCancelledTemplate, tokens);
                await _sender.SendAsync(a.Email, rendered.Subject, rendered.HtmlBody, ct);
            }
        }
        else
        {
            // Fallback: inline HTML (older test constructions with no provider).
            var html =
                $"<p>Hi {Enc(fn)},</p>" +
                $"<p>Your <strong>2-day ticket with Master Class</strong> for <strong>{Enc(ev)}</strong> has been " +
                "<strong>cancelled</strong>, so your Master Class seat has been released and your Event Hub " +
                "sign-in has been closed.</p>" +
                "<p>If this is unexpected — for example your ticket was re-assigned or re-purchased — " +
                "please contact us and we'll sort it out.</p>" +
                ContactLine() +
                "<p>The team</p>";
            using (_context.Set(context))
                await _sender.SendAsync(a.Email, $"Your ticket for {ev} was cancelled", html, ct);
        }

        // Delivered-vs-dropped seam: a ring-dropped / kill-switched send is NOT a
        // notification — the caller must not ledger it, so the next sweep retries.
        return _outcome is null || _outcome.LastSendDelivered;
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

        // §169 + §252 F6: the recipient's provisioned login Participant id — magic-link
        // CTA + participant-keyed ring gate (never fail-closed dropped as "unknown").
        var pid = await ResolveAttendeeParticipantIdAsync(_db, email, eventId, ct);

        if (_templates is not null)
        {
            var tokens = _templates.NewTokenSet(pid);
            tokens["firstName"] = fn;
            tokens["masterClassTitle"] = mcTitle;
            tokens["eventDisplayName"] = eventName;
            tokens["signupUrl"] = url;
            // §346 + the 🔒 §326by rule ("all emails that is coming from a participant click a
            // button … should never be ring-gated"). This mail is the RECEIPT for a button the
            // attendee just pressed — giving up their seat. Ring-gating it meant an attendee
            // outside the released ring cancelled and heard nothing back: a silent no-op they
            // cannot diagnose, which is exactly what that rule exists to prevent. It stays tagged
            // with the funnel's feature key (so §252 F5's "one ring for the whole funnel" grouping
            // is unchanged on the Settings page) but is RingExempt at the send.
            // 🔒 §707.20 — RING-GATED, by operator decision 2026-07-30 (*"it should also be
            // ring-gated. set it for ring 2 … it is a similar mail as any other mail"*). This send
            // was `RingExempt: true` from §346 under the §326by participant-clicked rule; he has
            // overruled that classification, so it now carries its identity and takes its own
            // (mail × role) ring like every other mail.
            using (_context.Set(new EmailContext(
                Category, eventId, pid, name,
                TemplateName: "masterclass-cancelled", FeatureKey: "welcome-email")))
            {
                var rendered = _templates.Render("masterclass-cancelled", tokens);
                await _sender.SendAsync(email, rendered.Subject, rendered.HtmlBody, ct);
            }
            return;
        }

        // Fallback: legacy inline HTML (older test constructions with no provider).
        var html =
            $"<p>Hi {Enc(fn)},</p>" +
            $"<p>Your Master Class seat for <strong>{Enc(mcTitle)}</strong> at <strong>{Enc(eventName)}</strong> has been <strong>cancelled</strong>.</p>" +
            $"<p>You can sign up for another Master Class on your <a href=\"{Enc(url)}\">self-service page</a> " +
            "(subject to availability).</p>";
        // §707.20 — the fallback carries the SAME identity and is no longer exempt, so both branches
        // gate identically (the asymmetry §707.2 flagged is closed rather than mirrored).
        await SendAsync(email, name, eventId, pid,
            $"Master Class cancelled: {mcTitle}", html, "masterclass-cancelled", ct);
    }
}
