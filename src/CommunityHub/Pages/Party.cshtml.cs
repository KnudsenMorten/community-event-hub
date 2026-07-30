using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages;

/// <summary>
/// AUTHENTICATED Party sign-up (§206). A signed-in participant RSVPs as themselves —
/// name + email come from their hub profile, never typed — and must ACTIVELY pick
/// <b>Yes</b> or <b>No</b> for the party (16:00–18:30, 9 Feb 2027, expo/food area at
/// Bella Center). There is NO default/auto-activated answer: when neither option is
/// chosen the form is not submittable and no RSVP row is created, so the signup/task
/// stays incomplete. When the participant picks <b>Yes</b> they can send themselves a
/// calendar invitation for the window (§193 mechanism), honoring their calendar/override
/// email. The anonymous (no-login) name+email path was removed (§206).
/// </summary>
[Authorize]
public class PartyModel : PageModel
{
    private readonly PartyRsvpService _svc;
    private readonly CommunityHub.Auth.ICurrentParticipantAccessor _participant;
    private readonly CalendarInviteEmailService _calendarInvite;
    private readonly ILogger<PartyModel> _log;

    public PartyModel(
        PartyRsvpService svc,
        CommunityHub.Auth.ICurrentParticipantAccessor participant,
        CalendarInviteEmailService calendarInvite,
        ILogger<PartyModel> log)
    {
        _svc = svc;
        _participant = participant;
        _calendarInvite = calendarInvite;
        _log = log;
    }

    public PartyRsvpService.PartyInfo? Party { get; private set; }
    public bool SubmittedOk { get; private set; }
    public string? ErrorMessage { get; private set; }

    /// <summary>True once a signed-in participant is resolved (the only supported path now).</summary>
    public bool IsSignedIn { get; private set; }

    /// <summary>§164: true when the signed-in participant is a SPONSOR — only then does the
    /// form show the "how many will attend from your company?" head-count input.</summary>
    public bool IsSponsor { get; private set; }

    /// <summary>§227: the signed-in participant's role — picks the role-matched intro copy.</summary>
    public ParticipantRole? Role { get; private set; }

    /// <summary>§227 (operator 2026-07-07): the VERBATIM role-matched party intro copy.</summary>
    public static string RoleIntro(ParticipantRole? role) => role switch
    {
        ParticipantRole.Attendee =>
            "The master class is just the start of pre-day. Once it wraps on 9 Feb 2027, stick around from " +
            "16:00–18:30 in the expo/food area for networking, good bites, and a drink or two — the perfect way " +
            "to mingle with fellow IT pros and reconnect with old colleagues.",
        ParticipantRole.Sponsor =>
            "Some of the best conversations happen away from the booth — and on 9 Feb 2027, from 16:00–18:30 in " +
            "the expo/food area, you'll have a room full of them. Attendees, speakers, and volunteers, all " +
            "together over food and drinks, in a relaxed setting built for real connection. Register your whole " +
            "team in one go — no need for everyone to sign up separately. Sign up your group now.",
        ParticipantRole.Speaker =>
            "Pre-day ends with something worth staying for: from 16:00–18:30 on 9 Feb 2027 in the expo/food " +
            "area, we're bringing everyone together for networking, bites, and drinks. Meet attendees off-stage " +
            "and catch up with fellow speakers before the Appreciation Dinner.",
        ParticipantRole.Volunteer =>
            "Being part of the crew has its perks — including this one. On 9 Feb 2027, from 16:00–18:30 in the " +
            "expo/food area, unwind with food, drinks, and well-earned downtime alongside attendees, speakers, " +
            "and sponsors.",
        ParticipantRole.EventPartner =>
            "On 9 Feb 2027, from 16:00–18:30 in the expo/food area, the whole community comes together over food " +
            "and drinks — attendees, speakers, sponsors, and volunteers in one room. It's the ideal setting to " +
            "strengthen relationships and be part of the buzz from day one.",
        ParticipantRole.Media =>
            "The real stories happen after the sessions. On 9 Feb 2027, from 16:00–18:30 in the expo/food area, " +
            "attendees, speakers, sponsors, and organizers gather for networking over food and drinks — a great " +
            "chance to connect with the community and get a feel for what makes this event tick.",
        ParticipantRole.Organizer =>
            "Pre-day ends the way it should — together. On 9 Feb 2027, from 16:00–18:30 in the expo/food area, " +
            "step out of go-mode and enjoy food, drinks, and a couple of well-earned hours of networking with " +
            "the community and each other.",
        _ =>
            "On 9 Feb 2027, from 16:00–18:30 in the expo/food area, the whole community comes together over " +
            "food and drinks.",
    };

    public string? Name { get; private set; }
    public string? Email { get; private set; }

    /// <summary>§206: the EXPLICIT yes/no choice. Null = unanswered ⇒ the form is not
    /// submittable and no RSVP row is created (no default). True = attending, false = not.</summary>
    [BindProperty] public bool? Attending { get; set; }

    /// <summary>§164: sponsor head count (how many from the company). Bound only for sponsors;
    /// ignored for every other role.</summary>
    [BindProperty] public int? HeadCount { get; set; }

    /// <summary>A confirmation banner shown after the calendar invite is e-mailed.</summary>
    public string? InviteMessage { get; private set; }

    /// <summary>§228: the sponsor company's single GROUP reservation (null = none yet).</summary>
    public PartyRsvpService.CompanyReservation? GroupReservation { get; private set; }

    /// <summary>§228: true when the group reservation was registered by ANOTHER linked contact.</summary>
    public bool GroupByOther { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Party = await _svc.GetActivePartyAsync(ct);
        await PrefillFromSignedInAsync(ct);
        return Page();
    }

    // §206: authenticated-only — resolve name + email from the signed-in participant,
    // surface the sponsor head-count input, and prefill any prior answer (so the explicit
    // choice persists and is editable). When unanswered, Attending stays null.
    private async Task PrefillFromSignedInAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return;
        IsSignedIn = true;
        IsSponsor = me.Role == ParticipantRole.Sponsor;
        Role = me.Role;
        Name = me.FullName;
        Email = me.Email;

        if (Party is null) return;

        // §228: sponsors hold ONE group reservation per company. Surface it to EVERY
        // linked contact — whoever registered — and prefill from it so an edit updates
        // the same single reservation instead of creating a second one.
        if (IsSponsor)
        {
            GroupReservation = await _svc.GetCompanyReservationAsync(Party.EventId, me.ParticipantId, ct);
            if (GroupReservation is not null)
            {
                GroupByOther = GroupReservation.ParticipantId != me.ParticipantId;
                Attending = GroupReservation.Attending;
                HeadCount = GroupReservation.HeadCount;
                return;
            }
        }

        var existing = await _svc.GetForParticipantAsync(Party.EventId, me.ParticipantId, ct);
        if (existing is not null)
        {
            Attending = existing.Attending;
            HeadCount = existing.HeadCount;
        }
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Party = await _svc.GetActivePartyAsync(ct);
        if (Party is null) { ErrorMessage = "There is no active event right now."; return Page(); }

        var me = _participant.Current;
        if (me is null) { ErrorMessage = "Please sign in to RSVP for the party."; return Page(); }
        IsSignedIn = true;
        IsSponsor = me.Role == ParticipantRole.Sponsor;
        Role = me.Role;
        Name = me.FullName;
        Email = me.Email;

        // §206: require an EXPLICIT yes/no. No default — if the participant did not pick an
        // option the form is incomplete: keep the task open, create no RSVP row.
        if (Attending is null)
        {
            ErrorMessage = "Please choose Yes or No so we know whether to expect you at the party.";
            return Page();
        }

        // §228: a sponsor's RSVP is the COMPANY's single group reservation — whichever
        // linked contact saves it updates the same reservation (never a second row).
        var result = IsSponsor
            ? await _svc.SubmitGroupAsync(
                me.FullName, me.Email, Attending.Value, HeadCount, me.ParticipantId, ct)
            : await _svc.SubmitAsync(
                me.FullName, me.Email, Attending.Value, ipHash: null, null, me.ParticipantId, ct);
        if (!result.Ok) { ErrorMessage = result.Error; return Page(); }

        SubmittedOk = true;
        return Page();
    }

    /// <summary>
    /// §206: e-mail the signed-in participant a calendar INVITATION for the party window
    /// (16:00–18:30, the §193 invite mechanism — honors their calendar/override email).
    /// Only meaningful once they have RSVP'd Yes; a stable UID means a re-send updates the
    /// same calendar entry rather than duplicating it.
    /// </summary>
    public async Task<IActionResult> OnPostInviteAsync(CancellationToken ct)
    {
        Party = await _svc.GetActivePartyAsync(ct);
        var me = _participant.Current;
        if (Party is null || me is null)
        {
            ErrorMessage = "There is no active event right now.";
            return Page();
        }
        IsSignedIn = true;
        IsSponsor = me.Role == ParticipantRole.Sponsor;
        Name = me.FullName;
        Email = me.Email;
        SubmittedOk = true;
        Attending = true;

        var (startUtc, endUtc) = PartyRsvpService.WindowUtc(Party);
        try
        {
            // §252 pass-2 orphan (a): alongside the attached .ics, offer "open the
            // calendar entry" web links (Google + Outlook compose) — a managed browser
            // tends to SAVE an .ics instead of handing it to the calendar app, and the
            // operator asked for open-not-download links.
            var summary = $"{Party.EventName} — Party";
            var details = $"Join us for the {Party.EventName} party — {Party.Location}.";
            var googleUrl = System.Net.WebUtility.HtmlEncode(CalendarLinkBuilder.GoogleUrl(
                summary, startUtc, endUtc, details, Party.Location));
            var outlookUrl = System.Net.WebUtility.HtmlEncode(CalendarLinkBuilder.OutlookUrl(
                summary, startUtc, endUtc, details, Party.Location));
            var sent = await _calendarInvite.SendItemInviteAsync(
                me.ParticipantId,
                uid: $"party-{Party.EventId}@eventhub",
                summary: summary,
                description: details,
                location: Party.Location,
                start: startUtc,
                end: endUtc,
                allDay: false,
                fileName: "party.ics",
                introHtml: "Here is your calendar invitation for the party. Prefer to add it online? "
                    + $"Open it in <a href=\"{googleUrl}\">Google Calendar</a> or "
                    + $"<a href=\"{outlookUrl}\">Outlook</a>.",
                ct: ct);
            InviteMessage = sent
                ? sent.Confirmation()
                : "Calendar invitations are turned off for this event.";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Party calendar invite failed for participant {Pid}.", me.ParticipantId);
            InviteMessage = "We couldn't send the invite just now — please try again later.";
        }

        return Page();
    }
}
