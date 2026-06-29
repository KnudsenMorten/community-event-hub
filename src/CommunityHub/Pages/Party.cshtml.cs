using System.Security.Cryptography;
using System.Text;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages;

/// <summary>
/// Anonymous (no-login) Party sign-up. Public form: name + email + opt-in to
/// attend the Party (16:00–18:00 on the pre-day). Drives the Bella Center food
/// headcount (REQUIREMENTS §6). Spam handling mirrors the no-login survey /
/// ask-a-question forms: a honeypot field + an IP hash (never reversible to PII).
/// Upserts by email so a re-submit updates the same RSVP.
/// </summary>
[AllowAnonymous]
public class PartyModel : PageModel
{
    private readonly PartyRsvpService _svc;
    private readonly CommunityHub.Auth.ICurrentParticipantAccessor _participant;
    private readonly ILogger<PartyModel> _log;

    public PartyModel(
        PartyRsvpService svc,
        CommunityHub.Auth.ICurrentParticipantAccessor participant,
        ILogger<PartyModel> log)
    {
        _svc = svc;
        _participant = participant;
        _log = log;
    }

    public PartyRsvpService.PartyInfo? Party { get; private set; }
    public bool SubmittedOk { get; private set; }
    public string? ErrorMessage { get; private set; }

    /// <summary>§164: true when a signed-in participant is filling the form — name + email are
    /// then auto-filled (read-only) from their hub profile, not typed.</summary>
    public bool IsSignedIn { get; private set; }

    /// <summary>§164: true when the signed-in participant is a SPONSOR — only then does the
    /// form show the "how many will attend from your company?" head-count input.</summary>
    public bool IsSponsor { get; private set; }

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public bool Attending { get; set; } = true;

    /// <summary>§164: sponsor head count (how many from the company). Bound only for sponsors;
    /// ignored for every other role.</summary>
    [BindProperty] public int? HeadCount { get; set; }

    /// <summary>Honeypot — hidden; any value ⇒ silent success, no write.</summary>
    [BindProperty] public string? Website { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Party = await _svc.GetActivePartyAsync(ct);
        await PrefillFromSignedInAsync(ct);
        return Page();
    }

    // §164: authenticated form — auto-fill name + email from the signed-in participant,
    // surface the sponsor head-count input, and prefill any prior answer so the form is
    // editable (a person can change their mind / their company head count).
    private async Task PrefillFromSignedInAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return;
        IsSignedIn = true;
        IsSponsor = me.Role == ParticipantRole.Sponsor;
        Name = me.FullName;
        Email = me.Email;

        if (Party is null) return;
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

        // §164: a signed-in participant RSVPs as THEMSELVES — take name + email from their hub
        // profile, never the posted fields (so they can't submit on someone else's behalf).
        var me = _participant.Current;
        if (me is not null)
        {
            IsSignedIn = true;
            IsSponsor = me.Role == ParticipantRole.Sponsor;
            Name = me.FullName;
            Email = me.Email;
        }

        if (!string.IsNullOrWhiteSpace(Website))   // honeypot tripped
        {
            _log.LogInformation("Party RSVP honeypot tripped from {Ip}",
                HttpContext.Connection.RemoteIpAddress);
            SubmittedOk = true;
            return Page();
        }

        var ipHash = HashIp(HttpContext.Connection.RemoteIpAddress?.ToString());
        // Head count is a sponsor-only figure; participant id stamps an authenticated RSVP
        // (null for anonymous) so the reminder/task wiring can mark their task Done.
        var headCount = IsSponsor ? HeadCount : null;
        var result = await _svc.SubmitAsync(
            Name, Email, Attending, ipHash, headCount, me?.ParticipantId, ct);
        if (!result.Ok) { ErrorMessage = result.Error; return Page(); }

        SubmittedOk = true;
        return Page();
    }

    private static string? HashIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("party:" + ip));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// "Add to my calendar" — a single-VEVENT RFC 5545 .ics for the Party (no email
    /// invite: the allowlist would drop a public address, and mailing arbitrary
    /// typed addresses is an abuse vector). Anonymous; no PII, no per-person token —
    /// the same party for everyone.
    /// </summary>
    public async Task<IActionResult> OnGetIcsAsync(CancellationToken ct)
    {
        var p = await _svc.GetActivePartyAsync(ct);
        if (p is null) return NotFound();

        DateTimeOffset Local(int h, int m) =>
            ScheduleService.EventLocal(p.Date.ToDateTime(new TimeOnly(h, m)));
        string Z(DateTimeOffset d) => d.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");

        var host = HttpContext.Request.Host.Host;
        var uid = $"party-{p.EventId}@{host}";
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//CommunityHub//Party//EN",
            "METHOD:PUBLISH", "CALSCALE:GREGORIAN",
            "BEGIN:VEVENT",
            $"UID:{uid}",
            $"DTSTAMP:{Z(Local(p.StartHour, p.StartMinute))}",
            $"DTSTART:{Z(Local(p.StartHour, p.StartMinute))}",
            $"DTEND:{Z(Local(p.EndHour, p.EndMinute))}",
            $"SUMMARY:{p.EventName} — Party",
            "END:VEVENT", "END:VCALENDAR") + "\r\n";

        // Inline (no filename) so it opens in the calendar app rather than downloading.
        return File(Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8");
    }
}
